using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AtomClipUsb
{
    public sealed class Packet
    {
        public readonly byte Kind;
        public readonly byte[] Payload;
        public Packet(byte kind, byte[] payload) { Kind = kind; Payload = payload; }
    }

    // Wire compatible with AtomClip 0.1.1. No dependency on a modern .NET runtime.
    public static class Protocol
    {
        public const int MaxText = 16384, MaxPayload = 16512;
        public const byte Data = 1, Ack = 2, Ping = 3, Status = 4, TypeJob = 5, JobResult = 6;
        public static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        public static byte[] Slice(byte[] b, int offset, int count)
        { var r = new byte[count]; Buffer.BlockCopy(b, offset, r, 0, count); return r; }
        public static string Id(byte[] b) { return BitConverter.ToString(b, 0, 16).Replace("-", ""); }
        public static uint Read32(byte[] b, int p)
        { return (uint)(b[p] | b[p + 1] << 8 | b[p + 2] << 16 | b[p + 3] << 24); }
        static void Write32(byte[] b, int p, uint n)
        { for (int i = 0; i < 4; i++) b[p + i] = (byte)(n >> (8 * i)); }
        public static uint Crc(byte[] b, int offset, int count)
        {
            uint c = 0xffffffff;
            for (int i = offset; i < offset + count; i++)
            {
                c ^= b[i];
                for (int j = 0; j < 8; j++) c = (c >> 1) ^ ((c & 1) != 0 ? 0xedb88320u : 0);
            }
            return ~c;
        }
        public static byte[] Frame(byte kind, byte[] payload)
        {
            if (payload.Length > MaxPayload || kind < 1 || kind > 6) throw new ArgumentException("数据格式错误");
            var b = new byte[13 + payload.Length];
            b[0] = 65; b[1] = 67; b[2] = 66; b[3] = 49; b[4] = kind;
            Write32(b, 5, (uint)payload.Length); Buffer.BlockCopy(payload, 0, b, 9, payload.Length);
            Write32(b, 9 + payload.Length, Crc(b, 4, 5 + payload.Length));
            return b;
        }
        public static byte[] Encrypt(string text, byte[] key)
        {
            if (text.IndexOf('\0') >= 0) throw new ArgumentException("文本含有不支持的空字符");
            if (Utf8.GetByteCount(text) > MaxText) throw new ArgumentException("文本超过 16 KiB，请分段复制");
            byte[] plain = Utf8.GetBytes(text);
            return EncryptBytes(plain, key);
        }
        public static byte[] EncryptBytes(byte[] plain, byte[] key)
        {
            if (plain.Length + 44 > MaxPayload) throw new ArgumentException("数据过长");
            try
            {
                byte[] prefix = new byte[28];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(prefix);
                byte[] tag = new byte[16];
                byte[] cipher = WindowsGcm.Transform(true, key, Slice(prefix, 16, 12), Slice(prefix, 0, 16), plain, tag);
                byte[] b = new byte[44 + plain.Length];
                Buffer.BlockCopy(prefix, 0, b, 0, 28); Buffer.BlockCopy(cipher, 0, b, 28, cipher.Length);
                Buffer.BlockCopy(tag, 0, b, 28 + cipher.Length, 16);
                return b;
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
        public static string Decrypt(byte[] payload, byte[] key)
        {
            if (payload.Length < 44 || payload.Length > MaxText + 44) throw new CryptographicException("数据长度错误");
            int n = payload.Length - 44;
            byte[] plain = WindowsGcm.Transform(false, key, Slice(payload, 16, 12), Slice(payload, 0, 16),
                Slice(payload, 28, n), Slice(payload, 28 + n, 16));
            try
            {
                string text = Utf8.GetString(plain);
                if (text.IndexOf('\0') >= 0) throw new CryptographicException("文本格式错误");
                return text;
            }
            finally { Array.Clear(plain, 0, plain.Length); }
        }
        public static byte[] MakeAck(byte[] id, byte[] key)
        {
            if (id.Length != 16) throw new ArgumentException("ID length");
            byte[] signed = new byte[17]; signed[0] = Ack; Buffer.BlockCopy(id, 0, signed, 1, 16);
            byte[] result = new byte[32]; Buffer.BlockCopy(id, 0, result, 0, 16);
            using (var hmac = new HMACSHA256(key)) Buffer.BlockCopy(hmac.ComputeHash(signed), 0, result, 16, 16);
            return result;
        }
        public static string ReadAck(byte[] payload, byte[] key)
        {
            if (payload.Length != 32) return null;
            byte[] expected = MakeAck(Slice(payload, 0, 16), key);
            int difference = 0;
            for (int i = 16; i < 32; i++) difference |= expected[i] ^ payload[i];
            return difference == 0 ? Id(payload) : null;
        }
#if !USB
        public static byte[] CreateTypeJob(string text, bool paste, byte interval, byte[] key)
        { return CreateTypeJob(text, paste, interval, 0, key); }
        public static byte[] CreateTypeJob(string text, bool paste, byte interval, byte startupSeconds, byte[] key)
        {
            if (interval < 1 || interval > 50) throw new ArgumentException("输入间隔需为 1–50 毫秒");
            if (text.IndexOf('\0') >= 0 || Utf8.GetByteCount(text) > MaxText) throw new ArgumentException("文本格式错误或超过 16 KiB");
            byte[] raw = Utf8.GetBytes(text), compressed;
            try
            {
                using (var stream = new MemoryStream())
                {
                    using (var gzip = new GZipStream(stream, CompressionMode.Compress, true)) gzip.Write(raw, 0, raw.Length);
                    compressed = stream.ToArray();
                }
            }
            finally { Array.Clear(raw, 0, raw.Length); }
            int header = startupSeconds == 0 ? 3 : 4;
            if (startupSeconds != 0 && (startupSeconds < 3 || startupSeconds > 60)) throw new ArgumentException("启动等待需为 3–60 秒");
            byte[] plain = new byte[header + compressed.Length]; plain[0] = startupSeconds == 0 ? (byte)1 : (byte)4; plain[1] = paste ? (byte)1 : (byte)0; plain[2] = interval;
            if (header == 4) plain[3] = startupSeconds;
            Buffer.BlockCopy(compressed, 0, plain, header, compressed.Length);
            return EncryptBytes(plain, key);
        }
        public static byte[] CreateDirectKeyJob(string text, byte[] key)
        {
            text = NormalizeDirectKeyText(text);
            byte[] plain = new byte[text.Length + 3]; plain[0] = 3; plain[2] = 4;
            Encoding.ASCII.GetBytes(text, 0, text.Length, plain, 3); return EncryptBytes(plain, key);
        }
        public static string NormalizeDirectKeyText(string text)
        {
            text = text.Replace("\r\n", "\n");
            if (text.Length == 0 || text.Length > MaxText) throw new ArgumentException("需要 1–16384 个 ASCII 字符");
            foreach (char c in text) if ((c < 32 || c > 126) && c != '\n' && c != '\t') throw new ArgumentException("直接键盘输入仅支持英文、数字、符号、Tab 和换行；不能读取剪贴板");
            return text;
        }
        public static byte[] CreateReceiverOpenJob(byte[] key)
        { return EncryptBytes(new byte[] { 2, 0, 4 }, key); }
        public const string PinyinProbeKeys = "vuc4e2d vuc6587 ";
        public static byte[] CreatePinyinProbeJob(byte[] key)
        {
            byte[] plain = new byte[PinyinProbeKeys.Length + 3]; plain[0] = 3; plain[2] = 35;
            Encoding.ASCII.GetBytes(PinyinProbeKeys, 0, PinyinProbeKeys.Length, plain, 3); return EncryptBytes(plain, key);
        }
        public static string NormalizePinyinText(string text)
        {
            text = text.Replace("\r\n", "\n");
            if (text.Length == 0 || Utf8.GetByteCount(text) > MaxText) throw new ArgumentException("需要 1–16384 UTF-8 字节文本");
            foreach (char c in text)
                if (char.IsSurrogate(c) || c >= 0xfffe || (char.IsControl(c) && c != '\n' && c != '\t'))
                    throw new ArgumentException("微软拼音键盘暂仅支持常用中文与 BMP 字符，不支持 emoji、扩展汉字或此控制字符；未发送");
            return text;
        }
        public static byte[] CreatePinyinJob(string text, bool compatible, byte[] key)
        {
            byte[] raw = Utf8.GetBytes(NormalizePinyinText(text));
            byte[] plain = new byte[raw.Length + 3]; plain[0] = 5;
            plain[1] = compatible ? (byte)80 : (byte)35; plain[2] = compatible ? (byte)4 : (byte)1;
            Buffer.BlockCopy(raw, 0, plain, 3, raw.Length); Array.Clear(raw, 0, raw.Length);
            return EncryptBytes(plain, key);
        }
        public const string KeyboardProbeText = "病ABC123，中文!";
        public static byte[] EncodeGbkText(string text)
        {
            text = NormalizePinyinText(text);
            foreach(char c in text) if(c == '\u20ac' || (c >= '\ue000' && c <= '\uf8ff')) throw new ArgumentException("GBK 不支持此字符，请改用 VUC。");
            var codec = Encoding.GetEncoding(936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            byte[] raw;
            try { raw = codec.GetBytes(text); }
            catch (EncoderFallbackException) { throw new ArgumentException("GBK 不支持这段文字中的部分字符，请改用 VUC 或移除不支持字符。"); }
            if (codec.GetString(raw) != text) throw new ArgumentException("GBK 无法无损表示这段文字，请改用 VUC。");
            for (int i = 0; i < raw.Length; i++) {
                int first = raw[i];
                if (first < 128) continue;
                if (first < 0x81 || first > 0xfe || ++i >= raw.Length || raw[i] < 0x40 || raw[i] > 0xfe || raw[i] == 0x7f)
                    throw new ArgumentException("GBK 方式暂不支持此字符，请改用 VUC。");
            }
            return raw;
        }
        public static byte[] CreateGbkJob(string text, bool compatible, byte[] key)
        {
            byte[] raw = EncodeGbkText(text), plain = new byte[raw.Length + 5];
            plain[0] = 6; plain[1] = compatible ? (byte)8 : (byte)2; plain[2] = plain[1];
            plain[3] = compatible ? (byte)15 : (byte)3; plain[4] = compatible ? (byte)30 : (byte)6;
            Buffer.BlockCopy(raw, 0, plain, 5, raw.Length); Array.Clear(raw, 0, raw.Length);
            return EncryptBytes(plain, key);
        }
        public static int ReadJobResult(byte[] payload, string id, byte[] key)
        {
            if (payload.Length != 33 || Id(payload) != id) return -1;
            byte[] signed = new byte[18]; signed[0] = JobResult; Buffer.BlockCopy(payload, 0, signed, 1, 17);
            byte[] expected; using (var hmac = new HMACSHA256(key)) expected = hmac.ComputeHash(signed);
            int diff = 0; for (int i = 0; i < 16; i++) diff |= expected[i] ^ payload[17 + i];
            return diff == 0 ? payload[16] : -1;
        }
#endif
    }

    public sealed class PacketParser
    {
        readonly List<byte> buffer = new List<byte>();
        public List<Packet> Feed(byte[] bytes, int count)
        {
            var packets = new List<Packet>();
            for (int i = 0; i < count; i++) buffer.Add(bytes[i]);
            int start = 0;
            while (buffer.Count - start >= 4)
            {
                if (buffer[start] != 65 || buffer[start + 1] != 67 || buffer[start + 2] != 66 || buffer[start + 3] != 49)
                { start++; continue; }
                if (buffer.Count - start < 9) break;
                uint size = (uint)(buffer[start + 5] | buffer[start + 6] << 8 | buffer[start + 7] << 16 | buffer[start + 8] << 24);
                if (size > Protocol.MaxPayload || buffer[start + 4] < 1 || buffer[start + 4] > 6) { start++; continue; }
                int total = 13 + (int)size;
                if (buffer.Count - start < total) break;
                byte[] b = buffer.GetRange(start, total).ToArray();
                if (Protocol.Crc(b, 4, 5 + (int)size) != Protocol.Read32(b, 9 + (int)size)) { start++; continue; }
                packets.Add(new Packet(b[4], Protocol.Slice(b, 9, (int)size))); start += total;
            }
            if (start > 0) buffer.RemoveRange(0, start);
            return packets;
        }
    }

    // AES-GCM is provided by Windows CNG (bcrypt.dll), including Windows 10 1903.
    // Managed byte arrays used in the authentication struct stay pinned until the call returns.
    static class WindowsGcm
    {
        [StructLayout(LayoutKind.Sequential)]
        struct AuthInfo
        {
            public int Size, Version;
            public IntPtr Nonce; public int NonceSize;
            public IntPtr Aad; public int AadSize;
            public IntPtr Tag; public int TagSize;
            public IntPtr Mac; public int MacSize, AadProcessed;
            public ulong DataProcessed; public int Flags;
        }
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        static extern int BCryptOpenAlgorithmProvider(out IntPtr algorithm, string name, string implementation, int flags);
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        static extern int BCryptSetProperty(IntPtr obj, string property, byte[] input, int count, int flags);
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        static extern int BCryptGetProperty(IntPtr obj, string property, byte[] output, int count, out int written, int flags);
        [DllImport("bcrypt.dll")]
        static extern int BCryptGenerateSymmetricKey(IntPtr algorithm, out IntPtr key, IntPtr keyObject, int objectSize, byte[] secret, int secretSize, int flags);
        [DllImport("bcrypt.dll")]
        static extern int BCryptEncrypt(IntPtr key, byte[] input, int count, ref AuthInfo auth, IntPtr iv, int ivSize, byte[] output, int outputSize, out int written, int flags);
        [DllImport("bcrypt.dll")]
        static extern int BCryptDecrypt(IntPtr key, byte[] input, int count, ref AuthInfo auth, IntPtr iv, int ivSize, byte[] output, int outputSize, out int written, int flags);
        [DllImport("bcrypt.dll")] static extern int BCryptDestroyKey(IntPtr key);
        [DllImport("bcrypt.dll")] static extern int BCryptCloseAlgorithmProvider(IntPtr algorithm, int flags);
        static void Check(int status)
        { if (status != 0) throw new CryptographicException("Windows 加密/认证失败，错误码 0x" + unchecked((uint)status).ToString("X8")); }
        public static byte[] Transform(bool encrypt, byte[] secret, byte[] nonce, byte[] aad, byte[] input, byte[] tag)
        {
            if (secret.Length != 32 || nonce.Length != 12 || tag.Length != 16) throw new CryptographicException("加密参数错误");
            IntPtr algorithm = IntPtr.Zero, key = IntPtr.Zero, obj = IntPtr.Zero;
            GCHandle noncePin = default(GCHandle), aadPin = default(GCHandle), tagPin = default(GCHandle);
            byte[] output = new byte[Math.Max(input.Length, 1)];
            try
            {
                Check(BCryptOpenAlgorithmProvider(out algorithm, "AES", null, 0));
                byte[] mode = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
                Check(BCryptSetProperty(algorithm, "ChainingMode", mode, mode.Length, 0));
                byte[] length = new byte[4]; int written;
                Check(BCryptGetProperty(algorithm, "ObjectLength", length, 4, out written, 0));
                int objectSize = BitConverter.ToInt32(length, 0);
                if (objectSize < 1 || objectSize > 1048576) throw new CryptographicException("加密对象长度错误");
                obj = Marshal.AllocHGlobal(objectSize);
                Check(BCryptGenerateSymmetricKey(algorithm, out key, obj, objectSize, secret, secret.Length, 0));
                noncePin = GCHandle.Alloc(nonce, GCHandleType.Pinned); aadPin = GCHandle.Alloc(aad, GCHandleType.Pinned); tagPin = GCHandle.Alloc(tag, GCHandleType.Pinned);
                var auth = new AuthInfo { Size = Marshal.SizeOf(typeof(AuthInfo)), Version = 1,
                    Nonce = noncePin.AddrOfPinnedObject(), NonceSize = nonce.Length, Aad = aadPin.AddrOfPinnedObject(), AadSize = aad.Length,
                    Tag = tagPin.AddrOfPinnedObject(), TagSize = tag.Length };
                int status = encrypt
                    ? BCryptEncrypt(key, input, input.Length, ref auth, IntPtr.Zero, 0, output, output.Length, out written, 0)
                    : BCryptDecrypt(key, input, input.Length, ref auth, IntPtr.Zero, 0, output, output.Length, out written, 0);
                Check(status);
                if (written != input.Length) throw new CryptographicException("加密结果长度错误");
                return Protocol.Slice(output, 0, written);
            }
            finally
            {
                Array.Clear(output, 0, output.Length);
                if (key != IntPtr.Zero) BCryptDestroyKey(key);
                if (obj != IntPtr.Zero) Marshal.FreeHGlobal(obj);
                if (algorithm != IntPtr.Zero) BCryptCloseAlgorithmProvider(algorithm, 0);
                if (tagPin.IsAllocated) tagPin.Free(); if (aadPin.IsAllocated) aadPin.Free(); if (noncePin.IsAllocated) noncePin.Free();
            }
        }
    }
}
