using System;
using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Security.Cryptography;
using System.Threading;

namespace AtomClipUsb
{
    // Trusted physical USB channel. Never accepts an ILink/BLE object.
    sealed class UsbBoardClient : IDisposable
    {
        readonly SerialPort port; readonly UsbManagementParser parser = new UsbManagementParser(); readonly CancellationToken token;
        public UsbBoardClient(string name, CancellationToken token)
        {
            if (!System.Text.RegularExpressions.Regex.IsMatch(name ?? "", "^COM[0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) throw new ArgumentException("请选择USB串口");
            this.token = token; port = new SerialPort(name, 115200) { DtrEnable = true, RtsEnable = false, ReadTimeout = 250, WriteTimeout = 3000, ReadBufferSize = 32768 }; try { port.Open(); } catch { port.Dispose(); throw; }
        }
        public sealed class Material
        {
            public readonly V2Protocol.Info Info; public readonly byte[] DataKey;
            public Material(byte[] b) { if (b.Length != 72) throw new InvalidDataException("USB配对信息长度错误"); Info = new V2Protocol.Info(V2Protocol.Part(b, 0, 40)); DataKey = V2Protocol.Part(b, 40, 32); Array.Clear(b, 0, b.Length); }
        }
        public Material Read() { byte[] request; return new Material(Call(4, new byte[0], out request)); }
        byte[] Call(byte opcode, byte[] arguments, out byte[] request)
        {
            token.ThrowIfCancellationRequested(); request = V2Protocol.Random(16); byte[] frame = UsbManagement.Request(opcode, request, arguments);
            try { port.Write(frame, 0, frame.Length); } finally { Array.Clear(frame, 0, frame.Length); }
            var clock = Stopwatch.StartNew(); byte[] buffer = new byte[512];
            while (clock.ElapsedMilliseconds < 4000)
            {
                token.ThrowIfCancellationRequested(); int n; try { n = port.Read(buffer, 0, buffer.Length); } catch (TimeoutException) { continue; }
                foreach (var packet in parser.Feed(buffer, n)) { byte[] result = UsbManagement.Response(packet, opcode, request); if (result != null) return result; }
            }
            throw new IOException("USB管理响应超时；确认开发板运行0.2.0固件，而非ROM下载模式");
        }
        void CheckPhysical(V2Protocol.Binding b, bool compareData)
        {
            var actual = Read(); try { if (!V2Protocol.Equal(actual.Info.Id, b.DeviceInfo.Id)) throw new CryptographicException("USB目标已改变；未写入绑定"); if (compareData && !V2Protocol.Equal(actual.DataKey, b.DataKey)) throw new CryptographicException("USB正文配对不一致；待确认配置仍已保留"); } finally { Array.Clear(actual.DataKey, 0, 32); }
        }
        V2Protocol.Info WithProof(byte opcode, byte[] arguments, V2Protocol.Binding b, string role)
        {
            byte[] request, response; try { response = Call(opcode, arguments, out request); } finally { Array.Clear(arguments, 0, arguments.Length); }
            if (response.Length != 72) throw new CryptographicException("USB绑定证明长度无效"); var info = new V2Protocol.Info(V2Protocol.Part(response, 0, 40));
            byte[] proof = V2Protocol.Mac(b.ControlKey, V2Protocol.Join(V2Protocol.Domain("AtomClip/v2/" + role), info.Bytes, request));
            if (!info.Configured || !V2Protocol.Equal(info.Id, b.DeviceInfo.Id) || !V2Protocol.Equal(proof, V2Protocol.Part(response, 40, 32))) throw new CryptographicException("USB绑定未确认；保留待确认配置，请接入USB核对"); return info;
        }
        public V2Protocol.Info Provision(V2Protocol.Binding b, bool replace)
        {
            CheckPhysical(b, false); var info = WithProof(2, V2Protocol.Join(new byte[] { (byte)(replace ? 1 : 0) }, b.ControlKey, b.DataKey), b, "usb-bind"); CheckPhysical(b, true); return info;
        }
        public V2Protocol.Info Confirm(V2Protocol.Binding b) { CheckPhysical(b, true); return WithProof(5, new byte[0], b, "usb-proof"); }
        public V2Protocol.Info Reset(V2Protocol.Info expected) { var actual = Read(); try { if (!V2Protocol.Equal(actual.Info.Id, expected.Id)) throw new CryptographicException("USB目标已改变，未执行恢复出厂"); } finally { Array.Clear(actual.DataKey, 0, 32); } byte[] request; byte[] result = Call(3, new byte[0], out request); var info = new V2Protocol.Info(result); if (info.Configured || !V2Protocol.Equal(info.Id, expected.Id)) throw new CryptographicException("设备未确认清除控制绑定"); return info; }
        public void Dispose() { port.Dispose(); }
    }
}
