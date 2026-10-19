using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtomClipUsb;

class EditorRenderingTests
{
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static object Get(object o,string n){return o.GetType().GetField(n,Hidden).GetValue(o);}
    static void Set(object o,string n,object v){o.GetType().GetField(n,Hidden).SetValue(o,v);}
    static object Call(object o,string n){return o.GetType().GetMethod(n,Hidden).Invoke(o,null);}
    static void Require(bool ok,string name){if(!ok)throw new Exception(name);}
    [DllImport("user32.dll",EntryPoint="GetWindowLongW")]static extern int GetWindowLong(IntPtr window,int index);
    sealed class Link:ILink {
        public Form Owner;
        public event Action<Packet> Received {add{} remove{}}
        public bool Connected {get{return true;}}
        public Task Send(byte[] frame,CancellationToken token){
            foreach(var p in new PacketParser().Feed(frame,frame.Length))if(p.Kind==Protocol.Data){
                byte[] key=new byte[32];string id=Protocol.ReadAck(Protocol.MakeAck(Protocol.Slice(p.Payload,0,16),key),key);
                TaskCompletionSource<bool> ack;
                if(((ConcurrentDictionary<string,TaskCompletionSource<bool>>)Get(Owner,"waiting")).TryGetValue(id,out ack))ack.SetResult(true);
            }
            return Task.FromResult(true);
        }
        public void Dispose(){}
    }
    sealed class Probe:NativeWindow,IDisposable {
        public int Erases,Paints,Moves,Resizes;
        public Probe(Control c){AssignHandle(c.Handle);}
        protected override void WndProc(ref Message m){if(m.Msg==0x14)Erases++;if(m.Msg==0xf)Paints++;if(m.Msg==0x47)Moves++;if(m.Msg==5)Resizes++;base.WndProc(ref m);}
        public void Clear(){Erases=Paints=Moves=Resizes=0;}
        public void Dispose(){ReleaseHandle();}
        public override string ToString(){return "paint="+Paints+" erase="+Erases+" position="+Moves+" size="+Resizes;}
    }
    [STAThread] static int Main(string[] args){
        try {
            var type=typeof(Protocol).Assembly.GetType("AtomClipUsb.MainForm");
            string folder=Path.Combine(Path.GetTempPath(),"AtomClip-render-"+Guid.NewGuid().ToString("N"));
            using(var form=(Form)Activator.CreateInstance(type,Hidden|BindingFlags.Public,null,new object[]{folder,new FileLog(folder),new byte[32]},null)) {
                ((System.Windows.Forms.Timer)Get(form,"timer")).Stop();
                var events=(EventHandlerList)typeof(Control).GetProperty("Events",Hidden).GetValue(form,null);
                var shown=typeof(Form).GetField("EVENT_SHOWN",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);events.RemoveHandler(shown,events[shown]);
                form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=args.Length>0?new Point(40,40):new Point(-30000,-30000);form.Size=new Size(680,800);form.Show();Application.DoEvents();
                ((RadioButton)Get(form,"subPowerShell")).Checked=true;
                Set(form,"link",new Link{Owner=form});Set(form,"connectionCts",new CancellationTokenSource());Set(form,"nextPing",DateTime.UtcNow.AddHours(1));
                var queue=(ConcurrentQueue<Packet>)Get(form,"incoming");byte[] ready={1,0,1,1,1,1,1,1};
                queue.Enqueue(new Packet(Protocol.Status,ready));((Task)Call(form,"Tick")).GetAwaiter().GetResult();Application.DoEvents();
                var editor=(TextBox)Get(form,"editor");int resized=0,recreated=0;editor.SizeChanged+=delegate{resized++;};editor.HandleCreated+=delegate{recreated++;};
                var viewport=(Panel)form.Controls[0];viewport.ScrollControlIntoView(editor);editor.Focus();Application.DoEvents();
                bool diagnose=Array.IndexOf(args,"diagnose")>=0;
                if(!diagnose)Require((GetWindowLong(viewport.Handle,-20)&0x02000000)!=0,"native editor and scrollbar must share the viewport compositing buffer");
                IntPtr handle=editor.Handle;int height=editor.Height,heightChanges=0;editor.SizeChanged+=delegate{if(editor.Height!=height)heightChanges++;};
                using(var probe=new Probe(editor)) {
                    for(int i=0;i<20;i++){Call(form,"UpdateEditorActions");Application.DoEvents();}
                    Console.WriteLine("idle refresh: "+probe+" bounds="+editor.Bounds+" resized="+resized);probe.Clear();resized=0;
                    for(int i=0;i<40;i++){editor.AppendText("中A");Application.DoEvents();}
                    Console.WriteLine("typing: "+probe+" bounds="+editor.Bounds+" resized="+resized);probe.Clear();resized=0;
                    for(int i=0;i<100;i++){queue.Enqueue(new Packet(Protocol.Status,ready));((Task)Call(form,"Tick")).GetAwaiter().GetResult();Application.DoEvents();}
                    Console.WriteLine("heartbeat: "+probe+" bounds="+editor.Bounds+" resized="+resized);probe.Clear();resized=0;
                    for(int i=0;i<20;i++){((Label)Get(form,"editorResult")).Text="准备进度 "+i+new string('测',i%3*40);Application.DoEvents();}
                    Console.WriteLine("progress: "+probe+" bounds="+editor.Bounds+" resized="+resized+" recreated="+recreated);
                    ((Label)Get(form,"editorResult")).Text="";editor.Select(7,3);Application.DoEvents();int caret=editor.SelectionStart,length=editor.SelectionLength;string draft=editor.Text;
                    for(int i=0;i<20;i++){((Task)Call(form,"SendEditorText")).GetAwaiter().GetResult();Application.DoEvents();Require(editor.Text==draft&&editor.SelectionStart==caret&&editor.SelectionLength==length,"send completion preserves draft and selection");}
                    for(int i=0;i<12;i++){((RadioButton)Get(form,"modeProgram")).Checked=true;Application.DoEvents();((RadioButton)Get(form,"subKeyboard")).Checked=true;((RadioButton)Get(form,"modeNoProgram")).Checked=true;Application.DoEvents();((RadioButton)Get(form,"subPowerShell")).Checked=true;Application.DoEvents();Require(editor.Text==draft,"mode switches restore the matching draft");}
                    Require(editor.Handle==handle&&recreated==0,"native EDIT handle survives send completion and mode switches");
                    Require(heightChanges==0,"editor height remains stable during result reflow and mode switches");
                    Require((int)Get(form,"sentCount")==20,"all simulated authenticated sends completed");
                    Console.WriteLine("PASS: 20 send completions and 36 mode changes; draft/selection/native handle/editor height retained; composited viewport includes native EDIT and scrollbar. Physical BLE and user display not tested.");
                }
                Set(form,"exiting",true);form.Close();
            }
            return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex.GetBaseException());return 1;}
    }
}
