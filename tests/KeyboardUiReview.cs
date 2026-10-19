using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using AtomClipUsb;
class KeyboardUiReview {
 const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
 [DllImport("user32.dll")]static extern bool SetProcessDPIAware();
 static object Get(object o,string n){return o.GetType().GetField(n,Hidden).GetValue(o);}
 static void Set(object o,string n,object v){o.GetType().GetField(n,Hidden).SetValue(o,v);}
 sealed class Link:ILink {public event Action<Packet> Received {add{}remove{}}public bool Connected{get{return true;}}public Task Send(byte[] b,CancellationToken t){return Task.FromResult(true);}public void Dispose(){}}
 [STAThread]static int Main(string[] args){
  SetProcessDPIAware();Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
  string folder=Path.Combine(Path.GetTempPath(),"AtomClip-ui-"+Guid.NewGuid().ToString("N"));
  var type=typeof(Protocol).Assembly.GetType("AtomClipUsb.MainForm");
  using(var form=(Form)Activator.CreateInstance(type,Hidden|BindingFlags.Public,null,new object[]{folder,new FileLog(folder),new byte[32]},null)){
   ((System.Windows.Forms.Timer)Get(form,"timer")).Stop();var events=(EventHandlerList)typeof(Control).GetProperty("Events",Hidden).GetValue(form,null);object key=typeof(Form).GetField("EVENT_SHOWN",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);events.RemoveHandler(key,events[key]);
   form.ShowInTaskbar=false;form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);
   form.Font=new Font(form.Font.FontFamily,float.Parse(args[4]));form.Size=new Size(int.Parse(args[2]),int.Parse(args[3]));
   ((RadioButton)Get(form,"subKeyboard")).Checked=true;((ComboBox)Get(form,"keyboardInput")).SelectedIndex=args[1].Contains("vuc")?1:2;
   Set(form,"link",new Link());Set(form,"connectionCts",new CancellationTokenSource());Set(form,"supportsTyping",true);Set(form,"supportsDirectKeys",true);Set(form,"supportsPinyin",true);Set(form,"supportsGbk",!args[1].Contains("old"));
   Set(form,"desired",true);((Button)Get(form,"connect")).Text="断开";
   if(args[1].Contains("required")||args[1].Contains("confirm"))((CheckBox)Get(form,"requireProbe")).Checked=true;
   if(args[1].Contains("ready")||args[1].Contains("confirm"))Set(form,"keyboardProbeSent",true);
   if(args[1].Contains("ready"))((CheckBox)Get(form,"pinyinConfirmed")).Checked=true;
   if(args[1].Contains("powershell"))((RadioButton)Get(form,"subPowerShell")).Checked=true;
   if(args[1].Contains("ready")||args[1].Contains("body"))((TextBox)Get(form,"editor")).Text="中文测试 A12，结果 6.8%\r\nFollow-up：2026-10-17，正常。";
   type.GetMethod("UpdateEditorActions",Hidden).Invoke(form,null);form.Show();Application.DoEvents();
   var view=(Panel)form.Controls[0];if(args.Length>5&&args[5]=="bottom"){view.AutoScrollPosition=new Point(0,view.Controls[0].Height);Application.DoEvents();}
   using(var bmp=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bmp,new Rectangle(Point.Empty,form.Size));bmp.Save(Path.GetFullPath(args[0]));}
   foreach(string name in new[]{"keyboardOptions","pinyinGuide","pinyinActionHost","pinyinConfirmed","keyboardReady","editor","directKeys"}){var c=(Control)Get(form,name);Console.WriteLine(name+" "+c.Bounds+" visible="+c.Visible+" parent="+c.Parent.ClientSize);}
   Console.WriteLine("scroll="+view.VerticalScroll.Visible+" root="+view.Controls[0].Size+" view="+view.ClientSize);
   Set(form,"exiting",true);form.Close();
  }return 0;
 }
}
