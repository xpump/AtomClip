using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Windows.Forms;
using AtomClipUsb;

public static class MemoryReceiverTests
{
    static void Require(bool ok, string name) { if (!ok) throw new Exception(name); }
    public static void Run(string peerAssembly)
    {
        var choose = typeof(MemoryReceiver).GetMethod("ChoosePort", BindingFlags.NonPublic | BindingFlags.Static);
        foreach (bool select in new[] { false, true })
        using (var timer = new System.Windows.Forms.Timer { Interval = 50 })
        {
            timer.Tick += delegate {
                foreach (Form window in Application.OpenForms) if (window.Text == "选择开发板") {
                    timer.Stop();
                    if (select) foreach (Control child in window.Controls) { var list = child as ComboBox; if (list != null) list.SelectedIndex = 1; }
                    window.DialogResult = select ? DialogResult.OK : DialogResult.Cancel;
                    break;
                }
            };
            timer.Start();
            string selected = (string)choose.Invoke(null,new object[]{new string[]{"COM900","COM901"}});
            Require(select ? selected == "COM901" : selected == null,"port picker select/cancel without typed commands");
        }
        Console.WriteLine("PASS: actual port picker select/cancel; no serial port opened.");
        var peer = Assembly.LoadFrom(peerAssembly).GetType("AtomClipUsb.Protocol");
        var encrypt = peer.GetMethod("Encrypt"); var encryptBytes = peer.GetMethod("EncryptBytes");
        var readAck = peer.GetMethod("ReadAck"); var key = new byte[32];
        for (int i = 0; i < key.Length; i++) key[i] = (byte)i;
        RunSender(key);
        Func<string, byte[]> payload = text => (byte[])encrypt.Invoke(null, new object[] { text, key });
        IDataObject original = Clipboard.GetDataObject(); var saved = new DataObject();
        if (original != null) foreach (string format in original.GetFormats(false)) saved.SetData(format, false, original.GetData(format, false));
        var writeClipboard = typeof(MemoryReceiver).GetMethod("WriteClipboard", BindingFlags.NonPublic | BindingFlags.Static);
        int writes = 0, acks = 0;
        Action<string> write = text => { writeClipboard.Invoke(null, new object[] { text }); writes++; };
        Action<byte[]> ack = frame => {
            var packets = new PacketParser().Feed(frame, frame.Length);
            Require(packets.Count == 1 && packets[0].Kind == Protocol.Ack, "framed ACK");
            Require(readAck.Invoke(null, new object[] { packets[0].Payload, key }) != null, "ACK accepted by real Windows protocol"); acks++;
        };
        try
        {
            using (var receiver = new MemoryReceiver(key))
            {
                foreach (string text in new[] { "中文\r\n第二行 😀🚀", new string('a', 16384), "" })
                {
                    byte[] data = payload(text); int beforeWrites = writes;
                    receiver.Accept(data, write, ack);
                    Require(Clipboard.GetText(TextDataFormat.UnicodeText) == text, "exact system clipboard");
                    receiver.Accept(data, write, ack); Require(writes == beforeWrites + 1, "deduplicated clipboard write");
                }
                int before = writes, ackBefore = acks;
                byte[] paused = payload("ignore paused content"); receiver.Paused = true;
                receiver.Accept(paused, write, ack); receiver.Paused = false; receiver.Accept(paused, write, ack);
                Require(writes == before && acks == ackBefore, "paused content not replayed after resume");
                byte[] retry = payload("retry after clipboard busy");
                try { receiver.Accept(retry, text => { throw new InvalidOperationException("busy"); }, ack); throw new Exception("write error swallowed"); }
                catch (InvalidOperationException) { }
                Require(acks == ackBefore, "no ACK before successful clipboard write");
                receiver.Accept(retry, write, ack); Require(writes == before + 1 && acks == ackBefore + 1, "retry succeeds after failed clipboard write");
                foreach (byte[] invalid in new[] {
                    (byte[])encryptBytes.Invoke(null,new object[]{new byte[]{0xc0,0xaf},key}),
                    (byte[])encryptBytes.Invoke(null,new object[]{new byte[]{0},key}),
                    (byte[])encryptBytes.Invoke(null,new object[]{new byte[16385],key}) })
                {
                    bool rejected = false;
                    try { receiver.Accept(invalid, write, ack); }
                    catch (System.Text.DecoderFallbackException) { rejected = true; }
                    catch (CryptographicException) { rejected = true; }
                    Require(rejected, "invalid UTF8/NUL/length rejection");
                }
                byte[] damaged = payload("authenticated only"); damaged[damaged.Length - 1] ^= 1;
                try { receiver.Accept(damaged, write, ack); throw new Exception("bad authentication accepted"); }
                catch (CryptographicException) { }
                Require(writes == before + 1 && acks == ackBefore + 1, "rejected data never written or acknowledged");
            }
        }
        finally { if (original == null) Clipboard.Clear(); else Clipboard.SetDataObject(saved, true); }
        Console.WriteLine("PASS: exact embedded receiver source under Windows PowerShell5 Add-Type; actual clipboard Chinese/multiline/emoji/16KiB/empty; authenticated ACK wire compatibility; dedup; paused replay suppression; write-before-ACK; damaged/invalid rejection. Original clipboard restored. No physical transport tested.");
    }
    static void RunSender(byte[] key)
    {
        var sender = new ClipboardSender(key); var now = DateTime.UtcNow;
        int count = 0; byte[] last = null;
        Action<byte[]> send = b => { last = b; count++; };
        sender.Reset(1); sender.Poll(2, true, () => "not enabled", send, now);
        Require(count == 0, "outbound requires explicit enable");
        sender.Enabled = true; sender.Reset(2);
        try { sender.Poll(3, true, () => { throw new System.Runtime.InteropServices.ExternalException(); }, send, now); throw new Exception("read error swallowed"); }
        catch (System.Runtime.InteropServices.ExternalException) { }
        string value = "内网向外网\r\n😀";
        sender.Poll(3, true, () => value, send, now);
        Require(count == 1, "clipboard busy retries same sequence");
        var packet = new PacketParser().Feed(last, last.Length)[0];
        Require(packet.Kind == Protocol.Data && Protocol.Decrypt(packet.Payload, key) == value, "outbound Unicode encrypted frame");
        sender.Poll(3, true, () => null, send, now.AddSeconds(11)); Require(count == 1, "nonblocking ACK wait");
        sender.Poll(3, true, () => null, send, now.AddSeconds(12)); Require(count == 2, "bounded retransmit");
        byte[] ack = Protocol.MakeAck(Protocol.Slice(packet.Payload,0,16), key);
        ack[31] ^= 1; sender.Acknowledge(ack); Require(sender.Confirmed == 0, "forged outbound ACK rejected");
        ack[31] ^= 1; sender.Acknowledge(ack); Require(sender.Confirmed == 1, "authenticated outbound ACK");
        sender.Poll(3, true, () => null, send, now.AddSeconds(25)); Require(count == 2, "confirmed data not resent");
        sender.Reset(4); sender.Poll(4, true, () => "echo", send, now); Require(count == 2, "received clipboard never echoed");
        sender.Poll(5, false, () => "offline", send, now); sender.Poll(5, true, () => "offline", send, now); Require(count == 2, "offline clipboard not replayed");
        sender.Poll(6, true, () => new string('a',16384), send, now); Require(count == 3, "16KiB outbound accepted");
        for (int i=1;i<=3;i++) sender.Poll(6,true,()=>null,send,now.AddSeconds(i*12));
        Require(count == 5, "three-attempt limit");
        sender.Enabled = false; sender.Poll(7,true,()=>"disabled",send,now); Require(count == 5,"disable cancels outbound");
        Console.WriteLine("PASS: system host outbound enable/busy retry/Unicode/16KiB/encrypted frame/authenticated ACK/nonblocking retry/echo suppression/offline and disable handling.");
    }
}
