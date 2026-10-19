using System;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Threading.Tasks;

namespace AtomClipUsb
{
    public interface ILink : IDisposable
    {
        event Action<Packet> Received;
        bool Connected { get; }
        Task Send(byte[] frame, CancellationToken token);
    }
    public sealed class UsbLink : ILink
    {
        readonly SerialPort port;
        readonly UsbManagementParser parser = new UsbManagementParser();
        readonly object readGate = new object();
        readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);
        public event Action<Packet> Received;
        public bool Connected { get { return port.IsOpen; } }
        public UsbLink(string name)
        {
            port = new SerialPort(name, 115200) { DtrEnable = true, RtsEnable = false, ReadTimeout = 1000, WriteTimeout = 3000, ReadBufferSize = 65536 };
            port.DataReceived += Read;
            try { port.Open(); }
            catch { port.Dispose(); throw; }
        }
        void Read(object sender, SerialDataReceivedEventArgs e)
        {
            try
            {
                lock (readGate)
                {
                    DrainCore(parser.Feed, () => port.IsOpen ? port.BytesToRead : 0,
                        bytes => port.Read(bytes, 0, bytes.Length), packet => { var handler = Received; if (handler != null) handler(packet); });
                }
            }
            catch (IOException) { }
            catch (InvalidOperationException) { }
            catch (TimeoutException) { }
        }
        // SerialPort.Read may short-read, and DataReceived is not one event per chunk.
        internal static void Drain(PacketParser parser, Func<int> available, Func<byte[], int> read, Action<Packet> accept)
        { DrainCore(parser.Feed, available, read, accept); }
        static void DrainCore(Func<byte[], int, System.Collections.Generic.List<Packet>> feed, Func<int> available, Func<byte[], int> read, Action<Packet> accept)
        {
            for (int remaining = available(); remaining > 0; remaining = available()) {
                byte[] bytes = new byte[Math.Min(remaining, 4096)]; int count = read(bytes); if (count == 0) break;
                foreach (var packet in feed(bytes, count)) accept(packet);
            }
        }
        public async Task Send(byte[] frame, CancellationToken token)
        {
            await sendGate.WaitAsync(token);
            try { await Task.Run(() => port.Write(frame, 0, frame.Length), token); }
            finally { sendGate.Release(); }
        }
        public void Dispose() { port.DataReceived -= Read; port.Dispose(); }
    }
}
