using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace AtomClipUsb
{
    public sealed class BleLink : ILink
    {
        public static readonly Guid ServiceId = new Guid("a29bc101-12a5-4ea1-81a0-2c4dd9cd0001");
        static readonly Guid RxId = new Guid("a29bc102-12a5-4ea1-81a0-2c4dd9cd0001");
        static readonly Guid TxId = new Guid("a29bc103-12a5-4ea1-81a0-2c4dd9cd0001");
        readonly PacketParser parser = new PacketParser(); readonly object readGate = new object();
        readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);
        BluetoothLEDevice device; GattDeviceService service; GattCharacteristic rx, tx; GattSession session;
        bool disposed;
        public event Action<Packet> Received;
        public bool Connected { get { return !disposed && device != null && device.ConnectionStatus == BluetoothConnectionStatus.Connected && rx != null; } }
        public static async Task<ulong> Scan(CancellationToken token)
        {
            var result = new TaskCompletionSource<ulong>();
            var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            watcher.Received += delegate(BluetoothLEAdvertisementWatcher s, BluetoothLEAdvertisementReceivedEventArgs e)
            { if (e.Advertisement.LocalName == "AtomClip-S3U") result.TrySetResult(e.BluetoothAddress); };
            watcher.Stopped += delegate(BluetoothLEAdvertisementWatcher s, BluetoothLEAdvertisementWatcherStoppedEventArgs e)
            { if (e.Error != BluetoothError.Success) result.TrySetException(new IOException("BLE 扫描失败：" + e.Error)); };
            using (token.Register(() => result.TrySetCanceled()))
            {
                watcher.Start(); try { return await result.Task; } finally { watcher.Stop(); }
            }
        }
        public async Task Open(ulong address, CancellationToken token)
        {
            device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(token);
            if (device == null) throw new IOException("无法打开蓝牙设备，请检查蓝牙开关");
            session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(token);
            if (session != null) session.MaintainConnection = true;
            var services = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(token);
            foreach (var candidate in services.Services)
            { if (candidate.Uuid == ServiceId && service == null) service = candidate; else candidate.Dispose(); }
            if (services.Status != GattCommunicationStatus.Success || service == null) throw new IOException("找不到 AtomClip 服务：" + services.Status);
            var characteristics = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(token);
            rx = characteristics.Characteristics.FirstOrDefault(c => c.Uuid == RxId);
            tx = characteristics.Characteristics.FirstOrDefault(c => c.Uuid == TxId);
            if (characteristics.Status != GattCommunicationStatus.Success || rx == null || tx == null) throw new IOException("BLE 服务不完整");
            tx.ValueChanged += Read;
            var status = await tx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(token);
            if (status != GattCommunicationStatus.Success) throw new IOException("无法订阅 BLE 接收通知：" + status);
        }
        void Read(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            lock (readGate)
            {
                byte[] bytes = args.CharacteristicValue.ToArray();
                foreach (var packet in parser.Feed(bytes, bytes.Length)) { var handler = Received; if (handler != null) handler(packet); }
            }
        }
        public async Task Send(byte[] frame, CancellationToken token)
        {
            await sendGate.WaitAsync(token);
            try
            {
                if (rx == null || disposed) throw new IOException("BLE 未连接");
                int size = Math.Min(180, Math.Max(20, (session == null ? 23 : session.MaxPduSize) - 3));
                for (int p = 0; p < frame.Length; p += size)
                {
                    token.ThrowIfCancellationRequested();
                    byte[] chunk = Protocol.Slice(frame, p, Math.Min(size, frame.Length - p));
                    var status = await rx.WriteValueAsync(chunk.AsBuffer(), GattWriteOption.WriteWithResponse).AsTask(token);
                    if (status != GattCommunicationStatus.Success) throw new IOException("BLE 发送失败：" + status);
                }
            }
            finally { sendGate.Release(); }
        }
        public void Dispose()
        {
            disposed = true;
            if (tx != null) tx.ValueChanged -= Read;
            if (session != null) { session.MaintainConnection = false; session.Dispose(); }
            if (service != null) service.Dispose(); if (device != null) device.Dispose();
        }
    }
}
