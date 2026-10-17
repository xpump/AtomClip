using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtomClipUsb;

class ConnectionModeTests
{
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    static object Get(object o,string n){return o.GetType().GetField(n,Hidden).GetValue(o);}
    static void Set(object o,string n,object value){o.GetType().GetField(n,Hidden).SetValue(o,value);}
    static object Call(object o,string n,params object[] args){return o.GetType().GetMethod(n,Hidden).Invoke(o,args);}
    static void Require(bool ok,string label){if(!ok)throw new Exception(label);}
    delegate bool WindowCallback(IntPtr hwnd,IntPtr data);
    [DllImport("kernel32.dll")]static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]static extern bool EnumThreadWindows(uint thread,WindowCallback callback,IntPtr data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr hwnd,StringBuilder text,int capacity);
    [DllImport("user32.dll")]static extern IntPtr SendMessage(IntPtr hwnd,int message,IntPtr wParam,IntPtr lParam);
    static void ClickTrial(Form form){
        bool accepted=false;
        using(var timer=new System.Windows.Forms.Timer{Interval=50}) {
            timer.Tick+=delegate {EnumThreadWindows(GetCurrentThreadId(),delegate(IntPtr hwnd,IntPtr ignored){var title=new StringBuilder(128);GetWindowText(hwnd,title,128);if(title.ToString()=="确认写入内网"){timer.Stop();accepted=true;SendMessage(hwnd,0x111,new IntPtr(1),IntPtr.Zero);}return true;},IntPtr.Zero);};
            timer.Start();((Button)Get(form,"probePinyin")).PerformClick();Application.DoEvents();timer.Stop();
        }
        Require(accepted,"trial displays preparation confirmation in this isolated UI thread");
        Require((bool)Get(form,"keyboardProbeSent")&&((CheckBox)Get(form,"pinyinConfirmed")).Enabled&&!((TextBox)Get(form,"editor")).Visible,"authenticated trial completion enables manual confirmation before revealing body");
    }
    sealed class TypingLink:ILink
    {
        public event Action<Packet> Received {add{} remove{}}
        public bool Connected {get{return true;}}
        public Form Owner;public byte[] Key,Plain;
        public Task Send(byte[] frame,CancellationToken token)
        {
            foreach(var packet in new PacketParser().Feed(frame,frame.Length))if(packet.Kind==Protocol.TypeJob){
                var p=packet.Payload;
                var transform=typeof(Protocol).Assembly.GetType("AtomClipUsb.WindowsGcm").GetMethod("Transform");
                Plain=(byte[])transform.Invoke(null,new object[]{false,Key,Protocol.Slice(p,16,12),Protocol.Slice(p,0,16),Protocol.Slice(p,28,p.Length-44),Protocol.Slice(p,p.Length-16,16)});
                byte[] result=new byte[33],signed=new byte[18];Array.Copy(p,result,16);result[16]=2;signed[0]=Protocol.JobResult;Array.Copy(result,0,signed,1,17);
                using(var mac=new HMACSHA256(Key))Array.Copy(mac.ComputeHash(signed),0,result,17,16);
                ((ConcurrentQueue<Packet>)Get(Owner,"incoming")).Enqueue(new Packet(Protocol.JobResult,result));
                ((Task)Call(Owner,"Tick")).GetAwaiter().GetResult();
            }
            return Task.FromResult(true);
        }
        public void Dispose(){}
    }
    [STAThread] static int Main()
    {
        try{
            var key=new byte[32];var type=typeof(Protocol).Assembly.GetType("AtomClipUsb.MainForm");
            string folder=Path.Combine(Path.GetTempPath(),"AtomClip-modes-"+Guid.NewGuid().ToString("N"));
            using(var form=(Form)Activator.CreateInstance(type,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance,null,new object[]{folder,new FileLog(folder),key},null)){
                ((System.Windows.Forms.Timer)Get(form,"timer")).Stop();
                var events=(EventHandlerList)typeof(Control).GetProperty("Events",Hidden).GetValue(form,null);
                var shown=typeof(Form).GetField("EVENT_SHOWN",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);
                events.RemoveHandler(shown,events[shown]);
                form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);form.Show();Application.DoEvents();
                var editor=(TextBox)Get(form,"editor");var powershell=(RadioButton)Get(form,"subPowerShell");var keyboard=(RadioButton)Get(form,"subKeyboard");
                Require(powershell.Checked&&((Control)Get(form,"modeOptions")).Visible,"one PowerShell choice is the no-program default");
                Require(((Control)Get(form,"modeOptions")).Controls[0].Controls.Count==2,"only PowerShell and USB keyboard subchoices");
                editor.Text="双向草稿";string duplex=(string)Call(form,"ReadReceiverBootstrap");Require(duplex.Contains(", $true)"),"PowerShell always defaults to duplex bootstrap");
                keyboard.Checked=true;Require(!((Control)Get(form,"startSetup")).Visible&&!((Control)Get(form,"sendEditor")).Visible&&((Control)Get(form,"probePinyin")).Visible&&editor.Visible,"optional trial leaves body visible by default");
                Require(!((CheckBox)Get(form,"requireProbe")).Checked,"trial is opt-in");
                Require(((ComboBox)Get(form,"keyboardInput")).SelectedIndex==2&&((RadioButton)Get(form,"keyboardGbk")).Checked,"visible GBK choice is default");
                ((RadioButton)Get(form,"keyboardAscii")).Checked=true;Require(((ComboBox)Get(form,"keyboardInput")).SelectedIndex==0,"visible choice selects input implementation");
                editor.Text="中文";Require(!(bool)Call(form,"EditorTextValid")&&((Label)Get(form,"editorSize")).Text.Contains("不支持"),"ASCII explains unsupported Chinese");
                editor.Text="ASCII 123";Require((bool)Call(form,"EditorTextValid"),"keyboard accepts ASCII");
                Call(form,"ReceiveData",Protocol.Encrypt("not received in keyboard mode",key));Require(((System.Collections.Generic.HashSet<string>)Get(form,"discarded")).Count==1,"keyboard ignores reverse clipboard without mutation");
                form.Font=new Font(form.Font.FontFamily,14);form.Size=new Size(720,720);Application.DoEvents();
                var options=(Panel)Get(form,"modeOptions");Require(options.Controls[0].Bottom<=options.ClientSize.Height,"wrapped mode choices stay inside parent");
                foreach(string name in new[]{"keyboardGbk","keyboardVuc","keyboardAscii"}){var choice=(Control)Get(form,name);Require(choice.Visible&&choice.Bottom<=choice.Parent.ClientSize.Height&&choice.Right<=choice.Parent.ClientSize.Width,"visible method choice fits narrow 14pt layout: "+name);}
                var hint=(Label)Get(form,"routeHint");var guide=(Label)Get(form,"pinyinGuide");Require(guide.Bottom<=guide.Parent.ClientSize.Height,"guide stays in its group");
                form.Font=new Font(form.Font.FontFamily,10);Application.DoEvents();
                powershell.Checked=true;Require(editor.Text=="双向草稿","PowerShell draft restored");
                Require(((Label)Get(form,"routeHint")).Text.Contains("按 B"),"PowerShell describes the receiver's B toggle");
                ((RadioButton)Get(form,"modeProgram")).Checked=true;Require(!((Control)Get(form,"modeOptions")).Visible&&!((Control)Get(form,"setupCard")).Visible,"program mode hides system-only choices");
                ((RadioButton)Get(form,"modeNoProgram")).Checked=true;
                var link=new TypingLink{Owner=form,Key=key};Set(form,"link",link);Set(form,"connectionCts",new CancellationTokenSource());Set(form,"peerReady",false);
                Set(form,"supportsTyping",true);Set(form,"supportsStartupDelay",true);Set(form,"supportsFastTyping",true);Set(form,"lastStatus",DateTime.UtcNow);Set(form,"nextPing",DateTime.UtcNow.AddHours(1));
                ((Task)Call(form,"SendTypeJob",duplex,true,false,false)).GetAwaiter().GetResult();Require(link.Plain[0]==4&&link.Plain[2]==1&&link.Plain[3]==10,"fast setup uses authenticated 1ms data pacing and keeps 10s startup");
                ((CheckBox)Get(form,"fastInput")).Checked=false;
                ((Task)Call(form,"SendTypeJob",duplex,true,false,false)).GetAwaiter().GetResult();Require(link.Plain[2]==4,"compatibility speed uses 4ms pacing");
                ((CheckBox)Get(form,"fastInput")).Checked=true;Set(form,"supportsFastTyping",false);
                ((Task)Call(form,"SendTypeJob",duplex,true,false,false)).GetAwaiter().GetResult();Require(link.Plain[2]==4,"old firmware falls back to compatibility pacing");
                var queue=(ConcurrentQueue<Packet>)Get(form,"incoming");
                byte[] ready=new byte[]{1,0,1,1,1,1,1,1,1};queue.Enqueue(new Packet(Protocol.Status,ready));((Task)Call(form,"Tick")).GetAwaiter().GetResult();Application.DoEvents();
                int hintChanges=0,visibilityChanges=0,stateChanges=0;
                hint.TextChanged+=delegate{hintChanges++;};((Control)Get(form,"setupCard")).VisibleChanged+=delegate{visibilityChanges++;};((Control)Get(form,"state")).TextChanged+=delegate{stateChanges++;};
                for(int i=0;i<100;i++){queue.Enqueue(new Packet(Protocol.Status,ready));((Task)Call(form,"Tick")).GetAwaiter().GetResult();}
                for(int i=0;i<20;i++)Call(form,"UpdateEditorActions");
                Require(hintChanges==0&&visibilityChanges==0&&stateChanges==0,"ready heartbeat and repeated presentation do not oscillate text or visibility");
                keyboard.Checked=true;((ComboBox)Get(form,"keyboardInput")).SelectedIndex=1;editor.Text="中文Ab123";
                Set(form,"peerReady",false);Set(form,"supportsDirectKeys",true);Set(form,"supportsPinyin",false);Call(form,"UpdateEditorActions");
                Require((bool)Call(form,"EditorTextValid")&&!((Control)Get(form,"probePinyin")).Enabled&&!((Control)Get(form,"directKeys")).Enabled&&((Label)Get(form,"keyboardReady")).Text.Contains("更新固件"),"unsupported firmware explains why trial is unavailable");
                Set(form,"supportsPinyin",true);Call(form,"UpdateEditorActions");Require(((Control)Get(form,"probePinyin")).Enabled&&((Control)Get(form,"probePinyin")).Text.Contains("试输")&&editor.Visible&&((Control)Get(form,"directKeys")).Enabled,"supported VUC can skip trial on older compatible firmware");
                ((Task)Call(form,"SendTypeJob","中文Ab123",true,false,true)).GetAwaiter().GetResult();Require(link.Plain[0]==5,"VUC sends actual encrypted mixed text without trial");
                ((CheckBox)Get(form,"requireProbe")).Checked=true;Require(!editor.Visible&&!((Control)Get(form,"directKeys")).Enabled,"opt-in trial gates body and send");
                byte[] prior=link.Plain;Require(((Task<int>)Call(form,"SendTypeJob","中文Ab123",true,false,true)).GetAwaiter().GetResult()==-1&&ReferenceEquals(prior,link.Plain),"actual send entry rejects unconfirmed required trial without transmitting");
                Set(form,"keyboardProbeSent",true);((CheckBox)Get(form,"pinyinConfirmed")).Checked=true;Call(form,"UpdateEditorActions");
                Require(((Control)Get(form,"directKeys")).Enabled,"new firmware and successful manual probe unlock Chinese send");
                editor.Text="😀";Require(!((Control)Get(form,"directKeys")).Enabled,"pinyin rejects emoji instead of typing broken surrogate pairs");
                ((ComboBox)Get(form,"keyboardInput")).SelectedIndex=0;Require(!((CheckBox)Get(form,"pinyinConfirmed")).Checked,"changing input method resets compatibility confirmation");
                ((ComboBox)Get(form,"keyboardInput")).SelectedIndex=2;editor.Text=Protocol.KeyboardProbeText;Call(form,"UpdateEditorActions");
                Require((bool)Call(form,"EditorTextValid")&&((Button)Get(form,"probePinyin")).Text.Contains("试输")&&((Button)Get(form,"probePinyin")).Enabled&&!editor.Visible,"GBK mixed text is accepted and trial is actionable");
                ((CheckBox)Get(form,"requireProbe")).Checked=false;Require(editor.Visible&&((Control)Get(form,"directKeys")).Enabled,"GBK body available without trial");
                ((Task)Call(form,"SendTypeJob",Protocol.KeyboardProbeText,true,false,true)).GetAwaiter().GetResult();Require(link.Plain[0]==6,"GBK sends actual encrypted mixed text without trial");
                Set(form,"supportsGbk",false);Call(form,"UpdateEditorActions");Require(!((Control)Get(form,"directKeys")).Enabled,"skipping trial does not bypass missing GBK firmware capability");
                Set(form,"supportsGbk",true);((CheckBox)Get(form,"requireProbe")).Checked=true;
                ClickTrial(form);
                Require(link.Plain[0]==6&&link.Plain[1]==2&&link.Plain[2]==2,"real GBK probe sends selected fast mixed-input job");
                Set(form,"keyboardProbeSent",true);((CheckBox)Get(form,"pinyinConfirmed")).Checked=true;
                ((Task)Call(form,"SendTypeJob","病ABC123，中文!\nNext\tline",true,false,true)).GetAwaiter().GetResult();Require(link.Plain[0]==6,"confirmed GBK mixed body uses actual encrypted GBK job");
                ((CheckBox)Get(form,"pinyinCompatible")).Checked=true;Require(!(bool)Get(form,"keyboardProbeSent")&&!((CheckBox)Get(form,"pinyinConfirmed")).Checked,"changing pace requires a fresh trial");
                ClickTrial(form);Require(link.Plain[1]==8&&link.Plain[4]==30,"compatibility trial uses slower complete Alt-number pacing");
                Set(form,"keyboardProbeSent",true);((CheckBox)Get(form,"pinyinConfirmed")).Checked=true;editor.Text="雪人☃";Require(!((Control)Get(form,"directKeys")).Enabled&&((Label)Get(form,"editorSize")).Text.Contains("VUC"),"unrepresentable GBK input is rejected with a VUC recovery path");
                ((ComboBox)Get(form,"keyboardInput")).SelectedIndex=1;Require((bool)Call(form,"EditorTextValid"),"same BMP text can be sent through VUC after its own trial");
                ClickTrial(form);Require(link.Plain[0]==5&&Protocol.Utf8.GetString(link.Plain,3,link.Plain.Length-3)==Protocol.KeyboardProbeText,"real VUC mixed trial uses complete probe text, not the old Chinese-only raw keys");
                ((CheckBox)Get(form,"pinyinConfirmed")).Checked=true;Require(editor.Visible&&editor.Text=="雪人☃","manual confirmation reveals the retained VUC draft");
                Set(form,"exiting",true);form.Close();
            }
            Console.WriteLine("PASS: modes/drafts, duplex bootstrap, encrypted pacing; 100 ready heartbeats and 20 presentation refreshes with zero text/visibility oscillation; Chinese capability/optional-probe gating and unsupported-character rejection. UI fixture only, no hardware or clipboard mutation.");return 0;
        }catch(Exception ex){Console.Error.WriteLine(ex.GetBaseException());return 1;}
    }
}
