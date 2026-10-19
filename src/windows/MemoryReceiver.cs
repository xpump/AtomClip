using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using System.IO.Ports;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Runtime.InteropServices;

namespace AtomClipUsb
{
    // System host; outbound clipboard monitoring is explicitly enabled with B.
    public sealed class MemoryReceiver : IDisposable
    {
        readonly byte[] key;
        readonly Dictionary<string, bool> seen = new Dictionary<string, bool>();
        readonly Queue<string> order = new Queue<string>();
        public bool Paused;
        [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
        public MemoryReceiver(byte[] sharedKey) { key = (byte[])sharedKey.Clone(); }
        public void Accept(byte[] payload, Action<string> write, Action<byte[]> ack)
        {
            string text = Protocol.Decrypt(payload, key), id = Protocol.Id(payload);
            if (!seen.ContainsKey(id))
            {
                if (!Paused) write(text);
                seen.Add(id, !Paused); order.Enqueue(id);
                if (order.Count > 256) seen.Remove(order.Dequeue());
                if (!Paused) Console.WriteLine("Received: " + Protocol.Utf8.GetByteCount(text) + " bytes");
            }
            if (Paused || !seen[id]) return;
            ack(Protocol.Frame(Protocol.Ack, Protocol.MakeAck(Protocol.Slice(payload, 0, 16), key)));
        }
        static void WriteClipboard(string text)
        {
            for (int attempt = 0; ; attempt++)
            {
                try { if (text.Length == 0) Clipboard.Clear(); else Clipboard.SetText(text, TextDataFormat.UnicodeText); return; }
                catch (System.Runtime.InteropServices.ExternalException) { if (attempt >= 12) throw; Thread.Sleep(100); }
            }
        }
        static string ChoosePort(string[] names)
        {
            using (var window = new Form { Text = "选择开发板", Width = 510, Height = 220, Font = new Font("Microsoft YaHei UI",10), BackColor = Color.FromArgb(245,245,247),
                StartPosition = FormStartPosition.CenterScreen })
            {
                var list = new ComboBox { FlatStyle = FlatStyle.Flat, Left = 16, Top = 78, Width = 270, DropDownStyle = ComboBoxStyle.DropDownList };
                list.Items.AddRange(names); if (names.Length == 1) list.SelectedIndex = 0;
                var ok = new Button { Text = "连接", Left = 310, Top = 76, Width = 130, Height = 32, Enabled = list.SelectedIndex >= 0, DialogResult = DialogResult.OK };
                list.SelectedIndexChanged += delegate { ok.Enabled = list.SelectedIndex >= 0; };
                window.Controls.Add(list); window.Controls.Add(ok); window.AcceptButton = ok;
                return window.ShowDialog() == DialogResult.OK && list.SelectedItem != null ? list.SelectedItem.ToString() : null;
            }
        }
        public static void Run(string encodedKey, bool bidirectional = false)
        {
            if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) throw new InvalidOperationException("STA required");
            bool first;
            using (var mutex = new Mutex(true, "Local\\AtomClip.TextBridge", out first))
            {
                if (!first) { Console.WriteLine("Already running"); return; }
                Console.Title = "AtomClip";
                Console.WriteLine("Q 退出；P 暂停；B 切换双向");
                string selected = ChoosePort(SerialPort.GetPortNames()); if (selected == null) return;
                selected = selected.Trim().ToUpperInvariant();
                if (!System.Text.RegularExpressions.Regex.IsMatch(selected, "^COM[0-9]+$")) return;
                byte[] secret = Convert.FromBase64String(encodedKey);
                using (var receiver = new MemoryReceiver(secret))
                {
                    var sender = new ClipboardSender(receiver.key) { Enabled = bidirectional };
                    Array.Clear(secret, 0, secret.Length);
                    for (;;)
                    {
                        try
                        {
                            using (var port = new SerialPort(selected, 115200) { DtrEnable = true, RtsEnable = false,
                                ReadTimeout = 200, WriteTimeout = 1500, ReadBufferSize = 65536 })
                            {
                                port.Open(); Console.WriteLine(selected + " 已打开，等待响应");
                                var parser = new PacketParser(); var nextPing = DateTime.MinValue;
                                bool confirmed = false; var lastStatus = DateTime.UtcNow; var input = new byte[4096];
                                bool peer = false; sender.Reset(GetClipboardSequenceNumber());
                                Action<byte[]> send = b => port.Write(b, 0, b.Length);
                                while (port.IsOpen)
                                {
                                    if (Console.KeyAvailable)
                                    {
                                        var pressed = Console.ReadKey(true).Key;
                                        if (pressed == ConsoleKey.Q) return;
                                        if (pressed == ConsoleKey.P) { receiver.Paused = !receiver.Paused; sender.Reset(GetClipboardSequenceNumber()); Console.WriteLine(receiver.Paused ? "已暂停" : "已恢复，请重新复制"); }
                                        if (pressed == ConsoleKey.B) { sender.Enabled = !sender.Enabled; sender.Reset(GetClipboardSequenceNumber()); Console.WriteLine(sender.Enabled ? "双向已开，请重新复制" : "仅接收"); }
                                    }
                                    if (DateTime.UtcNow >= nextPing) { send(Protocol.Frame(Protocol.Ping, new byte[0])); nextPing = DateTime.UtcNow.AddSeconds(5); }
                                    if (DateTime.UtcNow - lastStatus > TimeSpan.FromSeconds(18)) throw new IOException("Board status timeout");
                                    try { sender.Poll(GetClipboardSequenceNumber(), confirmed && peer && !receiver.Paused,
                                        () => Clipboard.ContainsText(TextDataFormat.UnicodeText) ? Clipboard.GetText(TextDataFormat.UnicodeText) : null, send, DateTime.UtcNow); }
                                    catch (ExternalException) { }
                                    catch (ArgumentException) { sender.Reset(GetClipboardSequenceNumber()); Console.WriteLine("未发送：文字无效或超过 16 KiB。"); }
                                    int count;
                                    try { count = port.Read(input, 0, input.Length); } catch (TimeoutException) { continue; }
                                    foreach (var packet in parser.Feed(input, count))
                                    {
                                        if (packet.Kind == Protocol.Status && packet.Payload.Length >= 2) { lastStatus = DateTime.UtcNow; bool ready = packet.Payload[1] != 0; if (ready != peer) { sender.Reset(GetClipboardSequenceNumber()); Console.WriteLine(ready ? "外网就绪，请重新复制" : "外网离线"); } peer = ready; if (!confirmed) { confirmed = true; Console.WriteLine("开发板就绪。B 切换双向；保持窗口运行。"); } }
                                        else if (packet.Kind == Protocol.Ack) { int before = sender.Confirmed; sender.Acknowledge(packet.Payload); if (sender.Confirmed != before) Console.WriteLine("外网已确认写入，共 " + sender.Confirmed + " 次。"); }
                                        else if (packet.Kind == Protocol.Data)
                                        {
                                            try { receiver.Accept(packet.Payload, text => { WriteClipboard(text); sender.Reset(GetClipboardSequenceNumber()); }, send); }
                                            catch (CryptographicException) { Console.WriteLine("Rejected authentication"); }
                                            catch (DecoderFallbackException) { Console.WriteLine("Rejected UTF-8"); }
                                            catch (ExternalException) { Console.WriteLine("Clipboard busy"); }
                                        }
                                    }
                                }
                            }
                        }
                        catch (Exception ex) { Console.WriteLine("Reconnect " + selected + " in 5s: " + ex.GetType().Name + " - " + ex.Message); Thread.Sleep(5000); }
                    }
                }
            }
        }
        public void Dispose() { Array.Clear(key, 0, key.Length); }
    }
}
