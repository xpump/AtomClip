using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.IO.Ports;
using Microsoft.Win32;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace AtomClipUsb
{
#if BLE
    sealed partial class MainForm
    {
        void OpenFirmwareInstaller()
        {
            if (typeBusy || sending) { MessageBox.Show(this, "请等待当前发送任务结束后再设置开发板。", "任务进行中"); return; }
            using (var installer = new FirmwareInstaller(logger, delegate { Stop(); })) installer.ShowDialog(this);
        }
        void ShowLicenses()
        {
            using (var window = new Form { Text = "关于 AtomClip 0.2.0", Font = Font, Size = new Size(720, 520), StartPosition = FormStartPosition.CenterParent })
            {
                var text = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
                using (var input = Assembly.GetExecutingAssembly().GetManifestResourceStream("AtomClip.Licenses"))
                using (var reader = new StreamReader(input)) text.Text = reader.ReadToEnd();
                window.Padding = new Padding(24); window.Controls.Add(text); Theme.Apply(window); window.ShowDialog(this);
            }
        }
    }

    public sealed class FlashCommandResult
    {
        public int ExitCode;
        public string Output;
    }

    public static class FlashPlan
    {
        public static async Task Execute(string port,string folder,FirmwareBuild build,bool factory,Func<string,Task<FlashCommandResult>> run)
        {
            if(!Regex.IsMatch(port??"","^COM[0-9]+$",RegexOptions.IgnoreCase)||folder.IndexOf('"')>=0)throw new IOException("USB 端口 / 固件路径无效");build.Validate(folder);
            string common="--chip esp32s3 --port "+port+" --baud 460800 --before no-reset --after no-reset ";
            var probe=await run(common+"flash-id");if(probe.ExitCode!=0||!Regex.IsMatch(probe.Output??"",@"Detected flash size:\s*"+(build.flashBytes/1048576)+@"MB\b",RegexOptions.IgnoreCase))throw new IOException("未确认匹配的 ESP32-S3 / Flash 容量，未写入");
            var efuse=await run(common+"read-mem 0x6000703c");var value=Regex.Match(efuse.Output??"",@"0x6000703c\s*(?:=|:)\s*0x([a-f0-9]+)",RegexOptions.IgnoreCase);if(efuse.ExitCode!=0||!value.Success||(Convert.ToUInt32(value.Groups[1].Value,16)&512)!=0)throw new IOException("无法确认 SPI NOR 类型；OPI Flash 不受支持");
            string table=Path.Combine(folder,"existing-partitions.bin");var read=await run(common+"read-flash 0x8000 3072 \""+table+"\"");if(read.ExitCode!=0||!File.Exists(table)||new FileInfo(table).Length!=3072)throw new IOException("分区表读取失败，未写入");
            if(!build.Compatible(folder,File.ReadAllBytes(table))&&!factory)throw new IOException("分区不兼容或首次安装：先备份加密绑定配置，再明确选择清空安装；之后可通过USB恢复绑定");
            if(factory){var clear=await run(common+"erase-flash");if(clear.ExitCode!=0)throw new IOException("清空 Flash 未完成，请重新进入下载模式");}
            var args=new StringBuilder(common+"write-flash --flash-mode keep --flash-freq keep --flash-size keep");foreach(var seg in build.segments)args.Append(" 0x").Append(seg.offset.ToString("x")).Append(" \"").Append(Path.Combine(folder,seg.file)).Append("\"");
            var written=await run(args.ToString());if(written.ExitCode!=0)throw new IOException("分段写入失败，请重新进入下载模式完整重试");
            args=new StringBuilder(common+"verify-flash");foreach(var seg in build.segments)args.Append(" 0x").Append(seg.offset.ToString("x")).Append(" \"").Append(Path.Combine(folder,seg.file)).Append("\"");var verified=await run(args.ToString());if(verified.ExitCode!=0||Regex.Matches(verified.Output??"",@"Verification successful",RegexOptions.IgnoreCase).Count!=4)throw new IOException("未确认逐段校验成功");
        }
    }

    sealed class FlashPort
    {
        public string Name, Label;
        public bool Espressif;
        public override string ToString() { return Label; }
    }

    sealed class FirmwareInstaller : Form
    {
        readonly ComboBox profiles = new ComboBox { DropDownStyle=ComboBoxStyle.DropDownList,Dock=DockStyle.Fill };
        readonly CheckBox factory = new CheckBox { Text="清空安装（删除所有板上程序、数据和绑定）",AutoSize=true };
        readonly BoardCatalog catalog=new BoardCatalog();
        readonly FileLog logger;
        readonly Action beforeFlash;
        readonly ComboBox ports = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        readonly Button refresh = new SoftButton { Text = "刷新设备" }, start = new SoftButton { Text = "识别并烧录" };
        readonly TextBox output = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill };
        readonly ProgressBar progress = new ProgressBar { Dock = DockStyle.Fill, Minimum = 0, Maximum = 100 };
        readonly Label status = new Label { AutoSize = true, Dock = DockStyle.Fill, Text = "等待选择开发板。" };
        bool busy;

        public FirmwareInstaller(FileLog logger, Action beforeFlash)
        {
            this.logger = logger; this.beforeFlash = beforeFlash;
            Text = "开发板设置 · ESP32-S3"; Font = new Font("Microsoft YaHei UI", 10);
            StartPosition = FormStartPosition.CenterParent; Size = new Size(780, 720); MinimumSize = new Size(560, 460);
            var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var root = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Padding = new Padding(28), RowCount = 7 };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < 7; i++) root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var title = new Label { AutoSize = true, Dock = DockStyle.Fill, Text = "新开发板初始化 / 固件更新", Font = new Font(Font.FontFamily, 18, FontStyle.Bold), Margin = new Padding(0, 0, 0, 12) };
            var header = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2 };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            header.Controls.Add(title, 0, 0); header.Controls.Add(new HelpButton(delegate { Theme.Help(this, "开发板设置帮助", Guide.Firmware); }), 1, 0); root.Controls.Add(header, 0, 0);
            root.Controls.Add(new Label { AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 0, 0, 14), Text =
                "1. 选择匹配板型（ESP32-S3、SPI NOR、原生 USB）。\r\n2. 按板厂说明进入 ROM 下载模式。\r\n3. 选择 USB 端口；首次安装或分区不兼容需选择清空安装。" }, 0, 1);
            var row = new CardPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14), Margin = new Padding(0, 0, 0, 16) };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            refresh.AutoSize = start.AutoSize = true; refresh.Padding = start.Padding = new Padding(8, 4, 8, 4);
            foreach(var p in catalog.Profiles.Values)profiles.Items.Add(p.name);profiles.SelectedIndex=0;row.Controls.Add(profiles,0,0);row.Controls.Add(ports, 0, 1);row.Controls.Add(factory,0,2);
            var flashActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 10, 0, 0) };
            flashActions.Controls.Add(refresh); flashActions.Controls.Add(start); row.Controls.Add(flashActions, 0, 3); root.Controls.Add(row, 0, 2);
            root.Controls.Add(status, 0, 3); root.Controls.Add(progress, 0, 4); var outputCard = new CardPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1, Padding = new Padding(14), Margin = new Padding(0, 12, 0, 16) }; outputCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            outputCard.Controls.Add(output); root.Controls.Add(outputCard, 0, 5);
            var close = new SoftButton { Text = "关闭", AutoSize = true, Anchor = AnchorStyles.Right }; close.Click += delegate { Close(); }; root.Controls.Add(close, 0, 6);
            output.Height = 160;
            viewport.Controls.Add(root); Controls.Add(viewport); Theme.Primary(start); Theme.Apply(this);
            refresh.Click += delegate { RefreshPorts(); }; start.Click += async delegate { await Flash(); };
            ports.SelectedIndexChanged += delegate { start.Enabled = !busy && ports.SelectedItem != null; };
            FormClosing += delegate(object sender, FormClosingEventArgs e) { if (busy) { e.Cancel = true; status.Text = "烧录进行中，请保持 USB 连接并等待完成。"; } };
            Shown += delegate { RefreshPorts(); };
        }
        void RefreshPorts()
        {
            var names = SerialPort.GetPortNames(); Array.Sort(names, StringComparer.OrdinalIgnoreCase);
            var details = new Dictionary<string, FlashPort>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (var usb = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\USB"))
                if (usb != null)
                    foreach (string hardware in usb.GetSubKeyNames())
                    {
                        using (var model = usb.OpenSubKey(hardware))
                        if (model != null)
                            foreach (string instance in model.GetSubKeyNames())
                            using (var device = model.OpenSubKey(instance))
                            using (var parameters = device == null ? null : device.OpenSubKey("Device Parameters"))
                            {
                                string name = parameters == null ? "" : Convert.ToString(parameters.GetValue("PortName"));
                                if (Array.IndexOf(names, name) < 0) continue;
                                string label = Convert.ToString(device.GetValue("FriendlyName"));
                                details[name] = new FlashPort { Name = name, Label = label.Length == 0 ? name + " · USB" : label, Espressif = hardware.StartsWith("VID_303A", StringComparison.OrdinalIgnoreCase) };
                            }
                    }
            }
            catch (Exception ex) { logger.Write("端口描述不可用：" + ex.GetType().Name); }
            ports.Items.Clear(); int selected = -1, knownCount = 0;
            foreach (var name in names)
            {
                FlashPort item; if (!details.TryGetValue(name, out item)) item = new FlashPort { Name = name, Label = name };
                int index = ports.Items.Add(item); if (item.Espressif) { selected = index; knownCount++; }
            }
            if (knownCount == 1) ports.SelectedIndex = selected;
            start.Enabled = ports.SelectedItem != null;
            status.Text = names.Length == 0 ? "未发现端口。请确认 USB 连接和下载模式，然后刷新。" : "请选择开发板的 USB 端口；不要选择主板通信端口。";
        }
        void Log(string line)
        {
            if (IsDisposed) return;
            output.AppendText(line + Environment.NewLine); logger.Write("烧录：" + line);
            var match = Regex.Match(line, @"\((\d+)\s*%\)"); int value;
            if (match.Success && int.TryParse(match.Groups[1].Value, out value)) progress.Value = Math.Min(100, value);
        }
        static string Extract(string name, string folder)
        {
            var assembly = Assembly.GetExecutingAssembly(); string expected;
            using (var input = assembly.GetManifestResourceStream("AtomClip.Flash." + name + ".sha256"))
            using (var reader = new StreamReader(input)) expected = reader.ReadToEnd().Trim();
            string file = Path.Combine(folder, name);
            using (var packed = assembly.GetManifestResourceStream("AtomClip.Flash." + name))
            using (var gzip = new GZipStream(packed, CompressionMode.Decompress))
            using (var destination = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None)) gzip.CopyTo(destination);
            string actual; using (var hash = SHA256.Create()) using (var input = File.OpenRead(file)) actual = BitConverter.ToString(hash.ComputeHash(input)).Replace("-", "");
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("内置烧录资源校验失败，请重新获取完整 EXE。");
            return file;
        }
        async Task<FlashCommandResult> RunTool(string tool, string arguments)
        {
            return await Task.Run(delegate
            {
                var buffer = new StringBuilder(); var gate = new object();
                using (var process = new Process { StartInfo = new ProcessStartInfo(tool, arguments) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 } })
                {
                    DataReceivedEventHandler receive = delegate(object sender, DataReceivedEventArgs e) { if (e.Data == null) return; lock (gate) buffer.AppendLine(e.Data); BeginInvoke((Action)delegate { Log(e.Data); }); };
                    process.StartInfo.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                    process.OutputDataReceived += receive; process.ErrorDataReceived += receive;
                    process.Start(); process.BeginOutputReadLine(); process.BeginErrorReadLine();
                    if (!process.WaitForExit(240000)) { try { process.Kill(); } catch { } process.WaitForExit(); throw new TimeoutException("烧录工具超时，请重新进入下载模式后重试。"); }
                    process.WaitForExit(); lock (gate) return new FlashCommandResult { ExitCode = process.ExitCode, Output = buffer.ToString() };
                }
            });
        }
        async Task Flash()
        {
            var selected = ports.SelectedItem as FlashPort; if (busy || selected == null) return;
            if (MessageBox.Show(this, "请确认所选板型、SPI NOR Flash、原生 USB 口和下载模式。\r\n普通升级仅在分区兼容时保留绑定；清空安装会删除全部数据，须先加密导出绑定，之后可通过 USB 恢复。\r\n所选板型："+profiles.SelectedItem+"\r\n端口：" + selected.Name + "，且已进入下载模式。", "确认烧录", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
            busy = true; start.Enabled = refresh.Enabled = ports.Enabled = profiles.Enabled = factory.Enabled = false; progress.Value = 0; output.Clear();
            string folder = Path.Combine(Path.GetTempPath(), "AtomClip-flash-" + Guid.NewGuid().ToString("N"));
            try
            {
                beforeFlash(); Directory.CreateDirectory(folder); status.Text = "正在校验内置资源和识别开发板…";
                string tool = Extract("esptool.exe", folder), bundleZip=Extract("firmware.zip",folder);ZipFile.ExtractToDirectory(bundleZip,folder);var bundle=FirmwareBundle.Load(folder);var profile=new List<BoardProfile>(catalog.Profiles.Values)[profiles.SelectedIndex];var build=bundle.Select(profile.id);if(!string.IsNullOrEmpty(build.pairingHash)){byte[] pairing;using(var input=Assembly.GetExecutingAssembly().GetManifestResourceStream("AtomClip.PairingKey")){pairing=new byte[32];if(input.Read(pairing,0,32)!=32)throw new IOException("配对资源无效");}try{if(FirmwareBuild.Hash(pairing)!=build.pairingHash)throw new IOException("迁移固件与本客户端旧配对不一致");}finally{Array.Clear(pairing,0,pairing.Length);}}
                await FlashPlan.Execute(selected.Name,folder,build,factory.Checked,delegate(string arguments) { return RunTool(tool, arguments); });
                progress.Value = 100; status.Text = "烧录与校验完成。";
                Log("完成：拔下并重新插入开发板，再将其移至内网电脑。外网连接 BLE 后，选择使用模式并完成接收设置。");
            }
            catch (Exception ex) { status.Text = "未完成烧录"; Log(ex.GetType().Name + "：" + ex.Message); }
            finally
            {
                busy = false; start.Enabled = refresh.Enabled = ports.Enabled = profiles.Enabled = factory.Enabled = true;
                try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (IOException) { logger.Write("烧录临时文件清理未完成。"); } catch (UnauthorizedAccessException) { logger.Write("烧录临时文件清理权限不足。"); }
            }
        }
    }
#endif
}
