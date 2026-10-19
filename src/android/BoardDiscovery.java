package dev.atomclip.mobile;

import android.bluetooth.*;
import android.bluetooth.le.*;
import android.content.Context;
import android.os.*;
import java.io.IOException;
import java.security.GeneralSecurityException;
import java.util.*;
import java.util.concurrent.CompletableFuture;

/** Scan hints are untrusted. Only full Info IDs select candidates; HMAC authenticates later. */
final class BoardDiscovery implements AutoCloseable {
 static final UUID SERVICE=UUID.fromString(V2Protocol.SERVICE);
 final Context context;final Handler handler=new Handler(Looper.getMainLooper());
 final LinkedHashMap<String,Observation> observations=new LinkedHashMap<>();final ArrayList<Probe> probes=new ArrayList<>();
 BluetoothLeScanner scanner;CompletableFuture<List<Observation>> scanFuture,nearbyFuture;boolean closed,scanning;
 static final class Observation extends BoardPresence.Observation {
  BluetoothDevice device;boolean v2;
 }
 BoardDiscovery(Context context){this.context=context.getApplicationContext();}
 static String address(BluetoothDevice d){return d.getAddress().replace(":","").toUpperCase(Locale.ROOT);}
 CompletableFuture<List<Observation>> scan(int milliseconds){
  scanFuture=new CompletableFuture<>();
  try{
   BluetoothAdapter adapter=((BluetoothManager)context.getSystemService(Context.BLUETOOTH_SERVICE)).getAdapter();if(adapter==null||!adapter.isEnabled())throw new IOException("请先打开蓝牙");
   scanner=adapter.getBluetoothLeScanner();if(scanner==null)throw new IOException("BLE 扫描不可用");
   ArrayList<ScanFilter> filters=new ArrayList<>();filters.add(new ScanFilter.Builder().setServiceUuid(new ParcelUuid(SERVICE)).build());
   scanning=true;scanner.startScan(filters,new ScanSettings.Builder().setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY).build(),callback);
   handler.postDelayed(()->{if(scanning&&!closed){stopScan();ArrayList<Observation> result=new ArrayList<>();for(Observation o:observations.values())if(o.v2)result.add(o);Collections.sort(result,(a,b)->a.address.compareTo(b.address));scanFuture.complete(result);}},milliseconds);
  }catch(Exception e){stopScan();scanFuture.completeExceptionally(e);}return scanFuture;
 }
 final ScanCallback callback=new ScanCallback(){
  @Override public void onScanResult(int kind,ScanResult result){handler.post(()->{
   if(!scanning||closed)return;
   try{String key=address(result.getDevice());Observation item=observations.get(key);if(item==null){if(observations.size()>=256)throw new IOException("附近广播过多，请缩小范围后重试");item=new Observation();item.device=result.getDevice();item.address=key;observations.put(key,item);}item.rssi=result.getRssi();
    ScanRecord record=result.getScanRecord();if(record==null)return;List<ParcelUuid> services=record.getServiceUuids();if(services!=null)item.v2|=services.contains(new ParcelUuid(SERVICE));
    String name=record.getDeviceName();if(name!=null&&name.matches("AC-[0-9A-Fa-f]{4}"))item.shortId=name.substring(3).toUpperCase(Locale.ROOT);
    byte[] data=record.getServiceData(new ParcelUuid(SERVICE));if(data!=null&&data.length==13&&data[0]==2){item.v2=true;item.shortId=V2Protocol.hex(V2Protocol.part(data,1,2));item.profile=V2Protocol.u16(data,3);}
   }catch(Exception e){stopScan();scanFuture.completeExceptionally(e);}
  });}
  @Override public void onScanFailed(int error){handler.post(()->{stopScan();if(scanFuture!=null)scanFuture.completeExceptionally(new IOException("BLE 扫描失败，代码 "+error));});}
 };
 void stopScan(){scanning=false;try{if(scanner!=null)scanner.stopScan(callback);}catch(Exception ignored){}}
 static final class Target {final Observation observation;final Probe probe;Target(Observation o,Probe p){observation=o;probe=p;}}
 CompletableFuture<Target> selected(V2Protocol.Binding binding){
  CompletableFuture<Target> result=new CompletableFuture<>();
  if(binding==null){result.completeExceptionally(new IOException("请先选择已绑定开发板"));return result;}
  scan(2500).whenComplete((list,error)->{
   if(error!=null){result.completeExceptionally(error);return;}if(closed){result.completeExceptionally(new IOException("扫描已取消"));return;}
   ArrayList<Observation> candidates=new ArrayList<>();for(Observation o:list)if(o.v2&&o.shortId.equals(binding.info.shortId()))candidates.add(o);
   if(candidates.size()>8){result.completeExceptionally(new GeneralSecurityException("同短号候选过多，请通过 USB 核对"));return;}
   resolve(candidates,0,binding,new ArrayList<>(),new LinkedHashMap<>(),result);
  });return result;
 }
 CompletableFuture<List<Observation>> nearby(BleTransport connected){
  final CompletableFuture<List<Observation>> result=new CompletableFuture<>();nearbyFuture=result;
  scan(3000).whenComplete((list,error)->{
   if(error!=null){result.completeExceptionally(error);return;}if(closed||result.isDone())return;
   int[] next={0},remaining={Math.min(16,list.size())};
   Runnable deadline=()->{if(result.complete(list))for(Probe p:probes)if(!p.adopted)p.close();};
   handler.postDelayed(deadline,12000);result.whenComplete((v,e)->handler.removeCallbacks(deadline));
   if(remaining[0]==0){result.complete(list);return;}
   Runnable lane=new Runnable(){public void run(){
    if(closed||result.isDone()||next[0]>=Math.min(16,list.size()))return;Observation o=list.get(next[0]++);
    if(connected!=null&&connected.ready&&connected.address.equals(o.address)){o.info=connected.authenticatedInfo();if(--remaining[0]==0)result.complete(list);else run();return;}
    Probe p=new Probe(o);probes.add(p);p.start().whenComplete((value,failure)->{
     if(!closed&&!result.isDone()&&failure==null)o.info=value.info;p.close();
     if(--remaining[0]==0)result.complete(list);else run();
    });
   }};for(int i=0;i<3;i++)lane.run();
  });return result;
 }
 void resolve(List<Observation> candidates,int at,V2Protocol.Binding binding,List<V2Protocol.Candidate> ids,Map<String,Probe> matches,CompletableFuture<Target> result){
  if(closed||result.isDone())return;
  if(at==candidates.size()){
   try{V2Protocol.Candidate selected=V2Protocol.selected(ids,binding.info.id);if(selected==null)throw new IOException("未发现选定完整编号；不会改连其他板");Probe p=matches.get(selected.address);if(p==null||p.closed)throw new IOException("选定设备已断开，请重试");result.complete(new Target(p.observation,p));}
   catch(Exception e){result.completeExceptionally(e);}return;
  }
  Observation observation=candidates.get(at);Probe probe=new Probe(observation);probes.add(probe);
  probe.start().whenComplete((p,error)->{
   if(error!=null){result.completeExceptionally(error);return;}if(closed){p.close();return;}
   try{ids.add(new V2Protocol.Candidate(observation.address,p.info,observation.rssi));if(V2Protocol.equal(p.info.id,binding.info.id))matches.put(observation.address,p);else p.close();V2Protocol.selected(ids,binding.info.id);resolve(candidates,at+1,binding,ids,matches,result);}catch(Exception e){result.completeExceptionally(e);}
  });
 }
 final class Probe implements AutoCloseable {
  final Observation observation;final CompletableFuture<Probe> result=new CompletableFuture<>();BluetoothGatt gatt;BluetoothGattService service;V2Protocol.Info info;BluetoothGattCallback forward;boolean done,closed,adopted;int mtu=23;
  Probe(Observation o){observation=o;}
  CompletableFuture<Probe> start(){try{gatt=observation.device.connectGatt(context,false,bridge,BluetoothDevice.TRANSPORT_LE,BluetoothDevice.PHY_LE_1M_MASK,handler);if(gatt==null)throw new IOException("无法连接候选设备");handler.postDelayed(()->{if(!done)fail(new IOException("候选设备信息未核实；不会改连其他板"));},4000);}catch(Exception e){fail(e);}return result;}
  void adopt(BluetoothGattCallback callback){if(closed||!done)throw new IllegalStateException("候选连接不可用");adopted=true;forward=callback;}
  void fail(Exception e){if(!done){done=true;result.completeExceptionally(e);}close();}
  final BluetoothGattCallback bridge=new BluetoothGattCallback(){
   @Override public void onConnectionStateChange(BluetoothGatt g,int status,int state){if(forward!=null){forward.onConnectionStateChange(g,status,state);return;}if(closed)return;if(status!=0||state==BluetoothProfile.STATE_DISCONNECTED){fail(new IOException("候选设备已断开，状态 "+status));return;}if(state==BluetoothProfile.STATE_CONNECTED&&!g.discoverServices())fail(new IOException("候选服务发现失败"));}
   @Override public void onServicesDiscovered(BluetoothGatt g,int status){if(forward!=null){forward.onServicesDiscovered(g,status);return;}if(closed)return;service=g.getService(SERVICE);BluetoothGattCharacteristic characteristic=service==null?null:service.getCharacteristic(UUID.fromString(V2Protocol.INFO));if(status!=0||characteristic==null||!g.readCharacteristic(characteristic))fail(new IOException("候选设备信息特征不可用"));}
   void read(byte[] value,int status){if(closed||done)return;try{if(status!=0)throw new IOException("候选信息读取失败");info=new V2Protocol.Info(value);done=true;result.complete(Probe.this);}catch(Exception e){fail(e);}}
   @Override public void onCharacteristicRead(BluetoothGatt g,BluetoothGattCharacteristic c,int status){if(forward!=null){forward.onCharacteristicRead(g,c,status);return;}read(c.getValue(),status);}
   @Override public void onCharacteristicRead(BluetoothGatt g,BluetoothGattCharacteristic c,byte[] value,int status){if(forward!=null){forward.onCharacteristicRead(g,c,value,status);return;}read(value,status);}
   @Override public void onMtuChanged(BluetoothGatt g,int size,int status){if(status==0)mtu=size;if(forward!=null)forward.onMtuChanged(g,size,status);}
   @Override public void onDescriptorWrite(BluetoothGatt g,BluetoothGattDescriptor d,int status){if(forward!=null)forward.onDescriptorWrite(g,d,status);}
   @Override public void onCharacteristicWrite(BluetoothGatt g,BluetoothGattCharacteristic c,int status){if(forward!=null)forward.onCharacteristicWrite(g,c,status);}
   @Override public void onCharacteristicChanged(BluetoothGatt g,BluetoothGattCharacteristic c){if(forward!=null)forward.onCharacteristicChanged(g,c);}
   @Override public void onCharacteristicChanged(BluetoothGatt g,BluetoothGattCharacteristic c,byte[] value){if(forward!=null)forward.onCharacteristicChanged(g,c,value);}
  };
  public void close(){if(closed)return;closed=true;if(!done){done=true;result.completeExceptionally(new IOException("候选读取已取消"));}forward=null;if(gatt!=null){try{gatt.disconnect();}catch(Exception ignored){}try{gatt.close();}catch(Exception ignored){}}}
 }
 public void close(){if(closed)return;closed=true;stopScan();handler.removeCallbacksAndMessages(null);if(scanFuture!=null&&!scanFuture.isDone())scanFuture.completeExceptionally(new IOException("扫描已取消"));if(nearbyFuture!=null&&!nearbyFuture.isDone())nearbyFuture.completeExceptionally(new IOException("扫描已取消"));for(Probe p:probes)if(!p.adopted)p.close();probes.clear();}
}
