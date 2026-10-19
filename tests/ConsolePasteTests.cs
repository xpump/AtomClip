using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
class ConsolePasteTest {
 [DllImport("kernel32.dll")]static extern bool AllocConsole();
 [DllImport("kernel32.dll")]static extern bool FreeConsole();
 [DllImport("kernel32.dll")]static extern IntPtr GetConsoleWindow();
 [DllImport("kernel32.dll")]static extern IntPtr GetStdHandle(int n);
 [DllImport("user32.dll")]static extern bool ShowWindow(IntPtr w,int c);
 [DllImport("user32.dll")]static extern bool PostMessage(IntPtr w,uint m,IntPtr a,IntPtr b);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern int GetWindowText(IntPtr w,StringBuilder s,int n);
 [DllImport("user32.dll")]static extern uint GetWindowThreadProcessId(IntPtr w,out uint pid);
 delegate bool Callback(IntPtr w,IntPtr p);
 [DllImport("user32.dll")]static extern bool EnumWindows(Callback c,IntPtr p);
 static string Title(IntPtr w){var b=new StringBuilder(260);GetWindowText(w,b,b.Capacity);return b.ToString();}
 [StructLayout(LayoutKind.Explicit,Size=20)]struct InputRecord{[FieldOffset(0)]public short Type;[FieldOffset(4)]public int Down;[FieldOffset(8)]public short Repeat;[FieldOffset(10)]public short Key;[FieldOffset(14)]public char Character;}
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]static extern bool WriteConsoleInput(IntPtr h,InputRecord[] records,uint n,out uint written);
 static void Enter(){uint written;WriteConsoleInput(GetStdHandle(-10),new[]{new InputRecord{Type=1,Down=1,Repeat=1,Key=13,Character='\r'},new InputRecord{Type=1,Down=0,Repeat=1,Key=13,Character='\r'}},2,out written);}
 static bool Test(string text){
  Process child=null;IntPtr picker=IntPtr.Zero;
  try {
   if(!AllocConsole())throw new Exception("console allocation");
   var console=GetConsoleWindow(); ShowWindow(console,0);
   child=Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"WindowsPowerShell\\v1.0\\powershell.exe"),"-NoProfile -STA -NoExit -Command \"Remove-Module PSReadLine -ErrorAction SilentlyContinue;[Console]::Title='AtomClip paste test ready'\""){UseShellExecute=false,CreateNoWindow=false});
   var watch=Stopwatch.StartNew();
   while(!Title(console).Contains("paste test ready") && watch.ElapsedMilliseconds<10000){if(child.HasExited)throw new Exception("early host exit");Thread.Sleep(50);}
   if(!Title(console).Contains("paste test ready"))throw new Exception("host readiness timeout");
   Clipboard.SetText(text,TextDataFormat.UnicodeText);
   if(!PostMessage(console,0x112,new IntPtr(0xfff1),IntPtr.Zero))throw new Exception("paste post failed");
   Thread.Sleep(500);Enter();
   watch.Restart();
   while(watch.ElapsedMilliseconds<16000 && picker==IntPtr.Zero){
    EnumWindows(delegate(IntPtr w,IntPtr unused){uint pid;GetWindowThreadProcessId(w,out pid);if(pid==child.Id && Title(w)=="选择开发板")picker=w;return true;},IntPtr.Zero);
    Thread.Sleep(50);
   }
   return picker!=IntPtr.Zero || Title(console)=="AtomClip paste sentinel";
  } finally {
   if(picker!=IntPtr.Zero)PostMessage(picker,0x10,IntPtr.Zero,IntPtr.Zero);
   if(child!=null){if(!child.HasExited){child.Kill();child.WaitForExit(3000);}child.Dispose();}
   FreeConsole();
  }
 }
 [STAThread]static int Main(string[] args){
  IDataObject original=Clipboard.GetDataObject();var saved=new DataObject();var output=new StringBuilder();
  try {
   if(original!=null)foreach(string format in original.GetFormats(false))saved.SetData(format,false,original.GetData(format,false));
   const string pairingPattern=@"(?<=::Run\(')[A-Za-z0-9+/=]+(?='(?:, \$true)?\))";
   string text=File.ReadAllText(args[0]); if(Regex.Matches(text,pairingPattern).Count!=1)throw new Exception("Expected one pairing placeholder");
   text=Regex.Replace(text,pairingPattern,Convert.ToBase64String(new byte[32]));
   // Isolate this fixture from a user's running AtomClip instance.
   text=text.Replace("Local\\\\AtomClip.TextBridge","Local\\\\AtomClip.ConsoleTest"+Guid.NewGuid().ToString("N"));
   string lf=text.Replace("\r\n","\n");string crlf=lf.Replace("\n","\r\n");
   bool oldOk=Test(lf),newOk=Test(crlf);
   output.AppendLine("Real classic-conhost clipboard paste; system PowerShell5 STA; PSReadLine removed; synthetic pairing key; full fixed readable bootstrap.");
   output.AppendLine("LF-only reached port picker: "+oldOk);
   output.AppendLine("CRLF reached port picker: "+newOk);
   output.AppendLine("No serial port opened; no BLE job sent; original clipboard restored.");
   File.WriteAllText(args[1],output.ToString()); return !oldOk && newOk ? 0:1;
  } catch(Exception ex){File.WriteAllText(args[1],"FAIL: "+ex.GetType().Name);return 2;}
  finally{if(original==null)Clipboard.Clear();else Clipboard.SetDataObject(saved,true);}
 }
}
