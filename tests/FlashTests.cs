using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using AtomClipUsb;
class FlashTests
{
 static void Require(bool value,string message){if(!value)throw new Exception(message);}
 static async Task CheckPlan(){
  int calls=0;
  await FlashPlan.Execute("COM14",@"C:\path with spaces\firmware.bin",delegate(string args){
   calls++;
   if(calls==1){Require(args.EndsWith("flash-id")&&!args.Contains("write-flash"),"probe before write");return Task.FromResult(new FlashCommandResult{ExitCode=0,Output="Detected flash size: 8MB"});}
   Require(args.Contains("write-flash 0x0 \"C:\\path with spaces\\firmware.bin\"")&&args.Contains("--port COM14"),"address, port, quoted path");
   return Task.FromResult(new FlashCommandResult{ExitCode=0,Output="Hash of data verified."});
  });Require(calls==2,"one probe, one write");
  foreach(string output in new[]{"Detected flash size: 4MB","Detected flash size: 16MB","no size detected"}){
   calls=0;try{await FlashPlan.Execute("COM4","f.bin",delegate(string args){calls++;return Task.FromResult(new FlashCommandResult{ExitCode=0,Output=output});});throw new Exception("unsafe flash accepted");}
   catch(IOException){Require(calls==1,"incorrect size blocks write");}
  }
  calls=0;try{await FlashPlan.Execute("COM4","f.bin",delegate(string args){calls++;return Task.FromResult(new FlashCommandResult{ExitCode=2,Output="Wrong chip"});});throw new Exception("wrong chip accepted");}
  catch(IOException){Require(calls==1,"failed probe blocks write");}
  foreach(int code in new[]{0,1}){
   calls=0;try{await FlashPlan.Execute("COM4","f.bin",delegate(string args){calls++;return Task.FromResult(new FlashCommandResult{ExitCode=calls==1?0:code,Output=calls==1?"Detected flash size: 8MB":"No verification"});});throw new Exception("unverified flash accepted");}
   catch(IOException){Require(calls==2,"exit code and hash verification required");}
  }
  calls=0;try{await FlashPlan.Execute("COM4 & injected","f.bin",delegate(string args){calls++;return Task.FromResult(new FlashCommandResult());});throw new Exception("invalid port accepted");}
  catch(ArgumentException){Require(calls==0,"invalid port blocked before process");}
 }
 static int Main(){
  string folder=Path.Combine(Path.GetTempPath(),"AtomClip-resource-test-"+Guid.NewGuid().ToString("N"));
  try{
   CheckPlan().GetAwaiter().GetResult();Directory.CreateDirectory(folder);
   var extract=typeof(FlashPlan).Assembly.GetType("AtomClipUsb.FirmwareInstaller").GetMethod("Extract",BindingFlags.Static|BindingFlags.NonPublic);
   string tool=(string)extract.Invoke(null,new object[]{"esptool.exe",folder});
   string firmware=(string)extract.Invoke(null,new object[]{"AtomClip-S3U.bin",folder});
   Require(new FileInfo(firmware).Length==8388608,"merged 8MB image extracted and SHA256 verified");
   using(var process=Process.Start(new ProcessStartInfo(tool,"--help"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})){
    var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
    Require(process.WaitForExit(30000),"tool help returned");Require(process.ExitCode==0&&stdout.Result.Contains("write-flash"),"embedded offline tool runs");stderr.GetAwaiter().GetResult();
   }
   Console.WriteLine("PASS: flash plan probe/8MB/chip/port/write/hash guards; embedded resources SHA256 verified; bundled esptool --help executed. No hardware flash or serial access.");return 0;
  }catch(Exception ex){Console.WriteLine("FAIL: "+ex.GetBaseException().Message);return 1;}
  finally{
   // Windows can briefly retain the just-executed embedded tool while scanning it.
   for(int attempt=0;attempt<3&&Directory.Exists(folder);attempt++){
    try{Directory.Delete(folder,true);}catch(IOException){if(attempt==2)Console.WriteLine("NOTE: temporary test tools retained for later cleanup.");else System.Threading.Thread.Sleep(100);}
    catch(UnauthorizedAccessException){if(attempt==2)Console.WriteLine("NOTE: temporary test tools retained for later cleanup.");else System.Threading.Thread.Sleep(100);}
   }
  }
 }
}
