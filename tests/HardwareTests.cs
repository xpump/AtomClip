using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtomClipUsb;

class HardwareTests
{
    static readonly BlockingCollection<Packet> usbPackets = new BlockingCollection<Packet>(), blePackets = new BlockingCollection<Packet>();
    static async Task<Packet> Receive(BlockingCollection<Packet> packets, byte kind, CancellationToken token)
    {
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            timeout.CancelAfter(90000);
            while (true) { var p = await Task.Run(() => packets.Take(timeout.Token)); if (p.Kind == kind) return p; }
        }
    }
    static async Task Heartbeat(ILink link, CancellationToken token)
    {
        try { while (true) { await link.Send(Protocol.Frame(Protocol.Ping,new byte[0]),token); await Task.Delay(2000,token); } }
        catch (OperationCanceledException) { }
    }
    static async Task Bridge(string port, CancellationToken token)
    {
        using(var usb = new UsbLink(port))
        using(var ble = new BleLink())
        using(var stop = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            usb.Received += p => usbPackets.Add(p); ble.Received += p => blePackets.Add(p);
            await ble.Open(await BleLink.Scan(token),token);
            Task uh = Heartbeat(usb,stop.Token), bh = Heartbeat(ble,stop.Token);
            try
            {
                foreach(var packets in new[]{usbPackets,blePackets})
                { Packet status; do { status=await Receive(packets,Protocol.Status,token); } while(status.Payload.Length<2||status.Payload[0]==0||status.Payload[1]==0); }
                byte[] key=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(key);
                string[] values={"中文硬件互通测试", "第一行\r\n第二行 😀🚀\r\nABC 123",new string('中',5461)+"a"};
                for(int i=0;i<values.Length;i++)foreach(bool fromUsb in new[]{true,false})
                {
                    ILink sender=fromUsb?(ILink)usb:ble,receiver=fromUsb?(ILink)ble:usb;
                    var destination=fromUsb?blePackets:usbPackets;var origin=fromUsb?usbPackets:blePackets;
                    var watch=Stopwatch.StartNew();byte[] payload=Protocol.Encrypt(values[i],key);
                    await sender.Send(Protocol.Frame(Protocol.Data,payload),token);
                    var received=await Receive(destination,Protocol.Data,token);
                    if(Protocol.Decrypt(received.Payload,key)!=values[i])throw new Exception("Bridge text mismatch");
                    await receiver.Send(Protocol.Frame(Protocol.Ack,Protocol.MakeAck(Protocol.Slice(payload,0,16),key)),token);
                    var ack=await Receive(origin,Protocol.Ack,token);
                    if(Protocol.ReadAck(ack.Payload,key)!=Protocol.Id(payload))throw new Exception("Bridge ACK mismatch");
                    Console.WriteLine("PASS bridge direction="+(fromUsb?"USB_to_BLE":"BLE_to_USB")+" utf8_bytes="+Protocol.Utf8.GetByteCount(values[i])+" seconds="+watch.Elapsed.TotalSeconds.ToString("F3"));
                }
                Array.Clear(key,0,key.Length);
            }
            finally { stop.Cancel(); Task.WhenAll(uh,bh).GetAwaiter().GetResult(); }
        }
    }
    static string ClipboardText()
    {
        string text=null;Exception error=null;
        var thread=new Thread(()=>{try{text=Clipboard.ContainsText(TextDataFormat.UnicodeText)?Clipboard.GetText(TextDataFormat.UnicodeText):"";}catch(Exception ex){error=ex;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();
        if(error!=null)return null;return text;
    }
    static void SetClipboardText(string value)
    {
        Exception error=null;var thread=new Thread(()=>{try{Clipboard.SetText(value,TextDataFormat.UnicodeText);}catch(Exception ex){error=ex;}});
        thread.SetApartmentState(ApartmentState.STA);thread.Start();thread.Join();if(error!=null)throw error;
    }
    static async Task GuiPeer(bool usbPeer,string port,CancellationToken token)
    {
        using(ILink peer=usbPeer?(ILink)new UsbLink(port):new BleLink())
        using(var stop=CancellationTokenSource.CreateLinkedTokenSource(token)) {
            peer.Received+=p=>blePackets.Add(p);
            if(!usbPeer)await ((BleLink)peer).Open(await BleLink.Scan(token),token);
            Task heartbeat=Heartbeat(peer,stop.Token);byte[] key=new byte[32];
            using(var stream=typeof(Protocol).Assembly.GetManifestResourceStream("AtomClip.PairingKey"))stream.Read(key,0,32);
            try {
                Packet status;do{status=await Receive(blePackets,Protocol.Status,token);}while(status.Payload.Length<2||status.Payload[0]==0||status.Payload[1]==0);
                await Task.Delay(2500,token);
                string[] values={"真实界面中文测试 "+Guid.NewGuid().ToString("N"),"第一行\r\n第二行 😀🚀\r\nEnglish 123 "+Guid.NewGuid().ToString("N"),new string('中',5461)+"a"};
                foreach(var value in values) {
                    byte[] payload=Protocol.Encrypt(value,key);await peer.Send(Protocol.Frame(Protocol.Data,payload),token);
                    var ack=await Receive(blePackets,Protocol.Ack,token);
                    if(Protocol.ReadAck(ack.Payload,key)!=Protocol.Id(payload)||ClipboardText()!=value)throw new Exception("GUI receive clipboard or authenticated ACK mismatch");
                    Console.WriteLine("PASS actual_"+(usbPeer?"BLE":"USB")+"_GUI clipboard_receive utf8_bytes="+Protocol.Utf8.GetByteCount(value));
                    string reverse="reverse "+value;
                    if(Protocol.Utf8.GetByteCount(reverse)>16384)reverse=value.Substring(0,value.Length-3)+"rev";
                    SetClipboardText(reverse);var received=await Receive(blePackets,Protocol.Data,token);
                    if(Protocol.Decrypt(received.Payload,key)!=reverse)throw new Exception("GUI clipboard_send mismatch");
                    await peer.Send(Protocol.Frame(Protocol.Ack,Protocol.MakeAck(Protocol.Slice(received.Payload,0,16),key)),token);
                    Console.WriteLine("PASS actual_"+(usbPeer?"BLE":"USB")+"_GUI clipboard_send utf8_bytes="+Protocol.Utf8.GetByteCount(reverse));
                    await Task.Delay(500,token);
                }
            } finally {Array.Clear(key,0,key.Length);stop.Cancel();heartbeat.GetAwaiter().GetResult();}
        }
    }
    static Form pasteWindow; static TextBox pasteTarget;
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window,int command);
    static void PreparePasteTarget()
    {
        var ready=new ManualResetEventSlim();
        var thread=new Thread(()=>{
            pasteWindow=new Form{Text="AtomClip synthetic paste test",Width=600,Height=250};
            pasteTarget=new TextBox{Multiline=true,Dock=DockStyle.Fill};pasteWindow.Controls.Add(pasteTarget);
            pasteWindow.Shown+=delegate{ShowWindow(pasteWindow.Handle,5);pasteWindow.Activate();SetForegroundWindow(pasteWindow.Handle);pasteTarget.Focus();ready.Set();};
            Application.Run(pasteWindow);
        });thread.SetApartmentState(ApartmentState.STA);thread.IsBackground=true;thread.Start();ready.Wait();
        IntPtr handle=(IntPtr)pasteWindow.Invoke(new Func<IntPtr>(()=>pasteWindow.Handle));
        if(GetForegroundWindow()!=handle)throw new Exception("Controlled paste target could not acquire foreground; no typing task sent");
    }
    static async Task Guard(string port,CancellationToken token)
    {
        using(var usb=new UsbLink(port))using(var ble=new BleLink())using(var stop=CancellationTokenSource.CreateLinkedTokenSource(token)) {
            ble.Received+=p=>blePackets.Add(p);await ble.Open(await BleLink.Scan(token),token);
            Task uh=Heartbeat(usb,stop.Token),bh=Heartbeat(ble,stop.Token);
            try {
                Packet status;do{status=await Receive(blePackets,Protocol.Status,token);}while(status.Payload.Length<3||status.Payload[0]!=1);
                byte[] key=new byte[32];using(var stream=typeof(Protocol).Assembly.GetManifestResourceStream("AtomClip.PairingKey"))stream.Read(key,0,32);
                byte[] payload=Protocol.CreateTypeJob("Synthetic guard test",false,4,key);
                await ble.Send(Protocol.Frame(Protocol.TypeJob,payload),token);
                var result=await Receive(blePackets,Protocol.JobResult,token);
                if(Protocol.ReadJobResult(result.Payload,Protocol.Id(payload),key)!=3)throw new Exception("Online USB guard did not reject typing");
                Array.Clear(key,0,key.Length);Console.WriteLine("PASS typing refused while USB program heartbeat is online");
            } finally {stop.Cancel();Task.WhenAll(uh,bh).GetAwaiter().GetResult();}
        }
    }
    static async Task Typing(CancellationToken token, bool diverse, bool paste)
    {
        Packet unused;while(blePackets.TryTake(out unused)){}
        using(var ble=new BleLink())
        using(var stop=CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            ble.Received+=p=>blePackets.Add(p);await ble.Open(await BleLink.Scan(token),token);
            Task heartbeat=Heartbeat(ble,stop.Token);
            byte[] key=new byte[32];
            using(var stream=typeof(Protocol).Assembly.GetManifestResourceStream("AtomClip.PairingKey"))
            {if(stream==null||stream.Read(key,0,32)!=32)throw new Exception("Missing embedded pairing");}
            try
            {
                Packet status;do{status=await Receive(blePackets,Protocol.Status,token);}while(status.Payload.Length<3||status.Payload[0]!=0||status.Payload[2]!=1);
                string[] values={"AtomClip 无程序实机测试\r\n第二行：中文与 emoji 😀🚀\r\nEnglish 123 "+Guid.NewGuid().ToString("N"),new string('中',1000)+"\r\n😀 " +Guid.NewGuid().ToString("N")};
                if(diverse) {
                    var text=new System.Text.StringBuilder();
                    for(int i=0;i<1000;i++)text.Append((char)(0x4e00+(i*7919)%20000));
                    values=new[]{text.ToString()+"\r\n😀 "+Guid.NewGuid().ToString("N")};
                }
                if(paste){values=new[]{values[0]};PreparePasteTarget();}
                foreach(string value in values)
                {
                    byte[] payload=Protocol.CreateTypeJob(value,paste,4,key);string id=Protocol.Id(payload);
                    var watch=Stopwatch.StartNew();await ble.Send(Protocol.Frame(Protocol.TypeJob,payload),token);
                    int result=-1;
                    do{var response=await Receive(blePackets,Protocol.JobResult,token);result=Protocol.ReadJobResult(response.Payload,id,key);}while(result<2);
                    if(result!=2)throw new Exception("Typing task rejected/aborted: "+result);
                    bool matched=false;
                    for(int attempt=0;attempt<20;attempt++){if(ClipboardText()==value){matched=true;break;}await Task.Delay(250,token);}
                    if(!matched)throw new Exception("Keyboard reports completed but actual clipboard did not match; inspect PowerShell and HID timing");
                    if(paste){
                        string actual="";for(int attempt=0;attempt<20;attempt++){
                            actual=(string)pasteTarget.Invoke(new Func<string>(()=>pasteTarget.Text));if(actual==value)break;await Task.Delay(250,token);
                        }
                        if(actual!=value)throw new Exception("Auto-paste target content mismatch");
                        Console.WriteLine("PASS actual_HID_automatic_paste into controlled empty textbox");
                    }
                    Console.WriteLine("PASS actual_HID_PowerShell_clipboard utf8_bytes="+Protocol.Utf8.GetByteCount(value)+" seconds="+watch.Elapsed.TotalSeconds.ToString("F3"));
                }
            }
            finally{if(pasteWindow!=null)pasteWindow.BeginInvoke(new Action(()=>pasteWindow.Close()));Array.Clear(key,0,key.Length);stop.Cancel();heartbeat.GetAwaiter().GetResult();}
        }
    }
    static int Main(string[] args)
    {
        try
        {
            using(var timeout=new CancellationTokenSource(600000))
            {
                if(args[0]=="bridge")Bridge(args[1],timeout.Token).GetAwaiter().GetResult();
                else if(args[0]=="gui-ble-peer")GuiPeer(true,args[1],timeout.Token).GetAwaiter().GetResult();
                else if(args[0]=="gui-usb-peer")GuiPeer(false,null,timeout.Token).GetAwaiter().GetResult();
                else if(args[0]=="guard")Guard(args[1],timeout.Token).GetAwaiter().GetResult();
                else if(args[0]=="typing"||args[0]=="typing-diverse"||args[0]=="typing-paste")Typing(timeout.Token,args[0]=="typing-diverse",args[0]=="typing-paste").GetAwaiter().GetResult();
                else throw new ArgumentException("bridge COMxx | typing");
            }
            Console.WriteLine("PASS real-hardware test. Android phone and Win10 new-version runtime require separate acceptance.");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
    }
}
