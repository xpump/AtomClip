package dev.atomclip.mobile;
import android.bluetooth.*;
import android.content.Context;
import android.os.*;
import java.io.IOException;
import java.security.GeneralSecurityException;
import java.util.*;
import java.util.concurrent.CompletableFuture;

final class BleTransport {
 static final UUID SERVICE=UUID.fromString(V2Protocol.SERVICE),RX=UUID.fromString(V2Protocol.RX),TX=UUID.fromString(V2Protocol.TX),AUTH=UUID.fromString(V2Protocol.AUTH),CCCD=UUID.fromString("00002902-0000-1000-8000-00805f9b34fb");
 interface Listener {void ready();void packet(Protocol.Packet packet);void disconnected(String reason);}
 final Context context;final Listener listener;final Handler handler=new Handler(Looper.getMainLooper());
 final V2Protocol.Binding binding;String address="";
 BoardDiscovery discovery;BoardDiscovery.Probe adopted;BluetoothGatt gatt;BluetoothGattService service;
 BluetoothGattCharacteristic rx,tx,auth;boolean ready,closed,stopReconnect;int mtu=23,writeGeneration,phase;
 final Deque<Send> queue=new ArrayDeque<>();final Protocol.Parser parser=new Protocol.Parser();
 final V2Protocol.Stream authStream=new V2Protocol.Stream(true),sessionStream=new V2Protocol.Stream(false);V2Protocol.Client authentication;
 static final class Send {final BluetoothGattCharacteristic channel;final byte[] bytes;int position,last;final CompletableFuture<Void> result=new CompletableFuture<>();Send(BluetoothGattCharacteristic channel,byte[] b){this.channel=channel;bytes=b;}}
 BleTransport(Context context,Listener listener,V2Protocol.Binding target){this.context=context.getApplicationContext();this.listener=listener;binding=target;}
 V2Protocol.Info authenticatedInfo(){return authentication!=null&&authentication.ready()?authentication.info:null;}
 void main(Runnable action){if(Looper.myLooper()==handler.getLooper())action.run();else handler.post(action);}
 void start(){
  if(binding==null)throw new IllegalStateException("请先在“我的开发板”中添加并选择具体设备");if(binding!=null&&binding.pending)throw new IllegalStateException("请先通过 USB 确认绑定");
  discovery=new BoardDiscovery(context);
  discovery.selected(binding).whenComplete((target,error)->main(()->{
   if(closed)return;
   if(error!=null){Throwable cause=error;while(cause.getCause()!=null)cause=cause.getCause();stopReconnect|=cause instanceof GeneralSecurityException||cause instanceof IllegalArgumentException||cause instanceof SecurityException;close(cause.getMessage()==null?"无法核实选定开发板":cause.getMessage());return;}
   try{
    address=target.observation.address;adopted=target.probe;gatt=adopted.gatt;service=adopted.service;mtu=adopted.mtu;adopted.adopt(callback);negotiate();
   }catch(Exception e){close("选定设备连接失败："+e.getClass().getSimpleName());}
  }));
  handler.postDelayed(()->{if(!ready&&!closed)close("选定开发板连接超时；不会改连其他板");},60000);
 }
 void negotiate(){if(closed)return;phase=2;if(mtu>23||!gatt.requestMtu(185))setup();else handler.postDelayed(()->{if(phase==2&&!closed)setup();},2000);}
 void setup(){
  if(closed||phase>=3)return;phase=3;
  if(service==null)service=gatt.getService(SERVICE);
  if(service==null){close("找不到选定协议的服务；不会自动降级");return;}
  rx=service.getCharacteristic(RX);tx=service.getCharacteristic(TX);auth=service.getCharacteristic(AUTH);
  if(rx==null||tx==null||auth==null){securityFailure("设备服务不完整");return;}
  authentication=new V2Protocol.Client(binding);subscribe(auth);
 }
 void subscribe(BluetoothGattCharacteristic c){BluetoothGattDescriptor d=c.getDescriptor(CCCD);if(d==null||!gatt.setCharacteristicNotification(c,true)){close("无法订阅设备通知");return;}d.setValue(BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE);if(!gatt.writeDescriptor(d))close("通知设置未启动");}
 final BluetoothGattCallback callback=new BluetoothGattCallback(){
  @Override public void onConnectionStateChange(BluetoothGatt g,int status,int state){main(()->{if(closed||g!=gatt)return;if(status!=0||state==BluetoothProfile.STATE_DISCONNECTED){close("BLE 已断开，状态 "+status);return;}if(state==BluetoothProfile.STATE_CONNECTED&&service==null&&phase==1){if(!g.discoverServices())close("无法发现选定设备服务");}});}
  @Override public void onServicesDiscovered(BluetoothGatt g,int status){main(()->{if(g!=gatt||closed)return;if(status!=0){close("选定设备服务发现失败");return;}service=g.getService(SERVICE);negotiate();});}
  @Override public void onMtuChanged(BluetoothGatt g,int size,int status){main(()->{if(g!=gatt||closed)return;if(status==0)mtu=size;if(phase==2)setup();});}
  @Override public void onDescriptorWrite(BluetoothGatt g,BluetoothGattDescriptor d,int status){main(()->{
   if(g!=gatt||closed||!CCCD.equals(d.getUuid()))return;if(status!=0){close("通知订阅失败，代码 "+status);return;}
   UUID channel=d.getCharacteristic().getUuid();
   if(AUTH.equals(channel)){subscribe(tx);return;}
   if(!channel.equals(TX))return;
   phase=4;enqueue(auth,authentication.hello());handler.postDelayed(()->{if(!closed&&!ready)securityFailure("身份认证超时；未开放发送");},15000);
  });}
  @Override public void onCharacteristicChanged(BluetoothGatt g,BluetoothGattCharacteristic c){byte[] b=c.getValue();receive(g,c.getUuid(),b==null?new byte[0]:b.clone());}
  @Override public void onCharacteristicChanged(BluetoothGatt g,BluetoothGattCharacteristic c,byte[] b){receive(g,c.getUuid(),b.clone());}
  @Override public void onCharacteristicWrite(BluetoothGatt g,BluetoothGattCharacteristic c,int status){main(()->{
   if(g!=gatt||closed||queue.isEmpty())return;Send s=queue.peek();if(!c.getUuid().equals(s.channel.getUuid())){close("BLE 写入回执通道不匹配");return;}writeGeneration++;
   if(status!=0){close("BLE 写入失败，代码 "+status);return;}s.position+=s.last;if(s.position==s.bytes.length){queue.remove();s.result.complete(null);}writeNext();
  });}
 };
 void receive(BluetoothGatt g,UUID channel,byte[] bytes){main(()->{
  if(g!=gatt||closed)return;
  try{
   if(AUTH.equals(channel)){
    if(authentication==null)throw new GeneralSecurityException("认证顺序无效");
    for(byte[] frame:authStream.feed(bytes,SystemClock.elapsedRealtime())){byte[] reply=authentication.accept(frame);if(reply!=null)enqueue(auth,reply);else{ready=true;phase=5;listener.ready();}}return;
   }
   if(!channel.equals(TX))return;
   if(!ready||authentication==null||!authentication.ready())throw new GeneralSecurityException("收到认证完成前的数据通知");
   for(byte[] frame:sessionStream.feed(bytes,SystemClock.elapsedRealtime())){
    byte[] plain=authentication.session.open(frame);if(!V2Protocol.validInner(plain))throw new GeneralSecurityException("会话内层帧无效");
    if(plain[4]==8){if(plain.length!=14)throw new GeneralSecurityException("识别回执无效");android.widget.Toast.makeText(context,plain[9]==0?"已请求开发板闪灯":"此板型无已知 LED，请通过 USB 核对完整编号",android.widget.Toast.LENGTH_LONG).show();}
    else for(Protocol.Packet p:parser.feed(plain))listener.packet(p);
   }
  }catch(Exception e){securityFailure("设备认证或会话校验失败："+e.getClass().getSimpleName()+"；不会改连其他板");}
 });}
 CompletableFuture<Void> send(byte[] frame){
  if(Looper.myLooper()!=handler.getLooper()){CompletableFuture<Void> f=new CompletableFuture<>();f.completeExceptionally(new IllegalStateException("BLE 操作需在主线程"));return f;}
  if(!ready||closed||(authentication==null||!authentication.ready())){CompletableFuture<Void> f=new CompletableFuture<>();f.completeExceptionally(new IllegalStateException("设备未认证或未连接，禁止发送"));return f;}
  if(queue.size()>=16){CompletableFuture<Void> f=new CompletableFuture<>();f.completeExceptionally(new IllegalStateException("发送队列已满"));return f;}
  try{if(!V2Protocol.validInner(frame))throw new GeneralSecurityException("内层帧无效");return enqueue(rx,authentication.session.seal(frame));}catch(Exception e){CompletableFuture<Void> f=new CompletableFuture<>();f.completeExceptionally(e);return f;}
 }
 CompletableFuture<Void> identify(){
  if(authenticatedInfo()==null||(authenticatedInfo().capabilities&32)==0){CompletableFuture<Void> f=new CompletableFuture<>();f.completeExceptionally(new IllegalStateException("此板型无已知 LED，请通过 USB 核对编号"));return f;}
  byte[] frame=V2Protocol.usbFrame(new byte[]{1});frame[4]=8;java.util.zip.CRC32 crc=new java.util.zip.CRC32();crc.update(frame,4,6);System.arraycopy(V2Protocol.le32((int)crc.getValue()),0,frame,10,4);return send(frame);
 }
 CompletableFuture<Void> enqueue(BluetoothGattCharacteristic channel,byte[] bytes){Send s=new Send(channel,bytes);if(closed||queue.size()>=16){s.result.completeExceptionally(new IOException("连接已关闭或发送队列已满"));return s.result;}queue.add(s);if(queue.size()==1)writeNext();return s.result;}
 void writeNext(){
  if(queue.isEmpty()||closed)return;Send s=queue.peek();s.last=Math.min(Math.min(180,Math.max(20,mtu-3)),s.bytes.length-s.position);byte[] chunk=Arrays.copyOfRange(s.bytes,s.position,s.position+s.last);s.channel.setWriteType(BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT);s.channel.setValue(chunk);
  try{if(!gatt.writeCharacteristic(s.channel)){close("BLE 写入未启动");return;}}catch(Exception e){close("BLE 写入失败："+e.getClass().getSimpleName());return;}
  int generation=++writeGeneration;handler.postDelayed(()->{if(!closed&&!queue.isEmpty()&&generation==writeGeneration)close("BLE 写入超时，当前会话已关闭");},15000);
 }
 void securityFailure(String reason){stopReconnect=true;close(reason);}
 void close(String reason){
  if(closed)return;closed=true;ready=false;writeGeneration++;handler.removeCallbacksAndMessages(null);
  if(discovery!=null)discovery.close();if(adopted!=null)adopted.close();else if(gatt!=null){try{gatt.disconnect();}catch(Exception ignored){}try{gatt.close();}catch(Exception ignored){}}gatt=null;
  if(authentication!=null)authentication.close();authStream.reset();sessionStream.reset();while(!queue.isEmpty())queue.remove().result.completeExceptionally(new IllegalStateException(reason));listener.disconnected(reason);
 }
}
