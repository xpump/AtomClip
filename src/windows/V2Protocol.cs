using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace AtomClipUsb
{
    // Platform-independent wire rules; AES-GCM uses the existing Windows CNG adapter.
    public static class V2Protocol
    {
        public const int InfoSize = 40, MaxInner = 16525;
        public const string Service = "a29bc201-12a5-4ea1-81a0-2c4dd9cd0001", InfoId = "a29bc202-12a5-4ea1-81a0-2c4dd9cd0001", AuthId = "a29bc203-12a5-4ea1-81a0-2c4dd9cd0001", RxId = "a29bc204-12a5-4ea1-81a0-2c4dd9cd0001", TxId = "a29bc205-12a5-4ea1-81a0-2c4dd9cd0001";
        public static byte[] Random(int count) { byte[] b = new byte[count]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(b); return b; }
        public static byte[] Part(byte[] b, int p, int n) { if (p < 0 || n < 0 || p > b.Length - n) throw new InvalidDataException("数据边界无效"); return Protocol.Slice(b, p, n); }
        public static byte[] Join(params byte[][] arrays) { int n = 0; foreach (var a in arrays) n += a.Length; byte[] b = new byte[n]; int p = 0; foreach (var a in arrays) { Buffer.BlockCopy(a, 0, b, p, a.Length); p += a.Length; } return b; }
        public static byte[] Domain(string value) { return Encoding.ASCII.GetBytes(value + "\0"); }
        public static ushort U16(byte[] b, int p) { return (ushort)(b[p] | b[p + 1] << 8); }
        public static uint U32(byte[] b, int p) { return Protocol.Read32(b, p); }
        public static byte[] Le32(uint n) { return new byte[] { (byte)n, (byte)(n >> 8), (byte)(n >> 16), (byte)(n >> 24) }; }
        public static byte[] Le64(long n) { byte[] b = new byte[8]; for (int i = 0; i < 8; i++) b[i] = (byte)((ulong)n >> (8 * i)); return b; }
        public static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", ""); }
        public static byte[] Sha(byte[] b) { using (var s = SHA256.Create()) return s.ComputeHash(b); }
        public static byte[] Mac(byte[] key, byte[] b) { using (var m = new HMACSHA256(key)) return m.ComputeHash(b); }
        public static bool Equal(byte[] a, byte[] b) { if (a.Length != b.Length) return false; int diff = 0; for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i]; return diff == 0; }
        public static void Length(byte[] b, int n) { if (b == null || b.Length != n) throw new InvalidDataException("数据长度无效"); }
        public sealed class Info
        {
            public readonly byte[] Bytes, Id;
            public readonly int Profile; public readonly uint Capabilities, Flash;
            public readonly bool Configured;
            public Info(byte[] b)
            {
                Length(b, 40); if (!Equal(Part(b, 0, 4), Encoding.ASCII.GetBytes("ACI2")) || b[4] != 2 || b[5] != 1 || b[19] != 0 || b[37] != 0 || b[38] != 0 || b[39] != 0) throw new InvalidDataException("不支持的设备信息版本");
                Bytes = (byte[])b.Clone(); Id = Part(b, 20, 16); Profile = U16(b, 6); Capabilities = U32(b, 8); Flash = U32(b, 12); Configured = (b[36] & 1) != 0;
                if (Profile == 0 || Flash == 0 || Equal(Id, new byte[16])) throw new InvalidDataException("设备信息无效");
            }
            public string Number { get { return Hex(Id); } }
            public string ShortId { get { return Hex(Part(Id, 0, 2)); } }
            public string Version { get { return Bytes[16] + "." + Bytes[17] + "." + Bytes[18]; } }
        }
        public sealed class Binding
        {
            public Info DeviceInfo; public readonly byte[] ControlKey, DataKey;
            public string Alias; public bool Pending;
            public Binding(Info info, byte[] control, byte[] data, string alias)
            {
                Length(control, 32); Length(data, 32); if (Equal(control, new byte[32]) || Equal(data, new byte[32])) throw new InvalidDataException("绑定密钥无效");
                DeviceInfo = info; ControlKey = (byte[])control.Clone(); DataKey = (byte[])data.Clone(); Alias = (alias ?? "我的开发板").Trim(' ');
                if (Alias.Length == 0 || System.Text.RegularExpressions.Regex.IsMatch(Alias,@"[\x00-\x1f\x7f]") || Protocol.Utf8.GetByteCount(Alias) > 160) throw new InvalidDataException("别名需为1–160 UTF-8字节");
            }
            public byte[] Encode() { byte[] name = Protocol.Utf8.GetBytes(Alias); return Join(Encoding.ASCII.GetBytes("ACBK2"), DeviceInfo.Bytes, ControlKey, DataKey, new byte[] { (byte)name.Length, (byte)(name.Length >> 8) }, name); }
            public static Binding Decode(byte[] b)
            {
                if (b.Length < 111 || b.Length > 271 || !Equal(Part(b, 0, 5), Encoding.ASCII.GetBytes("ACBK2")) || U16(b, 109) != b.Length - 111) throw new InvalidDataException("绑定配置格式错误");
                return new Binding(new Info(Part(b, 5, 40)), Part(b, 45, 32), Part(b, 77, 32), Protocol.Utf8.GetString(b, 111, b.Length - 111));
            }
        }
        public static byte[] Proof(byte[] key, string role, Info info, byte[] client, byte[] server) { return Mac(key, Join(Domain("AtomClip/v2/" + role), info.Bytes, client, server)); }
        public sealed class Candidate
        {
            public readonly string Address; public readonly Info DeviceInfo; public readonly int Rssi;
            public Candidate(string address, Info info, int rssi) { Address = address.Replace(":", "").ToUpperInvariant(); if (!System.Text.RegularExpressions.Regex.IsMatch(Address, "^[0-9A-F]{12}$")) throw new InvalidDataException("蓝牙地址无效"); DeviceInfo = info; Rssi = rssi; }
        }
        public static Candidate Selected(IEnumerable<Candidate> candidates, byte[] target)
        {
            Length(target, 16); var matches = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in candidates) if (Equal(c.DeviceInfo.Id, target)) matches[c.Address] = c;
            if (matches.Count > 1) throw new CryptographicException("发现重复完整设备编号，已停止自动连接，请通过USB核对");
            foreach (var c in matches.Values) return c; return null;
        }
        public static byte[] Control(int kind, byte[] payload)
        {
            if (kind < 1 || kind > 4 || payload.Length > 256) throw new InvalidDataException("认证消息无效");
            byte[] b = Join(new byte[] { 65, 67, 86, 50, (byte)kind, (byte)payload.Length, (byte)(payload.Length >> 8) }, payload, new byte[4]);
            Buffer.BlockCopy(Le32(Protocol.Crc(b, 4, b.Length - 8)), 0, b, b.Length - 4, 4); return b;
        }
        public static byte[] ControlPayload(byte[] b, int kind)
        {
            if (b.Length < 11 || b[4] != kind || b.Length != 11 + U16(b, 5) || U16(b, 5) > 256 || !Equal(Part(b, 0, 4), new byte[] { 65, 67, 86, 50 }) || Protocol.Crc(b, 4, b.Length - 8) != U32(b, b.Length - 4)) throw new CryptographicException("认证消息格式或校验错误");
            return Part(b, 7, b.Length - 11);
        }
        public sealed class Client : IDisposable
        {
            readonly Binding binding; readonly byte[] client; byte[] server; public Info DeviceInfo; int stage; public Session Session;
            public Client(Binding binding) : this(binding, Random(32)) { }
            public Client(Binding binding, byte[] nonce) { this.binding = binding; Length(nonce, 32); client = (byte[])nonce.Clone(); }
            public byte[] Hello() { if (stage != 0) throw new InvalidOperationException("认证已开始"); stage = 1; return Control(1, Join(binding.DeviceInfo.Id, client)); }
            public byte[] Accept(byte[] frame)
            {
                try
                {
                    if (stage == 1)
                    {
                        byte[] p = ControlPayload(frame, 2); Length(p, 104); DeviceInfo = new Info(Part(p, 0, 40));
                        if (!DeviceInfo.Configured || (DeviceInfo.Capabilities & 128) == 0 || !Equal(DeviceInfo.Id, binding.DeviceInfo.Id)) throw new CryptographicException("设备编号不匹配或尚未绑定");
                        server = Part(p, 40, 32); if (!Equal(Part(p, 72, 32), Proof(binding.ControlKey, "server", DeviceInfo, client, server))) throw new CryptographicException("开发板身份认证失败；不会改连其他板");
                        stage = 2; return Control(3, Proof(binding.ControlKey, "client", DeviceInfo, client, server));
                    }
                    if (stage == 2)
                    {
                        if (!Equal(ControlPayload(frame, 4), Proof(binding.ControlKey, "finish", DeviceInfo, client, server))) throw new CryptographicException("连接完成证明无效");
                        Session = new Session(binding.ControlKey, DeviceInfo, client, server, true); stage = 3; return null;
                    }
                    throw new CryptographicException("认证消息顺序错误");
                }
                catch { Dispose(); throw; }
            }
            public bool Ready { get { return stage == 3 && Session != null && !Session.Closed; } }
            public void Dispose() { stage = -1; Array.Clear(client, 0, client.Length); if (server != null) Array.Clear(server, 0, server.Length); if (Session != null) Session.Dispose(); }
        }
        public sealed class Session : IDisposable
        {
            readonly object gate = new object(); readonly byte[] sendKey, receiveKey, sendPrefix, receivePrefix; long sendSequence, receiveSequence; public bool Closed { get; private set; }
            public Session(byte[] key, Info info, byte[] client, byte[] server, bool isClient)
            {
                Length(key, 32); Length(client, 32); Length(server, 32); byte[] salt = Sha(Join(Domain("AtomClip/v2/session"), info.Bytes, client, server)), prk = Mac(salt, key);
                string send = isClient ? "c2d" : "d2c", receive = isClient ? "d2c" : "c2d";
                sendKey = Expand(prk, send + "/key", 32); receiveKey = Expand(prk, receive + "/key", 32); sendPrefix = Expand(prk, send + "/nonce", 4); receivePrefix = Expand(prk, receive + "/nonce", 4); Array.Clear(prk, 0, prk.Length);
            }
            static byte[] Expand(byte[] prk, string label, int n) { return Part(Mac(prk, Join(Domain("AtomClip/v2/" + label), new byte[] { 1 })), 0, n); }
            public byte[] Seal(byte[] plain)
            { lock (gate) {
                if (Closed || sendSequence == long.MaxValue) throw new CryptographicException("会话已结束"); if (plain.Length < 13 || plain.Length > MaxInner) throw new InvalidDataException("会话正文长度无效");
                byte[] header = Join(new byte[] { 65, 67, 83, 50 }, Le64(sendSequence), Le32((uint)plain.Length)), tag = new byte[16];
                byte[] cipher = WindowsGcm.Transform(true, sendKey, Join(sendPrefix, Le64(sendSequence)), header, plain, tag); sendSequence++; return Join(header, cipher, tag);
            }}
            public byte[] Open(byte[] frame)
            { lock (gate) {
                try
                {
                    if (Closed || receiveSequence == long.MaxValue || frame.Length < 45 || frame.Length > MaxInner + 32 || !Equal(Part(frame, 0, 4), new byte[] { 65, 67, 83, 50 }) || U32(frame, 12) != frame.Length - 32 || !Equal(Part(frame, 4, 8), Le64(receiveSequence))) throw new CryptographicException("会话序号、长度或版本无效");
                    byte[] plain = WindowsGcm.Transform(false, receiveKey, Join(receivePrefix, Le64(receiveSequence)), Part(frame, 0, 16), Part(frame, 16, frame.Length - 32), Part(frame, frame.Length - 16, 16)); receiveSequence++; return plain;
                }
                catch { Dispose(); throw; }
            }}
            public void Dispose() { lock (gate) { Closed = true; foreach (var b in new[] { sendKey, receiveKey, sendPrefix, receivePrefix }) Array.Clear(b, 0, b.Length); }}
        }
        public sealed class Stream
        {
            readonly bool auth; readonly MemoryStream pending = new MemoryStream(); int expected; long first, last;
            public Stream(bool auth) { this.auth = auth; }
            public void Reset() { pending.SetLength(0); expected = 0; }
            public List<byte[]> Feed(byte[] input, long now)
            {
                var result = new List<byte[]>(); if (pending.Length > 0 && (now - last > (auth ? 3000 : 5000) || now - first > (auth ? 15000 : 120000))) { Reset(); throw new IOException("分片接收超时"); }
                foreach (byte b in input)
                {
                    if (pending.Length == 0) first = now; pending.WriteByte(b); int n = (int)pending.Length;
                    if (n == 4 && !Equal(pending.ToArray(), auth ? new byte[] { 65, 67, 86, 50 } : new byte[] { 65, 67, 83, 50 })) { Reset(); throw new IOException("分片版本无效"); }
                    if (n == (auth ? 7 : 16)) { byte[] h = pending.ToArray(); uint len = auth ? U16(h, 5) : U32(h, 12); if (len < (auth ? 0 : 13) || len > (auth ? 256 : MaxInner)) { Reset(); throw new IOException("分片长度超限"); } expected = (int)len + (auth ? 11 : 32); }
                    if (expected != 0 && n == expected) { result.Add(pending.ToArray()); Reset(); }
                }
                last = now; return result;
            }
        }
        static byte[] DerivePassword(string password, byte[] salt)
        {
            if (password == null || password.Length < 12 || password.Length > 128) throw new CryptographicException("配置口令需为12–128字符");
            using (var p = new Rfc2898DeriveBytes(password, salt, 200000, HashAlgorithmName.SHA256)) return p.GetBytes(32);
        }
        public static byte[] ExportBinding(Binding binding, string password)
        {
            byte[] salt = Random(16), nonce = Random(12), plain = binding.Encode(), key = DerivePassword(password, salt), tag = new byte[16];
            try { byte[] header = Join(Encoding.ASCII.GetBytes("ACBF2"), salt, Le32(200000), nonce, Le32((uint)plain.Length)); return Join(header, WindowsGcm.Transform(true, key, nonce, header, plain, tag), tag); }
            finally { Array.Clear(key, 0, key.Length); Array.Clear(plain, 0, plain.Length); }
        }
        public static Binding ImportBinding(byte[] b, string password)
        {
            if (b.Length < 168 || b.Length > 328 || !Equal(Part(b, 0, 5), Encoding.ASCII.GetBytes("ACBF2")) || U32(b, 21) != 200000 || U32(b, 37) != b.Length - 57) throw new InvalidDataException("不支持的加密配置格式");
            byte[] key = DerivePassword(password, Part(b, 5, 16)), plain = null;
            try { plain = WindowsGcm.Transform(false, key, Part(b, 25, 12), Part(b, 0, 41), Part(b, 41, b.Length - 57), Part(b, b.Length - 16, 16)); return Binding.Decode(plain); }
            finally { Array.Clear(key, 0, key.Length); if (plain != null) Array.Clear(plain, 0, plain.Length); }
        }
        public static byte[] UsbFrame(byte[] payload)
        {
            byte[] b = Join(new byte[] { 65, 67, 66, 49, 7 }, Le32((uint)payload.Length), payload, new byte[4]); Buffer.BlockCopy(Le32(Protocol.Crc(b, 4, b.Length - 8)), 0, b, b.Length - 4, 4); return b;
        }
        public static bool ValidInner(byte[] b) { return b.Length >= 13 && b.Length <= MaxInner && b[0] == 65 && b[1] == 67 && b[2] == 66 && b[3] == 49 && U32(b, 5) == b.Length - 13 && Protocol.Crc(b, 4, b.Length - 8) == U32(b, b.Length - 4) && ((b[4] >= 1 && b[4] <= 6) || b[4] == 8); }
    }
}
