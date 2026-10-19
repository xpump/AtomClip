using System;
using System.IO;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace AtomClipUsb
{
    public sealed class UsbManagementParser
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
                if (size > Protocol.MaxPayload || buffer[start + 4] < 1 || buffer[start + 4] > 7) { start++; continue; }
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

    // Small shared USB-only framing. Kept independent of the BLE crypto/UI to fit the inner EXE budget.
    public static class UsbManagement
    {
        public static byte[] Request(byte opcode, byte[] request, byte[] arguments)
        {
            if (request.Length != 16 || arguments.Length > 128) throw new ArgumentException("USB请求无效");
            int n = 17 + arguments.Length; byte[] b = new byte[13 + n]; b[0] = 65; b[1] = 67; b[2] = 66; b[3] = 49; b[4] = 7;
            byte[] len = BitConverter.GetBytes(n); Buffer.BlockCopy(len, 0, b, 5, 4); b[9] = opcode; Buffer.BlockCopy(request, 0, b, 10, 16); Buffer.BlockCopy(arguments, 0, b, 26, arguments.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(Protocol.Crc(b, 4, n + 5)), 0, b, 9 + n, 4); return b;
        }
        public static byte[] Response(Packet packet, byte opcode, byte[] request)
        {
            if (packet.Kind != 7 || packet.Payload.Length < 18 || packet.Payload[0] != (opcode | 0x80)) return null;
            for (int i = 0; i < 16; i++) if (packet.Payload[1 + i] != request[i]) return null;
            if (packet.Payload[17] != 0) throw new IOException("USB设备管理请求未完成，代码" + packet.Payload[17]);
            return Protocol.Slice(packet.Payload, 18, packet.Payload.Length - 18);
        }
        public static async Task<byte[]> ReadDataKey(UsbLink link, CancellationToken token)
        {
            byte[] request = new byte[16]; using (var r = RandomNumberGenerator.Create()) r.GetBytes(request);
            var result = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<Packet> receive = p => { try { byte[] data = Response(p, 4, request); if (data != null) result.TrySetResult(data); } catch (Exception e) { result.TrySetException(e); } };
            link.Received += receive;
            try
            {
                await link.Send(Request(4, request, new byte[0]), token);
                Task timeout = Task.Delay(1000, token); if (await Task.WhenAny(result.Task, timeout) != result.Task) { token.ThrowIfCancellationRequested(); return null; }
                byte[] response = await result.Task;
                if (response.Length != 72 || response[0] != 65 || response[1] != 67 || response[2] != 73 || response[3] != 50 || response[4] != 2 || response[5] != 1 || (response[36] & 2) == 0) throw new IOException("USB正文配对信息无效");
                byte[] key = Protocol.Slice(response, 40, 32); Array.Clear(response, 40, 32); int nonzero = 0; foreach (byte b in key) nonzero |= b; if (nonzero == 0) throw new CryptographicException("USB正文配对密钥无效"); return key;
            }
            finally { link.Received -= receive; }
        }
    }
}
