using System;
using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtomClipUsb;

class UsbClipboardTests
{
    const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    static object Get(object o,string n) { return o.GetType().GetField(n,Hidden).GetValue(o); }
    static void Set(object o,string n,object v) { o.GetType().GetField(n,Hidden).SetValue(o,v); }
    static object Call(object o,string n,params object[] args) { return o.GetType().GetMethod(n,Hidden).Invoke(o,args); }
    static void Require(bool ok,string label) { if(!ok)throw new Exception(label); }
    [DllImport("user32.dll")] static extern uint GetClipboardSequenceNumber();
    sealed class Remote : ILink
    {
        public event Action<Packet> Received { add {} remove {} }
        public bool Connected { get {return true;} }
        public Form Owner; public byte[] Key; public int Count; public string Text;
        public Task Send(byte[] frame,CancellationToken token)
        {
            foreach(var p in new PacketParser().Feed(frame,frame.Length)) if(p.Kind==Protocol.Data) {
                Text=Protocol.Decrypt(p.Payload,Key);Count++;
                var pending=(ConcurrentDictionary<string,TaskCompletionSource<bool>>)Get(Owner,"waiting");
                string id=Protocol.ReadAck(Protocol.MakeAck(Protocol.Slice(p.Payload,0,16),Key),Key);
                pending[id].TrySetResult(true);
            }
            return Task.FromResult(true);
        }
        public void Dispose() {}
    }
    [STAThread] static int Main()
    {
        var original=Clipboard.GetDataObject();var saved=new DataObject();
        if(original!=null)foreach(string format in original.GetFormats(false))saved.SetData(format,false,original.GetData(format,false));
        try {
            var key=new byte[32];string folder=Path.Combine(Path.GetTempPath(),"AtomClip-USB-test-"+Guid.NewGuid().ToString("N"));
            string[] resources=typeof(Protocol).Assembly.GetManifestResourceNames();
            Require(resources.Length==1&&resources[0]=="AtomClip.PairingKey","USB carries only pairing resource, no duplicated artwork or external tools");
            Require(new FileInfo(typeof(Protocol).Assembly.Location).Length<=65536,"USB stays within 64 KiB");
            byte[] wire=Protocol.Frame(Protocol.Data,Protocol.Encrypt(new string('a',16384),key));
            int offset=0,packets=0,reads=0;
            typeof(UsbLink).GetMethod("Drain",BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,new object[]{new PacketParser(),
                new Func<int>(()=>wire.Length-offset),new Func<byte[],int>(buffer=>{int n=Math.Min(7,Math.Min(buffer.Length,wire.Length-offset));Array.Copy(wire,offset,buffer,0,n);offset+=n;reads++;return n;}),
                new Action<Packet>(packet=>{Require(Protocol.Decrypt(packet.Payload,key).Length==16384,"short-read maximum frame intact");packets++;})});
            Require(offset==wire.Length&&reads>1&&packets==1,"one serial event drains every short read exactly once");
            var type=typeof(Protocol).Assembly.GetType("AtomClipUsb.MainForm");
            using(var form=(Form)Activator.CreateInstance(type,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance,null,new object[]{folder,new FileLog(Path.Combine(folder,"logs")),key},null)) {
                ((System.Windows.Forms.Timer)Get(form,"timer")).Stop();Set(form,"receiveOnly",false);
                var remote=new Remote {Owner=form,Key=key};Set(form,"link",remote);Set(form,"peerReady",true);
                Set(form,"connectionCts",new CancellationTokenSource());Set(form,"lastStatus",DateTime.UtcNow);Set(form,"nextPing",DateTime.UtcNow.AddHours(1));
                foreach(string value in new[]{"内网自动回传\r\n第二行 😀",new string('中',5461)+"a"}) {
                    Clipboard.SetText(value);((Task)Call(form,"Tick")).GetAwaiter().GetResult();
                    Require(remote.Text==value,"USB main form sends actual new local clipboard");
                }
                int count=remote.Count;
                Call(form,"ReceiveData",Protocol.Encrypt("outside to inside",key));
                ((Task)Call(form,"Tick")).GetAwaiter().GetResult();Require(remote.Count==count,"received text not echoed");
                Set(form,"paused",true);Clipboard.SetText("paused");((Task)Call(form,"Tick")).GetAwaiter().GetResult();Require(remote.Count==count,"paused copy suppressed");
                Set(form,"paused",false);Set(form,"peerReady",false);Clipboard.SetText("copy before connection");((Task)Call(form,"Tick")).GetAwaiter().GetResult();Require(remote.Count==count,"offline copy suppressed");
                Set(form,"peerReady",true);typeof(Button).GetMethod("OnClick",Hidden).Invoke(Get(form,"resendClipboard"),new object[]{EventArgs.Empty});Require(remote.Text=="copy before connection","manual resend recovers copy made before peer readiness");
                Set(form,"exiting",true);
            }
            Console.WriteLine("PASS: serial short-read regression (7-byte reads, one notification, 16KiB frame); actual USB main form clipboard -> authenticated outbound; Chinese/multiline/emoji/16KiB; no echo; paused/offline suppression; manual resend. Simulated remote; no serial/BLE hardware.");return 0;
        } catch(Exception ex) {Console.Error.WriteLine(ex.GetBaseException());return 1;}
        finally {if(original==null)Clipboard.Clear();else Clipboard.SetDataObject(saved,true);}
    }
}
