using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.IO.Ports;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

#if BLE
[assembly: AssemblyTitle("AtomClip BLE")]
#else
[assembly: AssemblyTitle("AtomClip USB")]
#endif
[assembly: AssemblyDescription("USB and BLE text clipboard bridge")]
#if BLE
[assembly: AssemblyVersion("0.2.0.0")]
[assembly: AssemblyFileVersion("0.2.0.0")]
#else
[assembly: AssemblyVersion("0.2.0.0")]
[assembly: AssemblyFileVersion("0.2.0.0")]
#endif

namespace AtomClipUsb
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [STAThread]
        static void Main()
        {
            SetProcessDPIAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool first;
            using (var mutex = new Mutex(true, "Local\\AtomClip.TextBridge", out first))
            {
                if (!first) { MessageBox.Show("AtomClip 已在运行，请从右下角托盘退出旧程序后再打开。", "AtomClip"); return; }
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
#if BLE
                    "AtomClipBLE"
#else
                    "AtomClipUSB"
#endif
                    );
                var logger = new FileLog(Path.Combine(directory, "Logs")); logger.Cleanup();
                try { Application.Run(new MainForm(directory, logger)); }
                catch (Exception ex)
                {
                    logger.Write("启动失败：" + Error(ex));
                    MessageBox.Show("启动失败：" + Error(ex) + "\r\n日志目录：" + logger.DirectoryPath, "AtomClip");
                }
            }
        }
        public static string Error(Exception ex)
        { return ex.GetType().Name + "，0x" + ex.HResult.ToString("X8") + "：" + ex.Message; }
    }

    sealed class PendingReceive
    {
        public byte[] Payload;
        public string Id, Text;
        public int Attempts;
    }

    sealed partial class MainForm : Form
    {
        [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
        readonly ComboBox ports = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        readonly Button refresh = new SoftButton { Text = "刷新串口" }, connect = new SoftButton { Text = "连接" };
        readonly Button pause = new SoftButton { Text = "暂停同步" }, openLogs = new SoftButton { Text = "打开日志" };
        readonly Button resendClipboard = new SoftButton { Text = "发送当前内网剪贴板" };
        readonly Label state = new Label(), counts = new Label();
        readonly TextBox logView = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical };
        readonly NotifyIcon tray = new NotifyIcon { Icon = Theme.Brand, Text = "AtomClip USB", Visible = true };
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer { Interval = 250 };
        readonly ConcurrentQueue<Packet> incoming = new ConcurrentQueue<Packet>();
        readonly object incomingGate = new object();
        readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> waiting = new ConcurrentDictionary<string, TaskCompletionSource<bool>>();
        readonly HashSet<string> seen = new HashSet<string>(), discarded = new HashSet<string>();
        readonly Queue<string> seenOrder = new Queue<string>(), discardedOrder = new Queue<string>();
        readonly FileLog logger;
        readonly byte[] key;
        readonly byte[] legacyKey;
        readonly bool receiveOnly;
        readonly string settingsPath;
        ILink link;
#if BLE
        readonly TextBox editor = new TextBox { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, AccessibleName = "正文" };
        readonly Button sendEditor = new SoftButton { Text = "发送到内网剪贴板", Enabled = false };
        readonly Button loadClipboard = new SoftButton { Text = "读取外网剪贴板" };
        readonly Label editorSize = new Label(), editorResult = new Label();
        TableLayoutPanel bleRoot;
        Panel bleViewport;
        RowStyle editorRowStyle;
        float editorPanelHeight;
        readonly CheckBox autoClipboard = new CheckBox { Text = "自动发送外网新复制的文字", AutoSize = true };
        readonly Button typeText = new SoftButton { Text = "无 EXE：写入文字…", Enabled = false };
        readonly Button directKeys = new SoftButton { Text = "无 PowerShell：直接键盘输入…", Enabled = false };
        readonly Button prepareReceiver = new SoftButton { Text = "免文件初始化…", Enabled = false };
        readonly Button receiverHelp = new SoftButton { Text = "查看一次性启动步骤" };
        readonly RadioButton modeProgram = new SegmentButton { Text = "有内网程序", Appearance = Appearance.Button, AutoSize = true };
        readonly RadioButton modeNoProgram = new SegmentButton { Text = "无内网程序", Appearance = Appearance.Button, AutoSize = true, Checked = true };
        readonly Label modeDescription = new Label();
        readonly Panel setupCard = new Panel { AutoSize = true, Dock = DockStyle.Fill };
        Form setupWindow, logWindow;
        Control setupFirst, setupSecond;
        readonly List<Control> setupPages = new List<Control>();
        int setupStep = 1;
        readonly Button openReceiver = new SoftButton { Text = "打开内网接收窗口" };
        Label setupProgress;
        ToolStripMenuItem fallbackItem, pasteItem;
        readonly CheckBox paste = new CheckBox { Text = "完成后自动粘贴", AutoSize = true };
        string typeId;
        TaskCompletionSource<int> typeResult;
        bool typeBusy, supportsTyping, supportsReceiverStart, supportsDirectKeys, supportsStartupDelay;
        string connectionStage = "", connectionError = "";
        readonly NumericUpDown startupDelay = new NumericUpDown { Minimum = 3, Maximum = 60, Value = 10, Width = 90 };
        readonly System.Windows.Forms.Timer jobProgress = new System.Windows.Forms.Timer { Interval = 1000 };
        EventHandler jobProgressHandler;
#endif
        CancellationTokenSource connectionCts;
        bool desired, connecting, sending, paused, peerReady, exiting, logErrorShown;
        int sentCount, receiveCount;
        string pendingText, preferredPort = "";
        PendingReceive receiveRetry;
        uint sequence;
        DateTime nextRetry, nextPing, lastStatus;

        public MainForm(string directory, FileLog logger) : this(directory, logger, null) { }
        public MainForm(string directory, FileLog logger, byte[] receiverKey)
        {
            this.logger = logger;
            receiveOnly = receiverKey != null;
            if (receiverKey != null)
            {
                if (receiverKey.Length != 32) throw new CryptographicException("接收器配对信息错误");
                key = (byte[])receiverKey.Clone();
            }
            else
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AtomClip.PairingKey"))
            {
                if (stream == null || stream.Length != 32) throw new CryptographicException("EXE 配对信息缺失，请重新构建");
                key = new byte[32];
                int offset = 0, n;
                while (offset < key.Length && (n = stream.Read(key, offset, key.Length - offset)) > 0) offset += n;
                if (offset != 32) throw new CryptographicException("EXE 配对信息不完整");
            }
            legacyKey = (byte[])key.Clone();
#if BLE
            // The bootstrap template always contains the embedded legacy key, even
            // when the active receiver uses a different runtime data key.
            if(receiveOnly)using(var baseline=Assembly.GetExecutingAssembly().GetManifestResourceStream("AtomClip.PairingKey")){if(baseline==null||baseline.Length!=32||baseline.Read(legacyKey,0,32)!=32)throw new CryptographicException("启动模板配对资源缺失");}
#endif

#if BLE
            LoadVucSpeed(directory); deviceBindings = new DeviceBindings(directory); boardCatalog = new BoardCatalog(); myBoards.Click += delegate { OpenBoards(); };
#endif
            settingsPath = Path.Combine(directory, "port.txt");
            try { preferredPort = File.ReadAllText(settingsPath).Trim(); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            BuildLayout();
#if BLE
            UpdateBoardSummary();
#endif
            Theme.Primary(connect); Theme.Apply(this);
#if BLE
            typeText.Click += async delegate { if (EditorTextValid()) await SendTypeJob(editor.Text, false); };
            directKeys.Click += async delegate { if (EditorTextValid()) await SendTypeJob(editor.Text, false, false, true); };
            prepareReceiver.Click += async delegate {
                try {
                    setupProgress.Text = "正在写入启动代码，请勿操作内网键鼠…";
                    int result = await SendTypeJob(ReadReceiverBootstrap(), true);
                    if (result == 2) SetSetupStep(2);
                    else setupProgress.Text = "本次设置未确认完成，请检查内网窗口后再试。";
                }
                catch (Exception ex) { AddLog("无法准备接收器：" + ex.GetType().Name); }
            };
            receiverHelp.Click += delegate { ShowReceiverSteps(); };
            openReceiver.Click += async delegate {
                setupProgress.Text = "正在打开内网接收窗口，请勿操作内网键鼠…";
                int result = await SendTypeJob("", true, true);
                if (result == 2) SetSetupStep(3);
                else setupProgress.Text = "未确认打开成功。请先核对内网窗口，再决定是否重试。";
            };
            sendEditor.Click += async delegate { await SendEditorText(); };
            loadClipboard.Click += delegate {
                try {
                    if (Clipboard.ContainsText(TextDataFormat.UnicodeText)) editor.Text = Clipboard.GetText(TextDataFormat.UnicodeText);
                    else editorResult.Text = "外网剪贴板没有纯文本；也可以直接在输入框中输入。";
                } catch (ExternalException) { editorResult.Text = "外网剪贴板被占用，请稍后再试。"; }
            };
            editor.TextChanged += delegate {
                UpdateEditorValidation();
                if (!sending && !typeBusy) editorResult.Text = "";
            };
            autoClipboard.CheckedChanged += delegate {
                pendingText = null; sequence = GetClipboardSequenceNumber();
                AddLog(autoClipboard.Checked ? "已开启外网剪贴板自动发送；请重新复制文字。" : "已关闭外网剪贴板自动发送；可通过输入框手动发送。");
            };
            UpdateEditorActions();
#endif
            refresh.Click += delegate { RefreshPorts(); };
            resendClipboard.Click += async delegate {
                if (sending || paused || !peerReady || link == null || !link.Connected) { AddLog("尚不能发送：请确认两端就绪且未暂停。"); return; }
                try {
                    if (!Clipboard.ContainsText(TextDataFormat.UnicodeText)) { AddLog("内网剪贴板没有纯文本。"); return; }
                    string text = Clipboard.GetText(TextDataFormat.UnicodeText);
                    pendingText = null; sequence = GetClipboardSequenceNumber();
                    await SendText(text);
                } catch (ExternalException) { AddLog("剪贴板被占用，请稍后重试。"); }
            };
            connect.Click += async delegate { if (desired) Stop(); else { desired = true; await ConnectNow(); } };
            pause.Click += delegate { TogglePause(); };
            openLogs.Click += delegate { OpenLogFolder(); };
            var menu = new ContextMenuStrip(); Theme.Menu(menu);
            menu.Items.Add("打开窗口", null, delegate { Show(); WindowState = FormWindowState.Normal; Activate(); });
            menu.Items.Add("暂停 / 继续", null, delegate { TogglePause(); });
            menu.Items.Add("打开日志", null, delegate { OpenLogFolder(); });
            menu.Items.Add("退出", null, delegate { exiting = true; Close(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { Show(); WindowState = FormWindowState.Normal; Activate(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (!exiting && e.CloseReason == CloseReason.UserClosing) {
                    e.Cancel = true;
#if BLE
                    if (setupWindow != null) setupWindow.Hide();
                    if (logWindow != null) logWindow.Hide();
#endif
                    Hide();
                }
            };
            FormClosed += delegate { Stop(); timer.Stop(); tray.Dispose(); Array.Clear(key, 0, key.Length); logger.Write("程序退出。"); };
            timer.Tick += async delegate { try { await Tick(); } catch (Exception ex) { AddLog("运行错误：" + Program.Error(ex)); } };
            RefreshPorts(); sequence = GetClipboardSequenceNumber(); timer.Start();
            AddLog(receiveOnly ? "系统接收兼容测试模式。" :
                "单文件版 " + Assembly.GetExecutingAssembly().GetName().Version.ToString(3) + " 已启动；配对信息已内置。日志保留最近 7 天。");
            Shown += async delegate {
#if BLE
                if (deviceBindings.LoadError == null && deviceBindings.Registry.Active != null) { desired = true; await ConnectNow(); }
#else
                if (ports.SelectedItem != null) { desired = true; await ConnectNow(); }
#endif
            };
        }
        static Label WrappedLabel(string text)
        {
            return new Label { Text = text, AutoSize = true, Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 6), UseMnemonic = false };
        }
        static void FitButton(Button button)
        {
            button.AutoSize = true; button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.Padding = new Padding(16, 7, 16, 7); button.Margin = new Padding(0, 0, 10, 6);
            button.UseVisualStyleBackColor = true;
        }
        static void AddRow(TableLayoutPanel table, Control control, bool fill)
        {
            int row = table.RowCount++;
            table.RowStyles.Add(fill ? new RowStyle(SizeType.Percent, 100) : new RowStyle(SizeType.AutoSize));
            table.Controls.Add(control, 0, row);
        }
        void BuildLayout()
        {
#if BLE
            BuildBleLayout();
#else
            SuspendLayout();
            // Text determines control sizes. Scale the initial window once;
            // Framework 4.8 can otherwise scale nested AutoSize tables twice.
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 10);
            BackColor = Theme.Background;
            ForeColor = Theme.Ink;
            StartPosition = FormStartPosition.CenterScreen;
            Text = "AtomClip USB · 内网精简版 0.2.0";
            if (receiveOnly) { Text = "AtomClip PowerShell · 内网接收器"; tray.Text = "AtomClip PowerShell"; pause.Text = "暂停接收"; }
            var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var root = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 0,
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(28), Margin = Padding.Empty };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var heading = WrappedLabel("AtomClip");
            heading.Font = new Font(Font.FontFamily, 18, FontStyle.Bold);
            var usbHeader = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 3, Margin = new Padding(0, 0, 0, 20) };
            usbHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); usbHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); usbHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            usbHeader.Controls.Add(Theme.Mark(40), 0, 0); usbHeader.Controls.Add(heading, 1, 0);
            usbHeader.Controls.Add(new HelpButton(delegate { Theme.Help(this, "USB 使用帮助", Guide.Usb); }) { Anchor = AnchorStyles.Right | AnchorStyles.Top }, 2, 0); AddRow(root, usbHeader, false);
            FitButton(connect); FitButton(refresh); FitButton(pause); FitButton(openLogs);
            var portRow = new CardPanel { AutoSize = true, Dock = DockStyle.Fill,
                ColumnCount = 3, RowCount = 1, Padding = new Padding(14), Margin = new Padding(0, 0, 0, 12) };
            portRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            portRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            portRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            ports.Dock = DockStyle.Fill; ports.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            ports.Margin = new Padding(0, 4, 12, 6); ports.MinimumSize = new Size(100, 0);
            connect.Margin = new Padding(0, 0, 0, 6);
            portRow.Controls.Add(ports, 0, 0); portRow.Controls.Add(refresh, 1, 0); portRow.Controls.Add(connect, 2, 0);
            AddRow(root, portRow, false);
            var actions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 0, 0, 8) };
            FitButton(resendClipboard); resendClipboard.Visible = !receiveOnly;
            actions.Controls.Add(resendClipboard); actions.Controls.Add(pause); actions.Controls.Add(openLogs);
            AddRow(root, actions, false);
            var statusBox = new CardPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1,
                RowCount = 0, Padding = new Padding(12), Margin = new Padding(0, 0, 0, 12),
                BackColor = Color.White };
            statusBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            state.AutoSize = counts.AutoSize = true; state.Dock = counts.Dock = DockStyle.Fill;
            state.UseMnemonic = counts.UseMnemonic = false;
            state.Margin = new Padding(0, 0, 0, 6); counts.Margin = Padding.Empty;
            state.Font = new Font(Font, FontStyle.Bold);
            state.Text = "请选择开发板串口"; counts.Text = "等待复制新的文本";
            if (receiveOnly) counts.Text = "等待外网发送文字";
            AddRow(statusBox, state, false); AddRow(statusBox, counts, false);
            AddRow(root, statusBox, false);
            var logTitle = WrappedLabel("运行日志 · 自动保存"); logTitle.Font = new Font(Font, FontStyle.Bold);
            AddRow(root, logTitle, false);
            logView.Dock = DockStyle.Fill; logView.MinimumSize = new Size(0, 100);
            logView.BackColor = Color.White; logView.BorderStyle = BorderStyle.FixedSingle;
            logView.Margin = new Padding(0, 0, 0, 12); logView.WordWrap = true;
            int logRow = root.RowCount;
            var logCard = new CardPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(14), Margin = new Padding(0, 0, 0, 14) };
            logView.Margin = Padding.Empty; logCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); logCard.Controls.Add(logView);
            AddRow(root, logCard, false);
            root.RowStyles[logRow].SizeType = SizeType.Absolute;
            root.RowStyles[logRow].Height = logView.MinimumSize.Height + logCard.Padding.Vertical + logCard.Margin.Vertical;
            viewport.Controls.Add(root); Controls.Add(viewport);
            bool fitting = false;
            Action fitLog = delegate {
                if (fitting || !IsHandleCreated || root.RowCount <= logRow) return;
                fitting = true;
                try {
                    int[] heights = root.GetRowHeights();
                    int otherHeight = root.Padding.Vertical;
                    for (int i = 0; i < heights.Length; i++) if (i != logRow) otherHeight += heights[i];
                    int height = Math.Max(logView.MinimumSize.Height + logCard.Padding.Vertical + logCard.Margin.Vertical,
                        viewport.ClientSize.Height - otherHeight);
                    if (Math.Abs(root.RowStyles[logRow].Height - height) > 1) root.RowStyles[logRow].Height = height;
                } finally { fitting = false; }
            };
            viewport.ClientSizeChanged += delegate { fitLog(); };
            root.SizeChanged += delegate { fitLog(); };
            Shown += delegate { fitLog(); };
            float scale;
            using (var graphics = CreateGraphics()) scale = Math.Max(1, graphics.DpiX / 96f);
            ClientSize = new Size((int)(640 * scale), (int)(420 * scale));
            MinimumSize = new Size((int)(520 * scale), (int)(340 * scale));
            ResumeLayout(true);
            Load += delegate {
                Rectangle work = Screen.FromControl(this).WorkingArea;
                MinimumSize = new Size(Math.Min(MinimumSize.Width, work.Width), Math.Min(MinimumSize.Height, work.Height));
                Size = new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height));
                if (Left < work.Left || Right > work.Right) Left = work.Left + (work.Width - Width) / 2;
                if (Top < work.Top || Bottom > work.Bottom) Top = work.Top + (work.Height - Height) / 2;
            };
#endif
        }

#if BLE
        void BuildBleLayout()
        {
            AutoScaleMode = AutoScaleMode.None;
            Font = new Font("Microsoft YaHei UI", 10);
            BackColor = Theme.Background; ForeColor = Theme.Ink;
            Text = "AtomClip · 外网 0.2.0"; tray.Text = "AtomClip BLE";
            StartPosition = FormStartPosition.CenterScreen;
            var viewport = new BufferedViewport { Dock = DockStyle.Fill, AutoScroll = true };
            var root = new BufferedLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, Padding = new Padding(28) };
            bleRoot = root; bleViewport = viewport;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var header = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 2, Margin = new Padding(0, 0, 0, 16) };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var headerActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = Padding.Empty };
            var title = new Label { Text = "AtomClip", AutoSize = true, Font = new Font(Font.FontFamily, 19, FontStyle.Bold), Margin = new Padding(0, 0, 26, 6) };
            FitButton(connect); connect.Text = "连接设备";
            var provision = new SoftButton { Text = "开发板设置" }; FitButton(provision); provision.Click += delegate { OpenFirmwareInstaller(); };
            var more = new SoftButton { Text = "更多 ▾" }; FitButton(more);
            headerActions.Controls.Add(Theme.Mark(38)); headerActions.Controls.Add(title);
            header.Controls.Add(headerActions, 0, 0); header.Controls.Add(new HelpButton(delegate { Theme.Help(this, "使用帮助", Guide.Ble); }) { Anchor = AnchorStyles.Top | AnchorStyles.Right }, 1, 0);
            header.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
            headerActions.Layout += delegate {
                int height = 48;
                foreach (Control item in headerActions.Controls) height = Math.Max(height, item.Bottom + item.Margin.Bottom);
                if (Math.Abs(header.RowStyles[0].Height - height) > 1) header.RowStyles[0].Height = height;
            };
            AddRow(root, header, false);
            var deviceActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true, Margin = new Padding(0, 0, 0, 14) };
            FitButton(myBoards); deviceActions.Controls.Add(connect); deviceActions.Controls.Add(myBoards); deviceActions.Controls.Add(provision); deviceActions.Controls.Add(more); AddRow(root, deviceActions, false);
            AddRow(root, currentBoard, false); root.SizeChanged += delegate { currentBoard.MaximumSize = new Size(Math.Max(120, root.ClientSize.Width - root.Padding.Horizontal), 0); };
            var modes = new SegmentPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Anchor = AnchorStyles.Left, BackColor = Theme.ControlFill, Padding = new Padding(4), Margin = new Padding(0, 0, 0, 12) };
            foreach (var mode in new[] { modeProgram, modeNoProgram }) {
                mode.Padding = new Padding(22, 8, 22, 8); mode.Margin = new Padding(0);
                mode.FlatStyle = FlatStyle.Flat; mode.FlatAppearance.BorderColor = Color.FromArgb(203, 213, 225);
                mode.FlatAppearance.CheckedBackColor = Color.White; mode.UseVisualStyleBackColor = false;
                modes.Controls.Add(mode);
            }
            AddRow(root, modes, false);
            BuildModeOptions(root);
            modeDescription.AutoSize = true; modeDescription.Dock = DockStyle.Fill;
            modeDescription.Margin = new Padding(0, 0, 0, 16); modeDescription.ForeColor = Theme.Muted;
            modeDescription.Visible = false;
            var statusBox = new CardPanel { AutoSize = true, Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12),
                BackColor = Color.White, Margin = new Padding(0, 0, 0, 12) };
            statusBox.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            state.AutoSize = counts.AutoSize = true; state.Dock = counts.Dock = DockStyle.Fill;
            state.Font = new Font(Font, FontStyle.Bold); state.Margin = new Padding(0, 0, 0, 5);
            counts.Text = "已发送 0 次 · 已接收 0 次"; counts.Margin = Padding.Empty;
            AddRow(statusBox, state, false); AddRow(statusBox, counts, false); AddRow(root, statusBox, false);
            var setupContent = new CardPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 1, Padding = new Padding(18), BackColor = Color.White };
            setupContent.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));


            BuildRouteCard(setupContent); setupCard.Controls.Add(setupContent); FitDockedContent(setupCard, setupContent);
            setupCard.Margin = new Padding(0, 0, 0, 12); AddRow(root, setupCard, false);
            BuildKeyboardOptions(root);
            editorTitle.Font = new Font(Font, FontStyle.Bold); AddRow(root, editorTitle, false);
            editor.Dock = DockStyle.Fill; editor.AutoSize = false; editor.Height = Math.Max(112, Font.Height * 5); editor.MaxLength = 0;
            editor.BackColor = Color.White; editor.Margin = Padding.Empty;
            var editorCard = new CardPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(16), Margin = new Padding(0, 0, 0, 8) };
            editorCard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            editorCard.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); editorCard.Controls.Add(editor, 0, 0);
            int editorRow = root.RowCount; AddRow(root, editorCard, false);
            editorRowStyle = root.RowStyles[editorRow]; editorRowStyle.SizeType = SizeType.Absolute;
            Action fitEditor = delegate { editorPanelHeight = Math.Max(112, editor.Font.Height * 5) + editorCard.Padding.Vertical + editorCard.Margin.Vertical; editorRowStyle.Height = KeyboardBodyVisible ? editorPanelHeight : 0; };
            editor.FontChanged += delegate { fitEditor(); }; fitEditor();
            editorSize.AutoSize = true; editorSize.Dock = DockStyle.Fill; editorSize.Margin = new Padding(0, 0, 0, 10); AddRow(root, editorSize, false);
            sendEditor.Text = "发送到内网剪贴板"; loadClipboard.Text = "从剪贴板填入";
            FitButton(sendEditor); FitButton(loadClipboard);
            Theme.Primary(sendEditor);
            sendEditor.FlatStyle = FlatStyle.Flat; sendEditor.FlatAppearance.BorderSize = 0;
            var sendActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, Margin = Padding.Empty };
            FitButton(directKeys); directKeys.Text = "输入到内网窗口…"; Theme.Primary(directKeys);
            sendActions.Controls.Add(sendEditor); sendActions.Controls.Add(directKeys); sendActions.Controls.Add(loadClipboard); AddRow(root, sendActions, false);
            autoClipboard.Text = "自动发送外网新复制的文字"; autoClipboard.Margin = new Padding(0, 4, 0, 10); AddRow(root, autoClipboard, false);
            editorResult.AutoSize = true; editorResult.Dock = DockStyle.Fill; editorResult.UseMnemonic = false;
            editorResult.Text = ""; editorResult.Margin = new Padding(0, 0, 0, 16); AddRow(root, editorResult, false);
            viewport.Controls.Add(root); Controls.Add(viewport);
            var menu = new ContextMenuStrip(); Theme.Menu(menu);
            menu.Items.Add("我的开发板…", null, delegate { OpenBoards(); });
            menu.Items.Add("暂停 / 继续同步", null, delegate { TogglePause(); });
            menu.Items.Add("新开发板 / 固件更新…", null, delegate { OpenFirmwareInstaller(); });
            menu.Items.Add("查看运行日志", null, delegate { OpenLogWindow(); });
            menu.Items.Add("关于 / 开源许可", null, delegate { ShowLicenses(); });
            menu.Items.Add("打开日志文件夹", null, delegate { OpenLogFolder(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("重新设置内网接收…", null, delegate { modeNoProgram.Checked = true; if (KeyboardRoute) subPowerShell.Checked = true; OpenSetup(); });
            fallbackItem = new ToolStripMenuItem("备用：临时键盘写入…");
            fallbackItem.Click += async delegate { if (EditorTextValid()) await SendTypeJob(editor.Text, false); };
            menu.Items.Add(fallbackItem);
            pasteItem = new ToolStripMenuItem("键盘写入后自动粘贴") { CheckOnClick = true };
            pasteItem.CheckedChanged += delegate { paste.Checked = pasteItem.Checked; }; menu.Items.Add(pasteItem);
            more.Click += delegate { menu.Show(more, new Point(0, more.Height)); };
            modeProgram.CheckedChanged += delegate { if (modeProgram.Checked) SwitchRoute(); };
            modeNoProgram.CheckedChanged += delegate { if (modeNoProgram.Checked) SwitchRoute(); };
            Shown += delegate { }; // Layout-only callback, also used by isolated UI review.
            FormClosed += delegate { if (setupWindow != null) setupWindow.Dispose(); if (logWindow != null) logWindow.Dispose(); menu.Dispose(); };
            float scale; using (var graphics = CreateGraphics()) scale = Math.Max(1, graphics.DpiX / 96f);
            ClientSize = new Size((int)(680 * scale), (int)(720 * scale)); MinimumSize = new Size((int)(480 * scale), (int)(400 * scale));
            Load += delegate { var work = Screen.FromControl(this).WorkingArea; Size = new Size(Math.Min(Width, work.Width), Math.Min(Height, work.Height)); };
        }
        void UpdateBlePresentation()
        {
            bool stopAction = desired || (link != null && link.Connected);
            connect.Tag = stopAction ? null : "primary"; connect.BackColor = stopAction ? Theme.ControlFill : Theme.Blue; connect.ForeColor = stopAction ? Theme.Ink : Color.White;
            modeDescription.Text = modeProgram.Checked ? "内网运行 AtomClip USB，支持双向剪贴板同步。" : "系统 PowerShell 桥接默认双向；内网按 B 切换回传。";
            foreach (var mode in new[] { modeProgram, modeNoProgram }) {
                mode.BackColor = mode.Checked ? Color.White : Theme.ControlFill;
                mode.ForeColor = Theme.Ink;
            }
            sendEditor.BackColor = Theme.Blue;
            sendEditor.ForeColor = Color.White;
            UpdateRoutePresentation();
            if (typeBusy) state.Text = "正在准备内网 · 请勿操作内网键鼠";
            else if (connecting) state.Text = (connectionStage.Length == 0 ? "正在连接开发板…" : connectionStage) + (connectionError.Length == 0 ? "" : "\r\n上次未完成：" + connectionError);
            else if (link == null || !link.Connected) state.Text = connectionError.Length > 0 ? (desired ? "等待重连：" : "连接未完成：") + connectionError : desired ? "正在等待开发板连接…" : "尚未连接开发板";
            else if (paused) state.Text = "同步已暂停 · 可在“更多”中恢复";
            else if (KeyboardRoute) state.Text = peerReady ? "请先退出内网接收器，再使用 USB 键盘输入" : !supportsDirectKeys ? "开发板已连接 · 请更新固件以使用 USB 键盘" : "USB 键盘已连接 · 请将内网光标放好";
            else if (peerReady) state.Text = DuplexRoute ? "已就绪 · 可传输文字，内网按 B 切换回传" : "已就绪 · 可双向传输文字";
            else state.Text = modeProgram.Checked ? "开发板已连接 · 等待内网程序" : "开发板已连接 · 请先设置内网接收";
            if (fallbackItem != null) { fallbackItem.Visible = modeNoProgram.Checked && !KeyboardRoute; fallbackItem.Enabled = typeText.Enabled; }
            if (pasteItem != null) pasteItem.Visible = modeNoProgram.Checked && !KeyboardRoute;
        }
        void OpenLogWindow()
        {
            if (logWindow == null) {
                logWindow = new Form { Text = "运行日志 · 自动保存", Font = Font, Size = new Size(760, 440), StartPosition = FormStartPosition.CenterParent };
                logView.Dock = DockStyle.Fill; logWindow.Padding = new Padding(20); logWindow.Controls.Add(logView); Theme.Apply(logWindow);
                logWindow.FormClosing += delegate(object sender, FormClosingEventArgs e) { if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; logWindow.Hide(); } };
            }
            if (!logWindow.Visible) logWindow.Show(this);
            if (logWindow.WindowState == FormWindowState.Minimized) logWindow.WindowState = FormWindowState.Normal;
            logWindow.Activate();
        }

#endif
        void AddLog(string text)
        {
            logger.Write(text);
            // An owned log window can be disposed before the main FormClosed handler.
            if (logView.IsDisposed) return;
            if (logView.Lines.Length > 80) logView.Clear();
            logView.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine);
            if (logger.LastError != null && !logErrorShown)
            { logView.AppendText("日志写入失败：" + logger.LastError + Environment.NewLine); logErrorShown = true; }
            if (logger.LastError == null) logErrorShown = false;
        }
        void OpenLogFolder()
        {
            try { Directory.CreateDirectory(logger.DirectoryPath); Process.Start("explorer.exe", "\"" + logger.DirectoryPath + "\""); }
            catch (Exception ex) { AddLog("无法打开日志目录：" + Program.Error(ex)); }
        }
        void RefreshPorts()
        {
            string selected = ports.SelectedItem == null ? preferredPort : ports.SelectedItem.ToString();
            string[] names = SerialPort.GetPortNames(); Array.Sort(names, StringComparer.OrdinalIgnoreCase);
            ports.Items.Clear(); ports.Items.AddRange(names);
            if (ports.Items.Contains(selected)) ports.SelectedItem = selected;
            else if (names.Length == 1) ports.SelectedIndex = 0;
        }
        void TogglePause()
        {
            paused = !paused; pendingText = null;
            if (receiveRetry != null) Remember(discarded, discardedOrder, receiveRetry.Id);
            receiveRetry = null; sequence = GetClipboardSequenceNumber();
            pause.Text = paused ? "继续同步" : "暂停同步";
            if (receiveOnly) pause.Text = paused ? "继续接收" : "暂停接收";
#if BLE
            UpdateEditorActions();
#endif
            AddLog(receiveOnly ? (paused ? "已暂停接收。" : "已恢复接收；请在外网重新发送文字。") :
                (paused ? "已暂停双向新文本同步。" : "已恢复同步；请重新复制文本。"));
        }
        void ResetLink()
        {
            peerReady = false; pendingText = null; receiveRetry = null;
#if BLE
            supportsTyping = false; supportsReceiverStart = false; supportsDirectKeys = false; supportsStartupDelay = false; supportsFastTyping = false; supportsPinyin = false; supportsGbk = false; ResetKeyboardProbe(); UpdateEditorActions();
            if (typeResult != null) typeResult.TrySetCanceled();
#endif
            var stop = connectionCts; connectionCts = null;
            if (stop != null) { stop.Cancel(); stop.Dispose(); }
            var old = link; link = null;
            if (old != null) try { old.Dispose(); } catch (Exception ex) { AddLog("关闭串口失败：" + Program.Error(ex)); }
            foreach (var t in waiting.Values) t.TrySetCanceled(); waiting.Clear();
            lock (incomingGate) { Packet unused; while (incoming.TryDequeue(out unused)) { } }
            sequence = GetClipboardSequenceNumber();
#if BLE
            UpdateEditorActions();
#endif
        }
        void Stop()
        {
#if BLE
            connectionStage = connectionError = "";
#endif
            desired = false; ResetLink(); connect.Text = "连接"; ports.Enabled = true;
            state.Text = "尚未连接"; AddLog("本机通道已断开。");
#if BLE
            UpdateEditorActions();
#endif
        }
        void SubscribeLink(ILink candidate, CancellationToken token)
        {
            candidate.Received += delegate(Packet packet)
                {
                    lock (incomingGate) {
                    if (token.IsCancellationRequested) return;
                    if (packet.Kind == Protocol.Ack)
                    {
                        string id = Protocol.ReadAck(packet.Payload, key); TaskCompletionSource<bool> ack;
                        if (id != null && waiting.TryGetValue(id, out ack)) ack.TrySetResult(true);
                    }
                    else if (incoming.Count < 16) incoming.Enqueue(packet);
                    }
                };
        }
        async Task ConnectNow()
        {
            if (connecting || !desired) return;
#if BLE
            connectionStage = "正在扫描已绑定开发板…";
#endif
            connecting = true; ResetLink(); connect.Text = "断开"; state.Text = "正在连接…";
            connectionCts = new CancellationTokenSource(); var token = connectionCts.Token;
            try
            {
#if BLE
                var selectedBinding = deviceBindings.Registry.Active;
                if (deviceBindings.LoadError != null) throw new CryptographicException(deviceBindings.LoadError);
                if (selectedBinding == null) { desired = false; throw new IOException("请先从“我的开发板”通过USB绑定，或导入加密配置并设为当前。"); }
                Buffer.BlockCopy(selectedBinding == null ? legacyKey : selectedBinding.DataKey, 0, key, 0, 32);
                string name = selectedBinding.Alias;
                var candidate = new BleLink(selectedBinding); link = candidate;
                candidate.Progress += message => { if (!token.IsCancellationRequested && ReferenceEquals(link, candidate) && !IsDisposed) { connectionStage = message; UpdateBlePresentation(); AddLog("连接阶段：" + message); } };
                SubscribeLink(candidate, token);
                candidate.Identified += visible => BeginInvoke((Action)(() => MessageBox.Show(this, visible ? "已请求开发板闪灯。" : "此板型无已知LED，请通过USB核对完整编号。", "识别开发板")));
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token)) { timeout.CancelAfter(60000); await candidate.Open(timeout.Token); }
                connectionError = "";
                if (candidate.AuthenticatedInfo != null) { var info = candidate.AuthenticatedInfo; try { deviceBindings.Change(r => { V2Protocol.Binding existing; if (r.Confirmed.TryGetValue(info.Number, out existing)) existing.DeviceInfo = info; }); } catch(Exception) { AddLog("身份已验证，但最新设备信息未能保存。"); } UpdateBoardSummary(); }
#else
                RefreshPorts();
                string name = ports.SelectedItem == null ? "" : ports.SelectedItem.ToString();
                if (name.Length == 0) throw new IOException("请选择开发板 COM 口；插好板子后点击刷新串口。");
                var candidate = await Task.Run(() => new UsbLink(name), token);
                link = candidate;
                if (!receiveOnly)
                {
                    Buffer.BlockCopy(legacyKey, 0, key, 0, 32);
                    byte[] deviceDataKey = await UsbManagement.ReadDataKey(candidate, token);
                    if (deviceDataKey != null) { Buffer.BlockCopy(deviceDataKey, 0, key, 0, 32); Array.Clear(deviceDataKey, 0, 32); }
                }
#endif
                if (token.IsCancellationRequested) { candidate.Dispose(); token.ThrowIfCancellationRequested(); }
                link = candidate;
#if !BLE
                SubscribeLink(candidate, token);
#endif
                preferredPort = name;
                try { Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)); File.WriteAllText(settingsPath, name); }
                catch (Exception ex) { AddLog("端口设置未能保存：" + Program.Error(ex)); }
                ports.Enabled = false; sequence = GetClipboardSequenceNumber(); nextPing = DateTime.MinValue; lastStatus = DateTime.UtcNow;
                AddLog("已打开 " + name + "；等待开发板及另一端状态。");
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested) AddLog("连接失败：" + Program.Error(ex));
#if BLE
                if (!token.IsCancellationRequested) connectionError = connectionStage + " " + (ex is OperationCanceledException ? "连接超时，请重试" : Program.Error(ex));
                if (ex is CryptographicException) desired = false;
#endif
                ResetLink(); nextRetry = DateTime.UtcNow.AddSeconds(5); ports.Enabled = true;
                state.Text = desired ? "等待重连…" : "尚未连接";
            }
            finally { connecting = false;
#if BLE
                UpdateEditorActions();
#endif
            }
        }
        async Task Tick()
        {
            if (connecting) return;
#if BLE
            var disconnectedBle = link as BleLink;
            if (disconnectedBle != null && !disconnectedBle.Connected && disconnectedBle.StopReconnect) { string reason = disconnectedBle.Failure ?? "身份验证失败，请核对已绑定开发板。"; Stop(); AddLog(reason); return; }
#endif
            if (desired && (link == null || !link.Connected))
            {
                if (DateTime.UtcNow >= nextRetry) { nextRetry = DateTime.UtcNow.AddSeconds(5); await ConnectNow(); }
                return;
            }
            if (link == null) { sequence = GetClipboardSequenceNumber(); return; }
            Packet p;
            for (int i = 0; i < 8 && incoming.TryDequeue(out p); i++)
            {
                if (p.Kind == Protocol.Status && p.Payload.Length >= 2)
                {
                    lastStatus = DateTime.UtcNow;
                    bool ready = p.Payload[
#if BLE
                        0
#else
                        1
#endif
                        ] != 0;
#if BLE
                    bool changed = ready != peerReady;
                    bool typing = p.Payload.Length >= 3 && p.Payload[2] == 1;
                    bool receiverStart = p.Payload.Length >= 4 && p.Payload[3] == 1;
                    bool direct = p.Payload.Length >= 5 && p.Payload[4] == 1;
                    bool startup = p.Payload.Length >= 6 && p.Payload[5] == 1;
                    bool fast = p.Payload.Length >= 7 && p.Payload[6] == 1;
                    bool pinyin = p.Payload.Length >= 8 && p.Payload[7] == 1;
                    bool gbk = p.Payload.Length >= 9 && p.Payload[8] == 1;
                    var secureLink = link as BleLink; var trustedInfo = secureLink == null ? null : secureLink.AuthenticatedInfo;
                    if (trustedInfo != null) { uint caps = trustedInfo.Capabilities; typing &= (caps & 2) != 0; receiverStart &= (caps & 4) != 0; startup &= (caps & 4) != 0; fast &= (caps & 4) != 0; direct &= (caps & 2) != 0; pinyin &= (caps & 16) != 0; gbk &= (caps & 8) != 0; }
                    changed |= typing != supportsTyping || receiverStart != supportsReceiverStart || direct != supportsDirectKeys || startup != supportsStartupDelay || fast != supportsFastTyping || pinyin != supportsPinyin || gbk != supportsGbk;
#endif
                    if (ready != peerReady) {
                        sequence = GetClipboardSequenceNumber(); pendingText = null;
                        AddLog(ready ? "两端通道就绪，请重新复制文字；也可点击发送当前剪贴板。" : "另一端接收程序未就绪；等待连接。");
                    }
                    peerReady = ready;
#if !BLE
                    state.Text = ready ? "通道已连接 · 复制文本即可同步" : "已连接开发板 · 等待另一端程序";
                    if (receiveOnly && ready) state.Text = "通道已连接 · 等待外网发送文字";
#endif
#if BLE
                    supportsTyping = typing; supportsReceiverStart = receiverStart;
                    supportsDirectKeys = direct; supportsStartupDelay = startup; supportsFastTyping = fast; supportsPinyin = pinyin; supportsGbk = gbk;
                    if (changed) UpdateEditorActions();
#endif
                }
                else if (p.Kind == Protocol.Data) ReceiveData(p.Payload);
#if BLE
                else if (p.Kind == Protocol.JobResult && typeId != null)
                {
                    int result = Protocol.ReadJobResult(p.Payload, typeId, key);
                    if (result == 0) AddLog("开发板已接收输入任务；请勿操作内网键盘或切换窗口。");
                    else if (result >= 2 && typeResult != null) typeResult.TrySetResult(result);
                }
#endif
            }
            if (receiveRetry != null) RetryClipboard();
            if (DateTime.UtcNow - lastStatus > TimeSpan.FromSeconds(18))
            {
                AddLog("开发板状态超时；准备自动重连。"); ResetLink(); state.Text = "等待重连…";
                nextRetry = DateTime.UtcNow.AddSeconds(2); return;
            }
            if (DateTime.UtcNow >= nextPing) { nextPing = DateTime.UtcNow.AddSeconds(5); SendControl(Protocol.Ping, new byte[0]); }
            uint current = GetClipboardSequenceNumber();
            if (current != sequence)
            {
                try
                {
                    if (!receiveOnly && !paused && peerReady
#if BLE
                        && autoClipboard.Checked
#endif
                        && Clipboard.ContainsText(TextDataFormat.UnicodeText))
                    {
                        string text = Clipboard.GetText(TextDataFormat.UnicodeText);
                        if (Protocol.Utf8.GetByteCount(text) <= Protocol.MaxText) pendingText = text;
                        else { pendingText = null; AddLog("未发送：文本超过 16 KiB，请分段复制。"); }
                    }
                    sequence = current;
                    if (!receiveOnly && !paused && !peerReady) AddLog("检测到本机复制，但另一端未就绪；连接后请重新复制或手动发送当前剪贴板。");
                }
                catch (ExternalException) { }
            }
            if (!sending && !paused && peerReady && pendingText != null)
            { string text = pendingText; pendingText = null; await SendText(text); }
        }
        static void Remember(HashSet<string> set, Queue<string> order, string id)
        {
            if (!set.Add(id)) return;
            order.Enqueue(id); if (order.Count > 256) set.Remove(order.Dequeue());
        }
        void ReceiveData(byte[] payload)
        {
            try
            {
                string text = Protocol.Decrypt(payload, key), id = Protocol.Id(payload);
#if BLE
                if (KeyboardRoute) { Remember(discarded, discardedOrder, id); return; }
#endif
                if (discarded.Contains(id)) return;
                if (paused) { Remember(discarded, discardedOrder, id); AddLog("暂停期间收到的消息已忽略，不写入剪贴板。"); return; }
                if (seen.Contains(id)) { SendControl(Protocol.Ack, Protocol.MakeAck(Protocol.Slice(payload, 0, 16), key)); return; }
                if (receiveRetry != null && receiveRetry.Id == id) return;
                receiveRetry = new PendingReceive { Payload = payload, Id = id, Text = text }; RetryClipboard();
            }
            catch (CryptographicException) { AddLog("拒绝数据：配对密钥不一致或认证失败。"); }
            catch (DecoderFallbackException) { AddLog("拒绝数据：文本不是有效的 UTF-8。"); }
        }
        void RetryClipboard()
        {
            var r = receiveRetry; if (r == null || paused) return;
            try
            {
                pendingText = null;
                if (r.Text.Length == 0) Clipboard.Clear(); else Clipboard.SetText(r.Text, TextDataFormat.UnicodeText);
                sequence = GetClipboardSequenceNumber(); Remember(seen, seenOrder, r.Id);
#if BLE
                editorResult.Text = "已接收内网文字并写入外网剪贴板（" + Protocol.Utf8.GetByteCount(r.Text) + " 字节）。";
#endif
                receiveRetry = null; receiveCount++;
#if BLE
                UpdateRouteCounts();
#else
                counts.Text = "已发送 " + sentCount + " 次 · 已接收 " + receiveCount + " 次";
#endif
                AddLog("接收成功：" + Protocol.Utf8.GetByteCount(r.Text) + " UTF-8 字节；已写入剪贴板。");
                SendControl(Protocol.Ack, Protocol.MakeAck(Protocol.Slice(r.Payload, 0, 16), key));
            }
            catch (ExternalException)
            { if (++r.Attempts > 12) { AddLog("本机剪贴板持续被占用，接收失败。"); receiveRetry = null; } }
        }
        async void SendControl(byte kind, byte[] payload)
        {
            var current = link; var token = connectionCts == null ? CancellationToken.None : connectionCts.Token;
            if (current == null) return;
            try { await current.Send(Protocol.Frame(kind, payload), token); }
            catch (Exception) { /* Tick detects state timeout/disconnection. */ }
        }
        async Task<bool> SendText(string text)
        {
            sending = true; var current = link;
#if BLE
            UpdateEditorActions();
#endif
            var token = connectionCts == null ? CancellationToken.None : connectionCts.Token;
            string id = null;
            try
            {
                byte[] payload = Protocol.Encrypt(text, key); id = Protocol.Id(payload);
                var ack = new TaskCompletionSource<bool>(); waiting[id] = ack;
                byte[] frame = Protocol.Frame(Protocol.Data, payload); bool done = false;
                for (int attempt = 0; attempt < 3; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    if (paused || current != link || current == null) return false;
                    if (attempt != 0) AddLog("等待确认超时；重试第 " + attempt + " 次。");
                    using (var sendTimeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                    { sendTimeout.CancelAfter(90000); await current.Send(frame, sendTimeout.Token); }
                    using (var delayStop = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        Task delay = Task.Delay(12000, delayStop.Token);
                        Task winner = await Task.WhenAny(ack.Task, delay);
                        if (winner == ack.Task) { delayStop.Cancel(); await ack.Task; done = true; break; }
                        token.ThrowIfCancellationRequested();
                    }
                }
                if (done)
                {
                    sentCount++;
#if BLE
                    UpdateRouteCounts();
#else
                    counts.Text = "已发送 " + sentCount + " 次 · 已接收 " + receiveCount + " 次";
#endif
                    AddLog("发送成功：" + Protocol.Utf8.GetByteCount(text) + " UTF-8 字节；已收到对端认证回执。");
                }
                else AddLog("发送未确认；请检查对端是否暂停或断开，再重新发送。");
                return done;
            }
            catch (OperationCanceledException) { if (!token.IsCancellationRequested) AddLog("发送超时，请重新复制。"); }
            catch (Exception ex) { AddLog("发送失败：" + Program.Error(ex)); }
            finally {
                TaskCompletionSource<bool> unused; if (id != null) waiting.TryRemove(id, out unused); sending = false;
#if BLE
                UpdateEditorActions();
#endif
            }
            return false;
        }
#if BLE
        bool EditorTextValid()
        {
            try { if (GbkRoute) { Protocol.EncodeGbkText(editor.Text); return true; } if (PinyinRoute) { Protocol.NormalizePinyinText(editor.Text); return true; } if (KeyboardRoute) { Protocol.NormalizeDirectKeyText(editor.Text); return true; } return editor.Text.Length != 0 && editor.Text.IndexOf('\0') < 0 && Protocol.Utf8.GetByteCount(editor.Text) <= Protocol.MaxText; }
            catch (ArgumentException) { return false; }
        }
        void UpdateEditorActions()
        {
            ApplyBleLayout(delegate {
                UpdateEditorValidation();
                bool connected = link != null && link.Connected && connectionCts != null;
                UpdateKeyboardOptions(connected);
                UpdateBlePresentation();
            });
        }
        void ApplyBleLayout(Action update)
        {
            bleViewport.SuspendLayout(); bleRoot.SuspendLayout();
            try { update(); }
            finally { bleRoot.ResumeLayout(true); bleViewport.ResumeLayout(true); }
        }
        void UpdateEditorValidation()
        {
            bool valid = EditorTextValid();
            string sizeText;
            try {
                if (GbkRoute) { sizeText = Protocol.Utf8.GetByteCount(editor.Text) + " / 16384 UTF-8 字节 · GBK 中英文混排"; if(editor.Text.Length>0 && !valid) { try { Protocol.EncodeGbkText(editor.Text); } catch(ArgumentException ex) { sizeText = ex.Message; } } }
                else if (PinyinRoute) sizeText = editor.Text.Length == 0 || valid ? Protocol.Utf8.GetByteCount(editor.Text) + " / 16384 UTF-8 字节 · VUC 中英文混排" : "VUC 不支持 emoji、非 BMP 或此控制字符，请调整正文。";
                else if (KeyboardRoute) sizeText = editor.Text.Length == 0 || valid ? editor.Text.Replace("\r\n", "\n").Length + " / 16384 字符 · 英文、数字、符号、Tab 和换行" : "ASCII 方式不支持中文、emoji 或此控制字符；中文可选择微软拼音试输。";
                else sizeText = Protocol.Utf8.GetByteCount(editor.Text) + " / 16384 UTF-8 字节" + (valid || editor.Text.Length == 0 ? "" : " · 文字无效或过长，请分段");
            } catch (EncoderFallbackException) { sizeText = "文字包含不完整字符，请重新输入。"; }
            editorSize.Text = sizeText;
            editorSize.ForeColor = valid || editor.Text.Length == 0 ? Theme.Muted : Color.Firebrick;
            bool connected = link != null && link.Connected && connectionCts != null;
            sendEditor.Enabled = !KeyboardRoute && valid && connected && peerReady && !paused && !sending && !typeBusy;
            typeText.Enabled = valid && connected && supportsTyping && !peerReady && !typeBusy && !sending && !paused;
            directKeys.Text = "输入到内网窗口…";
            directKeys.Enabled = KeyboardRoute && KeyboardBodyVisible && valid && connected && supportsTyping && SelectedKeyboardSupported && !peerReady && !typeBusy && !sending && !paused;
            UpdateKeyboardReadiness(connected, valid);
            prepareReceiver.Enabled = !KeyboardRoute && connected && supportsTyping && !peerReady && !typeBusy && !sending && !paused;
            openReceiver.Enabled = !KeyboardRoute && connected && supportsReceiverStart && !peerReady && !typeBusy && !sending && !paused;
            startupDelay.Enabled = !typeBusy;
            fastInput.Enabled = !typeBusy;
            if (typingPaceNote != null) typingPaceNote.Text = supportsFastTyping ? "快速模式只加速启动后的数据输入；启动命令和等待余量保留。" : "当前固件使用兼容节奏；更新本版配套固件后可快速输入。";
        }
        async Task SendEditorText()
        {
            UpdateEditorActions();
            if (!sendEditor.Enabled) return;
            string text = editor.Text;
            // Drop an older automatic clipboard snapshot before this explicit send.
            pendingText = null; sequence = GetClipboardSequenceNumber();
            editorResult.Text = "正在发送，等待内网写入剪贴板后的认证回执…";
            bool success = await SendText(text);
            editorResult.Text = success ? "已收到内网写入确认。" :
                "未确认成功，请检查连接或暂停状态。文字已保留；重发前请先核对内网剪贴板。";
        }
        string ReadReceiverBootstrap()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AtomClip.DuplexBootstrap"))
            {
                if (stream == null || stream.Length > Protocol.MaxText) throw new InvalidDataException("接收器启动资源缺失或过长");
                using (var reader = new StreamReader(stream, Protocol.Utf8)) {
                    string template = reader.ReadToEnd(), original = Convert.ToBase64String(legacyKey); int at = template.IndexOf(original, StringComparison.Ordinal);
                    if (at < 0 || template.IndexOf(original, at + original.Length, StringComparison.Ordinal) >= 0) throw new InvalidDataException("启动模板配对位置无效");
                    string active = template.Replace(original, Convert.ToBase64String(key)); if (Protocol.Utf8.GetByteCount(active) > Protocol.MaxText) throw new InvalidDataException("运行时启动文本超过16KiB"); return active;
                }
            }
        }
        void ShowReceiverSteps()
        {
            SetSetupStep(2);
        }
        async Task<int> SendTypeJob(string text, bool initializeReceiver, bool openSystemReceiver = false, bool directKeyboard = false)
        {
            var secureLink=link as BleLink;if(secureLink!=null&&secureLink.AuthenticatedInfo!=null){uint caps=secureLink.AuthenticatedInfo.Capabilities;if((caps&2)==0||(!directKeyboard&&(caps&4)==0)){AddLog("所选开发板不支持此输入动作。");return -1;}}
            bool probe = directKeyboard && pinyinProbeInFlight;
            bool pinyin = directKeyboard && PinyinRoute, gbk = directKeyboard && GbkRoute;
            if (directKeyboard) text = text.Replace("\r\n", "\n");
            if (typeBusy || paused || link == null || peerReady || !supportsTyping) return -1;
            if ((pinyin || gbk) && (!SelectedKeyboardSupported || (!probe && requireProbe.Checked && (!keyboardProbeSent || !pinyinConfirmed.Checked)))) { editorResult.Text = "请检查固件支持；勾选试输时需完成试输并确认结果。"; return -1; }
            if (directKeyboard && !supportsDirectKeys) { MessageBox.Show(this, "此功能需更新开发板固件，请使用开发板设置烧录本版配套固件。", "直接键盘输入"); return -1; }
            if (openSystemReceiver && !supportsReceiverStart) return -1;
            if (!openSystemReceiver && (text.Length == 0 || text.IndexOf('\0') >= 0 || Protocol.Utf8.GetByteCount(text) > Protocol.MaxText)) return -1;
            bool automaticPaste = !initializeReceiver && paste.Checked;
            string warning = "即将通过开发板，在内网打开系统 PowerShell 写入剪贴板。\r\n请先在内网按 Win+R，在运行窗口按 Win+空格选择 ENG 英语键盘，并关闭大写锁定。\r\n使用系统英语键盘布局，不使用中文输入法的英文模式。\r\n清空运行输入框，保持窗口打开。输入期间请勿操作内网键鼠或切换窗口。\r\n" +
                (initializeReceiver ? "将写入固定接收器启动内容，界面会显示预计时间范围。随后需手动粘贴并执行一次；接收器保持运行后即可连续发送。" :
                    (automaticPaste ? "将自动粘贴到此前的窗口，请先把内网光标放好。" : "完成后请在内网目标窗口手动按 Ctrl+V。")) +
                "\r\n本次 " + Protocol.Utf8.GetByteCount(text) + " UTF-8 字节。是否开始？";
            if (directKeyboard) {
                try { if (gbk) Protocol.EncodeGbkText(text); else if (pinyin) Protocol.NormalizePinyinText(text); else Protocol.NormalizeDirectKeyText(text); } catch (ArgumentException ex) { MessageBox.Show(this, ex.Message, "直接键盘输入"); return -1; }
                warning = "请在内网打开目标文本编辑框，将光标放好，切换系统 ENG 并关闭大写锁定。\r\n开始后有 3 秒准备时间；只通过 USB 键盘直接输入，不打开 PowerShell。\r\n仅支持 ASCII；不会写入剪贴板，也无法读取内网文字。换行和 Tab 会产生相应按键。\r\n是否开始？";
                if (pinyin || gbk) warning = (gbk ? "内网光标放入空白文本框，切到 ENG 美式，开启 Num Lock，关闭大写锁定。GBK 依赖系统和目标程序处理 Alt 数字码；乱码时请换 VUC，不修改系统区域或注册表。" : "内网光标放入空白文本框，切到微软拼音全拼“中”、半角，保留默认 Shift 中英切换，关闭大写锁定。") + "\r\n" +
                    (probe ? "本次试输：" + Protocol.KeyboardProbeText + "\r\n请逐字核对汉字、英文、数字和标点；启用试输检查时请再勾选确认。" : "仅发送 USB 按键，不运行 PowerShell。输入期间请勿操作内网键鼠。换行和 Tab 会操作目标窗口；完成回执不证明目标文字正确。") + "\r\n开始后有 3 秒准备时间。是否开始？";
            }
            if (!initializeReceiver && MessageBox.Show(this, warning, "确认写入内网", MessageBoxButtons.OKCancel) != DialogResult.OK) return -1;
            if (typeBusy || paused || link == null || !link.Connected || connectionCts == null || peerReady || !supportsTyping)
            { AddLog("连接状态已变化，请确认内网程序未运行，再重新发起输入。"); return -1; }
            typeBusy = true; UpdateEditorActions();
            var current = link; var token = connectionCts.Token;
            try
            {
                byte[] payload = gbk ? Protocol.CreateGbkJob(text, gbkCompatible.Checked, key) : pinyin ? Protocol.CreatePinyinJob(text, vucGap, vucSettle, key) : directKeyboard ? Protocol.CreateDirectKeyJob(text, key) : openSystemReceiver ? Protocol.CreateReceiverOpenJob(key) :
                    Protocol.CreateTypeJob(text, automaticPaste, fastInput.Checked && supportsFastTyping ? (byte)1 : (byte)4, supportsStartupDelay ? (byte)startupDelay.Value : (byte)0, key);
                typeId = Protocol.Id(payload); typeResult = new TaskCompletionSource<int>();
                int chars = directKeyboard ? text.Replace("\r\n", "\n").Length : openSystemReceiver ? 100 : 260 + 4 * ((payload.Length - 47 + 2) / 3);
                bool fast = !directKeyboard && !openSystemReceiver && fastInput.Checked && supportsFastTyping;
                int lower = (directKeyboard ? 3 : (int)startupDelay.Value + 7) + (int)Math.Ceiling(chars * (fast ? .003 : .006));
                int upper = (directKeyboard ? 10 : (int)startupDelay.Value + 15) + (int)Math.Ceiling(chars * (fast ? .020 : .035));
                if (pinyin || gbk) {
                    int unicode = 0; foreach (char c in text) if (c >= 128) unicode++;
                    lower = 3 + (int)Math.Ceiling(unicode * (gbk ? gbkCompatible.Checked ? .125 : .029 : .16) + (text.Length - unicode) * .003);
                    upper = 10 + (int)Math.Ceiling(unicode * (gbk ? gbkCompatible.Checked ? .30 : .15 : .45) + (text.Length - unicode) * .020);
                }
                if (pinyin) { double seconds = Protocol.PinyinMilliseconds(text, vucGap, vucSettle) / 1000.0; lower = 3 + (int)Math.Ceiling(seconds); upper = 10 + (int)Math.Ceiling(seconds * 1.5 + chars * .020); }
                var elapsed = Stopwatch.StartNew();
                EventHandler progress = delegate {
                    string value = "已用 " + (int)elapsed.Elapsed.TotalSeconds + " 秒 · 预计约 " + lower + "–" + upper + " 秒；等待开发板完成回执，请勿操作内网键鼠。";
                    if (setupProgress != null && initializeReceiver) setupProgress.Text = value;
                    else editorResult.Text = value;
                };
                jobProgressHandler = progress; jobProgress.Tick += progress; progress(null, EventArgs.Empty); jobProgress.Start();
                await current.Send(Protocol.Frame(Protocol.TypeJob, payload), token);
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    var delay = Task.Delay(Math.Max(180000, (upper + 90) * 1000), timeout.Token);
                    if (await Task.WhenAny(typeResult.Task, delay) != typeResult.Task)
                    { if(directKeyboard)ResetKeyboardProbe(); token.ThrowIfCancellationRequested(); AddLog("输入任务未确认完成；请检查内网窗口，勿立即重复发送。"); return -1; }
                    timeout.Cancel(); int status = await typeResult.Task;
                    AddLog(status == 2 ? "键盘输入步骤已发送完毕；请在内网核对文字。此状态不证明 PowerShell 执行或粘贴成功。" :
                        status == 3 ? "开发板拒绝任务：内网程序仍在线或另有输入任务。" : "输入任务取消或失败；若内网仍有 PowerShell 窗口，请手动关闭。");
                    if (directKeyboard) { if(status!=2)ResetKeyboardProbe(); editorResult.Text = status == 2 ? probe ? "试输按键已发完，请核对“" + Protocol.KeyboardProbeText + "”；启用试输检查时请再勾选确认。" : "按键已发完，请在内网核对文本。" : "输入未完成，请核对内网状态；需要时重新试输。"; }
                    return status;
                }
            }
            catch (OperationCanceledException) { if(directKeyboard)ResetKeyboardProbe(); AddLog("连接已中断；输入结果未知，请检查内网。"); }
            catch (Exception ex) { if(directKeyboard)ResetKeyboardProbe(); AddLog("输入任务失败：" + Program.Error(ex)); }
            finally {
                jobProgress.Stop();
                if (jobProgressHandler != null) jobProgress.Tick -= jobProgressHandler; jobProgressHandler = null;
                typeId = null; typeResult = null; typeBusy = false; UpdateEditorActions();
            }
            return -1;
        }
#endif
    }
}
