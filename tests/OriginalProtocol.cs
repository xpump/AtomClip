using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AtomClip;

public record Packet(byte Kind, byte[] Payload);

public static class Protocol
{
    public const int MaxText = 16384;
    public const int MaxPayload = MaxText + 44;
    public const byte Data = 1, Ack = 2, Ping = 3, Status = 4;
    public static readonly byte[] Magic = "ACB1"u8.ToArray();
    public static readonly UTF8Encoding Utf8 = new(false, true);

    public static uint Crc(ReadOnlySpan<byte> bytes)
    {
        uint c = 0xffffffff;
        foreach (byte b in bytes)
        {
            c ^= b;
            for (int i = 0; i < 8; i++) c = (c >> 1) ^ ((c & 1) != 0 ? 0xedb88320u : 0);
        }
        return ~c;
    }

    public static byte[] Frame(byte kind, byte[] payload)
    {
        if (payload.Length > MaxPayload) throw new ArgumentException("数据过长");
        byte[] b = new byte[13 + payload.Length];
        Magic.CopyTo(b, 0); b[4] = kind;
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(5), (uint)payload.Length);
        payload.CopyTo(b, 9);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(9 + payload.Length), Crc(b.AsSpan(4, 5 + payload.Length)));
        return b;
    }

    public static byte[] Encrypt(string text, byte[] key)
    {
        if (text.Contains('\0')) throw new ArgumentException("文本含有不支持的空字符");
        byte[] plain = Utf8.GetBytes(text);
        if (plain.Length > MaxText) throw new ArgumentException("文本超过 16 KiB，请分段复制");
        byte[] b = new byte[44 + plain.Length];
        RandomNumberGenerator.Fill(b.AsSpan(0, 28)); // message id + nonce
        using var aes = new AesGcm(key, 16);
        aes.Encrypt(b.AsSpan(16, 12), plain, b.AsSpan(28, plain.Length), b.AsSpan(28 + plain.Length, 16), b.AsSpan(0, 16));
        CryptographicOperations.ZeroMemory(plain);
        return b;
    }

    public static (string Id, string Text) Decrypt(byte[] b, byte[] key)
    {
        if (b.Length < 44 || b.Length > MaxPayload) throw new CryptographicException("数据长度错误");
        byte[] p = new byte[b.Length - 44];
        using var aes = new AesGcm(key, 16);
        aes.Decrypt(b.AsSpan(16, 12), b.AsSpan(28, p.Length), b.AsSpan(28 + p.Length, 16), p, b.AsSpan(0, 16));
        string text = Utf8.GetString(p);
        CryptographicOperations.ZeroMemory(p);
        if (text.Contains('\0')) throw new CryptographicException("文本格式错误");
        return (Convert.ToHexString(b.AsSpan(0, 16)), text);
    }

    public static byte[] MakeAck(byte[] id, byte[] key)
    {
        if (id.Length != 16) throw new ArgumentException("ID length");
        byte[] b = new byte[32]; id.CopyTo(b, 0);
        byte[] signed = new byte[17]; signed[0] = Ack; id.CopyTo(signed, 1);
        HMACSHA256.HashData(key, signed).AsSpan(0, 16).CopyTo(b.AsSpan(16));
        return b;
    }

    public static string? ReadAck(byte[] b, byte[] key)
    {
        if (b.Length != 32) return null;
        var expected = MakeAck(b[..16], key);
        return CryptographicOperations.FixedTimeEquals(expected.AsSpan(16), b.AsSpan(16)) ? Convert.ToHexString(b.AsSpan(0, 16)) : null;
    }
}

// Incremental parser for both USB byte streams and BLE notification fragments.
public sealed class PacketParser
{
    readonly List<byte> buffer = new();
    public void Reset() => buffer.Clear();
    public List<Packet> Feed(ReadOnlySpan<byte> input)
    {
        var found = new List<Packet>();
        foreach (byte b in input) buffer.Add(b);
        while (buffer.Count >= 4)
        {
            if (!buffer.Take(4).SequenceEqual(Protocol.Magic)) { buffer.RemoveAt(0); continue; }
            if (buffer.Count < 9) break;
            uint n = BinaryPrimitives.ReadUInt32LittleEndian(buffer.Skip(5).Take(4).ToArray());
            if (n > Protocol.MaxPayload || buffer[4] is < 1 or > 4) { buffer.RemoveAt(0); continue; }
            int total = 13 + (int)n;
            if (buffer.Count < total) break;
            byte[] bytes = buffer.GetRange(0, total).ToArray();
            if (Protocol.Crc(bytes.AsSpan(4, 5 + (int)n)) != BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(9 + (int)n)))
            { buffer.RemoveAt(0); continue; }
            found.Add(new Packet(bytes[4], bytes.AsSpan(9, (int)n).ToArray()));
            buffer.RemoveRange(0, total);
        }
        return found;
    }
}
