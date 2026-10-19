using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AtomClipUsb
{
    public sealed class V2Registry
    {
        public readonly Dictionary<string, V2Protocol.Binding> Confirmed = new Dictionary<string, V2Protocol.Binding>(), Pending = new Dictionary<string, V2Protocol.Binding>();
        public string SelectedId = "";
        public V2Protocol.Binding Active { get { V2Protocol.Binding b; return Pending.ContainsKey(SelectedId) || !Confirmed.TryGetValue(SelectedId, out b) ? null : b; } }
        public V2Protocol.Binding Stage(V2Protocol.Binding b) { V2Protocol.Binding old; if (Pending.TryGetValue(b.DeviceInfo.Number, out old)) { if (!V2Protocol.Equal(old.ControlKey, b.ControlKey) || !V2Protocol.Equal(old.DataKey, b.DataKey)) throw new InvalidDataException("请先处理此板的待确认绑定"); return old; } var copy = V2Protocol.Binding.Decode(b.Encode()); copy.Pending = true; Pending[copy.DeviceInfo.Number] = copy; return copy; }
        public void Confirm(string id) { V2Protocol.Binding b; if (!Pending.TryGetValue(id, out b)) throw new InvalidDataException("没有待确认配置"); Pending.Remove(id); b.Pending = false; Confirmed[id] = b; SelectedId = id; }
        public void Select(string id) { if (!Confirmed.ContainsKey(id) || Pending.ContainsKey(id)) throw new InvalidDataException("请先通过USB确认绑定"); SelectedId = id; }
        public void Forget(string id) { Confirmed.Remove(id); Pending.Remove(id); if (SelectedId == id) SelectedId = ""; }
        static void WriteString(BinaryWriter w, string s) { byte[] b = Encoding.ASCII.GetBytes(s); if (b.Length > 32) throw new InvalidDataException("设备标识过长"); w.Write((byte)b.Length); w.Write(b); }
        static string ReadString(BinaryReader r) { int n = r.ReadByte(); if (n > 32) throw new InvalidDataException("设备标识过长"); byte[] b = r.ReadBytes(n); if (b.Length != n) throw new EndOfStreamException(); return Encoding.ASCII.GetString(b); }
        static void U16(BinaryWriter w, int n) { w.Write((byte)n); w.Write((byte)(n >> 8)); }
        static int U16(BinaryReader r) { return r.ReadByte() | r.ReadByte() << 8; }
        public byte[] Encode()
        {
            if (Confirmed.Count + Pending.Count > 64) throw new InvalidDataException("最多保存64条配置");
            using (var s = new MemoryStream()) using (var w = new BinaryWriter(s))
            {
                w.Write(Encoding.ASCII.GetBytes("ACBS2")); w.Write((byte)0); WriteString(w, SelectedId); WriteString(w, ""); U16(w, Confirmed.Count + Pending.Count);
                foreach (var map in new[] { Confirmed, Pending }) foreach (var b in map.Values) { byte[] blob = b.Encode(); U16(w, blob.Length); w.Write((byte)(ReferenceEquals(map, Pending) ? 1 : 0)); w.Write(blob); Array.Clear(blob, 0, blob.Length); } return s.ToArray();
            }
        }
        public static V2Registry Decode(byte[] b)
        {
            if (b.Length > 32768) throw new InvalidDataException("本地配置过大");
            using (var s = new MemoryStream(b)) using (var r = new BinaryReader(s))
            {
                if (!V2Protocol.Equal(r.ReadBytes(5), Encoding.ASCII.GetBytes("ACBS2"))) throw new InvalidDataException("本地配置版本无效"); var result = new V2Registry(); int legacy = r.ReadByte(); if (legacy > 1) throw new InvalidDataException("本地模式无效"); result.SelectedId = ReadString(r); string retiredAddress = ReadString(r);
                if (!System.Text.RegularExpressions.Regex.IsMatch(result.SelectedId, "^(?:[0-9A-F]{32})?$") || !System.Text.RegularExpressions.Regex.IsMatch(retiredAddress, "^(?:[0-9A-F]{12})?$")) throw new InvalidDataException("本地设备标识无效");
                int count = U16(r); if (count > 64) throw new InvalidDataException("本地设备数量无效"); for (int i = 0; i < count; i++) { int length = U16(r), state = r.ReadByte(); if (length < 111 || length > 271 || state > 1) throw new InvalidDataException("本地设备记录无效"); byte[] blob = r.ReadBytes(length); if (blob.Length != length) throw new EndOfStreamException(); try { var binding = V2Protocol.Binding.Decode(blob); binding.Pending = state == 1; var map = state == 1 ? result.Pending : result.Confirmed; if (map.ContainsKey(binding.DeviceInfo.Number)) throw new InvalidDataException("重复设备记录"); map.Add(binding.DeviceInfo.Number, binding); } finally { Array.Clear(blob, 0, blob.Length); } }
                if (s.Position != s.Length) throw new InvalidDataException("本地配置存在多余内容"); if (legacy == 1) result.SelectedId = ""; return result;
            }
        }
    }
    sealed class DeviceBindings
    {
        readonly string path; public V2Registry Registry = new V2Registry(); public string LoadError;
        static readonly byte[] Entropy = V2Protocol.Sha(Encoding.ASCII.GetBytes("AtomClip.BindingStore.v2"));
        public DeviceBindings(string directory)
        {
            path = Path.Combine(directory, "devices-v2.bin");
            if (!File.Exists(path)) return;
            try { if (new FileInfo(path).Length > 65536) throw new InvalidDataException(); byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser); try { Registry = V2Registry.Decode(plain); } finally { Array.Clear(plain, 0, plain.Length); } }
            catch { LoadError = "设备配置无法解密；请恢复已导出的加密配置。不会改连其他板。"; }
        }
        public void Save()
        {
            if (LoadError != null) throw new IOException(LoadError); byte[] plain = Registry.Encode();
            try { byte[] blob = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser); Directory.CreateDirectory(Path.GetDirectoryName(path)); string temporary = path + ".tmp"; using (var f = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None)) { f.Write(blob, 0, blob.Length); f.Flush(true); } if (File.Exists(path)) File.Replace(temporary, path, null); else File.Move(temporary, path); }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
        public void Change(Action<V2Registry> action) { if (LoadError != null) throw new IOException(LoadError); byte[] before = Registry.Encode(); try { action(Registry); Save(); } catch { Registry = V2Registry.Decode(before); throw; } finally { Array.Clear(before, 0, before.Length); } }
        public void ResetUnreadable() { if (LoadError == null) throw new InvalidOperationException(); if (File.Exists(path)) File.Move(path, path + ".unreadable-" + Guid.NewGuid().ToString("N")); Registry = new V2Registry(); LoadError = null; Save(); }
    }
}
