using System;
using System.IO;
using System.Text;

namespace AtomClipUsb
{
    public sealed class FileLog
    {
        readonly object gate = new object();
        public readonly string DirectoryPath;
        public string LastError { get; private set; }
        DateTime cleanupDay;
        public FileLog(string directory)
        {
            DirectoryPath = directory;
            Write("程序启动：AtomClip 0.1.0；日志不含剪贴板正文或配对密钥。");
        }
        public void Write(string message)
        {
            lock (gate)
            {
                try
                {
                    Directory.CreateDirectory(DirectoryPath);
                    if (cleanupDay != DateTime.Today) { Cleanup(); cleanupDay = DateTime.Today; }
                    string path = Path.Combine(DirectoryPath, "AtomClipUSB-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");
                    if (File.Exists(path) && new FileInfo(path).Length >= 2 * 1024 * 1024)
                    {
                        if (File.Exists(path + ".2")) File.Delete(path + ".2");
                        if (File.Exists(path + ".1")) File.Move(path + ".1", path + ".2");
                        File.Move(path, path + ".1");
                    }
                    string line = message.Replace('\r', ' ').Replace('\n', ' ');
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + "  " + line + Environment.NewLine, new UTF8Encoding(false));
                    LastError = null;
                }
                catch (Exception ex) { LastError = ex.GetType().Name + "，0x" + ex.HResult.ToString("X8"); }
            }
        }
        public void Cleanup()
        {
            lock (gate)
            {
                try
                {
                    if (!Directory.Exists(DirectoryPath)) return;
                    foreach (string path in Directory.GetFiles(DirectoryPath, "AtomClipUSB-*.log*"))
                        if (File.GetLastWriteTime(path).Date < DateTime.Today.AddDays(-6)) File.Delete(path);
                }
                catch (Exception) { }
            }
        }
    }
}
