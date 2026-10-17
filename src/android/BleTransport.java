package dev.atomclip.mobile;
import android.bluetooth.*;
import android.bluetooth.le.*;
import android.content.Context;
import android.os.*;
import java.util.*;
import java.util.concurrent.CompletableFuture;

final class BleTransport {
 static final UUID SERVICE=UUID.fromString("a29bc101-12a5-4ea1-81a0-2c4dd9cd0001"), RX=UUID.fromString("a29bc102-12a5-4ea1-81a0-2c4dd9cd0001"), TX=UUID.fromString("a29bc103-12a5-4ea1-81a0-2c4dd9cd0001"), CCCD=UUID.fromString("00002902-0000-1000-8000-00805f9b34fb");
 interface Listener {void ready();void packet(Protocol.Packet packet);void disconnected(String reason);}
 final Context context;final Listener listener;final Handler handler=new Handler(Looper.getMainLooper());
 BluetoothGatt gatt;BluetoothLeScanner scanner;BluetoothGattCharacteristic rx;boolean ready=false,closed=false,discovering=false;
 int mtu=23,writeGeneration=0;final Deque<Send> queue=new ArrayDeque<>();final Protocol.Parser parser=new Protocol.Parser();
 static class Send {byte[] bytes;int position,last;CompletableFuture<Void> result=new CompletableFuture<>();Send(byte[] b){bytes=b;}}
 BleTransport(Context context,Listener listener){this.context=context;this.listener=listener;}
 void start(){
  BluetoothAdapter adapter=((BluetoothManager)context.getSystemService(Context.BLUETOOTH_SERVICE)).getAdapter();
  if(adapter==null||!adapter.isEnabled())throw new IllegalStateException("请先打开系统蓝牙");
  scanner=adapter.getBluetoothLeScanner();if(scanner==null)throw new IllegalStateException("BLE 不可用");
  scanner.startScan(Collections.singletonList(new ScanFilter.Builder().setServiceUuid(new ParcelUuid(SERVICE)).build()),new ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY).build(),scan);
  handler.postDelayed(()->{if(gatt==null&&!closed)close("扫描超时，请检查开发板或关闭另一台 BLE 客户端");},20000);
 }
 final ScanCallback scan=new ScanCallback(){
  @Override public void onScanResult(int type,ScanResult result){handler.post(()->{
   if(gatt!=null||closed)return;
   try{scanner.stopScan(scan);gatt=result.getDevice().connectGatt(context,false,callback,BluetoothDevice.TRANSPORT_LE);
    handler.postDelayed(()->{if(!ready&&!closed)close("BLE 连接超时");},25000);
   }catch(Exception ex){close("连接失败："+ex.getClass().getSimpleName());}
  });}
  @Override public void onScanFailed(int code){handler.post(()->close("BLE 扫描失败，代码 "+code));}
 };
 final BluetoothGattCallback callback=new BluetoothGattCallback(){
  @Override public void onConnectionStateChange(BluetoothGatt g,int status,int state){handler.post(()->{
   if(closed||g!=gatt)return;
   if(status!=BluetoothGatt.GATT_SUCCESS||state==BluetoothProfile.STATE_DISCONNECTED){close("BLE 已断开，状态 "+status);return;}
   if(state==BluetoothProfile.STATE_CONNECTED){if(!g.requestMtu(185))discover();handler.postDelayed(()->{if(!discovering&&!closed)discover();},2000);}
  });}
  @Override public void onMtuChanged(BluetoothGatt g,int size,int status){handler.post(()->{if(g==gatt&&!closed){if(status==0)mtu=size;discover();}});}
  @Override public void onServicesDiscovered(BluetoothGatt g,int status){handler.post(()->{
   if(g!=gatt||closed)return;
   BluetoothGattService service=g.getService(SERVICE);
   if(status!=0||service==null){close("找不到 AtomClip 服务");return;}
   rx=service.getCharacteristic(RX);BluetoothGattCharacteristic tx=service.getCharacteristic(TX);
   if(rx==null||tx==null){close("BLE 服务不完整");return;}
   BluetoothGattDescriptor descriptor=tx.getDescriptor(CCCD);
   if(descriptor==null||!g.setCharacteristicNotification(tx,true)){close("无法订阅通知");return;}
   descriptor.setValue(BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE);
   if(!g.writeDescriptor(descriptor))close("无法写入通知设置");
  });}
  @Override public void onDescriptorWrite(BluetoothGatt g,BluetoothGattDescriptor d,int status){handler.post(()->{if(g!=gatt||closed||ready||!CCCD.equals(d.getUuid()))return;if(status!=0){close("通知订阅失败，代码 "+status);return;}ready=true;listener.ready();});}
  @Override public void onCharacteristicChanged(BluetoothGatt g,BluetoothGattCharacteristic c){byte[] b=c.getValue();receive(g,b==null?new byte[0]:b.clone());}
  @Override public void onCharacteristicChanged(BluetoothGatt g,BluetoothGattCharacteristic c,byte[] b){receive(g,b.clone());}
  @Override public void onCharacteristicWrite(BluetoothGatt g,BluetoothGattCharacteristic c,int status){handler.post(()->{
   if(g!=gatt||closed||queue.isEmpty())return;writeGeneration++;
   if(status!=0){close("BLE 写入失败，代码 "+status);return;}
   Send s=queue.peek();s.position+=s.last;
   if(s.position==s.bytes.length){queue.remove();s.result.complete(null);}writeNext();
  });}
 };
 void receive(BluetoothGatt g,byte[] b){handler.post(()->{if(g==gatt&&!closed)for(Protocol.Packet p:parser.feed(b))listener.packet(p);});}
 void discover(){if(gatt!=null&&!closed&&!discovering){discovering=true;if(!gatt.discoverServices())close("无法发现服务");}}
 CompletableFuture<Void> send(byte[] frame){
  Send s=new Send(frame);if(!ready||closed){s.result.completeExceptionally(new IllegalStateException("BLE 未连接"));return s.result;}
  if(queue.size()>=16){s.result.completeExceptionally(new IllegalStateException("发送队列已满"));return s.result;}
  queue.add(s);if(queue.size()==1)writeNext();return s.result;
 }
 void writeNext(){
  if(queue.isEmpty()||closed)return;Send s=queue.peek();s.last=Math.min(Math.min(180,Math.max(20,mtu-3)),s.bytes.length-s.position);
  byte[] chunk=Arrays.copyOfRange(s.bytes,s.position,s.position+s.last);rx.setWriteType(BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT);rx.setValue(chunk);
  try{if(!gatt.writeCharacteristic(rx)){close("BLE 写入未启动");return;}}catch(Exception ex){close("BLE 写入失败："+ex.getClass().getSimpleName());return;}
  final int generation=++writeGeneration;handler.postDelayed(()->{if(!closed&&!queue.isEmpty()&&generation==writeGeneration)close("BLE 写入超时");},15000);
 }
 void close(String reason){
  if(closed)return;closed=true;ready=false;writeGeneration++;
  try{if(scanner!=null)scanner.stopScan(scan);}catch(Exception ignored){}
  if(gatt!=null){try{gatt.disconnect();}catch(Exception ignored){}try{gatt.close();}catch(Exception ignored){}gatt=null;}
  while(!queue.isEmpty())queue.remove().result.completeExceptionally(new IllegalStateException(reason));listener.disconnected(reason);
 }
}
