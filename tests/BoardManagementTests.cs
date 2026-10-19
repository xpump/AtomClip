using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using AtomClipUsb;

// Real controls and production callbacks; isolated synthetic store, no scan/serial callbacks invoked.
class BoardManagementTests
{
    const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
    const BindingFlags Fields=BindingFlags.Instance|BindingFlags.NonPublic|BindingFlags.Public;
    static int checks;
    static void Check(bool ok,string label){checks++;if(!ok)throw new Exception(label);}
    static IEnumerable<Control> All(Control c){yield return c;foreach(Control child in c.Controls)foreach(var item in All(child))yield return item;}
    static object State(object value,int depth)
    {
        if(value==null||depth==0||value is Control)return null;
        var fields=value.GetType().GetFields(Fields);
        if(fields.Any(f=>f.FieldType==typeof(List<BoardAdvertisement>)))return value;
        foreach(var field in fields)if(field.FieldType.Name.Contains("DisplayClass")){var found=State(field.GetValue(value),depth-1);if(found!=null)return found;}
        return null;
    }
    static V2Protocol.Binding Binding(int variant,string alias)
    {
        byte[] info=new byte[40];Array.Copy(System.Text.Encoding.ASCII.GetBytes("ACI2"),info,4);info[4]=2;info[5]=1;info[6]=1;info[8]=255;info[14]=128;info[17]=2;info[36]=3;
        for(int j=20;j<36;j++)info[j]=(byte)j;info[22]+=(byte)variant;
        var key=new byte[32];key[0]=1;return new V2Protocol.Binding(new V2Protocol.Info(info),key,key,alias);
    }
    static void Capture(Form f,string output,string name)
    {
        if(output==null)return;Directory.CreateDirectory(output);using(var image=new Bitmap(f.Width,f.Height)){f.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(output,name+".png"));}
    }
    static void Modal(Form owner,Action open,string title,Action<Form> inspect)
    {
        Exception failure=null;using(var timer=new Timer{Interval=60}){
            timer.Tick+=delegate{
                var f=Application.OpenForms.Cast<Form>().FirstOrDefault(x=>x!=owner&&x.Text==title);if(f==null)return;timer.Stop();
                try{inspect(f);}catch(Exception e){failure=e;}finally{f.Close();}
            };
            timer.Start();open();timer.Stop();
        }
        if(failure!=null)throw failure;
    }
    [STAThread] static int Main(string[] args)
    {
        try{
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            string folder=Path.Combine(Path.GetTempPath(),"AtomClip-boards-"+Guid.NewGuid().ToString("N"));string output=args.Length==0?null:args[0];
            var type=typeof(Protocol).Assembly.GetType("AtomClipUsb.MainForm",true);
            using(var form=(Form)Activator.CreateInstance(type,Fields,null,new object[]{folder,new FileLog(folder),new byte[32]},null)){
                ((Timer)type.GetField("timer",Hidden).GetValue(form)).Stop();
                var events=(EventHandlerList)typeof(Control).GetProperty("Events",Hidden).GetValue(form,null);var shown=typeof(Form).GetField("EVENT_SHOWN",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);events.RemoveHandler(shown,events[shown]);
                form.StartPosition=FormStartPosition.Manual;form.Location=new Point(-30000,-30000);form.ShowInTaskbar=false;form.Show();Application.DoEvents();
                object store=type.GetField("deviceBindings",Hidden).GetValue(form);var registry=(V2Registry)store.GetType().GetField("Registry").GetValue(store);
                var bound=Binding(0,"我的开发板（模拟）");var pending=Binding(1,"待确认开发板（模拟）");pending.Pending=true;registry.Confirmed.Add(bound.DeviceInfo.Number,bound);registry.Pending.Add(pending.DeviceInfo.Number,pending);
                byte[] initial=registry.Encode();
                Modal(form,()=>type.GetMethod("OpenBoards",Hidden).Invoke(form,null),"我的开发板",w=>{
                    var controls=All(w).ToList();var list=controls.OfType<ListView>().Single();var buttons=controls.OfType<Button>().ToList();
                    Check(buttons.Count==4&&new[]{"连接","添加开发板","扫描附近","更多 ▾"}.All(t=>buttons.Any(b=>b.Text==t)),"four primary actions");
                    Check(!controls.Any(c=>c.Text.Contains("旧固件")),"no retired firmware UI");
                    var scan=buttons.Single(b=>b.Text=="扫描附近");var clickKey=typeof(Control).GetField("EventClick",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);var handlers=(EventHandlerList)typeof(Control).GetProperty("Events",Hidden).GetValue(scan,null);
                    var state=handlers[clickKey].GetInvocationList().Select(d=>State(d.Target,4)).FirstOrDefault(x=>x!=null);Check(state!=null,"production discovery display state found");
                    var fields=state.GetType().GetFields(Fields);var nearby=fields.Single(f=>f.FieldType==typeof(List<BoardAdvertisement>));var populate=(Action)fields.Single(f=>f.Name.Contains("populate")&&f.FieldType==typeof(Action)).GetValue(state);
                    var signal=new BoardAdvertisement{Address="AABBCCDDEE01",ShortId=bound.DeviceInfo.ShortId,Info=bound.DeviceInfo,Rssi=-55,V2=true};
                    nearby.SetValue(state,new List<BoardAdvertisement>{signal});populate();Check(list.Items.Count==2,"real list merges bound board and nearby scan once");
                    list.Items[0].Selected=true;Check(buttons.Single(b=>b.Text=="连接").Enabled,"confirmed row enables connection");
                    Check(list.Items[0].SubItems[2].Text=="-55 dBm","merged row carries signal");Check(controls.OfType<Label>().Any(l=>l.Text.Contains(bound.DeviceInfo.Number)),"selected full ID visible outside table");
                    Capture(w,output,"01-boards");w.Size=w.MinimumSize;Application.DoEvents();
                    Check(list.Columns.Cast<ColumnHeader>().Sum(c=>c.Width)<=list.ClientSize.Width,"all four columns fit minimum window");
                    Check(buttons.All(b=>b.Parent.ClientRectangle.Contains(b.Bounds)),"primary buttons fit at minimum width");Capture(w,output,"02-boards-small");
                    list.Items[0].Selected=false;list.Items[1].Selected=true;Check(!buttons.Single(b=>b.Text=="连接").Enabled,"pending row cannot connect");
                    nearby.SetValue(state,new List<BoardAdvertisement>{signal,new BoardAdvertisement{Address="AABBCCDDEE02",Info=bound.DeviceInfo,V2=true,Rssi=-40}});populate();
                    Check(list.Items.Count==4,"conflict retained instead of silently merged");list.Items[1].Selected=false;list.Items[0].Selected=true;Check(!buttons.Single(b=>b.Text=="连接").Enabled,"conflicting full ID cannot connect from list");Capture(w,output,"03-conflict");
                });
                Check(V2Protocol.Equal(initial,registry.Encode()),"opening and displaying scans never changes stored bindings");
                Modal(form,()=>type.GetMethod("OpenUsbBoard",Hidden).Invoke(form,new object[]{form,0,null}),"添加开发板",w=>{
                    var controls=All(w).ToList();Check(!controls.OfType<CheckBox>().Any(),"USB add has no migration toggles");Check(!controls.OfType<Button>().Single(b=>b.Text=="确认添加").Enabled,"cannot bind without physical Info read");
                    Check(controls.OfType<Button>().Any(b=>b.Text=="读取开发板"),"direct read action visible");Capture(w,output,"04-usb-add");w.Size=w.MinimumSize;Application.DoEvents();Capture(w,output,"05-usb-small");
                });
                Check(!File.Exists(Path.Combine(folder,"devices-v2.bin")),"layout review does not write binding store");type.GetField("exiting",Hidden).SetValue(form,true);form.Close();
            }
            Console.WriteLine("PASS: board management real-control regressions: "+checks+" checks; synthetic scan data, no BLE/USB transport or user store accessed.");return 0;
        }catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
