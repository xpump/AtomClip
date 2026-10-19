package dev.atomclip.mobile;

import android.app.PendingIntent;
import android.content.*;
import android.content.pm.PackageManager;
import android.hardware.usb.*;
import android.os.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import org.json.*;
import java.util.concurrent.*;

/** Retained across rotation; only holds application context. No background flashing. */
final class UsbFlashSession {
 interface Listener {void changed();}
 final Context context;final UsbManager manager;final Handler main=new Handler(Looper.getMainLooper());
 final ExecutorService worker=Executors.newSingleThreadExecutor();
 final String permissionAction;volatile Listener listener;
 volatile boolean busy,cancelled,waitingPermission,inspected,flashing,disposed;
 volatile String message="手机 USB 烧录功能待实机验证。先识别开发板，确认后再覆盖写入。";
 volatile int percent;volatile UsbCdcTransport transport;UsbDevice device;String identity;String profileKey="atoms3u-8m";boolean factory;FirmwareBundle selectedBundle;boolean compatible;
 final BroadcastReceiver receiver=new BroadcastReceiver(){@Override public void onReceive(Context c,Intent i){
  if(permissionAction.equals(i.getAction())&&waitingPermission){waitingPermission=false;if(device!=null&&manager.hasPermission(device))inspectWorker();else finish("USB 权限未授予；可再次识别并授权。",false);}
  else if(UsbManager.ACTION_USB_DEVICE_DETACHED.equals(i.getAction())){UsbDevice detached=i.getParcelableExtra(UsbManager.EXTRA_DEVICE);if(device!=null&&detached!=null&&device.getDeviceId()==detached.getDeviceId()){inspected=false;cancelIfRunning();if(!busy)publish("USB 已拔出，请连接并重新识别。",0);}}
 }};
 UsbFlashSession(Context app){
  context=app;manager=(UsbManager)app.getSystemService(Context.USB_SERVICE);permissionAction=app.getPackageName()+".USB_PERMISSION."+UUID.randomUUID();
  IntentFilter f=new IntentFilter(permissionAction);f.addAction(UsbManager.ACTION_USB_DEVICE_DETACHED);
  if(Build.VERSION.SDK_INT>=33)app.registerReceiver(receiver,f,Context.RECEIVER_NOT_EXPORTED);else app.registerReceiver(receiver,f);
 }
 boolean hasHost(){return manager!=null&&context.getPackageManager().hasSystemFeature(PackageManager.FEATURE_USB_HOST);}
 List<UsbDevice> devices(){ArrayList<UsbDevice> found=new ArrayList<>();if(hasHost())for(UsbDevice d:manager.getDeviceList().values())if(UsbCdcTransport.candidate(d))found.add(d);return found;}
 void notifyChanged(){main.post(()->{Listener l=listener;if(l!=null&&!disposed)l.changed();});}
 void publish(String text,int progress){message=text;percent=progress;notifyChanged();}
 void inspect(UsbDevice selected){
  if(busy||disposed)return;device=selected;inspected=false;cancelled=false;busy=true;percent=0;
  if(!hasHost()){finish("此手机未提供 USB Host / OTG 能力，请使用 Windows 烧录。",false);return;}
  if(!manager.hasPermission(device)){
   waitingPermission=true;publish("请在系统弹窗中允许访问 USB 设备…",0);
   try{PendingIntent pi=PendingIntent.getBroadcast(context,0,new Intent(permissionAction).setPackage(context.getPackageName()),PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE);manager.requestPermission(device,pi);}
   catch(Exception ex){waitingPermission=false;finish("无法申请 USB 权限，请重新连接后重试。",false);}return;
  }
  inspectWorker();
 }
 byte[] readAsset(String name,int maximum)throws IOException{
  try(InputStream in=context.getAssets().open(name);ByteArrayOutputStream out=new ByteArrayOutputStream()){
   byte[] b=new byte[8192];int n;while((n=in.read(b))!=-1){if(out.size()+n>maximum)throw new IOException("内置资源长度异常");out.write(b,0,n);}return out.toByteArray();
  }
 }
 FirmwareBundle firmware()throws IOException{
  try{JSONObject root=new JSONObject(new String(readAsset("firmware/bundle.json",131072),StandardCharsets.UTF_8));if(root.getInt("schema")!=2)throw new IOException("固件目录版本错误");JSONArray builds=root.getJSONArray("builds");JSONObject build=null;for(int i=0;i<builds.length();i++)if(builds.getJSONObject(i).getString("key").equals(profileKey)){if(build!=null)throw new IOException("固件板型重复");build=builds.getJSONObject(i);}if(build==null||!build.getString("chip").equals("esp32s3"))throw new IOException("缺少所选板型固件");
   JSONArray list=build.getJSONArray("segments");FirmwareBundle.Segment[] segs=new FirmwareBundle.Segment[list.length()];for(int i=0;i<segs.length;i++){JSONObject item=list.getJSONObject(i);String file=item.getString("file");if(!file.matches("[a-z0-9-]+\\.bin"))throw new IOException("固件路径无效");byte[] bytes=readAsset("firmware/"+file,8*1024*1024);if(bytes.length!=item.getInt("length"))throw new IOException("固件长度无效");segs[i]=new FirmwareBundle.Segment(item.getInt("offset"),bytes,item.getString("sha256"));}
   String pairHash=build.optString("pairingHash","");if(!pairHash.isEmpty()){byte[] pair=readAsset("pairing.bin",32);try{if(pair.length!=32||!EspRom.digest("SHA-256",pair).equals(pairHash))throw new IOException("迁移固件与客户端旧配对不一致");}finally{Arrays.fill(pair,(byte)0);}}
   return new FirmwareBundle(build.getInt("profile"),build.getInt("flashBytes"),profileKey,pairHash,segs);
  }catch(JSONException e){throw new IOException("固件目录无效",e);}
 }
 void selectProfile(String key){if(busy)throw new IllegalStateException("USB 正忙");if(!profileKey.equals(key)){profileKey=key;inspected=false;selectedBundle=null;publish("板型已改变，请重新识别。",0);}}
 EspRom rom()throws IOException{
  if(cancelled)throw new IOException("已中止");transport=new UsbCdcTransport(manager,device);
  return new EspRom(transport,new EspRom.Progress(){public void update(String s,int p){publish(s,p);}public boolean cancelled(){return cancelled;}});
 }
 void inspectWorker(){
  worker.execute(()->{
   String result;boolean identified=false;
   try{publish("校验内置分段固件…",0);selectedBundle=firmware();EspRom r=rom();identity=r.identify();if(r.flashBytes!=selectedBundle.flashBytes)throw new IOException("Flash 容量与所选板型不同");compatible=selectedBundle.compatible(r.readFlash(0x8000,3072));if(cancelled)throw new IOException("已中止");identified=true;result=identity+"\n"+(compatible?"分区兼容，可保留绑定升级。":"首次安装或分区不兼容，需明确选择清空安装。")+"\n请核对板型与原生 USB 口。";}catch(Exception ex){result=failure(ex);}finally{closeTransport();}
   inspected=identified&&!cancelled;finish(result,inspected);
  });
 }
 void write(){
  if(busy||!inspected||disposed)return;busy=true;flashing=true;cancelled=false;inspected=false;
  worker.execute(()->{
   String result;boolean success=false;
   try{FirmwareBundle bundle=firmware();EspRom r=rom();String actual=r.identify();if(!actual.equals(identity))throw new IOException("识别结果已改变，请重新识别开发板");r.writeBundle(bundle,factory);success=true;result="烧录与校验完成。请拔插开发板，移至内网，再手动连接蓝牙并继续传输。";}catch(Exception ex){result=failure(ex);}finally{closeTransport();flashing=false;}
   if(!success)percent=0;finish(result,false);
  });
 }
 String failure(Exception ex){return (cancelled?"烧录 / 识别已中止。":ex.getMessage()==null?"USB 操作未完成。":ex.getMessage())+"\n未确认烧录成功；请重新进入下载模式后识别并完整重试。";}
 void finish(String s,boolean keepInspection){if(!keepInspection)inspected=false;busy=false;waitingPermission=false;if(percent==100&&!keepInspection&&!cancelled)publish(s,100);else publish(s,keepInspection?5:0);}
 void closeTransport(){UsbCdcTransport t=transport;transport=null;if(t!=null)t.close();}
 void cancelIfRunning(){if(!busy)return;cancelled=true;if(waitingPermission){waitingPermission=false;finish("已取消 USB 授权流程，请重新识别。",false);}else closeTransport();}
 void dispose(){disposed=true;listener=null;cancelIfRunning();try{context.unregisterReceiver(receiver);}catch(IllegalArgumentException ignored){}worker.shutdownNow();}
}
