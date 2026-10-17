using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace AtomClipUsb
{
    // Hosted by the system Windows PowerShell process; no distributed EXE is launched.
    public static class PowerShellEntry
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("kernel32.dll")] static extern IntPtr GetConsoleWindow();
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window, int command);
        public static void Run(string encodedKey)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                throw new InvalidOperationException("请通过接收启动文件启动 STA PowerShell。");
            bool first;
            using (var mutex = new Mutex(true, "Local\\AtomClip.TextBridge", out first))
            {
                if (!first) { MessageBox.Show("内网接收器或 USB 程序已在运行，请从右下角托盘打开。", "AtomClip PowerShell"); return; }
                SetProcessDPIAware(); Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AtomClipPowerShell");
                var logger = new FileLog(Path.Combine(directory, "Logs"));
                byte[] key = Convert.FromBase64String(encodedKey);
                try {
                    var form = new MainForm(directory, logger, key);
                    form.Shown += delegate { var console = GetConsoleWindow(); if (console != IntPtr.Zero) ShowWindow(console, 0); };
                    Application.Run(form);
                }
                catch (Exception ex)
                {
                    logger.Write("接收器错误：" + Program.Error(ex));
                    MessageBox.Show("接收器发生错误：" + Program.Error(ex) + "\r\n日志目录：" + logger.DirectoryPath, "AtomClip PowerShell");
                }
                finally { Array.Clear(key, 0, key.Length); }
            }
        }
    }
}
