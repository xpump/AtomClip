package dev.atomclip.mobile;

import android.app.*;
import android.content.*;
import android.hardware.usb.UsbDevice;
import android.net.Uri;
import android.os.Bundle;
import android.text.InputType;
import android.view.*;
import android.widget.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.concurrent.CompletableFuture;

/** UI-only owner; the USB workflow and protected registry survive Activity rotation separately. */
final class AndroidBoardsUi {
 final MainActivity a;final DeviceBindings store;final BoardCatalog catalog;final UsbBoardSession usb;
 Dialog devicesDialog,usbDialog;LinearLayout list;TextView usbStatus;Button usbRead,usbApply;EditText alias;TextView scanStatus;Button scanButton;List<BoardDiscovery.Observation> nearby=new ArrayList<>();
 BoardDiscovery scanner;boolean rotating;byte[] pendingExport,pendingImport;
 AndroidBoardsUi(MainActivity activity,DeviceBindings store,BoardCatalog catalog,UsbBoardSession usb,Bundle saved){a=activity;this.store=store;this.catalog=catalog;this.usb=usb;if(saved!=null){pendingExport=saved.getByteArray("bindingExport");pendingImport=saved.getByteArray("bindingImport");}usb.listener=()->{if(!a.isDestroyed()){renderUsb();a.bindingStateChanged();}};if(usb.uiOpen)a.handler.post(()->showUsb(false));if(pendingImport!=null)a.handler.post(()->importPassword());}
 void save(Bundle b){if(pendingExport!=null)b.putByteArray("bindingExport",pendingExport);if(pendingImport!=null)b.putByteArray("bindingImport",pendingImport);}
 void show(){
  if(a.sending){Toast.makeText(a,"请先等待当前发送结束，再管理开发板。",Toast.LENGTH_LONG).show();return;}if(devicesDialog!=null&&devicesDialog.isShowing())return;
  a.autoClipboard.setChecked(false);a.baselineClipboard();
  LinearLayout body=a.column();body.setPadding(a.dp(16),a.dp(10),a.dp(16),a.dp(10));a.label(body,"我的开发板",20);a.label(body,"选择已绑定的板即可连接。扫描附近会按完整编号合并；新板通过 USB 添加。",12).setTextColor(MainActivity.MUTED);
  if(store.loadError!=null)a.label(body,store.loadError,13);
  list=a.column();body.addView(list);renderList();
  a.button(body,"添加开发板",()->openUsb(0,null));
  scanStatus=a.label(body,"通过 USB 添加一次，以后可直接连接。",12);scanStatus.setTextColor(MainActivity.MUTED);
  scanButton=a.button(body,"扫描附近",()->scan());
  a.button(body,"更多…",()->new AlertDialog.Builder(a).setTitle("开发板设置").setItems(new String[]{"导入加密配置…","清除板上绑定…"},(d,w)->{if(w==0){Intent i=new Intent(Intent.ACTION_OPEN_DOCUMENT).setType("*/*").addCategory(Intent.CATEGORY_OPENABLE);a.startActivityForResult(i,302);}else openUsb(3,null);}).setNegativeButton("取消",null).show());
  if(store.loadError!=null)a.button(body,"清除不可读的本地配置…",()->new AlertDialog.Builder(a).setTitle("清除本地配置").setMessage("将创建空配置。需要重新导入加密备份或通过 USB 改绑；不会改写开发板。").setNegativeButton("取消",null).setPositiveButton("清除本地配置",(d,w)->safe(()->{a.stopForBoardSettings();store.resetUnreadable();a.bindingStateChanged();renderList();})).show());
  a.button(body,"完成",()->devicesDialog.dismiss());ScrollView scroll=new ScrollView(a);scroll.addView(body);devicesDialog=new Dialog(a);devicesDialog.setContentView(scroll);devicesDialog.setOnDismissListener(d->{if(scanner!=null){scanner.close();scanner=null;}});devicesDialog.show();devicesDialog.getWindow().setLayout(Math.min(a.getResources().getDisplayMetrics().widthPixels-a.dp(16),a.dp(560)),-2);
 }
 interface Action {void run()throws Exception;}
 void safe(Action action){try{action.run();}catch(Exception e){message(e.getMessage()==null?"操作未完成":e.getMessage());}}
 void message(String s){if(!a.isDestroyed())new AlertDialog.Builder(a).setMessage(s).setPositiveButton("确定",null).show();}
 void renderList(){
  if(list==null||a.isDestroyed())return;list.removeAllViews();LinkedHashMap<String,V2Protocol.Binding> records=new LinkedHashMap<>(store.registry.confirmed);records.putAll(store.registry.pending);
  List<BoardPresence.Row> rows=BoardPresence.merge(records.values(),nearby);
  if(rows.isEmpty())a.label(list,"尚无开发板，点击“添加开发板”开始。",13).setTextColor(MainActivity.MUTED);
  for(BoardPresence.Row row:rows){V2Protocol.Binding b=row.binding;BoardPresence.Observation n=row.nearby;
   boolean live=b!=null&&a.ble!=null&&a.ble.ready&&a.ble.authenticatedInfo()!=null&&V2Protocol.equal(a.ble.authenticatedInfo().id,b.info.id);
   String state=row.conflict?"编号冲突 · 请通过 USB 核对":b!=null&&b.pending?"待 USB 确认":live?"已连接 · 已认证":b!=null?(n==null?"已绑定":"附近 · 连接时验证"):(n.info!=null?"附近 · 未绑定":"附近 · 编号未读取");
   if(b!=null&&!b.pending&&!live&&store.registry.selectedId.equals(row.number()))state="当前 · "+state;
   LinearLayout card=a.card(list);a.label(card,b!=null?b.alias:"开发板 #"+(n.info!=null?n.info.shortId():n.shortId),16);a.label(card,state+(n==null?"":" · "+n.rssi+" dBm"),12).setTextColor(MainActivity.MUTED);
   int profile=b!=null?b.info.profile:n.info!=null?n.info.profile:n.profile;a.label(card,catalog.name(profile)+(row.number().isEmpty()?"":"\n编号 "+row.number()),12).setTextIsSelectable(true);
   if(b!=null){if(!b.pending&&!row.conflict)a.button(card,live?"已连接":"连接",()->safe(()->{a.stopForBoardSettings();store.change(r->r.select(b.info.number()));a.bindingStateChanged();devicesDialog.dismiss();a.desired=true;a.connectNow();})).setEnabled(!live);a.button(card,b.pending?"确认已有绑定":"更多…",()->{if(b.pending)openUsb(1,b);else manage(b);});if(b.pending)a.button(card,"更多…",()->manage(b));}
   else a.button(card,"通过 USB 添加",()->openUsb(0,null));
  }
 }
 void manage(V2Protocol.Binding b){
  String[] options=b.pending?new String[]{"通过 USB 确认","重试待确认写入","更名","忘记本机记录"}:new String[]{"设为当前","识别当前已连接的板","导出加密配置","更名","通过 USB 恢复此配置","忘记本机记录"};
  new AlertDialog.Builder(a).setTitle(b.alias).setItems(options,(dialog,index)->{
   if(b.pending){if(index==0)openUsb(1,b);else if(index==1)openUsb(4,b);else if(index==2)rename(b);else forget(b);return;}
   if(index==0)safe(()->{a.stopForBoardSettings();store.change(r->r.select(b.info.number()));a.bindingStateChanged();renderList();});
   else if(index==1){if(a.ble==null||!a.ble.ready||a.ble.authenticatedInfo()==null||!V2Protocol.equal(a.ble.authenticatedInfo().id,b.info.id)){message("先连接这块已认证开发板；无已知 LED 的板请通过 USB 核对编号。");return;}a.ble.identify().whenComplete((v,e)->{if(e!=null)a.handler.post(()->message(e.getMessage()));});}
   else if(index==2)exportPassword(b);else if(index==3)rename(b);else if(index==4)openUsb(2,b);else forget(b);
  }).setNegativeButton("取消",null).show();
 }
 void rename(V2Protocol.Binding b){EditText input=new EditText(a);input.setText(b.alias);new AlertDialog.Builder(a).setTitle("开发板别名").setView(input).setNegativeButton("取消",null).setPositiveButton("保存",(d,w)->safe(()->{V2Protocol.Binding renamed=new V2Protocol.Binding(b.info,b.controlKey,b.dataKey,input.getText().toString());renamed.pending=b.pending;store.change(r->{if(b.pending)r.pending.put(b.info.number(),renamed);else r.confirmed.put(b.info.number(),renamed);});a.updateBoardSummary();renderList();})).show();}
 void forget(V2Protocol.Binding b){boolean pendingOnly=b.pending&&store.registry.confirmed.containsKey(b.info.number());new AlertDialog.Builder(a).setTitle(pendingOnly?"撤销待确认记录":"忘记本机设备").setMessage(pendingOnly?"只删除待确认记录，保留原已确认绑定，不改写开发板。如果板上已经改绑，原配置将认证失败，需通过 USB 恢复。":"仅删除本机配置，不改写开发板。删除已写入板内的唯一控制密钥后，可能需要物理 USB 重新改绑。").setNegativeButton("取消",null).setPositiveButton(pendingOnly?"撤销待确认":"忘记",(d,w)->safe(()->{a.stopForBoardSettings();store.change(r->{if(pendingOnly)r.pending.remove(b.info.number());else r.forget(b.info.number());});a.bindingStateChanged();renderList();})).show();}
 void scan(){
  if(devicesDialog==null||!devicesDialog.isShowing()||a.isDestroyed())return;
  if(!a.requireBluetoothPermissions(()->scan()))return;if(scanner!=null)return;
  scanner=new BoardDiscovery(a);BoardDiscovery current=scanner;scanButton.setEnabled(false);scanStatus.setText("正在扫描并读取完整编号…");
  current.nearby(a.ble).whenComplete((all,error)->a.handler.post(()->{
   if(a.isDestroyed()||scanner!=current)return;current.close();scanner=null;scanButton.setEnabled(true);
   if(error!=null){scanStatus.setText("扫描未完成，请检查蓝牙与附近设备权限。");return;}
   nearby=all;renderList();scanStatus.setText(all.isEmpty()?"没有发现广播，开发板可能未供电或正被其他客户端连接。":"相同完整编号已合并；未读到编号的广播单独显示。连接时进行身份认证。");
  }));
 }
 EditText passwordInput(){EditText input=new EditText(a);input.setInputType(InputType.TYPE_CLASS_TEXT|InputType.TYPE_TEXT_VARIATION_PASSWORD);input.setSaveEnabled(false);input.setHint("12–128 字符的配置口令");return input;}
 void exportPassword(V2Protocol.Binding binding){EditText one=passwordInput();EditText two=passwordInput();two.setHint("再次输入相同口令");LinearLayout body=a.column();body.setPadding(a.dp(18),0,a.dp(18),0);body.addView(one);body.addView(two);new AlertDialog.Builder(a).setTitle("加密导出绑定").setView(body).setNegativeButton("取消",null).setPositiveButton("导出",(d,w)->{
  String p=one.getText().toString();if(!p.equals(two.getText().toString())){message("两次口令不一致");return;}char[] password=p.toCharArray();CompletableFuture.supplyAsync(()->{try{return V2Protocol.exportBinding(binding,password);}catch(Exception e){throw new java.util.concurrent.CompletionException(e);}finally{Arrays.fill(password,'\0');}}).whenComplete((bytes,error)->a.handler.post(()->{if(a.isDestroyed())return;if(error!=null){message("导出失败，请使用 12–128 字符口令。");return;}pendingExport=bytes;Intent i=new Intent(Intent.ACTION_CREATE_DOCUMENT).setType("application/octet-stream").addCategory(Intent.CATEGORY_OPENABLE);i.putExtra(Intent.EXTRA_TITLE,"AtomClip-"+binding.info.shortId()+".acbind");a.startActivityForResult(i,301);}));
 }).show();}
 void importPassword(){if(pendingImport==null||a.isDestroyed())return;EditText input=passwordInput();new AlertDialog.Builder(a).setTitle("导入可信客户端的配置").setView(input).setNegativeButton("取消",(d,w)->pendingImport=null).setPositiveButton("解密",(d,w)->{
  final byte[] data=pendingImport;pendingImport=null;char[] password=input.getText().toString().toCharArray();CompletableFuture.supplyAsync(()->{try{return V2Protocol.importBinding(data,password);}catch(Exception e){throw new java.util.concurrent.CompletionException(e);}finally{Arrays.fill(password,'\0');}}).whenComplete((binding,error)->a.handler.post(()->{if(a.isDestroyed())return;if(error!=null){message("配置格式、口令或校验无效，未修改本地记录。");return;}
   if(store.registry.pending.containsKey(binding.info.number())){message("此板有待确认记录，请先通过 USB 核对或明确撤销。");return;}
   Runnable commit=()->safe(()->{a.stopForBoardSettings();store.change(r->{r.confirmed.put(binding.info.number(),binding);r.select(binding.info.number());});a.bindingStateChanged();renderList();message("配置已导入并选中；可以连接相同完整编号的开发板。");});
   if(store.registry.confirmed.containsKey(binding.info.number()))new AlertDialog.Builder(a).setTitle("替换本地配置").setMessage("此完整编号已有配置，是否替换为导入内容？不会改写开发板。").setNegativeButton("取消",null).setPositiveButton("替换",(x,y)->commit.run()).show();else commit.run();
  }));
 }).show();}
 boolean activityResult(int request,int result,Intent data){
  if(request!=301&&request!=302)return false;if(result!=Activity.RESULT_OK||data==null){pendingExport=null;return true;}Uri uri=data.getData();if(uri==null)return true;
  if(request==301){byte[] bytes=pendingExport;pendingExport=null;if(bytes==null){message("待导出内容已失效，请重新导出。");return true;}CompletableFuture.runAsync(()->{try(OutputStream out=a.getContentResolver().openOutputStream(uri,"wt")){if(out==null)throw new IOException();out.write(bytes);}catch(Exception e){a.handler.post(()->message("保存未完成，请重新导出。"));}});}
  else CompletableFuture.supplyAsync(()->{try(InputStream in=a.getContentResolver().openInputStream(uri);ByteArrayOutputStream out=new ByteArrayOutputStream()){if(in==null)throw new IOException();byte[] b=new byte[256];int n;while((n=in.read(b))!=-1){if(out.size()+n>328)throw new IOException();out.write(b,0,n);}return out.toByteArray();}catch(Exception e){throw new java.util.concurrent.CompletionException(e);}}).whenComplete((bytes,error)->a.handler.post(()->{if(a.isDestroyed())return;if(error!=null){message("无法读取配置，或文件超过绑定格式上限。");return;}pendingImport=bytes;importPassword();}));return true;
 }
 void openUsb(int mode,V2Protocol.Binding record){safe(()->{if(usb.busy||a.flashSession.busy)throw new IOException("请先完成当前 USB 操作");a.stopForBoardSettings();usb.prepare(mode,record);showUsb(true);});}
 void showUsb(boolean newlyOpened){
  if(usbDialog!=null&&usbDialog.isShowing())return;usb.uiOpen=true;
  LinearLayout body=a.column();body.setPadding(a.dp(18),a.dp(10),a.dp(18),a.dp(10));a.label(body,usb.requestedMode==0?"添加开发板":"USB 开发板管理",20);a.label(body,"用数据线接入开发板，读取后确认即可。已绑定的板自动识别，现有正文配对保留。无 OTG 可从 Windows 导入加密配置。",12);
  alias=new EditText(a);alias.setText(usb.aliasDraft);alias.addTextChangedListener(new android.text.TextWatcher(){public void beforeTextChanged(CharSequence s,int a,int c,int f){}public void onTextChanged(CharSequence s,int a,int b,int c){usb.aliasDraft=s.toString();}public void afterTextChanged(android.text.Editable e){}});alias.setHint("开发板别名");alias.setEnabled(usb.mode==0);body.addView(alias);
  usbStatus=a.label(body,usb.message,13);usbStatus.setTextIsSelectable(true);
  usbRead=a.button(body,"读取开发板",()->readUsb());
  usbApply=a.button(body,"确认添加",()->{
   if(usb.material==null||usb.busy)return;
   Runnable commit=()->safe(()->usb.write(alias.getText().toString(),usb.material.info.configured));
   if(usb.mode==1||usb.mode==5||(usb.mode==0&&!usb.material.info.configured)){commit.run();return;}
   String detail=usb.mode==3?"清除控制绑定并生成新的正文配对，现有客户端配置将失效。":usb.mode==2||usb.mode==4?"将所选配置写入开发板，替换板上现有控制绑定。":"板上已有其他绑定。继续将替换控制绑定，正文配对保留。";
   new AlertDialog.Builder(a).setTitle("确认 USB 目标").setMessage(detail+"\n完整编号："+usb.material.info.number()).setNegativeButton("取消",null).setPositiveButton("确认",(d,w)->commit.run()).show();
  });
  a.button(body,"中止 / 关闭",()->{if(usb.busy)usb.cancel();else usbDialog.dismiss();});ScrollView scroll=new ScrollView(a);scroll.addView(body);usbDialog=new Dialog(a);usbDialog.setContentView(scroll);usbDialog.setOnDismissListener(d->{if(!rotating){usb.uiOpen=false;usb.clearMaterial();}renderList();});usbDialog.show();usbDialog.getWindow().setLayout(Math.min(a.getResources().getDisplayMetrics().widthPixels-a.dp(16),a.dp(560)),-2);renderUsb();
 }
 void readUsb(){
  List<UsbDevice> found=usb.devices();if(found.isEmpty()){message(usb.host()?"未找到原生 USB CDC 设备，请检查 OTG、数据线及运行模式，无需进入下载模式。":"手机不支持 OTG，请从可信 Windows 客户端导入配置。");return;}
  if(found.size()==1){usb.read(found.get(0));return;}
  String[] titles=new String[found.size()];for(int i=0;i<titles.length;i++){UsbDevice d=found.get(i);titles[i]=(d.getProductName()==null?"USB 设备":d.getProductName())+" · "+d.getDeviceName();}
  new AlertDialog.Builder(a).setTitle("选择开发板的 USB 设备").setItems(titles,(d,w)->usb.read(found.get(w))).setNegativeButton("取消",null).show();
 }
 void renderUsb(){
  if(usbStatus==null||usbDialog==null||!usbDialog.isShowing())return;String text=usb.message;if(usb.material!=null){V2Protocol.Info i=usb.material.info;text=catalog.name(i.profile)+"\n完整编号："+i.number()+"\n"+text;}
  MainActivity.setLabel(usbStatus,text);usbRead.setEnabled(!usb.busy&&usb.host());usbApply.setEnabled(!usb.busy&&usb.material!=null);alias.setEnabled(!usb.busy&&usb.mode==0);
  if(!alias.getText().toString().equals(usb.aliasDraft))alias.setText(usb.aliasDraft);
  usbApply.setText(usb.mode==1?"确认已有绑定":usb.mode==5?"使用已有绑定":usb.mode==2?"恢复配置…":usb.mode==3?"清除绑定…":usb.mode==4?"重试写入…":"确认添加");usbDialog.setCancelable(!usb.busy);
 }
 void dispose(boolean rotation){rotating=rotation;if(scanner!=null)scanner.close();usb.listener=null;if(devicesDialog!=null)devicesDialog.dismiss();if(usbDialog!=null)usbDialog.dismiss();}
}
