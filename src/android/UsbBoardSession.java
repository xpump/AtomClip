package dev.atomclip.mobile;

import android.app.PendingIntent;
import android.content.*;
import android.content.pm.PackageManager;
import android.hardware.usb.*;
import android.os.*;
import java.io.*;
import java.util.*;
import java.util.concurrent.*;

/** Retained physical binding workflow. Durable Pending is committed before any key write. */
final class UsbBoardSession {
 interface Listener {void changed();}
 final Context context;final DeviceBindings bindings;final UsbManager manager;
 final Handler main=new Handler(Looper.getMainLooper());final ExecutorService worker=Executors.newSingleThreadExecutor();
 final String permissionAction;volatile Listener listener;volatile boolean busy,waitingPermission,cancelled,disposed,uiOpen;
 volatile String message="接入运行 0.2.0 固件的开发板，先读取完整编号。";
 volatile UsbCdcTransport transport;UsbDevice device;UsbBoardClient.Material material;V2Protocol.Binding existing;int mode,requestedMode;String aliasDraft="我的开发板";
 final BroadcastReceiver receiver=new BroadcastReceiver(){@Override public void onReceive(Context c,Intent intent){
  if(permissionAction.equals(intent.getAction())&&waitingPermission){waitingPermission=false;if(device!=null&&manager.hasPermission(device))readWorker();else finish("USB 权限未授予，请重新读取并授权。");}
  else if(UsbManager.ACTION_USB_DEVICE_DETACHED.equals(intent.getAction())){UsbDevice d=intent.getParcelableExtra(UsbManager.EXTRA_DEVICE);if(d!=null&&device!=null&&d.getDeviceId()==device.getDeviceId()){cancel();clearMaterial();if(!busy)publish("USB 已拔出，请接入运行新版固件的目标板后重新读取。");}}
 }};
 UsbBoardSession(Context app,DeviceBindings bindings){context=app.getApplicationContext();this.bindings=bindings;manager=(UsbManager)context.getSystemService(Context.USB_SERVICE);permissionAction=context.getPackageName()+".BOARD_USB."+UUID.randomUUID();IntentFilter f=new IntentFilter(permissionAction);f.addAction(UsbManager.ACTION_USB_DEVICE_DETACHED);if(Build.VERSION.SDK_INT>=33)context.registerReceiver(receiver,f,Context.RECEIVER_NOT_EXPORTED);else context.registerReceiver(receiver,f);}
 boolean host(){return manager!=null&&context.getPackageManager().hasSystemFeature(PackageManager.FEATURE_USB_HOST);}
 List<UsbDevice> devices(){ArrayList<UsbDevice> list=new ArrayList<>();if(host())for(UsbDevice d:manager.getDeviceList().values())if(UsbCdcTransport.candidate(d))list.add(d);return list;}
 void useRecord(V2Protocol.Binding record){if(existing!=null){Arrays.fill(existing.controlKey,(byte)0);Arrays.fill(existing.dataKey,(byte)0);}existing=record==null?null:V2Protocol.Binding.decode(record.encode());}
 void prepare(int mode,V2Protocol.Binding record){if(busy)throw new IllegalStateException("USB 操作进行中");this.mode=requestedMode=mode;useRecord(record);aliasDraft=existing==null?"我的开发板":existing.alias;clearMaterial();message="用数据线接入开发板，然后读取。无需进入下载模式。";uiOpen=true;}
 void notifyChanged(){main.post(()->{Listener l=listener;if(l!=null&&!disposed)l.changed();});}
 void publish(String text){message=text;notifyChanged();}
 void finish(String text){busy=false;waitingPermission=false;publish(text);}
 void clearMaterial(){UsbBoardClient.Material m=material;material=null;if(m!=null)Arrays.fill(m.dataKey,(byte)0);}
 void read(UsbDevice selected){
  if(busy||disposed)return;clearMaterial();if(requestedMode==0){mode=0;useRecord(null);}device=selected;cancelled=false;busy=true;
  if(!host()){finish("手机不支持 USB Host / OTG；请在 Windows 绑定后导出加密配置，再在手机导入。");return;}
  if(!manager.hasPermission(device)){waitingPermission=true;publish("请在系统弹窗中允许访问 USB 设备…");try{manager.requestPermission(device,PendingIntent.getBroadcast(context,0,new Intent(permissionAction).setPackage(context.getPackageName()),PendingIntent.FLAG_UPDATE_CURRENT|PendingIntent.FLAG_IMMUTABLE));}catch(Exception e){waitingPermission=false;finish("无法申请 USB 权限，请重新接入后重试。");}return;}readWorker();
 }
 UsbBoardClient open()throws IOException{if(cancelled)throw new IOException("操作已取消");transport=new UsbCdcTransport(manager,device);return new UsbBoardClient(transport);}
 void closeTransport(){UsbCdcTransport t=transport;transport=null;if(t!=null)t.close();}
 void readWorker(){worker.execute(()->{UsbBoardClient.Material result=null;String failure=null;try{publish("读取物理 USB 设备信息…");try(UsbBoardClient usb=open()){result=usb.read();if(existing!=null&&!V2Protocol.equal(existing.info.id,result.info.id))throw new IOException("USB 完整编号与所选记录不同，禁止修改");if(cancelled)throw new IOException("读取已取消");}}catch(Exception e){failure=e.getMessage();if(result!=null)Arrays.fill(result.dataKey,(byte)0);result=null;}finally{closeTransport();}
  final UsbBoardClient.Material read=result;final String error=failure;main.post(()->{if(disposed||cancelled){if(read!=null)Arrays.fill(read.dataKey,(byte)0);finish("读取已取消");return;}material=read;if(read!=null&&requestedMode==0){V2Protocol.Binding known=bindings.registry.pending.get(read.info.number());if(known!=null)mode=1;else{known=bindings.registry.confirmed.get(read.info.number());if(known!=null)mode=5;}useRecord(known);if(known!=null)aliasDraft=known.alias;}
   finish(read==null?(error==null?"USB 读取未完成":error):mode==5?"已在本机绑定，核对后可直接使用。":mode==1?"找到待确认配置，将核对上次绑定结果。":"已读取开发板，点击确认添加。");});
 });}
 void write(String alias,boolean allowReplace){
  if(busy||disposed||material==null)return;
  if(material.info.configured&&mode!=1&&mode!=3&&mode!=5&&!allowReplace)throw new IllegalStateException("需明确确认物理改绑");
  final V2Protocol.Info expected=material.info;
  final V2Protocol.Binding candidate=mode==3?null:mode==0?new V2Protocol.Binding(expected,V2Protocol.random(32),material.dataKey,alias):V2Protocol.Binding.decode(existing.encode());
  cancelled=false;busy=true;
  worker.execute(()->{String result;try{
   publish("核对 USB 目标并保存配置…");try(UsbBoardClient usb=open()){
    if(mode==3){usb.reset(expected);bindings.change(r->r.forget(expected.number()));result="板上控制绑定已清除，并生成独立正文配对；旧客户端将无法认证。";}
    else if(mode==1||mode==5){V2Protocol.Info verified=usb.confirm(candidate);bindings.change(r->{if(mode==1){r.pending.get(verified.number()).info=verified;r.confirm(verified.number());}else{r.confirmed.get(verified.number()).info=verified;r.select(verified.number());}});result="已有绑定已核实，可直接连接。";}
    else {bindings.change(r->r.stage(candidate));V2Protocol.Info verified=usb.provision(candidate,allowReplace);bindings.change(r->{r.pending.get(verified.number()).info=verified;r.confirm(verified.number());});result="绑定已保存。将开发板移至内网，再连接选定设备；可加密导出给另一台客户端。";}
   }
  }catch(Exception e){result=(e.getMessage()==null?"USB 操作未完成":e.getMessage())+"\n若已开始写入，待确认密钥已保留；可经 USB 核对或重试写入。";}
   finally{closeTransport();if(candidate!=null){Arrays.fill(candidate.controlKey,(byte)0);Arrays.fill(candidate.dataKey,(byte)0);}}
   clearMaterial();finish(result);
  });notifyChanged();
 }
 void cancel(){if(!busy)return;cancelled=true;if(waitingPermission){waitingPermission=false;finish("USB 授权流程已取消，请重新读取。");}else closeTransport();}
 void dispose(){disposed=true;listener=null;cancel();try{context.unregisterReceiver(receiver);}catch(Exception ignored){}worker.shutdownNow();clearMaterial();useRecord(null);}
}
