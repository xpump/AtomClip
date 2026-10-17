package dev.atomclip.mobile;

import android.Manifest;
import android.app.*;
import android.content.*;
import android.content.pm.PackageManager;
import android.content.res.ColorStateList;
import android.graphics.Color;
import android.graphics.Typeface;
import android.graphics.drawable.*;
import android.os.*;
import android.text.Editable;
import android.text.TextWatcher;
import android.view.*;
import android.widget.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.text.SimpleDateFormat;
import java.util.*;

public final class MainActivity extends Activity implements BleTransport.Listener {
 final Handler handler=new Handler(Looper.getMainLooper());
 byte[] key; BleTransport ble;
 boolean resumed,desired,peerReady,supportsTyping,supportsReceiverStart,supportsDirectKeys,supportsStartupDelay,paused,sending,noProgram=true;
 int startupSeconds=10,subMode,previousRoute=2; boolean fastTyping=true,supportsFastTyping,supportsPinyin,supportsGbk,pinyinInput,gbkInput,pinyinCompatible,keyboardProbeSent;
 TextView keyboardGuide,keyboardReady,switchChinese; Boolean probePrimary,connectPrimary;
 int keyboardMethodIndex=2; RadioGroup keyboardMethods; RadioButton[] methodButtons=new RadioButton[3]; LinearLayout keyboardOptions,pinyinControls; CheckBox pinyinConfirmed,pinyinCompatibility,requireProbe; Button probeChinese;
 final String[] routeDrafts=new String[4]; Spinner environmentMode; RadioButton modeApp,modeSystem;
 TextView routeTitle,textHeading; Button setupStart;
 int epoch,attempts,setupStep=1,jobAction;
 long lastStatus;
 TextView state,logView,size,description,setupStatus; EditText text;
 Button connect,send,setupAction,loadClipboard; CheckBox autoClipboard;
 LinearLayout setupCard,setupBody; RadioGroup modes; Dialog setupDialog;
 File logs; String outgoingId,jobId,lastClipboardText; byte[] outgoingFrame;
 final LinkedHashSet<String> seen=new LinkedHashSet<>(),discarded=new LinkedHashSet<>();
 final ClipboardManager.OnPrimaryClipChangedListener clipboardChanged=()->onClipboardChanged();

 @Override public void onCreate(Bundle saved){
  super.onCreate(saved); logs=new File(getFilesDir(),"logs");
  try(InputStream in=getAssets().open("pairing.bin")){
   key=new byte[32];int p=0,n;while(p<32&&(n=in.read(key,p,32-p))>0)p+=n;if(p!=32)throw new IOException();
  }catch(Exception ex){new AlertDialog.Builder(this).setMessage("配对信息缺失，无法启动。请获取同一套配套程序。").setPositiveButton("退出",(d,w)->finish()).show();return;}
  getWindow().setStatusBarColor(BG);getWindow().setNavigationBarColor(BG);
  getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR|(Build.VERSION.SDK_INT>=26?View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR:0));
  LinearLayout root=column();root.setBackgroundColor(BG);int pad=dp(22);root.setPadding(pad,pad,pad,pad);
  root.setOnApplyWindowInsetsListener((v,i)->{v.setPadding(pad+i.getSystemWindowInsetLeft(),pad+i.getSystemWindowInsetTop(),pad+i.getSystemWindowInsetRight(),pad+i.getSystemWindowInsetBottom());return i;});
  ScrollView scroll=new ScrollView(this);scroll.setBackgroundColor(BG);scroll.setFillViewport(true);scroll.setClipToPadding(false);scroll.addView(root);setContentView(scroll);
  LinearLayout brand=new LinearLayout(this);brand.setGravity(Gravity.CENTER_VERTICAL);root.addView(brand);
  ImageView mark=new ImageView(this);mark.setImageDrawable(getApplicationInfo().loadIcon(getPackageManager()));brand.addView(mark,new LinearLayout.LayoutParams(dp(44),dp(44)));
  TextView title=new TextView(this);title.setText("AtomClip");title.setTextColor(INK);title.setTextSize(30);title.setTypeface(Typeface.create("sans-serif-medium",Typeface.NORMAL));
  LinearLayout.LayoutParams titleParams=new LinearLayout.LayoutParams(0,-2,1);titleParams.setMargins(dp(12),0,0,0);brand.addView(title,titleParams);brand.addView(helpIcon(()->showHelp()),new LinearLayout.LayoutParams(dp(48),dp(48)));
  gap(root,16);
  connect=button(root,"连接开发板",()->toggleConnection());gap(root,12);
  modes=new RadioGroup(this);modes.setOrientation(RadioGroup.HORIZONTAL);modes.setPadding(dp(4),dp(4),dp(4),dp(4));modes.setBackground(shape(SOFT,12,false));
  RadioButton app=modeApp=segment("有内网程序"),system=modeSystem=segment("无内网程序");modes.addView(app,new RadioGroup.LayoutParams(0,-2,1));modes.addView(system,new RadioGroup.LayoutParams(0,-2,1));system.setChecked(true);root.addView(modes);
  modes.setOnCheckedChangeListener((g,id)->{noProgram=id==system.getId();changeRoute();});
  environmentMode=new Spinner(this);environmentMode.setContentDescription("内网允许的方式");
  ArrayAdapter<String> choices=new ArrayAdapter<>(this,android.R.layout.simple_spinner_item,new String[]{"PowerShell","仅 USB 键盘"});choices.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);environmentMode.setAdapter(choices);root.addView(environmentMode,new LinearLayout.LayoutParams(-1,dp(52)));
  environmentMode.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener(){public void onItemSelected(AdapterView<?> parent,View view,int position,long id){subMode=position==1?2:0;changeRoute();}public void onNothingSelected(AdapterView<?> parent){}});
  description=new TextView(this);gap(root,16);
  LinearLayout connectionCard=card(root);label(connectionCard,"连接状态",12).setTextColor(MUTED);state=label(connectionCard,"尚未连接开发板",17);state.setTypeface(Typeface.create("sans-serif-medium",Typeface.NORMAL));
  setupCard=card(root);
  routeTitle=label(setupCard,"",18);setupCard.addView(description);description.setTextSize(14);description.setTextColor(MUTED);
  setupStart=button(setupCard,"设置内网接收…",()->showSetup());
  keyboardOptions=card(root);label(keyboardOptions,"选择输入方式",18);
  keyboardMethods=new RadioGroup(this);keyboardMethods.setOrientation(RadioGroup.VERTICAL);keyboardMethods.setContentDescription("USB 键盘输入方式");keyboardMethods.setPadding(dp(4),dp(4),dp(4),dp(4));keyboardMethods.setBackground(shape(SOFT,12,false));
  String[] methodNames={"纯英文","VUC 微软拼音 · 中英文","GBK 高速 · 中英文"};
  for(int index:new int[]{2,1,0}){methodButtons[index]=segment(methodNames[index]);keyboardMethods.addView(methodButtons[index],new RadioGroup.LayoutParams(-1,-2));}keyboardOptions.addView(keyboardMethods);methodButtons[2].setChecked(true);gbkInput=true;
  keyboardGuide=label(keyboardOptions,"",14);keyboardGuide.setTextColor(MUTED);pinyinControls=column();keyboardOptions.addView(pinyinControls);
  requireProbe=new CheckBox(this);requireProbe.setText("发送前先试输（可选）");requireProbe.setMinHeight(dp(48));requireProbe.setOnCheckedChangeListener((b,c)->{resetKeyboardProbe();updateButtons();});pinyinControls.addView(requireProbe);
  probeChinese=button(pinyinControls,"试输中英文…",()->confirmPinyin(true));
  pinyinCompatibility=new CheckBox(this);pinyinCompatibility.setText("兼容速度");pinyinCompatibility.setMinHeight(dp(48));pinyinCompatibility.setOnCheckedChangeListener((b,c)->{pinyinCompatible=c;resetKeyboardProbe();updateButtons();});pinyinControls.addView(pinyinCompatibility);
  pinyinConfirmed=new CheckBox(this);pinyinConfirmed.setText("试输结果正确：病ABC123，中文!");pinyinConfirmed.setMinHeight(dp(48));pinyinConfirmed.setOnCheckedChangeListener((b,c)->updateButtons());pinyinControls.addView(pinyinConfirmed);
  keyboardReady=label(keyboardOptions,"",14);keyboardReady.setTextColor(MUTED);switchChinese=label(keyboardOptions,"切换到中英文输入",15);switchChinese.setTextColor(BLUE);switchChinese.setMinHeight(dp(48));switchChinese.setFocusable(true);switchChinese.setOnClickListener(v->selectKeyboardMethod(2));
  keyboardMethods.setOnCheckedChangeListener((g,id)->{for(int i=0;i<3;i++)if(methodButtons[i].getId()==id){keyboardMethodIndex=i;pinyinInput=i==1;gbkInput=i==2;resetKeyboardProbe();updateButtons();break;}});
  gap(root,8);textHeading=label(root,"发送文字",20);text=new EditText(this);text.setTextColor(INK);text.setHintTextColor(MUTED);text.setTextSize(17);text.setHint("输入文字，或从剪贴板填入");text.setMinLines(5);text.setGravity(Gravity.TOP);text.setPadding(dp(16),dp(16),dp(16),dp(16));text.setBackground(shape(Color.WHITE,16,true));
  text.setInputType(android.text.InputType.TYPE_CLASS_TEXT|android.text.InputType.TYPE_TEXT_FLAG_MULTI_LINE);root.addView(text,new LinearLayout.LayoutParams(-1,dp(190)));
  size=label(root,"0 / 16384 UTF-8 字节",12);size.setTextColor(MUTED);
  text.addTextChangedListener(new TextWatcher(){public void beforeTextChanged(CharSequence s,int a,int c,int f){}public void onTextChanged(CharSequence s,int a,int b,int c){updateButtons();}public void afterTextChanged(Editable e){}});
  send=button(root,"发送到内网剪贴板",()->{if(keyboardRoute()){if(chineseKeyboard())confirmPinyin(requireProbe.isChecked()&&!(keyboardProbeSent&&pinyinConfirmed.isChecked()));else confirmDirectKeys();}else sendText(text.getText().toString());});
  loadClipboard=button(root,"从剪贴板填入",()->{String value=readClipboard();if(value!=null)text.setText(value);else log("剪贴板中没有可用的纯文本。");});
  autoClipboard=new CheckBox(this);autoClipboard.setText("自动发送前台新复制的文字");autoClipboard.setTextColor(INK);autoClipboard.setTextSize(14);autoClipboard.setMinHeight(dp(48));autoClipboard.setButtonTintList(ColorStateList.valueOf(BLUE));root.addView(autoClipboard);autoClipboard.setOnCheckedChangeListener((b,checked)->baselineClipboard());
  button(root,"更多",()->showMore());
  gap(root,8);
  logView=label(root,"",12);logView.setTextIsSelectable(true);logView.setBackground(shape(Color.WHITE,14,true));logView.setPadding(dp(14),dp(14),dp(14),dp(14));logView.setTextColor(MUTED);logView.setVisibility(View.GONE);
  if(saved!=null){String[] drafts=saved.getStringArray("drafts");if(drafts!=null)System.arraycopy(drafts,0,routeDrafts,0,Math.min(4,drafts.length));subMode=saved.getInt("subMode",0)==2?2:0;environmentMode.setSelection(subMode==2?1:0);if(!saved.getBoolean("noProgram",true))app.setChecked(true);previousRoute=activeRoute();text.setText(saved.getString("draft",""));setupStep=Math.max(1,Math.min(4,saved.getInt("step",1)));desired=saved.getBoolean("desired",false);paused=saved.getBoolean("paused",false);startupSeconds=saved.getInt("startupSeconds",10);fastTyping=saved.getBoolean("fastTyping",true);int method=Math.max(0,Math.min(2,saved.getInt("keyboardMethod",2)));selectKeyboardMethod(method);pinyinInput=method==1;gbkInput=method==2;pinyinCompatibility.setChecked(saved.getBoolean("keyboardCompatible",false));requireProbe.setChecked(saved.getBoolean("requireProbe",false));}
  clipboard().addPrimaryClipChangedListener(clipboardChanged);updateButtons();
  log("App 0.1.0 已启动；日志仅记录状态和字节数，不含正文或配对信息。");
 }
 static final int BG=0xfff5f5f7,INK=0xff1d1d1f,MUTED=0xff6e6e73,BLUE=0xff0071e3,SOFT=0xffe9e9ee;
 int dp(int x){return (int)(x*getResources().getDisplayMetrics().density+.5f);}
 GradientDrawable shape(int color,int radius,boolean border){GradientDrawable d=new GradientDrawable();d.setColor(color);d.setCornerRadius(dp(radius));if(border)d.setStroke(dp(1),0xffe0e0e5);return d;}
 void gap(LinearLayout parent,int height){Space s=new Space(this);parent.addView(s,new LinearLayout.LayoutParams(1,dp(height)));}
 LinearLayout column(){LinearLayout l=new LinearLayout(this);l.setOrientation(LinearLayout.VERTICAL);return l;}
 LinearLayout card(LinearLayout parent){LinearLayout l=column();l.setPadding(dp(18),dp(12),dp(18),dp(14));l.setBackground(shape(Color.WHITE,16,true));LinearLayout.LayoutParams p=new LinearLayout.LayoutParams(-1,-2);p.setMargins(0,0,0,dp(12));parent.addView(l,p);return l;}
 TextView label(LinearLayout parent,String value,int size){TextView t=new TextView(this);t.setText(value);t.setTextColor(INK);t.setTextSize(size);t.setFontFeatureSettings("kern");t.setPadding(0,dp(5),0,dp(7));t.setLineSpacing(dp(3),1);if(size>=18)t.setTypeface(Typeface.create("sans-serif-medium",Typeface.NORMAL));parent.addView(t);return t;}
 Button button(LinearLayout parent,String title,Runnable action){
  boolean primary=title.equals("连接开发板")||title.equals("发送到内网剪贴板")||title.equals("写入启动内容")||title.equals("打开内网接收窗口")||title.equals("完成设置")||title.equals("已出现端口选择窗口");
  Button b=new Button(this);b.setText(title);b.setAllCaps(false);b.setTextSize(16);b.setTypeface(Typeface.create("sans-serif-medium",Typeface.NORMAL));b.setMinHeight(dp(48));b.setMinimumHeight(dp(48));b.setPadding(dp(16),dp(10),dp(16),dp(10));
  StateListDrawable bg=new StateListDrawable();bg.addState(new int[]{-android.R.attr.state_enabled},shape(SOFT,12,false));bg.addState(new int[]{android.R.attr.state_pressed},shape(primary?0xff005fbd:0xffdedee5,12,false));bg.addState(new int[]{},shape(primary?BLUE:SOFT,12,false));
  b.setBackground(new RippleDrawable(ColorStateList.valueOf(0x200071e3),bg,null));b.setTextColor(new ColorStateList(new int[][]{new int[]{-android.R.attr.state_enabled},new int[]{}},new int[]{MUTED,primary?Color.WHITE:INK}));b.setStateListAnimator(null);
  b.setOnClickListener(v->action.run());LinearLayout.LayoutParams params=new LinearLayout.LayoutParams(-1,-2);params.setMargins(0,dp(4),0,dp(4));parent.addView(b,params);return b;
 }
 void styleAction(Button target,boolean primary){StateListDrawable bg=new StateListDrawable();bg.addState(new int[]{-android.R.attr.state_enabled},shape(SOFT,12,false));bg.addState(new int[]{android.R.attr.state_pressed},shape(primary?0xff005fbd:0xffdedee5,12,false));bg.addState(new int[]{},shape(primary?BLUE:SOFT,12,false));target.setBackground(new RippleDrawable(ColorStateList.valueOf(0x200071e3),bg,null));target.setTextColor(new ColorStateList(new int[][]{new int[]{-android.R.attr.state_enabled},new int[]{}},new int[]{MUTED,primary?Color.WHITE:INK}));}
 void styleProbe(boolean primary){if(probePrimary!=null&&probePrimary==primary)return;probePrimary=primary;styleAction(probeChinese,primary);}
 RadioButton segment(String value){RadioButton b=new RadioButton(this);b.setId(View.generateViewId());b.setText(value);b.setButtonDrawable((Drawable)null);b.setGravity(Gravity.CENTER);b.setTextSize(15);b.setTextColor(INK);b.setMinHeight(dp(48));b.setPadding(dp(8),dp(10),dp(8),dp(10));StateListDrawable bg=new StateListDrawable();bg.addState(new int[]{android.R.attr.state_checked},shape(Color.WHITE,9,true));bg.addState(new int[]{},shape(Color.TRANSPARENT,9,false));b.setBackground(bg);return b;}
 View helpIcon(Runnable action){
  View icon=new View(this){android.graphics.Paint p=new android.graphics.Paint(3);
   @Override protected void onDraw(android.graphics.Canvas canvas){super.onDraw(canvas);float x=getWidth()/2f,y=getHeight()/2f,d=dp(12);p.setColor(MUTED);p.setStyle(android.graphics.Paint.Style.STROKE);p.setStrokeWidth(dp(1));canvas.drawRoundRect(x-d,y-d,x+d,y+d,dp(3),dp(3),p);p.setStyle(android.graphics.Paint.Style.FILL);p.setColor(INK);p.setTextSize(dp(17));p.setTextAlign(android.graphics.Paint.Align.CENTER);canvas.drawText("?",x,y-(p.ascent()+p.descent())/2,p);}
  };icon.setContentDescription("使用帮助");icon.setFocusable(true);icon.setClickable(true);icon.setBackground(new RippleDrawable(ColorStateList.valueOf(0x200071e3),null,shape(Color.WHITE,10,false)));icon.setOnClickListener(v->action.run());return icon;
 }
 void helpSection(LinearLayout body,String title){gap(body,16);label(body,title,20);}
 void helpStep(LinearLayout body,String title,String detail){TextView h=label(body,title,16);h.setTypeface(Typeface.DEFAULT,Typeface.BOLD);label(body,detail,15);}
 void helpNote(LinearLayout body,String detail){label(body,detail,14).setTextColor(MUTED);}
 void showHelp(){
  LinearLayout body=column();body.setPadding(dp(24),dp(16),dp(24),dp(16));
  helpSection(body,"选择使用方式");
  helpStep(body,"有内网程序","内网运行 AtomClip USB，连接开发板后可双向传输剪贴板。");
  helpStep(body,"无内网程序","选择 PowerShell 或仅 USB 键盘。各模式保留独立草稿；切换后自动发送关闭。");
  helpSection(body,"PowerShell · 默认双向");
  helpStep(body,"1 · 写入启动内容","内网打开 Win+R，切至系统 ENG、关闭大写锁定、清空运行框，再点击写入。等待完成回执，期间请勿操作内网键鼠。");
  helpNote(body,"启动等待默认 10 秒，慢电脑建议 20–30 秒。快速输入只加速启动后的数据；漏字时可关闭。");
  helpStep(body,"2 · 打开接收窗口","再次准备 ENG 空白运行框，点击打开内网接收窗口。");
  helpStep(body,"3 · 粘贴并执行","在新 PowerShell 窗口按 Ctrl+V、Enter，等待端口选择窗口。");
  helpNote(body,"注意：启动内容含配对信息，请勿截图或分享。需允许 PowerShell、动态编译和 USB 串口。");
  helpStep(body,"4 · 连接开发板","在内网选择开发板对应的 USB 串行端口并连接。默认双向；重新复制内网文字即可回传。");
  helpStep(body,"内网快捷键","B：关闭或恢复内网回传。P：暂停或继续传输。Q：退出。窗口可最小化，须保持运行。");
  helpSection(body,"USB 键盘 · 中英文混排");
  helpStep(body,"GBK 高速","ENG 美式、Num Lock 开启、关闭大写锁定。目标程序出现乱码时改用 VUC。");
  helpStep(body,"VUC 微软拼音","微软拼音全拼“中”、半角、默认 Shift 中英切换，关闭大写锁定。纯英文另有独立选项。");
  helpStep(body,"试输可以跳过","默认直接显示正文，可直接发送。也可点击试输；勾选“发送前先试输”后，须核对“病ABC123，中文!”并勾选结果正确，才能发送正文。漏字时开启兼容速度。");
  helpNote(body,"切换方式、速度或断线会清除确认，草稿保留；未勾选试输检查时仍可发送。仅键盘无法读取内网剪贴板，不支持 emoji。回执只说明按键已发出，需自行核对结果。");
  helpSection(body,"设备与连接");
  helpStep(body,"固件与更新","GBK 需具备 GBK 能力的配套固件，VUC 可用支持该任务的旧固件。跳过试输不绕过固件检查。此前已刷开发版 0.5.4 / 0.5.5 的用户无需重烧，原内网 EXE 仍兼容。新板或缺失功能时用 Windows“开发板设置”烧录。");
  helpStep(body,"手机前台使用","一次只能连接手机或 Windows 外网端。离开前台会断开，返回后重连；不补发离线内容。Android 8–11 需定位，12 及以上需附近设备权限。");
  helpStep(body,"文字与日志","剪贴板模式支持中文、换行和 emoji，单条最多 16384 UTF-8 字节。日志只记录状态和字节数，保留 7 天，可从“更多”导出。");
  helpNote(body,"AtomClip 0.1.0 · Android 8.0+");
  ScrollView scroll=new ScrollView(this);scroll.addView(body);new AlertDialog.Builder(this).setTitle("使用帮助").setView(scroll).setPositiveButton("完成",null).show();
 }
 ClipboardManager clipboard(){return (ClipboardManager)getSystemService(CLIPBOARD_SERVICE);}
 boolean keyboardRoute(){return noProgram&&subMode==2;}
 boolean chineseKeyboard(){return keyboardRoute()&&(pinyinInput||gbkInput);}
 boolean keyboardSupported(){return gbkInput?supportsGbk:pinyinInput?supportsPinyin:supportsDirectKeys;}
 void resetKeyboardProbe(){keyboardProbeSent=false;if(pinyinConfirmed!=null)pinyinConfirmed.setChecked(false);}
 boolean duplexRoute(){return noProgram&&!keyboardRoute();}
 int activeRoute(){return !noProgram?0:keyboardRoute()?3:2;}
 void selectKeyboardMethod(int index){keyboardMethodIndex=index;pinyinInput=index==1;gbkInput=index==2;methodButtons[index].setChecked(true);}
 void changeRoute(){if(text==null)return;int next=activeRoute();if(next!=previousRoute){routeDrafts[previousRoute]=text.getText().toString();previousRoute=next;if(autoClipboard!=null)autoClipboard.setChecked(false);resetKeyboardProbe();baselineClipboard();text.setText(routeDrafts[next]==null?"":routeDrafts[next]);setupStep=1;if(setupDialog!=null)setupDialog.dismiss();}updateButtons();}
 String readClipboard(){try{ClipData clip=clipboard().getPrimaryClip();if(clip!=null&&clip.getItemCount()>0){CharSequence value=clip.getItemAt(0).getText();return value==null?null:value.toString();}}catch(Exception ignored){}return null;}
 void baselineClipboard(){lastClipboardText=readClipboard();}
 void onClipboardChanged(){
  if(!resumed||autoClipboard==null||!autoClipboard.isChecked()||paused||sending||!peerReady)return;
  String value=readClipboard();if(value==null||Objects.equals(value,lastClipboardText))return;
  lastClipboardText=value;if(!value.isEmpty())sendText(value);
 }
 @Override protected void onResume(){super.onResume();resumed=true;baselineClipboard();if(desired&&key!=null)handler.postDelayed(()->connectNow(),500);}
 @Override protected void onSaveInstanceState(Bundle saved){super.onSaveInstanceState(saved);if(text!=null){saved.putString("draft",text.getText().toString());saved.putStringArray("drafts",routeDrafts);saved.putInt("subMode",subMode);saved.putInt("startupSeconds",startupSeconds);saved.putBoolean("fastTyping",fastTyping);saved.putInt("keyboardMethod",keyboardMethodIndex);saved.putBoolean("keyboardCompatible",pinyinCompatible);saved.putBoolean("requireProbe",requireProbe.isChecked());saved.putInt("step",setupStep);saved.putBoolean("desired",desired);saved.putBoolean("paused",paused);saved.putBoolean("noProgram",noProgram);}}
 @Override protected void onPause(){resumed=false;epoch++;clearPending();if(ble!=null)ble.close("应用已离开前台；返回后自动重连。");super.onPause();}
 @Override protected void onDestroy(){desired=false;handler.removeCallbacksAndMessages(null);clipboard().removePrimaryClipChangedListener(clipboardChanged);if(ble!=null)ble.close("应用已退出。");if(key!=null)Arrays.fill(key,(byte)0);super.onDestroy();}
 void clearPending(){outgoingId=null;jobId=null;outgoingFrame=null;sending=false;}
 void toggleConnection(){if(desired){desired=false;if(ble!=null)ble.close("已手动断开。");connect.setText("连接开发板");}else{desired=true;connectNow();}}
 void connectNow(){
  if(!desired||!resumed||key==null||(ble!=null&&!ble.closed))return;
  String[] permissions=Build.VERSION.SDK_INT>=31?new String[]{Manifest.permission.BLUETOOTH_SCAN,Manifest.permission.BLUETOOTH_CONNECT}:new String[]{Manifest.permission.ACCESS_FINE_LOCATION};
  for(String permission:permissions)if(checkSelfPermission(permission)!=PackageManager.PERMISSION_GRANTED){requestPermissions(permissions,101);return;}
  try{ble=new BleTransport(this,this);state.setText("正在连接开发板…");connect.setText("断开");ble.start();}
  catch(Exception ex){log("连接失败："+ex.getClass().getSimpleName()+"。检查蓝牙与权限；Android 8–11 还需开启定位。");if(ble!=null)ble.close("连接未完成。");}
 }
 @Override public void onRequestPermissionsResult(int request,String[] permissions,int[] results){super.onRequestPermissionsResult(request,permissions,results);if(request==101){if(results.length==0){desired=false;return;}for(int result:results)if(result!=PackageManager.PERMISSION_GRANTED){desired=false;log("蓝牙权限未授予，请在系统设置中启用后重试。");return;}connectNow();}}
 @Override public void ready(){log("已连接开发板，等待内网接收状态。");lastStatus=SystemClock.elapsedRealtime();baselineClipboard();heartbeat(epoch);}
 void heartbeat(int generation){
  if(generation!=epoch||!resumed||ble==null||!ble.ready)return;
  if(SystemClock.elapsedRealtime()-lastStatus>18000){ble.close("开发板响应超时，正在重新连接。");return;}
  ble.send(Protocol.frame(Protocol.PING,new byte[0]));handler.postDelayed(()->heartbeat(generation),5000);
 }
 @Override public void disconnected(String reason){peerReady=false;supportsTyping=false;supportsReceiverStart=false;supportsPinyin=false;supportsGbk=false;resetKeyboardProbe();epoch++;clearPending();if(connect==null)return;connect.setText("连接开发板");state.setText(reason);updateButtons();log(reason);if(desired&&resumed){final int generation=epoch;handler.postDelayed(()->{if(generation==epoch)connectNow();},5000);}}
 static void setLabel(TextView view,String value){if(!android.text.TextUtils.equals(view.getText(),value))view.setText(value);}
 void updateButtons(){
  if(send==null)return;boolean connected=ble!=null&&ble.ready;String value=text.getText().toString();boolean valid=false;
  boolean connectIsPrimary=!desired&&!connected;if(connectPrimary==null||connectPrimary!=connectIsPrimary){connectPrimary=connectIsPrimary;styleAction(connect,connectIsPrimary);}
  setLabel(connect,desired?connected?"断开":"取消连接":"连接开发板");
  String sizeText;
  try{int length=Protocol.utf8(value).length;sizeText=length+" / 16384 UTF-8 字节";valid=length>0;}catch(Exception ex){sizeText="文本超出 16 KiB 或包含无效字符，不会截断发送。";}
  if(keyboardRoute()){try{if(gbkInput)Protocol.encodeGbkText(value);else if(pinyinInput)Protocol.normalizePinyinText(value);else Protocol.normalizeDirectText(value);valid=true;sizeText=Protocol.utf8(value.replace("\r\n","\n")).length+" / 16384 UTF-8 字节 · "+(gbkInput?"GBK 中英文混排":pinyinInput?"VUC 中英文混排":"纯英文");}catch(Exception ex){valid=false;sizeText=value.isEmpty()?"0 / 16384 UTF-8 字节":gbkInput?"GBK 不支持部分字符，请改用 VUC 或调整正文。":pinyinInput?"VUC 不支持 emoji、非 BMP 或此控制字符。":"纯英文方式不支持中文，请切换到中英文输入。";}}
  setLabel(size,sizeText);
  boolean body=!chineseKeyboard()||!requireProbe.isChecked()||(keyboardProbeSent&&pinyinConfirmed.isChecked());
  send.setEnabled(connected&&(keyboardRoute()?keyboardSupported()&&!peerReady&&body&&valid:peerReady&&valid)&&!sending&&!paused);setLabel(send,keyboardRoute()?"输入到内网窗口…":"发送到内网剪贴板");
  textHeading.setVisibility(body?View.VISIBLE:View.GONE);text.setVisibility(body?View.VISIBLE:View.GONE);size.setVisibility(body?View.VISIBLE:View.GONE);send.setVisibility(body?View.VISIBLE:View.GONE);loadClipboard.setVisibility(body?View.VISIBLE:View.GONE);
  setupCard.setVisibility(noProgram&&!keyboardRoute()?View.VISIBLE:View.GONE);autoClipboard.setVisibility(keyboardRoute()?View.GONE:View.VISIBLE);environmentMode.setVisibility(noProgram?View.VISIBLE:View.GONE);
  environmentMode.setEnabled(!sending);for(int i=0;i<modes.getChildCount();i++)modes.getChildAt(i).setEnabled(!sending);
  setLabel(routeTitle,keyboardRoute()?"USB 键盘输入":"PowerShell · 剪贴板同步");
  setLabel(description,"支持中文、多行和 emoji。默认双向，内网接收窗口按 B 切换内网回传。");
  keyboardOptions.setVisibility(keyboardRoute()?View.VISIBLE:View.GONE);pinyinControls.setVisibility(chineseKeyboard()?View.VISIBLE:View.GONE);for(RadioButton method:methodButtons)method.setEnabled(!sending);requireProbe.setEnabled(!sending);pinyinConfirmed.setVisibility(requireProbe.isChecked()&&keyboardProbeSent?View.VISIBLE:View.GONE);pinyinConfirmed.setEnabled(keyboardProbeSent&&!sending);pinyinCompatibility.setEnabled(!sending);probeChinese.setVisibility(chineseKeyboard()?View.VISIBLE:View.GONE);probeChinese.setEnabled(connected&&keyboardSupported()&&!peerReady&&!sending&&!paused);setLabel(probeChinese,keyboardProbeSent?"重新试输…":"试输中英文…");styleProbe(!body);
  setLabel(keyboardGuide,gbkInput?"切到 ENG 美式，开启 Num Lock，关闭大写锁定。乱码请换 VUC。":pinyinInput?"切到微软拼音全拼“中”、半角，保留 Shift 中英切换，关闭大写锁定。":"切到 ENG 美式，关闭大写锁定。仅输入英文、数字和符号。");
  setLabel(keyboardReady,!connected?"连接开发板后可试输。":peerReady?"请先退出内网接收器。":!keyboardSupported()?"此方式需更新固件，请用 Windows 外网端“开发板设置”。":sending?"按键发送中，请勿操作内网键鼠。":paused?"传输已暂停，请在“更多”中恢复。":chineseKeyboard()&&requireProbe.isChecked()&&!keyboardProbeSent?"试输“病ABC123，中文!”；成功确认后展开正文。":chineseKeyboard()&&requireProbe.isChecked()&&!pinyinConfirmed.isChecked()?"请核对内网文字，再勾选“试输结果正确”。":value.isEmpty()?"可直接输入正文，也可先试输；开始前有 3 秒准备时间。":!valid?"正文含不支持字符，请查看下方提示。":"已准备好，开始前有 3 秒准备时间。");
  switchChinese.setVisibility(keyboardRoute()&&!chineseKeyboard()&&!value.isEmpty()&&!valid?View.VISIBLE:View.GONE);
  setupStart.setVisibility(keyboardRoute()?View.GONE:View.VISIBLE);setupStart.setText("设置剪贴板同步…");setupStart.setEnabled(!sending);textHeading.setText(keyboardRoute()?"要输入的文字":"发送到内网");
  if(connected&&keyboardRoute())state.setText(paused?"传输已暂停":peerReady?"先退出内网接收器，再使用 USB 键盘":!supportsDirectKeys?"请更新配套固件以使用 USB 键盘":"USB 键盘已连接 · 请将内网光标放好");
  if(connected&&!keyboardRoute())state.setText(paused?"传输已暂停":sending?"正在发送，等待确认…":peerReady?"已就绪 · 可以发送文字":"开发板已连接 · 等待内网接收器");
  if(setupAction!=null)setupAction.setEnabled(!sending&&(setupStep>=3||(connected&&!peerReady&&supportsTyping&&(setupStep!=2||supportsReceiverStart))));
 }
 static void remember(LinkedHashSet<String> set,String id){set.add(id);while(set.size()>256)set.remove(set.iterator().next());}
 @Override public void packet(Protocol.Packet p){
  try{
   if(p.kind==Protocol.STATUS&&p.payload.length>=2){boolean previous=peerReady,typing=p.payload.length>=3&&p.payload[2]==1,receiver=p.payload.length>=4&&p.payload[3]==1,direct=p.payload.length>=5&&p.payload[4]==1,startup=p.payload.length>=6&&p.payload[5]==1,fast=p.payload.length>=7&&p.payload[6]==1,pinyin=p.payload.length>=8&&p.payload[7]==1,gbk=p.payload.length>=9&&p.payload[8]==1,ready=p.payload[0]!=0;boolean changed=ready!=peerReady||typing!=supportsTyping||receiver!=supportsReceiverStart||direct!=supportsDirectKeys||startup!=supportsStartupDelay||fast!=supportsFastTyping||pinyin!=supportsPinyin||gbk!=supportsGbk;peerReady=ready;supportsTyping=typing;supportsReceiverStart=receiver;supportsDirectKeys=direct;supportsStartupDelay=startup;supportsFastTyping=fast;supportsPinyin=pinyin;supportsGbk=gbk;lastStatus=SystemClock.elapsedRealtime();if(previous!=peerReady)baselineClipboard();if(changed)updateButtons();}
   else if(p.kind==Protocol.ACK){String id=Protocol.readAck(p.payload,key);if(id!=null&&id.equals(outgoingId)){outgoingId=null;outgoingFrame=null;sending=false;updateButtons();log("发送成功：已收到内网认证回执。");}}
   else if(p.kind==Protocol.DATA){
    String value=Protocol.decrypt(p.payload,key),id=Protocol.id(p.payload);if(discarded.contains(id))return;
    if(keyboardRoute()){remember(discarded,id);return;}
    if(!resumed||paused){remember(discarded,id);return;}
    if(!seen.contains(id)){lastClipboardText=value;clipboard().setPrimaryClip(ClipData.newPlainText("AtomClip",value));remember(seen,id);log("已写入手机剪贴板："+Protocol.utf8(value).length+" UTF-8 字节。");}
    ble.send(Protocol.frame(Protocol.ACK,Protocol.ack(p.payload,key)));
   }else if(p.kind==Protocol.JOB_RESULT&&jobId!=null){
    int result=Protocol.readResult(p.payload,jobId,key);
    if(result==0)log("开发板已接收任务，请保持内网窗口不变。");
    else if(result>=2){int action=jobAction;jobId=null;sending=false;if(result==2&&(action==4||action==6))keyboardProbeSent=true;else if(result!=2&&action>=3)resetKeyboardProbe();updateButtons();
     if(result==2){log(action==4||action==6?"试输按键已发送；请核对“"+Protocol.KEYBOARD_PROBE_TEXT+"”；启用试输检查时请再勾选确认。":"键盘任务已发送完毕；仍需在内网确认执行结果。");if(action==1||action==2){setupStep=action+1;if(setupDialog!=null&&setupDialog.isShowing())renderSetup();}}
     else log(result==3?"任务被拒绝：内网接收器已在线或其他任务进行中。":"任务已中止，请检查内网窗口。");
    }
   }
  }catch(Exception ex){log("接收处理未完成："+ex.getClass().getSimpleName()+"。未认证的数据不会写入剪贴板。");}
 }
 void sendText(String value){
  if(keyboardRoute())return;
  if(sending||!peerReady||paused||ble==null||!ble.ready)return;
  try{if(value.isEmpty())return;byte[] payload=Protocol.encrypt(value,key);outgoingId=Protocol.id(payload);outgoingFrame=Protocol.frame(Protocol.DATA,payload);attempts=0;sending=true;updateButtons();attemptSend(epoch,outgoingId);}
  catch(Exception ex){log("未发送：需要有效纯文本，单条最多 16384 UTF-8 字节。");}
 }
 void attemptSend(int generation,String id){
  if(generation!=epoch||!id.equals(outgoingId))return;
  if(paused||!peerReady||attempts++>=3){outgoingId=null;outgoingFrame=null;sending=false;updateButtons();log("发送未确认；文字保留在编辑框中，请核对内网后重试。");return;}
  ble.send(outgoingFrame).whenComplete((v,error)->handler.post(()->{
   if(generation!=epoch||!id.equals(outgoingId))return;
   if(error!=null){outgoingId=null;outgoingFrame=null;sending=false;updateButtons();log("BLE 发送失败，请检查连接。");return;}
   handler.postDelayed(()->attemptSend(generation,id),12000);
  }));
 }
 String bootstrap()throws Exception{try(InputStream in=getAssets().open("receiver-duplex.txt")){ByteArrayOutputStream out=new ByteArrayOutputStream();byte[] b=new byte[4096];int n;while((n=in.read(b))>0)out.write(b,0,n);String value=new String(out.toByteArray(),StandardCharsets.UTF_8);Protocol.utf8(value);return value;}}
 void startJob(int action,String value,boolean paste){
  if(sending||paused||peerReady||ble==null||!ble.ready||!supportsTyping||(action==2&&!supportsReceiverStart))return;
  if(action>=3&&!supportsDirectKeys){log("直接键盘输入需先通过 Windows 更新开发板固件。");return;}
  if((action==4||action==5)&&!supportsPinyin){log("请更新配套固件以使用 VUC。");return;}
  if((action==6||action==7)&&!supportsGbk){log("请更新 0.1.0 固件以使用 GBK。");return;}
  if((action==5||action==7)&&requireProbe.isChecked()&&(!keyboardProbeSent||!pinyinConfirmed.isChecked())){log("请先完成中英文试输并确认结果。");return;}
  try{
   int interval=fastTyping&&supportsFastTyping?1:4;
   byte[] payload=action==6||action==7?Protocol.gbkJob(value,pinyinCompatible,key):action==4||action==5?Protocol.pinyinJob(value,pinyinCompatible,key):action==3?Protocol.directKeys(value,key):action==2?Protocol.receiverOpenJob(key):Protocol.job(value,paste,supportsStartupDelay?startupSeconds:0,interval,key);jobId=Protocol.id(payload);jobAction=action;String id=jobId;sending=true;updateButtons();
   int chars=action>=3?value.replace("\r\n","\n").length():action==2?100:260+4*((payload.length-47+2)/3),unicode=0;for(int i=0;i<value.length();i++)if(value.charAt(i)>=128)unicode++;
   final int lower=action>=4?3+(int)Math.ceil(unicode*(action>=6?pinyinCompatible?.125:.029:pinyinCompatible?.16:.07)+(chars-unicode)*.003):action>=3?3+(int)Math.ceil(chars*.003):startupSeconds+7+(int)Math.ceil(chars*(interval==1?.003:.006));
   final int upper=action>=4?10+(int)Math.ceil(unicode*(action>=6?pinyinCompatible?.30:.15:pinyinCompatible?.45:.25)+(chars-unicode)*.020):action>=3?8+(int)Math.ceil(chars*.020):startupSeconds+15+(int)Math.ceil(chars*(interval==1?.020:.035));long began=SystemClock.elapsedRealtime();
   Runnable progress=new Runnable(){public void run(){if(!id.equals(jobId))return;String message="已用 "+((SystemClock.elapsedRealtime()-began)/1000)+" 秒 · 预计约 "+lower+"–"+upper+" 秒；请勿操作内网键鼠";if(setupStatus!=null)setupStatus.setText(message);if(state!=null)state.setText(message);handler.postDelayed(this,1000);}};handler.post(progress);
   ble.send(Protocol.frame(Protocol.TYPE_JOB,payload)).whenComplete((v,error)->handler.post(()->{if(error!=null&&id.equals(jobId)){jobId=null;sending=false;if(action>=3)resetKeyboardProbe();updateButtons();log("键盘任务发送失败，内网执行结果未知。");}}));
   handler.postDelayed(()->{if(id.equals(jobId)){jobId=null;sending=false;if(action>=3)resetKeyboardProbe();updateButtons();log("任务超时，先检查内网窗口，避免重复执行。");}},Math.max(180000,(upper+90)*1000));
  }catch(Exception ex){log("无法创建键盘任务："+ex.getClass().getSimpleName());}
 }
 void showSetup(){
  if(!noProgram)modeSystem.setChecked(true);if(keyboardRoute()){subMode=0;environmentMode.setSelection(0);changeRoute();}
  if(setupDialog!=null&&setupDialog.isShowing()){renderSetup();return;}
  setupDialog=new Dialog(this);setupDialog.requestWindowFeature(Window.FEATURE_NO_TITLE);setupBody=column();setupBody.setPadding(dp(24),dp(20),dp(24),dp(20));setupBody.setBackground(shape(BG,20,false));
  ScrollView scroll=new ScrollView(this);scroll.addView(setupBody);setupDialog.setContentView(scroll);renderSetup();setupDialog.show();setupDialog.getWindow().setBackgroundDrawableResource(android.R.color.transparent);setupDialog.getWindow().setLayout(Math.min(getResources().getDisplayMetrics().widthPixels-dp(24),dp(560)),-2);
 }
 void renderSetup(){
  setupBody.removeAllViews();LinearLayout setupHeader=new LinearLayout(this);setupHeader.setGravity(Gravity.CENTER_VERTICAL);setupBody.addView(setupHeader);TextView setupTitle=new TextView(this);setupTitle.setText("PowerShell 设置");setupTitle.setTextSize(22);setupTitle.setTextColor(INK);setupHeader.addView(setupTitle,new LinearLayout.LayoutParams(0,-2,1));setupHeader.addView(helpIcon(()->showHelp()),new LinearLayout.LayoutParams(dp(48),dp(48)));label(setupBody,"第 "+setupStep+" 步 / 共 4 步",14).setTextColor(BLUE);
  String[] help={
   "内网：Win+R → 系统 ENG → 关闭大写锁定 → 清空运行框。\n\n显示预计范围和已用时间，以完成回执为准；期间请勿操作内网键鼠。",
   "内网：再次打开 Win+R → 系统 ENG → 清空运行框。",
   "内网 PowerShell：Ctrl+V → Enter → 等待端口选择窗口。\n\n启动内容含配对信息，请勿截图或分享。",
   "内网：选择开发板的 USB 串行端口 → 连接。\n\n"+"默认双向，请重新复制内网文字测试回传。按 B 切换内网回传。"+"P 暂停，Q 退出；窗口须保持运行。"};
  label(setupBody,help[setupStep-1],16);
  final EditText delayInput;
  if(setupStep==1){label(setupBody,"默认双向；内网接收窗口按 B 可关闭或恢复内网回传。",14);label(setupBody,"启动等待 3–60 秒（默认 10，慢电脑建议 20–30）",14);delayInput=new EditText(this);delayInput.setInputType(android.text.InputType.TYPE_CLASS_NUMBER);delayInput.setText(String.valueOf(startupSeconds));setupBody.addView(delayInput);CheckBox fast=new CheckBox(this);fast.setText("快速输入（漏字时关闭，改用兼容速度）");fast.setChecked(fastTyping);fast.setOnCheckedChangeListener((b,checked)->fastTyping=checked);setupBody.addView(fast);if(!supportsFastTyping)label(setupBody,"当前固件使用兼容节奏；更新本版固件可加速。",13);}else delayInput=null;
  setupAction=button(setupBody,setupStep==1?"写入启动内容":setupStep==2?"打开内网接收窗口":setupStep==3?"已出现端口选择窗口":"完成设置",()->{
   if(setupStep==1){try{startupSeconds=Integer.parseInt(delayInput.getText().toString());if(startupSeconds<3||startupSeconds>60){setupStatus.setText("请输入 3–60 秒");return;}startJob(1,bootstrap(),false);}catch(Exception ex){log("请检查等待秒数和初始化资源。");}}
   else if(setupStep==2)startJob(2,"",false);
   else if(setupStep==3){setupStep=4;renderSetup();}
   else if(peerReady)setupDialog.dismiss();else setupStatus.setText("尚未收到内网连接状态。请核对 USB 端口和接收窗口。");
  });
  if(setupStep>1)button(setupBody,"上一步",()->{if(!sending){setupStep--;renderSetup();}});
  if(setupStep==1)button(setupBody,"已完成写入，继续打开窗口",()->{if(!sending){setupStep=2;renderSetup();}});
  if(setupStep==4)button(setupBody,"重新打开接收窗口",()->{if(!sending){setupStep=2;renderSetup();}});
  setupStatus=label(setupBody,"",13);updateButtons();
 }
 void showMore(){
  String[] entries={paused?"继续传输":"暂停传输","重新设置内网接收","临时键盘写入","显示 / 隐藏日志","导出日志","开发板初始化说明","无 PowerShell：直接键盘输入"};
  new AlertDialog.Builder(this).setTitle("更多").setItems(entries,(dialog,which)->{
   if(which==0){paused=!paused;baselineClipboard();updateButtons();log(paused?"传输已暂停；已发送的任务无法撤回。":"传输已恢复，离线及暂停期间的内容不会补发。");}
   else if(which==1)showSetup();
   else if(which==2)confirmTyping();
   else if(which==3)logView.setVisibility(logView.getVisibility()==View.VISIBLE?View.GONE:View.VISIBLE);
   else if(which==4){Intent i=new Intent(Intent.ACTION_CREATE_DOCUMENT).setType("text/plain").addCategory(Intent.CATEGORY_OPENABLE);i.putExtra(Intent.EXTRA_TITLE,"AtomClipAndroid-log.txt");startActivityForResult(i,202);}
   else if(which==6){modeSystem.setChecked(true);subMode=2;environmentMode.setSelection(1);changeRoute();}
   else new AlertDialog.Builder(this).setTitle("新开发板").setMessage("新 AtomS3U 的首次 USB 烧录由 Windows 外网 EXE 完成：开发板设置 → 按提示进入下载模式 → 识别并烧录。\n\n手机可独立通过 BLE 完成内网接收设置和日常收发；本 App 不提供 USB 烧录。").setPositiveButton("确定",null).show();
  }).show();
 }
 void confirmTyping(){
  if(keyboardRoute()){if(chineseKeyboard())confirmPinyin(requireProbe.isChecked()&&!(keyboardProbeSent&&pinyinConfirmed.isChecked()));else confirmDirectKeys();return;}
  if(peerReady||sending||!supportsTyping){log("临时键盘写入仅在开发板已连接、内网接收器未运行且无其他任务时可用。");return;}
  final String value=text.getText().toString();try{if(value.isEmpty())return;Protocol.utf8(value);}catch(Exception ex){log("文本无效或超过 16 KiB。");return;}
  CheckBox automatic=new CheckBox(this);automatic.setText("完成后自动粘贴至原窗口");automatic.setPadding(dp(18),0,dp(18),0);
  new AlertDialog.Builder(this).setTitle("临时键盘写入").setMessage("在内网按 Win+R，选择系统 ENG 英语键盘，关闭大写锁定，清空运行框并保持打开。执行期间不要操作内网键鼠。默认完成后在目标窗口按 Ctrl+V；启用自动粘贴前须先确认原窗口的目标位置。\n\n连续发送建议使用常驻接收器。").setView(automatic).setNegativeButton("取消",null).setPositiveButton("开始",(d,w)->startJob(0,value,automatic.isChecked())).show();
 }
 void confirmDirectKeys(){
  String value=text.getText().toString();try{Protocol.directKeys(value,key);}catch(Exception ex){log("直接键盘输入仅支持英文、数字、符号、Tab 和换行（ASCII），不能读取内网剪贴板。");return;}
  new AlertDialog.Builder(this).setTitle("直接键盘输入").setMessage("请把内网光标放入文本编辑框，选择系统 ENG 并关闭大写锁定。开始后有 3 秒准备时间；不运行 PowerShell，不写入剪贴板。Tab 和换行会产生相应按键，仅支持外网到内网单向输入。").setNegativeButton("取消",null).setPositiveButton("开始",(d,w)->startJob(3,value,false)).show();
 }
 void confirmPinyin(boolean probe){
  if(sending||paused||peerReady||ble==null||!ble.ready||!supportsDirectKeys)return;
  if(!keyboardSupported()||(!probe&&requireProbe.isChecked()&&(!keyboardProbeSent||!pinyinConfirmed.isChecked()))){log("请检查固件支持；勾选试输时需完成试输并确认结果。");return;}
  final String value=probe?Protocol.KEYBOARD_PROBE_TEXT:text.getText().toString();final boolean gbk=gbkInput;
  try{if(gbk)Protocol.encodeGbkText(value);else Protocol.normalizePinyinText(value);}catch(Exception ex){log(gbk?"GBK 不支持部分字符，请换 VUC 或调整正文。":"VUC 不支持 emoji、非 BMP 或此控制字符。");return;}
  if(probe)resetKeyboardProbe();
  String preparation=gbk?"内网光标放到空白文本框，切到 ENG 美式，开启 Num Lock，关闭大写锁定。GBK 依赖系统与目标程序处理数字码，乱码请换 VUC。":"内网光标放到空白文本框，切到微软拼音全拼“中”、半角，默认 Shift 中英切换，关闭大写锁定。";
  new AlertDialog.Builder(this).setTitle(probe?"中英文兼容性试输":gbk?"GBK 中英文输入":"VUC 中英文输入").setMessage(preparation+"\n\n"+(probe?"试输："+Protocol.KEYBOARD_PROBE_TEXT+"\n逐字核对汉字、英文、数字和标点；启用“发送前先试输”时请再勾选确认。":"仅发送 USB 按键，不运行 PowerShell；换行、Tab 会操作目标窗口。完成回执不证明文字正确。")+"\n\n开始后有 3 秒准备时间，期间请勿操作内网键鼠。").setNegativeButton("取消",null).setPositiveButton("开始",(d,w)->startJob(gbk?probe?6:7:probe?4:5,value,false)).show();
 }
 void log(String line){
  String stamp=new SimpleDateFormat("yyyy-MM-dd HH:mm:ss.SSS",Locale.ROOT).format(new Date());
  if(logView!=null){String old=logView.getText().toString();if(old.length()>8000)old="";logView.setText(old+stamp+"  "+line+"\n");}
  try{
   logs.mkdirs();File f=new File(logs,"AtomClip-"+stamp.substring(0,10)+".log");File[] files=logs.listFiles();
   if(files!=null)for(File old:files)if(old.lastModified()<System.currentTimeMillis()-7L*86400000)old.delete();
   if(f.length()>=2*1024*1024){File backup=new File(f.getPath()+".1");backup.delete();f.renameTo(backup);}
   try(OutputStream out=new FileOutputStream(f,true)){out.write((stamp+"  "+line.replace('\n',' ').replace('\r',' ')+"\n").getBytes(StandardCharsets.UTF_8));}
  }catch(Exception ignored){if(logView!=null)logView.append("\n日志文件写入失败。\n");}
 }
 @Override protected void onActivityResult(int request,int result,Intent data){
  super.onActivityResult(request,result,data);if(request!=202||result!=RESULT_OK||data==null)return;
  try(OutputStream out=getContentResolver().openOutputStream(data.getData())){File[] files=logs.listFiles();if(files==null)files=new File[0];Arrays.sort(files);byte[] buffer=new byte[8192];for(File f:files)try(InputStream in=new FileInputStream(f)){int n;while((n=in.read(buffer))>0)out.write(buffer,0,n);}log("日志已导出。");}
  catch(Exception ex){log("日志导出失败。");}
 }
}
