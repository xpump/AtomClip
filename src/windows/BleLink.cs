using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;

namespace AtomClipUsb
{
    public sealed class BleLink : ILink
    {
        public static readonly Guid ServiceId = new Guid(V2Protocol.Service);
        readonly V2Protocol.Binding binding;
        readonly PacketParser parser = new PacketParser(); readonly object readGate = new object();
        readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1); readonly CancellationTokenSource lifetime = new CancellationTokenSource();
        readonly V2Protocol.Stream authStream = new V2Protocol.Stream(true), sessionStream = new V2Protocol.Stream(false);
        readonly TaskCompletionSource<bool> handshake = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        BluetoothLEDevice device; GattDeviceService service; GattCharacteristic rx, tx, auth; GattSession gattSession;
        V2Protocol.Client authentication; volatile bool disposed, ready;
        public string Address { get; private set; }
        public bool StopReconnect { get; private set; }
        public string Failure { get; private set; }
        public V2Protocol.Info AuthenticatedInfo { get { return authentication != null && authentication.Ready ? authentication.DeviceInfo : null; } }
        public event Action<Packet> Received;
        public event Action<bool> Identified;
        public event Action<string> Progress;
        void Report(string message) { var progress = Progress; if (progress != null) progress(message); }
        public BleLink(V2Protocol.Binding binding)
        {
            if (binding == null) throw new IOException("请先添加并选择已绑定开发板");
            if (binding != null && binding.Pending) throw new IOException("请先通过USB确认此设备绑定");
            this.binding = binding;
        }
        public bool Connected { get { try { return ready && !disposed && device != null && device.ConnectionStatus == BluetoothConnectionStatus.Connected && rx != null; } catch { return false; } } }
        static long Now() { return (long)(Stopwatch.GetTimestamp() * (1000.0 / Stopwatch.Frequency)); }
        public async Task Open(CancellationToken token)
        {
            Report("正在扫描已绑定开发板…");
            ulong address = await BoardDiscovery.Selected(binding, token, Report); token.ThrowIfCancellationRequested();
            Report("编号已核对，正在打开蓝牙服务…");
            Address = address.ToString("X12"); device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(token); if (device == null) throw new IOException("无法打开选定蓝牙设备，请检查蓝牙与权限");
            gattSession = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId).AsTask(token); if (gattSession != null) gattSession.MaintainConnection = true;
            var services = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(token); Guid serviceId = ServiceId;
            foreach (var candidate in services.Services) { if (candidate.Uuid == serviceId && service == null) service = candidate; else candidate.Dispose(); }
            if (services.Status != GattCommunicationStatus.Success || service == null) throw new IOException("找不到选定协议的服务；不会自动降级");
            var chars = await service.GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(token);
            rx = chars.Characteristics.FirstOrDefault(c => c.Uuid == new Guid(V2Protocol.RxId));
            tx = chars.Characteristics.FirstOrDefault(c => c.Uuid == new Guid(V2Protocol.TxId));
            if (chars.Status != GattCommunicationStatus.Success || rx == null || tx == null) throw new IOException("BLE服务不完整");

            {
                auth = chars.Characteristics.FirstOrDefault(c => c.Uuid == new Guid(V2Protocol.AuthId)); var info = chars.Characteristics.FirstOrDefault(c => c.Uuid == new Guid(V2Protocol.InfoId));
                if (auth == null || info == null) throw new CryptographicException("身份认证特征缺失");
                var read = await info.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(token); if (read.Status != GattCommunicationStatus.Success) throw new IOException("无法读取选定设备编号");
                var actual = new V2Protocol.Info(read.Value.ToArray()); if (!V2Protocol.Equal(actual.Id, binding.DeviceInfo.Id)) throw new CryptographicException("连接目标编号已改变，已停止");
                Report("正在订阅开发板通知…");
                authentication = new V2Protocol.Client(binding); auth.ValueChanged += ReadAuth;
                var authNotify = await auth.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(token); if (authNotify != GattCommunicationStatus.Success) throw new IOException("无法订阅身份认证通知");
            }
            tx.ValueChanged += Read;
            var notify = await tx.WriteClientCharacteristicConfigurationDescriptorAsync(GattClientCharacteristicConfigurationDescriptorValue.Notify).AsTask(token); if (notify != GattCommunicationStatus.Success) throw new IOException("无法订阅接收通知");

            {
                using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token))
                {
                    Report("正在验证开发板身份…");
                    timeout.CancelAfter(15000); using (timeout.Token.Register(() => handshake.TrySetCanceled())) { await WriteRaw(auth, authentication.Hello(), timeout.Token); await handshake.Task; }
                }
                if (!authentication.Ready) throw new CryptographicException("双方身份认证未完成");
            }
            token.ThrowIfCancellationRequested(); if (disposed) throw new IOException("连接已取消"); ready = true;
            Report("身份已验证，蓝牙连接完成");
        }
        void ReadAuth(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            lock (readGate)
            {
                if (disposed || authentication == null) return;
                try
                {
                    foreach (var frame in authStream.Feed(args.CharacteristicValue.ToArray(), Now()))
                    {
                        byte[] reply = authentication.Accept(frame);
                        if (reply == null) handshake.TrySetResult(true);
                        else WriteRaw(auth, reply, lifetime.Token).ContinueWith(t => { if (t.IsFaulted) Fault("身份认证写入失败", true); }, TaskScheduler.Default);
                    }
                }
                catch (Exception e) { handshake.TrySetException(e); Fault("身份认证失败：" + e.GetType().Name, true); }
            }
        }
        void Read(GattCharacteristic sender, GattValueChangedEventArgs args)
        {
            lock (readGate)
            {
                if (disposed) return;
                try
                {
                    byte[] bytes = args.CharacteristicValue.ToArray();
                    if (authentication == null || !authentication.Ready) throw new CryptographicException("收到认证完成前的数据通知");
                    foreach (var frame in sessionStream.Feed(bytes, Now())) Deliver(authentication.Session.Open(frame));
                }
                catch (Exception e) { Fault("会话数据认证失败：" + e.GetType().Name, true); }
            }
        }
        void Deliver(byte[] bytes)
        {
            if (!V2Protocol.ValidInner(bytes)) throw new CryptographicException("认证会话内层帧无效");
            if (bytes.Length == 14 && bytes[4] == 8)
            {
                if (bytes[0] != 65 || bytes[1] != 67 || bytes[2] != 66 || bytes[3] != 49 || Protocol.Read32(bytes, 5) != 1 || Protocol.Crc(bytes, 4, 6) != Protocol.Read32(bytes, 10)) throw new CryptographicException("识别回执无效"); var identified = Identified; if (identified != null) identified(bytes[9] == 0); return;
            }
            foreach (var packet in parser.Feed(bytes, bytes.Length)) { var handler = Received; if (handler != null) handler(packet); }
        }
        void Fault(string reason, bool security)
        {
            Failure = reason; StopReconnect |= security; ready = false; handshake.TrySetException(new IOException(reason)); Dispose();
        }
        async Task WriteRaw(GattCharacteristic characteristic, byte[] frame, CancellationToken token)
        {
            await sendGate.WaitAsync(token);
            try
            {
                if (disposed || characteristic == null) throw new IOException("BLE未连接");
                int size = Math.Min(180, Math.Max(20, (gattSession == null ? 23 : gattSession.MaxPduSize) - 3));
                for (int p = 0; p < frame.Length; p += size)
                {
                    token.ThrowIfCancellationRequested(); if (disposed) throw new IOException("BLE已断开");
                    byte[] chunk = Protocol.Slice(frame, p, Math.Min(size, frame.Length - p)); var status = await characteristic.WriteValueAsync(chunk.AsBuffer(), GattWriteOption.WriteWithResponse).AsTask(token); if (status != GattCommunicationStatus.Success) throw new IOException("BLE写入失败：" + status);
                }
            }
            finally { sendGate.Release(); }
        }
        public async Task Send(byte[] frame, CancellationToken token)
        {
            if (!Connected || ((authentication == null || !authentication.Ready))) throw new IOException("设备尚未通过身份认证，禁止发送");
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token))
            {
                // Serialise nonce assignment and the entire ATT frame, including concurrent heartbeat calls.
                await applicationSend.WaitAsync(linked.Token);
                try { byte[] wire = authentication.Session.Seal(frame); await WriteRaw(rx, wire, linked.Token); }
                catch { Fault("BLE发送未完成，当前会话已关闭", false); throw; }
                finally { applicationSend.Release(); }
            }
        }
        readonly SemaphoreSlim applicationSend = new SemaphoreSlim(1, 1);
        public Task Identify(CancellationToken token)
        {
            if (AuthenticatedInfo == null || (AuthenticatedInfo.Capabilities & 32) == 0) throw new IOException("此板型没有已知LED，请通过USB核对完整编号");
            byte[] frame = V2Protocol.UsbFrame(new byte[] { 1 }); frame[4] = 8; Buffer.BlockCopy(V2Protocol.Le32(Protocol.Crc(frame, 4, 6)), 0, frame, 10, 4); return Send(frame, token);
        }
        public void Dispose()
        {
            lock (readGate)
            {
                if (disposed) return; disposed = true; ready = false; lifetime.Cancel(); authStream.Reset(); sessionStream.Reset(); if (authentication != null) authentication.Dispose();
                try { if (tx != null) tx.ValueChanged -= Read; if (auth != null) auth.ValueChanged -= ReadAuth; } catch { }
                try { if (gattSession != null) { gattSession.MaintainConnection = false; gattSession.Dispose(); } } catch { }
                try { if (service != null) service.Dispose(); } catch { } try { if (device != null) device.Dispose(); } catch { }
            }
        }
    }
}
