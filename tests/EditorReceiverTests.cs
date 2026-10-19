using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtomClipUsb;

public class EditorReceiverTests
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static FieldInfo Field(object o, string name) { return o.GetType().GetField(name, Hidden); }
    static object Get(object o, string name) { return Field(o, name).GetValue(o); }
    static void Set(object o, string name, object value) { Field(o, name).SetValue(o, value); }
    static object Call(object o, string name, params object[] args) { return o.GetType().GetMethod(name, Hidden).Invoke(o, args); }
    static void Require(bool ok, string name) { if (!ok) throw new Exception(name); }
    static void Pump(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.ElapsedMilliseconds < 4000) { Application.DoEvents(); Thread.Sleep(5); }
        Require(task.IsCompleted, "operation completed without timeout"); task.GetAwaiter().GetResult();
    }
    static Form NewForm(byte[] key, string folder)
    {
        var assembly = typeof(Protocol).Assembly;
        var form = (Form)Activator.CreateInstance(assembly.GetType("AtomClipUsb.MainForm"),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new object[] { folder, new FileLog(Path.Combine(folder, "Logs")), key }, null);
        ((System.Windows.Forms.Timer)Get(form, "timer")).Stop();
        return form;
    }
    sealed class TestLink : ILink
    {
        public event Action<Packet> Received { add { } remove { } }
        public bool Connected { get { return true; } }
        public Form Owner; public byte[] Key; public string LastText;
        public bool Tamper, Hold; public int DataCount, AckCount;
        public TaskCompletionSource<bool> Gate = new TaskCompletionSource<bool>();
        public Task Send(byte[] frame, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            foreach (var p in new PacketParser().Feed(frame, frame.Length))
            {
                if (p.Kind == Protocol.Data)
                {
                    LastText = Protocol.Decrypt(p.Payload, Key); DataCount++;
                    byte[] ack = Protocol.MakeAck(Protocol.Slice(p.Payload, 0, 16), Key);
                    if (Tamper) ack[31] ^= 1;
                    string id = Protocol.ReadAck(ack, Key);
                    TaskCompletionSource<bool> pending;
                    var waiting = (ConcurrentDictionary<string, TaskCompletionSource<bool>>)Get(Owner, "waiting");
                    if (id != null && waiting.TryGetValue(id, out pending)) pending.TrySetResult(true);
                }
                else if (p.Kind == Protocol.Ack)
                { Require(Protocol.ReadAck(p.Payload, Key) != null, "receiver authenticated ACK"); AckCount++; }
            }
            return Hold ? Gate.Task : Task.FromResult(true);
        }
        public void Dispose() { }
    }
    static TestLink Attach(Form form, byte[] key)
    {
        var link = new TestLink { Owner = form, Key = key };
        Set(form, "link", link); Set(form, "connectionCts", new CancellationTokenSource()); Set(form, "peerReady", true);
        Set(form, "lastStatus", DateTime.UtcNow); Set(form, "nextPing", DateTime.UtcNow.AddMinutes(5));
        return link;
    }
    public static void RunEditor()
    {
        var key = new byte[32]; for (int i = 0; i < key.Length; i++) key[i] = (byte)i;
        string folder = Path.Combine(Path.GetTempPath(), "AtomClip-editor-test-" + Guid.NewGuid().ToString("N"));
        using (var form = NewForm(key, folder))
        {
            var link = Attach(form, key); var editor = (TextBox)Get(form, "editor");
            var button = (Button)Get(form, "sendEditor");
            Require(((RadioButton)Get(form, "modeNoProgram")).Checked, "file-free mode selected by default");
            editor.Text = "切换模式保留草稿";
            ((RadioButton)Get(form, "modeProgram")).Checked = true;
            ((CheckBox)Get(form, "autoClipboard")).Checked = true;
            Set(form, "pendingText", "queued automatic text");
            ((RadioButton)Get(form, "modeNoProgram")).Checked = true;
            Require(!((CheckBox)Get(form, "autoClipboard")).Checked && Get(form, "pendingText") == null,
                "file-free mode stops automatic sending and discards queued snapshots");
            Require(editor.Text == "切换模式保留草稿", "switching modes retains editor draft");
            editor.Clear();
            string bootstrap = (string)Call(form, "ReadReceiverBootstrap");
            Require(bootstrap.Contains(Convert.ToBase64String(key)),"bootstrap contains active runtime key");
            byte[] changed=(byte[])key.Clone();changed[0]^=128;Buffer.BlockCopy(changed,0,(byte[])Get(form,"key"),0,32);string substituted=(string)Call(form,"ReadReceiverBootstrap");Require(substituted.Contains(Convert.ToBase64String(changed))&&substituted.Length==bootstrap.Length,"runtime key substitution keeps exact bootstrap length");Buffer.BlockCopy(key,0,(byte[])Get(form,"key"),0,32);
            Require(!bootstrap.Contains("$PSScriptRoot") && !bootstrap.Contains("$RepoRoot"), "pasted receiver has no build-script directory dependency");
            Require(bootstrap.Contains("\r\n") && !bootstrap.Replace("\r\n", "").Contains("\n"), "bootstrap uses only Windows CRLF for classic-console paste");
            Require(bootstrap.EndsWith("\r\n\r\n"), "native console multiline bootstrap ends with a blank line");
            Require(Protocol.Utf8.GetByteCount(bootstrap) <= Protocol.MaxText && bootstrap.Contains("[AtomClipUsb.MemoryReceiver]::Run("), "fixed in-memory initialization resource fits firmware limit");
            byte[] job = Protocol.CreateTypeJob(bootstrap, false, 4, key);
            var transform = typeof(Protocol).Assembly.GetType("AtomClipUsb.WindowsGcm").GetMethod("Transform", BindingFlags.Public | BindingFlags.Static);
            byte[] openJob = Protocol.CreateReceiverOpenJob(key);
            byte[] openPlain = (byte[])transform.Invoke(null, new object[] { false, key, Protocol.Slice(openJob,16,12), Protocol.Slice(openJob,0,16), Protocol.Slice(openJob,28,openJob.Length-44), Protocol.Slice(openJob,openJob.Length-16,16) });
            Require(openPlain.Length == 3 && openPlain[0] == 2 && openPlain[1] == 0 && openPlain[2] == 4, "authenticated open-host request contains only fixed opcode, no script or auto-paste");
            Call(form,"UpdateEditorActions");
            Require(!((Button)Get(form,"openReceiver")).Enabled, "old firmware cannot start fixed-host job");
            Set(form,"supportsReceiverStart",true); Set(form,"peerReady",false); Call(form,"UpdateEditorActions");
            Require(((Button)Get(form,"openReceiver")).Enabled, "new firmware enables fixed-host button");
            Set(form,"peerReady",true);
            byte[] plain = (byte[])transform.Invoke(null, new object[] { false, key, Protocol.Slice(job,16,12), Protocol.Slice(job,0,16), Protocol.Slice(job,28,job.Length-44), Protocol.Slice(job,job.Length-16,16) });
            Require(plain[0] == 1 && plain[1] == 0, "initializer is data-only typing without auto-paste");
            using (var compressed = new MemoryStream(plain,3,plain.Length-3))
            using (var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionMode.Decompress))
            using (var reader = new StreamReader(gzip,Protocol.Utf8)) Require(reader.ReadToEnd() == bootstrap, "complete fixed initialization text survives encrypted typing transport");
            Array.Clear(plain,0,plain.Length);
            Set(form,"supportsTyping",true); Set(form,"peerReady",false); Call(form,"UpdateEditorActions");
            Require(((Label)Get(form,"state")).Text.Contains("请先设置内网接收"), "file-free mode explains missing receiver");
            Require(((Button)Get(form,"prepareReceiver")).Enabled, "initialization enabled without an editor draft");
            Set(form,"peerReady",true); Call(form,"UpdateEditorActions");
            Require(!((Button)Get(form,"prepareReceiver")).Enabled, "initialization disabled once USB receiver is online");
            Require(!((CheckBox)Get(form, "autoClipboard")).Checked, "manual-only send by default");
            foreach (string text in new[] { "中文\r\n第二行 😀🚀", new string('a', 16384) })
            {
                editor.Text = text; Set(form, "pendingText", "older clipboard snapshot");
                Pump((Task)Call(form, "SendEditorText"));
                Require(link.LastText == text, "editor text arrives intact in encrypted protocol");
                Require(editor.Text == text, "draft retained after successful send");
                Require(Get(form, "pendingText") == null, "older automatic snapshot discarded");
                Require(((Label)Get(form, "editorResult")).Text.Contains("写入确认"), "authenticated success displayed");
            }
            int sent = link.DataCount;
            foreach (string text in new[] { "", new string('中', 5462) })
            { editor.Text = text; Pump((Task)Call(form, "SendEditorText")); Require(!button.Enabled && link.DataCount == sent, "empty/oversize disabled"); }
            editor.Text = "暂停时不得发送"; Set(form, "paused", true);
            Pump((Task)Call(form, "SendEditorText")); Require(link.DataCount == sent, "paused send suppressed");
            Set(form, "paused", false); Set(form, "peerReady", false);
            Pump((Task)Call(form, "SendEditorText")); Require(link.DataCount == sent, "offline send suppressed");
            Set(form, "peerReady", true); link.Hold = true; editor.Text = "single in-flight send";
            Task held = (Task)Call(form, "SendEditorText");
            Require(!button.Enabled, "button disabled during send");
            Pump((Task)Call(form, "SendEditorText")); Require(link.DataCount == sent + 1, "double send prevented");
            link.Gate.SetResult(true); Pump(held); link.Hold = false;
            link.Tamper = true; editor.Text = "forged ACK must not succeed";
            var canceled = (Task)Call(form, "SendEditorText");
            Require(!canceled.IsCompleted, "forged ACK does not complete send");
            Call(form, "ResetLink"); Pump(canceled);
            Require(editor.Text == "forged ACK must not succeed", "draft retained on disconnect");
            Require(!((Label)Get(form, "editorResult")).Text.Contains("写入确认"), "disconnect does not claim success");
            string logs = string.Join("\n", Array.ConvertAll(Directory.GetFiles(Path.Combine(folder, "Logs")), File.ReadAllText));
            Require(!logs.Contains(editor.Text) && !logs.Contains("第二行"), "no editor body in logs");
        }
        Console.WriteLine("PASS: embedded memory initialization size/encrypted gzip transport/no auto-paste/button gating; editor Chinese/multiline/emoji/16KiB; limits; pause/offline; concurrency; ACK authentication; draft retention; no content logs.");
    }
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    public static void RunReceiver()
    {
        // Materialize every existing format before touching the test machine's clipboard.
        IDataObject original = Clipboard.GetDataObject(); var saved = new DataObject();
        if (original != null) foreach (string format in original.GetFormats(false)) saved.SetData(format, false, original.GetData(format, false));
        var key = new byte[32]; for (int i = 0; i < key.Length; i++) key[i] = (byte)i;
        string folder = Path.Combine(Path.GetTempPath(), "AtomClip-ps-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var form = NewForm(key, folder))
            {
                var link = Attach(form, key);
                Require((bool)Get(form, "receiveOnly"), "PowerShell receiver is receive-only");
                foreach (string text in new[] { "中文\r\n第二行 😀🚀", new string('a', 16384) })
                {
                    byte[] payload = Protocol.Encrypt(text, key); Call(form, "ReceiveData", payload);
                    Require(Clipboard.GetText(TextDataFormat.UnicodeText) == text, "PowerShell hosted clipboard exact receive");
                    uint seq = GetClipboardSequenceNumber(); int acks = link.AckCount;
                    Call(form, "ReceiveData", payload);
                    Require(link.AckCount == acks + 1 && seq == GetClipboardSequenceNumber(), "duplicate ACK without rewriting clipboard");
                }
                string before = Clipboard.GetText(TextDataFormat.UnicodeText);
                byte[] bad = Protocol.Encrypt("damaged content", key); bad[bad.Length - 1] ^= 1;
                Call(form, "ReceiveData", bad); Require(Clipboard.GetText() == before, "bad authentication rejected");
                Call(form, "TogglePause"); Call(form, "ReceiveData", Protocol.Encrypt("ignored while paused", key));
                Require(Clipboard.GetText() == before, "paused receiver does not write");
                Call(form, "TogglePause");
                Clipboard.SetText("local-only synthetic data"); Pump((Task)Call(form, "Tick"));
                Require(link.DataCount == 0, "receiver never sends local clipboard");
            }
        }
        finally { if (original == null) Clipboard.Clear(); else Clipboard.SetDataObject(saved, true); }
        Console.WriteLine("PASS: real Windows PowerShell5 Add-Type host; clipboard Chinese/multiline/emoji/16KiB; authenticated ACK; deduplication; pause; no outbound clipboard. Original clipboard restored.");
    }
    static LinkLabel FindLink(Control parent, string text)
    {
        foreach (Control child in parent.Controls)
        {
            var link = child as LinkLabel;
            if (link != null && link.Text == text) return link;
            link = FindLink(child, text); if (link != null) return link;
        }
        return null;
    }
    static void ActivateLink(Control parent, string text)
    {
        var link = FindLink(parent, text); Require(link != null, "navigation link exists");
        typeof(LinkLabel).GetMethod("OnLinkClicked", Hidden).Invoke(link, new object[] { new LinkLabelLinkClickedEventArgs(link.Links[0]) });
    }
    static void RunAuxiliaryWindows()
    {
        string folder = Path.Combine(Path.GetTempPath(), "AtomClip-window-test-" + Guid.NewGuid().ToString("N"));
        using (var form = NewForm(new byte[32], folder))
        {
            // Never show the main form: its Shown event starts real BLE scanning.
            Call(form, "OpenSetup"); var setup = (Form)Get(form, "setupWindow");
            Call(form, "OpenSetup");
            Require(setup.Visible && !setup.Modal, "reopening visible setup is modeless and safe");
            for (int i = 0; i < 3; i++)
            {
                ActivateLink(setup, "已完成写入？继续第 2 步");
                Require(((Control)Get(form, "setupSecond")).Visible && !((Control)Get(form, "setupFirst")).Visible, "advance setup while already visible");
                ActivateLink(setup, "返回第 1 步，重新写入");
                Require(((Control)Get(form, "setupFirst")).Visible && !((Control)Get(form, "setupSecond")).Visible, "return setup while already visible");
            }
            setup.Close(); Require(!setup.Visible && !setup.IsDisposed, "setup close hides for reuse");
            Call(form,"SetSetupStep",3); Require(((List<Control>)Get(form,"setupPages"))[2].Visible, "paste-only third step visible");
            Call(form,"SetSetupStep",4); Require(((List<Control>)Get(form,"setupPages"))[3].Visible, "port selection fourth step visible");
            Call(form, "OpenSetup"); Require(setup.Visible && ReferenceEquals(setup, Get(form, "setupWindow")), "hidden setup reopens with same controls");
            setup.WindowState = FormWindowState.Minimized; Call(form, "OpenSetup");
            Require(setup.WindowState == FormWindowState.Normal, "setup restores from minimized");
            Call(form, "OpenLogWindow"); var logs = (Form)Get(form, "logWindow");
            Call(form, "OpenLogWindow"); Require(logs.Visible && !logs.Modal, "visible logs reopen safely");
            logs.Close(); Require(!logs.Visible && !logs.IsDisposed, "log close hides for reuse");
            Call(form, "OpenLogWindow"); Require(logs.Visible && ReferenceEquals(logs, Get(form, "logWindow")), "hidden logs reopen with same content");
            logs.WindowState = FormWindowState.Minimized; Call(form, "OpenLogWindow");
            Require(logs.WindowState == FormWindowState.Normal, "logs restore from minimized");
            Set(form, "exiting", true); form.Close();
            Require(setup.IsDisposed && logs.IsDisposed, "application exit disposes auxiliary windows");
        }
        Console.WriteLine("PASS: visible setup/log reopen; repeated setup forward/back; close/hide/reopen; minimize/restore; exit disposal. Main window never shown; no BLE/USB access or clipboard transfer.");
    }
    [STAThread] public static int Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
        try { RunAuxiliaryWindows(); if (args.Length == 0) RunEditor(); return 0; }
        catch (Exception ex) { ex = ex.GetBaseException(); Console.Error.WriteLine("FAIL: " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); return 1; }
    }
}
