using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;

namespace AtomClipUsb
{
    public sealed class BoardProfile
    {
        public int id; public string key, name, chip, backend, usbPort, partitionFile; public int flashBytes, flashFrequencyMHz, buttonPin, rgbLedPin; public uint capabilities; public bool nativeUsb;
    }
    sealed class BoardCatalog
    {
        public sealed class Document { public int schema; public BoardProfile[] profiles; public Document() { } }
        public readonly Dictionary<int, BoardProfile> Profiles = new Dictionary<int, BoardProfile>();
        public BoardCatalog()
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("AtomClip.BoardCatalog"))
            {
                if (s == null || s.Length > 131072) throw new IOException("开发板配置资源缺失"); using (var reader = new StreamReader(s)) { var d = new JavaScriptSerializer().Deserialize<Document>(reader.ReadToEnd()); if (d.schema != 1 || d.profiles == null) throw new IOException("开发板配置版本错误"); foreach (var p in d.profiles) { if (Profiles.ContainsKey(p.id) || p.id <= 0 || p.flashBytes <= 0) throw new IOException("开发板配置重复或无效"); Profiles.Add(p.id, p); } }
            }
        }
        public string Name(int id) { BoardProfile p; return Profiles.TryGetValue(id, out p) ? p.name : "未知板型 #" + id; }
        public string Capabilities(uint caps) { return (caps & 1) != 0 ? ((caps & 2) != 0 ? "剪贴板桥接、USB键盘" : "仅剪贴板桥接；不支持HID自动输入") : "缺少USB CDC桥接能力"; }
    }
}
