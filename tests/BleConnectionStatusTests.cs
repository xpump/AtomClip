using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using AtomClipUsb;
class BleConnectionStatusTests {
 const BindingFlags Hidden=BindingFlags.Instance|BindingFlags.NonPublic;
 static object Get(object o,string n){return o.GetType().GetField(n,Hidden).GetValue(o);}
 static void Set(object o,string n,object v){o.GetType().GetField(n,Hidden).SetValue(o,v);}
 static void Update(object o){o.GetType().GetMethod("UpdateEditorActions",Hidden).Invoke(o,null);}
 static void Check(bool ok,string label){if(!ok)throw new Exception(label);}
 [STAThread] static int Main(){try{
  string directory=Path.Combine(Path.GetTempPath(),"AtomClip-status-"+Guid.NewGuid().ToString("N"));var type=typeof(Protocol).Assembly.GetType("AtomClipUsb.MainForm");
  using(var form=(Form)Activator.CreateInstance(type,Hidden|BindingFlags.Public,null,new object[]{directory,new FileLog(directory),new byte[32]},null)){
   ((Timer)Get(form,"timer")).Stop();var state=(Label)Get(form,"state");
   Set(form,"desired",true);Set(form,"connecting",true);Set(form,"connectionStage","正在读取完整设备编号…");
   for(int i=0;i<20;i++)Update(form);Check(state.Text.Contains("读取完整设备编号"),"refresh must preserve connection stage");
   Set(form,"connecting",false);Set(form,"connectionError","服务读取失败 0x80070016");
   for(int i=0;i<20;i++)Update(form);Check(state.Text.Contains("0x80070016")&&state.Text.Contains("等待重连"),"retry must expose actual failure");
   Set(form,"connecting",true);Set(form,"connectionStage","正在扫描已绑定开发板…");Update(form);Check(state.Text.Contains("扫描")&&state.Text.Contains("0x80070016"),"next attempt preserves previous failure");
   Set(form,"connecting",false);Set(form,"desired",false);Update(form);Check(state.Text.Contains("连接未完成")&&state.Text.Contains("0x80070016"),"stopped retry keeps failure until explicit disconnect");
   type.GetMethod("Stop",Hidden).Invoke(form,null);Update(form);Check(!state.Text.Contains("0x80070016")&&state.Text.Contains("尚未连接"),"manual disconnect clears old failure");
  }
  Console.WriteLine("PASS: 5 BLE connection-stage/error persistence checks; no hardware or user settings accessed.");return 0;
 }catch(Exception e){Console.Error.WriteLine(e);return 1;}}
}
