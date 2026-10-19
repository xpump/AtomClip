using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Foundation;

namespace AtomClipUsb
{
    public sealed class BoardAdvertisement : BoardPresence.Observation
    {
        public uint Capabilities; public bool V2;
    }
    public static class BoardDiscovery
    {
        public static readonly Guid V2Service = new Guid(V2Protocol.Service);
        static readonly byte[] V2WireUuid = WireUuid(V2Service);
        static byte[] WireUuid(Guid id) { string s = id.ToString("N"); byte[] b = new byte[16]; for (int i = 0; i < 16; i++) b[i] = Convert.ToByte(s.Substring((15 - i) * 2, 2), 16); return b; }
        public static async Task<List<BoardAdvertisement>> Scan(int milliseconds, CancellationToken token)
        {
            var observations = new Dictionary<ulong, BoardAdvertisement>(); var gate = new object(); bool scanning = true;
            var failure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var watcher = new BluetoothLEAdvertisementWatcher { ScanningMode = BluetoothLEScanningMode.Active };
            TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementReceivedEventArgs> received = delegate(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementReceivedEventArgs e)
            {
                lock (gate)
                {
                    if (!scanning) return; BoardAdvertisement item;
                    if (!observations.TryGetValue(e.BluetoothAddress, out item)) { if (observations.Count >= 256) { failure.TrySetException(new IOException("附近广播过多，请缩小范围后重试")); return; } item = new BoardAdvertisement { Address = e.BluetoothAddress.ToString("X12") }; observations.Add(e.BluetoothAddress, item); }
                    item.Rssi = e.RawSignalStrengthInDBm;
                    if (e.Advertisement.ServiceUuids.Contains(V2Service)) item.V2 = true;
                    string name = e.Advertisement.LocalName ?? ""; if (System.Text.RegularExpressions.Regex.IsMatch(name, "^AC-[0-9A-Fa-f]{4}$")) item.ShortId = name.Substring(3).ToUpperInvariant();
                    foreach (var section in e.Advertisement.DataSections)
                    {
                        if (section.DataType != 0x21) continue; byte[] data = section.Data.ToArray();
                        if (data.Length != 29 || !V2Protocol.Equal(V2Protocol.Part(data, 0, 16), V2WireUuid) || data[16] != 2) continue;
                        item.V2 = true; item.ShortId = V2Protocol.Hex(V2Protocol.Part(data, 17, 2)); item.Profile = V2Protocol.U16(data, 19); item.Capabilities = V2Protocol.U32(data, 21);
                    }
                }
            };
            TypedEventHandler<BluetoothLEAdvertisementWatcher, BluetoothLEAdvertisementWatcherStoppedEventArgs> stopped = delegate(BluetoothLEAdvertisementWatcher sender, BluetoothLEAdvertisementWatcherStoppedEventArgs e) { if (e.Error != BluetoothError.Success) failure.TrySetException(new IOException("BLE扫描失败：" + e.Error)); };
            watcher.Received += received; watcher.Stopped += stopped;
            try
            {
                watcher.Start(); Task delay = Task.Delay(milliseconds, token); Task done = await Task.WhenAny(delay, failure.Task); if (done == failure.Task) await failure.Task; else await delay;
                lock (gate) return observations.Values.Where(i => i.V2).OrderBy(i => i.Address).ToList();
            }
            finally { lock (gate) scanning = false; watcher.Stop(); watcher.Received -= received; watcher.Stopped -= stopped; }
        }
        public static async Task<V2Protocol.Info> ReadInfo(ulong address, CancellationToken token)
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
            {
                timeout.CancelAfter(4000);
                using (var device = await BluetoothLEDevice.FromBluetoothAddressAsync(address).AsTask(timeout.Token))
                {
                    if (device == null) throw new IOException("无法读取候选开发板信息；未确认其是否在线");
                    // Some Windows Bluetooth stacks reject filtered service discovery with
                    // ERROR_BAD_COMMAND (0x80070016). Enumerate uncached, then select locally.
                    var services = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached).AsTask(timeout.Token);
                    try
                    {
                        var matches = services.Services.Where(s => s.Uuid == V2Service).ToList();
                        if (services.Status != GattCommunicationStatus.Success || matches.Count != 1) throw new IOException("开发板服务读取未完成（" + services.Status + "），请检查蓝牙与供电");
                        var chars = await matches[0].GetCharacteristicsAsync(BluetoothCacheMode.Uncached).AsTask(timeout.Token);
                        var info = chars.Characteristics.Where(c => c.Uuid == new Guid(V2Protocol.InfoId)).ToList();
                        if (chars.Status != GattCommunicationStatus.Success || info.Count != 1) throw new IOException("开发板信息特征不完整（" + chars.Status + "）");
                        var result = await info[0].ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(timeout.Token); if (result.Status != GattCommunicationStatus.Success) throw new IOException("开发板编号读取未完成（" + result.Status + "）；不会改连其他板"); return new V2Protocol.Info(result.Value.ToArray());
                    }
                    finally { foreach (var service in services.Services) service.Dispose(); }
                }
            }
        }
        public static async Task<List<BoardAdvertisement>> Nearby(BleLink connected, CancellationToken token)
        {
            var found = await Scan(3000, token);
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var slots = new SemaphoreSlim(3))
            {
                deadline.CancelAfter(12000);
                await Task.WhenAll(found.Take(16).Select(async item =>
                {
                    if (connected != null && connected.Connected && connected.Address == item.Address) { item.Info = connected.AuthenticatedInfo; return; }
                    bool entered = false;
                    try { await slots.WaitAsync(deadline.Token); entered = true; item.Info = await ReadInfo(Convert.ToUInt64(item.Address,16), deadline.Token); }
                    catch (OperationCanceledException) { token.ThrowIfCancellationRequested(); }
                    catch (IOException) { }
                    catch (Exception) { token.ThrowIfCancellationRequested(); }
                    finally { if (entered) slots.Release(); }
                }));
            }
            token.ThrowIfCancellationRequested(); return found;
        }
        public static async Task<ulong> Selected(V2Protocol.Binding target, CancellationToken token, Action<string> progress = null)
        {
            if (target == null) throw new IOException("请先选择已绑定开发板");
            var advertisements = await Scan(2500, token);
            var candidates = advertisements.Where(i => i.V2 && i.ShortId == target.DeviceInfo.ShortId).ToList(); if (candidates.Count > 8) throw new CryptographicException("同短号候选过多，已停止自动选择，请通过USB核对");
            if (progress != null) progress(candidates.Count == 0 ? "未发现目标广播，请检查开发板供电和蓝牙距离" : "已发现目标广播，正在读取完整设备编号…");
            var verified = new List<V2Protocol.Candidate>();
            foreach (var c in candidates) { V2Protocol.Info info = await ReadInfo(Convert.ToUInt64(c.Address,16), token); verified.Add(new V2Protocol.Candidate(c.Address, info, c.Rssi)); }
            var selected = V2Protocol.Selected(verified, target.DeviceInfo.Id); if (selected == null) throw new IOException("未发现选定的完整设备编号；不会改连其他板"); return Convert.ToUInt64(selected.Address, 16);
        }
    }
}
