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
 byte[] key,legacyKey; BleTransport ble;
 DeviceBindings deviceBindings;BoardCatalog boardCatalog;UsbBoardSession usbBoardSession;AndroidBoardsUi boardsUi;TextView boardSummary;Spinner flashProfileChoice;CheckBox flashFactoryChoice;String appliedDevice="";
 boolean resumed,desired,peerReady,supportsTyping,supportsReceiverStart,supportsDirectKeys,supportsStartupDelay,paused,sending,noProgram=true;
 int startupSeconds=10,subMode,previousRoute=2; boolean fastTyping=true,supportsFastTyping,supportsPinyin,supportsGbk,pinyinInput,gbkInput,gbkCompatible,keyboardProbeSent;
 TextView keyboardGuide,keyboardReady,switchChinese; Boolean probePrimary,connectPrimary;
 int vucGap=4,vucSettle=80; Button vucSpeed;
 String vucSpeedSummary(){return (vucGap==4&&vucSettle==80?"兼容速度":"自定义速度")+"：按键 "+vucGap+"ms，确认等待 "+vucSettle+"ms。";}
 void loadVucSpeed(){
  try{android.content.SharedPreferences prefs=getSharedPreferences("vuc-speed",MODE_PRIVATE);int g=prefs.getInt("gap",4),s=prefs.getInt("settle",80);Protocol.validatePinyinSpeed(g,s);vucGap=g;vucSettle=s;}catch(IllegalArgumentException|ClassCastException e){vucGap=4;vucSettle=80;}
 }
 void showVucSpeed(){
  if(sending)return;
  LinearLayout body=column();body.setPadding(dp(20),dp(8),dp(20),dp(8));
  label(body,"默认兼容速度为 4ms / 80ms。数值越小越快，调快后建议先试输。仅影响 VUC。",13);
  label(body,"按键间隔（ms，1–50）",14);EditText gap=new EditText(this);gap.setInputType(android.text.InputType.TYPE_CLASS_NUMBER);gap.setSingleLine();gap.setMinHeight(dp(48));gap.setContentDescription("按键间隔（毫秒）");gap.setText(Integer.toString(vucGap));body.addView(gap);
  label(body,"确认 / 中英切换等待（ms，20–200）",14);EditText settle=new EditText(this);settle.setInputType(android.text.InputType.TYPE_CLASS_NUMBER);settle.setSingleLine();settle.setMinHeight(dp(48));settle.setContentDescription("确认等待（毫秒）");settle.setText(Integer.toString(vucSettle));body.addView(settle);
  button(body,"恢复兼容速度",()->{gap.setText("4");settle.setText("80");});TextView error=label(body,"",13);error.setTextColor(0xffb3261e);
  ScrollView scroll=new ScrollView(this);scroll.addView(body);AlertDialog dialog=new AlertDialog.Builder(this).setTitle("VUC 自定义速度").setView(scroll).setPositiveButton("保存",null).setNegativeButton("取消",null).create();
  dialog.setOnShowListener(d->dialog.getButton(AlertDialog.BUTTON_POSITIVE).setOnClickListener(v->{
   if(sending){error.setText("请等待当前发送完成。");return;}
   try{int g=Integer.parseInt(gap.getText().toString().trim()),s=Integer.parseInt(settle.getText().toString().trim());Protocol.validatePinyinSpeed(g,s);getSharedPreferences("vuc-speed",MODE_PRIVATE).edit().putInt("gap",g).putInt("settle",s).apply();vucGap=g;vucSettle=s;resetKeyboardProbe();updateButtons();dialog.dismiss();}
   catch(IllegalArgumentException ex){error.setText("请输入整数：按键间隔 1–50ms，确认等待 20–200ms。");}
  }));dialog.show();
 }
 int keyboardMethodIndex=2; RadioGroup keyboardMethods; RadioButton[] methodButtons=new RadioButton[3]; LinearLayout keyboardOptions,pinyinControls; CheckBox pinyinConfirmed,gbkCompatibility,requireProbe; Button probeChinese;
 final String[] routeDrafts=new String[4]; RadioButton modeApp,modeSystem,modeKeyboard;
 EditorHistory[] histories={new EditorHistory(),new EditorHistory(),new EditorHistory(),new EditorHistory()};
 boolean historyMuted,editAtomic,fullscreen,optionsExpanded;
 LinearLayout shell,header,routeChrome,editorPane,editTools,optionDetails,footer; ScrollView contentScroll;
 int normalScroll;
 Button undoEdit,clearEdit,fullscreenEdit,pauseAction,optionsToggle; TextView fullStatus;
 UsbFlashSession flashSession; Dialog flashDialog; TextView flashStatus; ProgressBar flashProgress; Button flashStart;
 static final class Retained {EditorHistory[] histories;UsbFlashSession flash;DeviceBindings bindings;UsbBoardSession usbBoards;}
 final class DraftEditText extends EditText {
  DraftEditText(){super(MainActivity.this);}
  @Override public boolean onTextContextMenuItem(int id){
   if(id==android.R.id.undo){undoDraft();return true;}
   boolean transaction=id==android.R.id.paste||id==android.R.id.pasteAsPlainText||id==android.R.id.cut;
   if(!transaction)return super.onTextContextMenuItem(id);
   histories[previousRoute].boundary();editAtomic=true;
   try{return super.onTextContextMenuItem(id);}finally{editAtomic=false;histories[previousRoute].boundary();captureDraft();}
  }
  @Override public boolean onKeyShortcut(int code,KeyEvent event){if(code==KeyEvent.KEYCODE_Z&&event.isCtrlPressed()&&!event.isShiftPressed()){undoDraft();return true;}return super.onKeyShortcut(code,event);}
 }
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
  super.onCreate(saved); logs=new File(getFilesDir(),"logs");loadVucSpeed();
  try(InputStream in=getAssets().open("pairing.bin")){
   key=new byte[32];int p=0,n;while(p<32&&(n=in.read(key,p,32-p))>0)p+=n;if(p!=32)throw new IOException();
  }catch(Exception ex){new AlertDialog.Builder(this).setMessage("配对信息缺失，无法启动。请获取同一套配套程序。").setPositiveButton("退出",(d,w)->finish()).show();return;}
  legacyKey=key.clone();
  getWindow().setStatusBarColor(BG);getWindow().setNavigationBarColor(BG);
  getWindow().getDecorView().setSystemUiVisibility(View.SYSTEM_UI_FLAG_LIGHT_STATUS_BAR|(Build.VERSION.SDK_INT>=26?View.SYSTEM_UI_FLAG_LIGHT_NAVIGATION_BAR:0));
  if(Build.VERSION.SDK_INT>=30)getWindow().setDecorFitsSystemWindows(false);
  Retained retained=(Retained)getLastNonConfigurationInstance();
  if(retained!=null)histories=retained.histories;
  deviceBindings=retained!=null&&retained.bindings!=null?retained.bindings:new DeviceBindings(getApplicationContext());
  try{boardCatalog=new BoardCatalog(this);}catch(Exception e){new AlertDialog.Builder(this).setMessage("开发板配置资源缺失，请获取完整 APK。").setPositiveButton("退出",(d,w)->finish()).show();return;}
  usbBoardSession=retained!=null&&retained.usbBoards!=null?retained.usbBoards:new UsbBoardSession(getApplicationContext(),deviceBindings);
  flashSession=retained!=null?retained.flash:new UsbFlashSession(getApplicationContext());
  shell=column();shell.setBackgroundColor(BG);int pad=dp(14);shell.setPadding(pad,0,pad,0);
  shell.setOnApplyWindowInsetsListener((v,i)->{
   if(Build.VERSION.SDK_INT>=30){android.graphics.Insets bars=i.getInsets(WindowInsets.Type.systemBars()|WindowInsets.Type.displayCutout()),ime=i.getInsets(WindowInsets.Type.ime());v.setPadding(pad+bars.left,bars.top,pad+bars.right,Math.max(bars.bottom,ime.bottom));return WindowInsets.CONSUMED;}
   // API 26-29 uses adjustResize. Do not add the IME height a second time.
   v.setPadding(pad+i.getSystemWindowInsetLeft(),i.getSystemWindowInsetTop(),pad+i.getSystemWindowInsetRight(),Math.min(i.getSystemWindowInsetBottom(),i.getStableInsetBottom()));return i.consumeSystemWindowInsets();
  });
  setContentView(shell);
  header=new LinearLayout(this);header.setGravity(Gravity.CENTER_VERTICAL);shell.addView(header);
  LinearLayout identity=column();header.addView(identity,new LinearLayout.LayoutParams(0,-2,1));
  TextView title=label(identity,"AtomClip",22);title.setPadding(0,dp(6),0,0);
  boardSummary=label(identity,"我的开发板 · 尚未选择",11);boardSummary.setTextColor(MUTED);boardSummary.setMaxLines(1);boardSummary.setEllipsize(android.text.TextUtils.TruncateAt.END);identity.setMinimumHeight(dp(48));identity.setClickable(true);identity.setFocusable(true);identity.setContentDescription("我的开发板与当前目标");identity.setOnClickListener(v->{if(boardsUi!=null)boardsUi.show();});
  state=label(identity,"尚未连接开发板",12);state.setTextColor(MUTED);state.setMaxLines(2);
  connect=button(header,"连接开发板",()->toggleConnection());connect.setLayoutParams(new LinearLayout.LayoutParams(-2,-2));
  Button more=button(header,"更多",()->showMore());more.setLayoutParams(new LinearLayout.LayoutParams(-2,-2));more.setContentDescription("更多设置与使用帮助");
  contentScroll=new ScrollView(this);contentScroll.setFillViewport(true);contentScroll.setClipToPadding(false);shell.addView(contentScroll,new LinearLayout.LayoutParams(-1,0,1));
  LinearLayout root=column();contentScroll.addView(root);
  routeChrome=column();root.addView(routeChrome);
  modes=new RadioGroup(this);modes.setOrientation(RadioGroup.HORIZONTAL);modes.setBaselineAligned(false);modes.setPadding(dp(4),dp(4),dp(4),dp(4));modes.setBackground(shape(SOFT,12,false));modes.setContentDescription("传输模式");
  modeApp=segment("内网程序");modeSystem=segment("PowerShell");modeKeyboard=segment("USB 键盘");
  for(RadioButton mode:new RadioButton[]{modeApp,modeSystem,modeKeyboard})modes.addView(mode,new RadioGroup.LayoutParams(0,ViewGroup.LayoutParams.MATCH_PARENT,1));
  modeSystem.setChecked(true);routeChrome.addView(modes);gap(routeChrome,8);
  modes.setOnCheckedChangeListener((g,id)->{noProgram=id!=modeApp.getId();subMode=id==modeKeyboard.getId()?2:0;changeRoute();});
  setupCard=card(routeChrome);LinearLayout setupRow=new LinearLayout(this);setupRow.setGravity(Gravity.CENTER_VERTICAL);setupCard.addView(setupRow);
  routeTitle=label(setupRow,"PowerShell · 默认双向",14);routeTitle.setLayoutParams(new LinearLayout.LayoutParams(0,-2,1));
  setupStart=button(setupRow,"设置…",()->showSetup());setupStart.setLayoutParams(new LinearLayout.LayoutParams(-2,-2));
  description=label(setupCard,"支持中文、多行和 emoji；内网按 B 切换回传。",12);description.setTextColor(MUTED);
  keyboardOptions=card(routeChrome);label(keyboardOptions,"输入方式",13).setTypeface(Typeface.DEFAULT,Typeface.BOLD);
  // Let the tallest label determine the row height, then stretch every tile to it.
  // Mixed one/two-line baselines must not shift checked backgrounds outside the row.
  keyboardMethods=new RadioGroup(this);keyboardMethods.setOrientation(RadioGroup.HORIZONTAL);keyboardMethods.setBaselineAligned(false);keyboardMethods.setContentDescription("USB 键盘输入方式");keyboardMethods.setPadding(dp(3),dp(3),dp(3),dp(3));keyboardMethods.setBackground(shape(SOFT,10,false));
  String[] methodNames={"纯英文","VUC\n中英文","GBK\n中英文"};
  for(int index:new int[]{2,1,0}){methodButtons[index]=segment(methodNames[index]);methodButtons[index].setContentDescription(index==2?"GBK 高速中英文":index==1?"VUC 微软拼音中英文":"纯英文输入");keyboardMethods.addView(methodButtons[index],new RadioGroup.LayoutParams(0,ViewGroup.LayoutParams.MATCH_PARENT,1));}
  keyboardOptions.addView(keyboardMethods);methodButtons[2].setChecked(true);gbkInput=true;
  keyboardGuide=label(keyboardOptions,"",12);keyboardGuide.setTextColor(MUTED);
  vucSpeed=button(keyboardOptions,"自定义速度…",()->showVucSpeed());
  pinyinControls=column();keyboardOptions.addView(pinyinControls);
  optionsToggle=button(pinyinControls,"试输与兼容选项 ▾",()->{optionsExpanded=!optionsExpanded;updateButtons();});
  optionDetails=column();pinyinControls.addView(optionDetails);
  requireProbe=new CheckBox(this);requireProbe.setText("发送前先试输（可选）");requireProbe.setTextSize(13);requireProbe.setMinHeight(dp(48));requireProbe.setOnCheckedChangeListener((b,c)->{resetKeyboardProbe();updateButtons();});optionDetails.addView(requireProbe);
  LinearLayout trialRow=new LinearLayout(this);trialRow.setGravity(Gravity.CENTER_VERTICAL);optionDetails.addView(trialRow);
  probeChinese=button(trialRow,"试输中英文…",()->confirmPinyin(true));probeChinese.setLayoutParams(new LinearLayout.LayoutParams(0,-2,1));
  gbkCompatibility=new CheckBox(this);gbkCompatibility.setText("兼容速度");gbkCompatibility.setTextSize(13);gbkCompatibility.setMinHeight(dp(48));gbkCompatibility.setOnCheckedChangeListener((b,c)->{gbkCompatible=c;resetKeyboardProbe();updateButtons();});trialRow.addView(gbkCompatibility);
  pinyinConfirmed=new CheckBox(this);pinyinConfirmed.setText("试输正确：病ABC123，中文!");pinyinConfirmed.setTextSize(13);pinyinConfirmed.setMinHeight(dp(48));pinyinConfirmed.setOnCheckedChangeListener((b,c)->updateButtons());optionDetails.addView(pinyinConfirmed);
  keyboardReady=label(keyboardOptions,"",12);keyboardReady.setTextColor(MUTED);
  switchChinese=label(keyboardOptions,"切换到中英文输入",13);switchChinese.setTextColor(BLUE);switchChinese.setGravity(Gravity.CENTER_VERTICAL);switchChinese.setMinHeight(dp(48));switchChinese.setFocusable(true);switchChinese.setOnClickListener(v->selectKeyboardMethod(2));
  keyboardMethods.setOnCheckedChangeListener((g,id)->{for(int i=0;i<3;i++)if(methodButtons[i].getId()==id){keyboardMethodIndex=i;pinyinInput=i==1;gbkInput=i==2;resetKeyboardProbe();updateButtons();break;}});
  editorPane=column();editorPane.setMinimumHeight(dp(224));editorPane.setPadding(dp(10),0,dp(10),0);editorPane.setBackground(shape(Color.WHITE,14,true));
  root.addView(editorPane,new LinearLayout.LayoutParams(-1,-2,1));
  textHeading=label(editorPane,"发送到内网",13);textHeading.setTypeface(Typeface.DEFAULT,Typeface.BOLD);
  editTools=new LinearLayout(this);editTools.setBaselineAligned(false);editorPane.addView(editTools);
  loadClipboard=editorTool(editTools,"粘贴",()->{String value=readClipboard();if(value!=null)replaceDraft(value);else log("剪贴板中没有可用的纯文本。");});
  undoEdit=editorTool(editTools,"撤销",()->undoDraft());
  clearEdit=editorTool(editTools,"清空",()->replaceDraft(""));
  fullscreenEdit=editorTool(editTools,"全屏",()->setFullscreen(!fullscreen));
  text=new DraftEditText();text.setId(View.generateViewId());text.setSaveEnabled(false);text.setTextColor(INK);text.setHintTextColor(MUTED);text.setTextSize(16);text.setHint("输入文字，或粘贴剪贴板内容");text.setMinHeight(dp(128));text.setGravity(Gravity.TOP);text.setPadding(dp(2),dp(8),dp(2),dp(8));text.setBackgroundColor(Color.TRANSPARENT);
  text.setInputType(android.text.InputType.TYPE_CLASS_TEXT|android.text.InputType.TYPE_TEXT_FLAG_MULTI_LINE);text.setImeOptions(android.view.inputmethod.EditorInfo.IME_FLAG_NO_EXTRACT_UI);editorPane.addView(text,new LinearLayout.LayoutParams(-1,0,1));textHeading.setLabelFor(text.getId());
  size=label(editorPane,"0 / 16384 UTF-8 字节",12);size.setTextColor(MUTED);
  text.addTextChangedListener(new TextWatcher(){
   public void beforeTextChanged(CharSequence s,int a,int c,int f){if(!historyMuted)histories[previousRoute].record(s.toString(),text.getSelectionStart(),text.getSelectionEnd(),a,c,f,SystemClock.elapsedRealtime(),editAtomic||c>2||f>2);}
   public void onTextChanged(CharSequence s,int a,int b,int c){if(!historyMuted)histories[previousRoute].remember(s.toString(),text.getSelectionStart(),text.getSelectionEnd());updateButtons();}
   public void afterTextChanged(Editable e){}
  });
  footer=column();shell.addView(footer);
  fullStatus=label(footer,"尚未连接开发板",12);fullStatus.setTextColor(MUTED);fullStatus.setMaxLines(2);fullStatus.setEllipsize(android.text.TextUtils.TruncateAt.END);fullStatus.setAccessibilityLiveRegion(View.ACCESSIBILITY_LIVE_REGION_POLITE);fullStatus.setVisibility(View.GONE);
  LinearLayout transferTools=new LinearLayout(this);transferTools.setGravity(Gravity.CENTER_VERTICAL);footer.addView(transferTools);
  send=button(transferTools,"发送到内网剪贴板",()->{if(keyboardRoute()){if(chineseKeyboard())confirmPinyin(requireProbe.isChecked()&&!(keyboardProbeSent&&pinyinConfirmed.isChecked()));else confirmDirectKeys();}else sendText(text.getText().toString());});send.setLayoutParams(new LinearLayout.LayoutParams(0,-2,1));
  pauseAction=button(transferTools,"暂停",()->togglePaused());LinearLayout.LayoutParams pauseParams=new LinearLayout.LayoutParams(-2,-2);pauseParams.setMargins(dp(8),0,0,0);pauseAction.setLayoutParams(pauseParams);pauseAction.setContentDescription("暂停后续传输；已经发出的按键无法撤回");
  autoClipboard=new CheckBox(this);autoClipboard.setText("自动发送前台新复制的文字");autoClipboard.setTextColor(INK);autoClipboard.setTextSize(13);autoClipboard.setMinHeight(dp(48));autoClipboard.setButtonTintList(ColorStateList.valueOf(BLUE));footer.addView(autoClipboard);autoClipboard.setOnCheckedChangeListener((b,checked)->baselineClipboard());
  logView=label(root,"",12);logView.setTextIsSelectable(true);logView.setTextColor(MUTED);logView.setVisibility(View.GONE);
  if(saved!=null){
   String[] drafts=saved.getStringArray("drafts");if(retained==null&&drafts!=null)for(int i=0;i<Math.min(4,drafts.length);i++)histories[i].value=drafts[i]==null?"":drafts[i];
   noProgram=saved.getBoolean("noProgram",true);subMode=saved.getInt("subMode",0)==2?2:0;
   previousRoute=activeRoute();modes.check(!noProgram?modeApp.getId():subMode==2?modeKeyboard.getId():modeSystem.getId());
   historyMuted=true;text.setText(retained!=null?histories[previousRoute].value:saved.getString("draft",""));historyMuted=false;
   int start=saved.getInt("selectionStart",text.length()),end=saved.getInt("selectionEnd",start);text.setSelection(Math.max(0,Math.min(start,text.length())),Math.max(0,Math.min(end,text.length())));
   setupStep=Math.max(1,Math.min(4,saved.getInt("step",1)));desired=saved.getBoolean("desired",false);paused=saved.getBoolean("paused",false);startupSeconds=saved.getInt("startupSeconds",10);fastTyping=saved.getBoolean("fastTyping",true);
   selectKeyboardMethod(Math.max(0,Math.min(2,saved.getInt("keyboardMethod",2))));gbkCompatibility.setChecked(saved.getBoolean("keyboardCompatible",false));requireProbe.setChecked(saved.getBoolean("requireProbe",false));
   optionsExpanded=saved.getBoolean("optionsExpanded",false);setFullscreen(saved.getBoolean("fullscreen",false));normalScroll=saved.getInt("normalScroll",0);
   int editorX=saved.getInt("editorX",0),editorY=saved.getInt("editorY",0),pageY=saved.getInt("pageY",0);contentScroll.post(()->{text.scrollTo(editorX,editorY);contentScroll.scrollTo(0,pageY);});
  }
  boardsUi=new AndroidBoardsUi(this,deviceBindings,boardCatalog,usbBoardSession,saved);
  applyDeviceKey();if(saved==null&&deviceBindings.loadError==null&&deviceBindings.registry.active()!=null)desired=true;
  attachFlashSession();
  clipboard().addPrimaryClipChangedListener(clipboardChanged);updateButtons();
  log("App 0.2.0 已启动；日志仅记录状态和字节数，不含正文或配对信息。");
 }
 static final int BG=0xfff5f5f7,INK=0xff1d1d1f,MUTED=0xff6e6e73,BLUE=0xff0071e3,SOFT=0xffe9e9ee;
 int dp(int x){return (int)(x*getResources().getDisplayMetrics().density+.5f);}
 GradientDrawable shape(int color,int radius,boolean border){GradientDrawable d=new GradientDrawable();d.setColor(color);d.setCornerRadius(dp(radius));if(border)d.setStroke(dp(1),0xffe0e0e5);return d;}
 void gap(LinearLayout parent,int height){Space s=new Space(this);parent.addView(s,new LinearLayout.LayoutParams(1,dp(height)));}
 LinearLayout column(){LinearLayout l=new LinearLayout(this);l.setOrientation(LinearLayout.VERTICAL);return l;}
 LinearLayout card(LinearLayout parent){LinearLayout l=column();l.setPadding(dp(12),dp(6),dp(12),dp(6));l.setBackground(shape(Color.WHITE,16,true));LinearLayout.LayoutParams p=new LinearLayout.LayoutParams(-1,-2);p.setMargins(0,0,0,dp(8));parent.addView(l,p);return l;}
 TextView label(LinearLayout parent,String value,int size){TextView t=new TextView(this);t.setText(value);t.setTextColor(INK);t.setTextSize(size);t.setFontFeatureSettings("kern");t.setPadding(0,dp(3),0,dp(4));t.setLineSpacing(dp(1),1);if(size>=18)t.setTypeface(Typeface.create("sans-serif-medium",Typeface.NORMAL));parent.addView(t);return t;}
 Button button(LinearLayout parent,String title,Runnable action){
  boolean primary=title.equals("连接开发板")||title.equals("发送到内网剪贴板")||title.equals("写入启动内容")||title.equals("打开内网接收窗口")||title.equals("完成设置")||title.equals("已出现端口选择窗口");
  Button b=new Button(this);b.setText(title);b.setAllCaps(false);b.setTextSize(14);b.setTypeface(Typeface.create("sans-serif-medium",Typeface.NORMAL));b.setMinHeight(dp(48));b.setMinimumHeight(dp(48));b.setPadding(dp(12),dp(8),dp(12),dp(8));b.setMinimumWidth(0);b.setMinWidth(0);
  StateListDrawable bg=new StateListDrawable();bg.addState(new int[]{-android.R.attr.state_enabled},shape(SOFT,12,false));bg.addState(new int[]{android.R.attr.state_pressed},shape(primary?0xff005fbd:0xffdedee5,12,false));bg.addState(new int[]{},shape(primary?BLUE:SOFT,12,false));
  b.setBackground(new RippleDrawable(ColorStateList.valueOf(0x200071e3),bg,null));b.setTextColor(new ColorStateList(new int[][]{new int[]{-android.R.attr.state_enabled},new int[]{}},new int[]{MUTED,primary?Color.WHITE:INK}));b.setStateListAnimator(null);
  b.setOnClickListener(v->action.run());LinearLayout.LayoutParams params=new LinearLayout.LayoutParams(-1,-2);params.setMargins(0,dp(4),0,dp(4));parent.addView(b,params);return b;
 }
 void styleAction(Button target,boolean primary){StateListDrawable bg=new StateListDrawable();bg.addState(new int[]{-android.R.attr.state_enabled},shape(SOFT,12,false));bg.addState(new int[]{android.R.attr.state_pressed},shape(primary?0xff005fbd:0xffdedee5,12,false));bg.addState(new int[]{},shape(primary?BLUE:SOFT,12,false));target.setBackground(new RippleDrawable(ColorStateList.valueOf(0x200071e3),bg,null));target.setTextColor(new ColorStateList(new int[][]{new int[]{-android.R.attr.state_enabled},new int[]{}},new int[]{MUTED,primary?Color.WHITE:INK}));}
 void styleProbe(boolean primary){if(probePrimary!=null&&probePrimary==primary)return;probePrimary=primary;styleAction(probeChinese,primary);}
 RadioButton segment(String value){RadioButton b=new RadioButton(this);b.setId(View.generateViewId());b.setText(value);b.setButtonDrawable((Drawable)null);b.setGravity(Gravity.CENTER);b.setTextSize(13);b.setTextColor(INK);b.setMinHeight(dp(48));b.setPadding(dp(3),dp(7),dp(3),dp(7));b.setMinimumWidth(0);b.setMinWidth(0);StateListDrawable bg=new StateListDrawable();bg.addState(new int[]{android.R.attr.state_checked},shape(Color.WHITE,9,true));bg.addState(new int[]{},shape(Color.TRANSPARENT,9,false));b.setBackground(new RippleDrawable(ColorStateList.valueOf(0x200071e3),bg,null));b.setTextColor(new ColorStateList(new int[][]{new int[]{-android.R.attr.state_enabled},new int[]{android.R.attr.state_checked},new int[]{}},new int[]{MUTED,BLUE,INK}));return b;}
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
  helpStep(body,"固件与更新","通过“我的开发板 → 添加开发板”读取 USB 设备并确认。已绑定板自动识别，现有正文配对保留。附近扫描按完整编号合并，连接时进行身份认证。需要 0.2.0 或更新固件；已正常绑定的板无需重烧。");
  helpStep(body,"手机前台使用","一次只能连接手机或 Windows 外网端。离开前台会断开，返回后重连；不补发离线内容。Android 8–11 需定位，12 及以上需附近设备权限。");
  helpStep(body,"文字与日志","剪贴板模式支持中文、换行和 emoji，单条最多 16384 UTF-8 字节。日志只记录状态和字节数，保留 7 天，可从“更多”导出。");
  helpSection(body,"编辑与手机烧录");
  helpStep(body,"编辑工具","粘贴、撤销、清空、全屏位于文本框上方。清空立即执行，可撤销恢复；不同模式保留独立草稿和编辑历史。全屏底部仍可发送与暂停后续传输。");
  helpStep(body,"USB OTG 烧录 · 待实机验证","支持 AtomS3U 8MB、Generic ESP32-S3 SPI NOR 4/8/16MB。须为 DIO 80MHz、原生 USB，固件不启用 PSRAM；OPI 不支持。手机需支持 OTG，按板厂说明进入下载模式，选择匹配配置后识别、确认写入。");
  helpNote(body,"分区兼容时正常升级保留 NVS 绑定；首次或分区不兼容须先加密备份，明确清空安装。清空后可经 USB 恢复配置。保持 App 前台及供电；拔线、离开前台或超时后重新识别重试；所有段校验成功才算完成。");
  helpNote(body,"AtomClip 0.2.0 · Android 8.0+");
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
 Button editorTool(LinearLayout row,String title,Runnable action){
  Button b=button(row,title,action);b.setTextSize(13);b.setPadding(dp(2),dp(8),dp(2),dp(8));
  StateListDrawable bg=new StateListDrawable();bg.addState(new int[]{android.R.attr.state_pressed},shape(SOFT,9,false));bg.addState(new int[]{android.R.attr.state_focused},shape(SOFT,9,false));bg.addState(new int[]{},shape(Color.TRANSPARENT,9,false));
  b.setBackground(new RippleDrawable(ColorStateList.valueOf(0x200071e3),bg,shape(Color.WHITE,9,false)));
  b.setTextColor(new ColorStateList(new int[][]{new int[]{-android.R.attr.state_enabled},new int[]{}},new int[]{MUTED,BLUE}));
  LinearLayout.LayoutParams p=new LinearLayout.LayoutParams(0,ViewGroup.LayoutParams.MATCH_PARENT,1);if(row.getChildCount()>1)p.setMarginStart(dp(8));p.topMargin=p.bottomMargin=dp(4);b.setLayoutParams(p);return b;
 }
 void captureDraft(){if(text==null)return;EditorHistory h=histories[previousRoute];h.remember(text.getText().toString(),text.getSelectionStart(),text.getSelectionEnd());}
 void replaceDraft(String value){if(value.equals(text.getText().toString()))return;histories[previousRoute].boundary();editAtomic=true;text.setText(value);text.setSelection(text.length());editAtomic=false;histories[previousRoute].boundary();captureDraft();}
 void undoDraft(){EditorHistory.Snapshot snap=histories[previousRoute].undo();if(snap==null)return;historyMuted=true;text.setText(snap.value);text.setSelection(Math.max(0,Math.min(snap.start,text.length())),Math.max(0,Math.min(snap.end,text.length())));historyMuted=false;captureDraft();updateButtons();}
 void setFullscreen(boolean full){
  captureDraft();int sx=text.getScrollX(),sy=text.getScrollY();
  if(full!=fullscreen){
   if(full){normalScroll=contentScroll.getScrollY();editorPane.removeView(editTools);shell.addView(editTools,0,new LinearLayout.LayoutParams(-1,-2));}
   else{shell.removeView(editTools);editorPane.addView(editTools,1,new LinearLayout.LayoutParams(-1,-2));}
  }
  fullscreen=full;header.setVisibility(full?View.GONE:View.VISIBLE);routeChrome.setVisibility(full?View.GONE:View.VISIBLE);textHeading.setVisibility(full?View.GONE:View.VISIBLE);editorPane.setMinimumHeight(dp(full?128:224));
  fullStatus.setVisibility(full?View.VISIBLE:View.GONE);setLabel(fullStatus,state.getText().toString());setLabel(fullscreenEdit,full?"收起":"全屏");fullscreenEdit.setContentDescription(full?"退出全屏，继续编辑":"全屏编辑文字");
  contentScroll.post(()->{if(fullscreen==full){contentScroll.scrollTo(0,full?0:normalScroll);text.scrollTo(sx,sy);}});updateButtons();
 }
 @Override public void onBackPressed(){if(fullscreen){setFullscreen(false);return;}super.onBackPressed();}
 void togglePaused(){paused=!paused;baselineClipboard();updateButtons();log(paused?"已暂停后续传输；已经发出的按键无法撤回。":"传输已恢复，不补发暂停期间的内容。");}
 void setState(String value){if(state!=null)setLabel(state,value);if(fullStatus!=null)setLabel(fullStatus,value);}
 void changeRoute(){if(text==null)return;int next=activeRoute();if(next!=previousRoute){captureDraft();histories[previousRoute].boundary();previousRoute=next;if(autoClipboard!=null)autoClipboard.setChecked(false);resetKeyboardProbe();baselineClipboard();EditorHistory h=histories[next];historyMuted=true;text.setText(h.value);text.setSelection(Math.min(h.start,text.length()),Math.min(h.end,text.length()));historyMuted=false;setupStep=1;if(setupDialog!=null)setupDialog.dismiss();}updateButtons();}
 String readClipboard(){try{ClipData clip=clipboard().getPrimaryClip();if(clip!=null&&clip.getItemCount()>0){CharSequence value=clip.getItemAt(0).getText();return value==null?null:value.toString();}}catch(Exception ignored){}return null;}
 void baselineClipboard(){lastClipboardText=readClipboard();}
 void onClipboardChanged(){
  if(!resumed||autoClipboard==null||!autoClipboard.isChecked()||paused||sending||!peerReady)return;
  String value=readClipboard();if(value==null||Objects.equals(value,lastClipboardText))return;
  lastClipboardText=value;if(!value.isEmpty())sendText(value);
 }
 @Override protected void onResume(){super.onResume();resumed=true;baselineClipboard();if(desired&&key!=null&&flashSession!=null&&usbBoardSession!=null&&!flashSession.busy&&!usbBoardSession.busy)handler.postDelayed(()->connectNow(),500);}
 @Override protected void onSaveInstanceState(Bundle saved){captureDraft();super.onSaveInstanceState(saved);if(text!=null){for(int i=0;i<4;i++)routeDrafts[i]=histories[i].value;if(boardsUi!=null)boardsUi.save(saved);saved.putString("draft",text.getText().toString());saved.putStringArray("drafts",routeDrafts);saved.putInt("selectionStart",text.getSelectionStart());saved.putInt("selectionEnd",text.getSelectionEnd());saved.putBoolean("fullscreen",fullscreen);saved.putInt("normalScroll",normalScroll);saved.putInt("editorX",text.getScrollX());saved.putInt("editorY",text.getScrollY());saved.putInt("pageY",contentScroll.getScrollY());saved.putBoolean("optionsExpanded",optionsExpanded);saved.putInt("subMode",subMode);saved.putInt("startupSeconds",startupSeconds);saved.putBoolean("fastTyping",fastTyping);saved.putInt("keyboardMethod",keyboardMethodIndex);saved.putBoolean("keyboardCompatible",gbkCompatible);saved.putBoolean("requireProbe",requireProbe.isChecked());saved.putInt("step",setupStep);saved.putBoolean("desired",desired);saved.putBoolean("paused",paused);saved.putBoolean("noProgram",noProgram);}}
 @Override public Object onRetainNonConfigurationInstance(){captureDraft();Retained r=new Retained();r.histories=histories;r.flash=flashSession;r.bindings=deviceBindings;r.usbBoards=usbBoardSession;return r;}
 @Override protected void onPause(){resumed=false;epoch++;clearPending();if(ble!=null)ble.close("应用已离开前台；返回后自动重连。");super.onPause();}
 @Override protected void onStop(){if(!isChangingConfigurations()){if(flashSession!=null)flashSession.cancelIfRunning();if(usbBoardSession!=null)usbBoardSession.cancel();}super.onStop();}
 @Override protected void onDestroy(){desired=false;handler.removeCallbacksAndMessages(null);if(text!=null)clipboard().removePrimaryClipChangedListener(clipboardChanged);if(ble!=null)ble.close("应用已退出。");if(flashSession!=null){flashSession.listener=null;if(!isChangingConfigurations())flashSession.dispose();}if(boardsUi!=null)boardsUi.dispose(isChangingConfigurations());if(usbBoardSession!=null&&!isChangingConfigurations())usbBoardSession.dispose();if(flashDialog!=null)flashDialog.dismiss();if(setupDialog!=null)setupDialog.dismiss();if(key!=null)Arrays.fill(key,(byte)0);if(legacyKey!=null)Arrays.fill(legacyKey,(byte)0);super.onDestroy();}
 void clearPending(){outgoingId=null;jobId=null;outgoingFrame=null;sending=false;}
 void toggleConnection(){if(desired){desired=false;if(ble!=null)ble.close("已手动断开。");connect.setText("连接开发板");}else{desired=true;connectNow();}}
 void connectNow(){
  if(!desired||!resumed||key==null||flashSession==null||usbBoardSession==null||flashSession.busy||usbBoardSession.busy||(ble!=null&&!ble.closed))return;
  if(deviceBindings.loadError!=null){desired=false;log(deviceBindings.loadError);return;}
  V2Protocol.Binding target=deviceBindings.registry.active();
  if(target==null){desired=false;setState("先从“我的开发板”通过 USB 添加或导入配置。");updateButtons();return;}
  if(!requireBluetoothPermissions(()->connectNow()))return;applyDeviceKey();
  try{ble=new BleTransport(this,this,target);setState("正在核对选定开发板…");connect.setText("断开");ble.start();}
  catch(Exception ex){log("连接失败："+ex.getClass().getSimpleName()+"。检查蓝牙与权限；Android 8–11 还需开启定位。");if(ble!=null)ble.close("连接未完成。");}
 }
 Runnable bluetoothPermissionAction;
 boolean requireBluetoothPermissions(Runnable action){String[] permissions=Build.VERSION.SDK_INT>=31?new String[]{Manifest.permission.BLUETOOTH_SCAN,Manifest.permission.BLUETOOTH_CONNECT}:new String[]{Manifest.permission.ACCESS_FINE_LOCATION};for(String permission:permissions)if(checkSelfPermission(permission)!=PackageManager.PERMISSION_GRANTED){bluetoothPermissionAction=action;requestPermissions(permissions,101);return false;}return true;}
 @Override public void onRequestPermissionsResult(int request,String[] permissions,int[] results){super.onRequestPermissionsResult(request,permissions,results);if(request!=101)return;Runnable action=bluetoothPermissionAction;bluetoothPermissionAction=null;if(results.length==0){desired=false;return;}for(int result:results)if(result!=PackageManager.PERMISSION_GRANTED){desired=false;log("蓝牙权限未授予，请在系统设置中启用后重试。");return;}if(action!=null)action.run();}
 @Override public void ready(){
  V2Protocol.Info trusted=ble.authenticatedInfo();if(trusted!=null){try{deviceBindings.change(r->{V2Protocol.Binding b=r.confirmed.get(trusted.number());if(b!=null)b.info=trusted;});}catch(Exception e){log("身份已验证，但最新设备信息未能保存。");}}
  updateBoardSummary();log("设备身份已验证，等待内网接收状态。");lastStatus=SystemClock.elapsedRealtime();baselineClipboard();heartbeat(epoch);
 }
 void stopForBoardSettings(){desired=false;epoch++;clearPending();autoClipboard.setChecked(false);if(ble!=null)ble.close("设备设置期间已断开蓝牙，请连接选定开发板。");baselineClipboard();updateButtons();}
 void applyDeviceKey(){V2Protocol.Binding active=deviceBindings.registry.active();String id=active==null?"":active.info.number();byte[] current=active==null?legacyKey:active.dataKey;if(!appliedDevice.equals(id)||!V2Protocol.equal(key,current)){System.arraycopy(current,0,key,0,32);appliedDevice=id;seen.clear();discarded.clear();baselineClipboard();}updateBoardSummary();}
 void bindingStateChanged(){if(isDestroyed())return;applyDeviceKey();updateButtons();}
 void updateBoardSummary(){if(boardSummary==null||deviceBindings==null)return;V2Protocol.Binding active=deviceBindings.registry.active();String label=deviceBindings.loadError!=null?"配置需恢复 · 打开我的开发板":active!=null?active.alias+" · #"+active.info.shortId()+(ble!=null&&ble.ready?" · 已认证":" · 已绑定"):"我的开发板 · 尚未选择";setLabel(boardSummary,label);}
 void heartbeat(int generation){
  if(generation!=epoch||!resumed||ble==null||!ble.ready)return;
  if(SystemClock.elapsedRealtime()-lastStatus>18000){ble.close("开发板响应超时，正在重新连接。");return;}
  ble.send(Protocol.frame(Protocol.PING,new byte[0]));handler.postDelayed(()->heartbeat(generation),5000);
 }
 @Override public void disconnected(String reason){if(ble!=null&&ble.stopReconnect)desired=false;peerReady=false;supportsTyping=false;supportsReceiverStart=false;supportsDirectKeys=false;supportsStartupDelay=false;supportsFastTyping=false;supportsPinyin=false;supportsGbk=false;resetKeyboardProbe();epoch++;clearPending();if(connect==null)return;connect.setText("连接开发板");setState(reason);updateBoardSummary();updateButtons();log(reason);if(desired&&resumed){final int generation=epoch;handler.postDelayed(()->{if(generation==epoch)connectNow();},5000);}}
 static void setLabel(TextView view,String value){if(!android.text.TextUtils.equals(view.getText(),value))view.setText(value);}
 void updateButtons(){
  if(send==null)return;boolean connected=ble!=null&&ble.ready&&!flashSession.busy&&!usbBoardSession.busy;String value=text.getText().toString();boolean valid=false;
  boolean connectIsPrimary=!desired&&!connected;if(connectPrimary==null||connectPrimary!=connectIsPrimary){connectPrimary=connectIsPrimary;styleAction(connect,connectIsPrimary);}
  setLabel(connect,desired?connected?"断开":"取消连接":"连接开发板");
  String sizeText;
  try{int length=Protocol.utf8(value).length;sizeText=length+" / 16384 UTF-8 字节";valid=length>0;}catch(Exception ex){sizeText="文本超出 16 KiB 或包含无效字符，不会截断发送。";}
  if(keyboardRoute()){try{if(gbkInput)Protocol.encodeGbkText(value);else if(pinyinInput)Protocol.normalizePinyinText(value);else Protocol.normalizeDirectText(value);valid=true;sizeText=Protocol.utf8(value.replace("\r\n","\n")).length+" / 16384 UTF-8 字节 · "+(gbkInput?"GBK 中英文混排":pinyinInput?"VUC 中英文混排":"纯英文");}catch(Exception ex){valid=false;sizeText=value.isEmpty()?"0 / 16384 UTF-8 字节":gbkInput?"GBK 不支持部分字符，请改用 VUC 或调整正文。":pinyinInput?"VUC 不支持 emoji、非 BMP 或此控制字符。":"纯英文方式不支持中文，请切换到中英文输入。";}}
  setLabel(size,sizeText);
  boolean body=!chineseKeyboard()||!requireProbe.isChecked()||(keyboardProbeSent&&pinyinConfirmed.isChecked());
  send.setEnabled(connected&&(keyboardRoute()?keyboardSupported()&&!peerReady&&body&&valid:peerReady&&valid)&&!sending&&!paused);setLabel(send,keyboardRoute()?"输入到内网窗口…":"发送到内网剪贴板");
  undoEdit.setEnabled(histories[previousRoute].canUndo());clearEdit.setEnabled(text.length()>0);connect.setEnabled(!flashSession.busy&&!usbBoardSession.busy);pauseAction.setEnabled(!flashSession.busy&&!usbBoardSession.busy);setLabel(pauseAction,paused?"继续":"暂停");
  setupCard.setVisibility(noProgram&&!keyboardRoute()?View.VISIBLE:View.GONE);autoClipboard.setVisibility(keyboardRoute()||fullscreen?View.GONE:View.VISIBLE);
  for(int i=0;i<modes.getChildCount();i++)modes.getChildAt(i).setEnabled(!sending&&!flashSession.busy);
  setLabel(routeTitle,keyboardRoute()?"USB 键盘输入":"PowerShell · 默认双向");
  setLabel(description,"支持中文、多行和 emoji；内网按 B 切换回传。");
  keyboardOptions.setVisibility(keyboardRoute()?View.VISIBLE:View.GONE);pinyinControls.setVisibility(chineseKeyboard()?View.VISIBLE:View.GONE);optionDetails.setVisibility(optionsExpanded||requireProbe.isChecked()?View.VISIBLE:View.GONE);setLabel(optionsToggle,(gbkInput?"试输与兼容选项":"试输选项")+(optionsExpanded||requireProbe.isChecked()?" ▴":" ▾"));for(RadioButton method:methodButtons)method.setEnabled(!sending);requireProbe.setEnabled(!sending);pinyinConfirmed.setVisibility(requireProbe.isChecked()&&keyboardProbeSent?View.VISIBLE:View.GONE);pinyinConfirmed.setEnabled(keyboardProbeSent&&!sending);vucSpeed.setVisibility(pinyinInput?View.VISIBLE:View.GONE);vucSpeed.setEnabled(!sending);gbkCompatibility.setVisibility(gbkInput?View.VISIBLE:View.GONE);gbkCompatibility.setEnabled(!sending);probeChinese.setVisibility(chineseKeyboard()?View.VISIBLE:View.GONE);probeChinese.setEnabled(connected&&keyboardSupported()&&!peerReady&&!sending&&!paused);setLabel(probeChinese,keyboardProbeSent?"重新试输…":"试输中英文…");styleProbe(!body);
  setLabel(keyboardGuide,gbkInput?"切到 ENG 美式，开启 Num Lock，关闭大写锁定。乱码请换 VUC。":pinyinInput?"切到微软拼音全拼“中”、半角，保留 Shift 中英切换，关闭大写锁定。"+vucSpeedSummary():"切到 ENG 美式，关闭大写锁定。仅输入英文、数字和符号。");
  setLabel(keyboardReady,!connected?"连接开发板后可试输。":peerReady?"请先退出内网接收器。":!keyboardSupported()?"此方式需更新配套固件，请打开“更多 → 开发板设置”。":sending?"按键发送中，请勿操作内网键鼠。":paused?"传输已暂停，请在“更多”中恢复。":chineseKeyboard()&&requireProbe.isChecked()&&!keyboardProbeSent?"试输“病ABC123，中文!”；核对成功后才能发送。":chineseKeyboard()&&requireProbe.isChecked()&&!pinyinConfirmed.isChecked()?"请核对内网文字，再勾选“试输结果正确”。":value.isEmpty()?"可直接输入正文，也可先试输；开始前有 3 秒准备时间。":!valid?"正文含不支持字符，请查看下方提示。":"已准备好，开始前有 3 秒准备时间。");
  switchChinese.setVisibility(keyboardRoute()&&!chineseKeyboard()&&!value.isEmpty()&&!valid?View.VISIBLE:View.GONE);
  setupStart.setVisibility(keyboardRoute()?View.GONE:View.VISIBLE);setLabel(setupStart,"设置…");setupStart.setEnabled(!sending);setLabel(textHeading,keyboardRoute()?"要输入的文字":"发送到内网");
  if(connected&&keyboardRoute())setState(paused?"传输已暂停":sending?"按键发送中，请勿操作内网键鼠":peerReady?"先退出内网接收器，再使用 USB 键盘":!keyboardSupported()?"请更新配套固件以使用此输入方式":!body?"请先完成试输并确认；全屏时点“收起”返回设置":"USB 键盘已连接 · 请将内网光标放好");
  if(connected&&!keyboardRoute())setState(paused?"传输已暂停":sending?"正在发送，等待确认…":peerReady?"已就绪 · 可以发送文字":"开发板已连接 · 等待内网接收器");
  if(setupAction!=null)setupAction.setEnabled(!sending&&(setupStep>=3||(connected&&!peerReady&&supportsTyping&&(setupStep!=2||supportsReceiverStart))));
 }
 static void remember(LinkedHashSet<String> set,String id){set.add(id);while(set.size()>256)set.remove(set.iterator().next());}
 @Override public void packet(Protocol.Packet p){
  try{
   if(p.kind==Protocol.STATUS&&p.payload.length>=2){boolean previous=peerReady,typing=p.payload.length>=3&&p.payload[2]==1,receiver=p.payload.length>=4&&p.payload[3]==1,direct=p.payload.length>=5&&p.payload[4]==1,startup=p.payload.length>=6&&p.payload[5]==1,fast=p.payload.length>=7&&p.payload[6]==1,pinyin=p.payload.length>=8&&p.payload[7]==1,gbk=p.payload.length>=9&&p.payload[8]==1,ready=p.payload[0]!=0;V2Protocol.Info trusted=ble==null?null:ble.authenticatedInfo();if(trusted!=null){int caps=trusted.capabilities;typing&=(caps&2)!=0;receiver&=(caps&4)!=0;startup&=(caps&4)!=0;fast&=(caps&4)!=0;direct&=(caps&2)!=0;pinyin&=(caps&16)!=0;gbk&=(caps&8)!=0;}boolean changed=ready!=peerReady||typing!=supportsTyping||receiver!=supportsReceiverStart||direct!=supportsDirectKeys||startup!=supportsStartupDelay||fast!=supportsFastTyping||pinyin!=supportsPinyin||gbk!=supportsGbk;peerReady=ready;supportsTyping=typing;supportsReceiverStart=receiver;supportsDirectKeys=direct;supportsStartupDelay=startup;supportsFastTyping=fast;supportsPinyin=pinyin;supportsGbk=gbk;lastStatus=SystemClock.elapsedRealtime();if(previous!=peerReady)baselineClipboard();if(changed)updateButtons();}
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
 String bootstrap()throws Exception{try(InputStream in=getAssets().open("receiver-duplex.txt")){ByteArrayOutputStream out=new ByteArrayOutputStream();byte[] b=new byte[4096];int n;while((n=in.read(b))>0)out.write(b,0,n);String value=new String(out.toByteArray(),StandardCharsets.UTF_8),original=java.util.Base64.getEncoder().encodeToString(legacyKey);int at=value.indexOf(original);if(at<0||value.indexOf(original,at+original.length())>=0)throw new IOException("接收器模板配对位置无效");value=value.replace(original,java.util.Base64.getEncoder().encodeToString(key));Protocol.utf8(value);return value;}}
 void startJob(int action,String value,boolean paste){
  if(sending||paused||peerReady||ble==null||!ble.ready||!supportsTyping||(action==2&&!supportsReceiverStart))return;
  V2Protocol.Info jobInfo=ble.authenticatedInfo();if(jobInfo!=null&&((jobInfo.capabilities&2)==0||(action<=2&&(jobInfo.capabilities&4)==0))){log("所选开发板不支持此输入动作。");return;}
  if(action>=3&&!supportsDirectKeys){log("直接键盘输入需先通过 Windows 更新开发板固件。");return;}
  if((action==4||action==5)&&!supportsPinyin){log("请更新配套固件以使用 VUC。");return;}
  if((action==6||action==7)&&!supportsGbk){log("请更新配套固件以使用 GBK。");return;}
  if((action==5||action==7)&&requireProbe.isChecked()&&(!keyboardProbeSent||!pinyinConfirmed.isChecked())){log("请先完成中英文试输并确认结果。");return;}
  try{
   int interval=fastTyping&&supportsFastTyping?1:4;
   byte[] payload=action==6||action==7?Protocol.gbkJob(value,gbkCompatible,key):action==4||action==5?Protocol.pinyinJob(value,vucGap,vucSettle,key):action==3?Protocol.directKeys(value,key):action==2?Protocol.receiverOpenJob(key):Protocol.job(value,paste,supportsStartupDelay?startupSeconds:0,interval,key);jobId=Protocol.id(payload);jobAction=action;String id=jobId;sending=true;updateButtons();
   int chars=action>=3?value.replace("\r\n","\n").length():action==2?100:260+4*((payload.length-47+2)/3),unicode=0;for(int i=0;i<value.length();i++)if(value.charAt(i)>=128)unicode++;
   double vucSeconds=action==4||action==5?Protocol.pinyinMilliseconds(value,vucGap,vucSettle)/1000.0:0;
   final int lower=action==4||action==5?3+(int)Math.ceil(vucSeconds):action>=4?3+(int)Math.ceil(unicode*(action>=6?gbkCompatible?.125:.029:.16)+(chars-unicode)*.003):action>=3?3+(int)Math.ceil(chars*.003):startupSeconds+7+(int)Math.ceil(chars*(interval==1?.003:.006));
   final int upper=action==4||action==5?10+(int)Math.ceil(vucSeconds*1.5+chars*.020):action>=4?10+(int)Math.ceil(unicode*(action>=6?gbkCompatible?.30:.15:.45)+(chars-unicode)*.020):action>=3?8+(int)Math.ceil(chars*.020):startupSeconds+15+(int)Math.ceil(chars*(interval==1?.020:.035));long began=SystemClock.elapsedRealtime();
   Runnable progress=new Runnable(){public void run(){if(!id.equals(jobId))return;String message="已用 "+((SystemClock.elapsedRealtime()-began)/1000)+" 秒 · 预计约 "+lower+"–"+upper+" 秒；请勿操作内网键鼠";if(setupStatus!=null)setupStatus.setText(message);if(state!=null)setState(message);handler.postDelayed(this,1000);}};handler.post(progress);
   ble.send(Protocol.frame(Protocol.TYPE_JOB,payload)).whenComplete((v,error)->handler.post(()->{if(error!=null&&id.equals(jobId)){jobId=null;sending=false;if(action>=3)resetKeyboardProbe();updateButtons();log("键盘任务发送失败，内网执行结果未知。");}}));
   handler.postDelayed(()->{if(id.equals(jobId)){jobId=null;sending=false;if(action>=3)resetKeyboardProbe();updateButtons();log("任务超时，先检查内网窗口，避免重复执行。");}},Math.max(180000,(upper+90)*1000));
  }catch(Exception ex){log("无法创建键盘任务："+ex.getClass().getSimpleName());}
 }
 void showSetup(){
  modeSystem.setChecked(true);
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
  String[] entries={"使用帮助","开发板设置",paused?"继续传输":"暂停传输","PowerShell 接收设置","临时键盘写入","显示 / 隐藏日志","导出日志","开源许可","我的开发板"};
  new AlertDialog.Builder(this).setTitle("更多").setItems(entries,(dialog,which)->{
   if(which==8){boardsUi.show();return;}
   if(which==0)showHelp();else if(which==1)showBoardSettings();else if(which==2)togglePaused();else if(which==3)showSetup();else if(which==4)confirmTyping();
   else if(which==5)logView.setVisibility(logView.getVisibility()==View.VISIBLE?View.GONE:View.VISIBLE);
   else if(which==6){Intent i=new Intent(Intent.ACTION_CREATE_DOCUMENT).setType("text/plain").addCategory(Intent.CATEGORY_OPENABLE);i.putExtra(Intent.EXTRA_TITLE,"AtomClipAndroid-log.txt");startActivityForResult(i,202);}
   else if(which==7){try{String licenses=new String(flashSession.readAsset("licenses.txt",1024*1024),StandardCharsets.UTF_8);LinearLayout body=column();body.setPadding(dp(16),dp(8),dp(16),dp(8));label(body,licenses,12).setTextIsSelectable(true);ScrollView scroll=new ScrollView(this);scroll.addView(body);new AlertDialog.Builder(this).setTitle("开源许可").setView(scroll).setPositiveButton("完成",null).show();}catch(IOException ex){log("无法读取开源许可，请获取完整 APK。");}}
  }).show();
 }
 void attachFlashSession(){
  flashSession.listener=()->{
   if(isFinishing()||isDestroyed())return;
   if(flashSession.busy)getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);else getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
   if(flashStatus!=null){setLabel(flashStatus,flashSession.message);flashProgress.setProgress(flashSession.percent);setLabel(flashStart,flashSession.inspected?"确认覆盖并烧录…":"识别 USB 开发板");flashStart.setEnabled(!flashSession.busy&&flashSession.hasHost());}
   if(flashDialog!=null)flashDialog.setCancelable(!flashSession.busy);if(flashProfileChoice!=null)flashProfileChoice.setEnabled(!flashSession.busy);if(flashFactoryChoice!=null)flashFactoryChoice.setEnabled(!flashSession.busy);
   if(flashSession.busy)setState("USB 烧录 / 识别进行中 · 蓝牙已暂停");updateButtons();
  };
  if(flashSession.busy){desired=false;paused=true;handler.post(()->{showBoardSettings();flashSession.notifyChanged();});}
 }
 void chooseUsbDevice(){
  if(sending||usbBoardSession.busy){Toast.makeText(this,"请等待当前任务完成，再进行 USB 烧录。",Toast.LENGTH_LONG).show();return;}
  List<android.hardware.usb.UsbDevice> devices=flashSession.devices();
  if(devices.isEmpty()){setLabel(flashStatus,flashSession.hasHost()?"未找到原生 USB CDC 下载设备。请启用 OTG，检查数据连接，长按 RESET 约 2 秒进入下载模式，再点击识别。":"此手机未提供 USB Host / OTG，请使用 Windows 外网端烧录。");return;}
  Runnable begin=()->{desired=false;paused=true;epoch++;clearPending();if(ble!=null)ble.close("USB 操作期间蓝牙已暂停；完成后请手动连接并继续传输。");};
  if(devices.size()==1){begin.run();flashSession.inspect(devices.get(0));flashSession.notifyChanged();}
  else {String[] labels=new String[devices.size()];for(int i=0;i<labels.length;i++)labels[i]=devices.get(i).getDeviceName();new AlertDialog.Builder(this).setTitle("选择 AtomS3U USB 设备").setItems(labels,(d,w)->{begin.run();flashSession.inspect(devices.get(w));flashSession.notifyChanged();}).setNegativeButton("取消",null).show();}
 }
 void showBoardSettings(){
  if(flashDialog!=null&&flashDialog.isShowing())return;
  flashDialog=new Dialog(this);flashDialog.requestWindowFeature(Window.FEATURE_NO_TITLE);
  LinearLayout body=column();body.setPadding(dp(18),dp(12),dp(18),dp(12));body.setBackgroundColor(BG);
  label(body,"开发板设置",20);label(body,"USB OTG 烧录 · 待实机验证",13).setTextColor(BLUE);
  label(body,"支持已列出的 ESP32-S3 SPI NOR 板型，使用原生 USB 口；不支持 OPI Flash。请手动进入下载模式，手机开启 OTG 并保持前台。",12);
  final ArrayList<BoardCatalog.Profile> flashProfiles=new ArrayList<>(boardCatalog.profiles.values());String[] flashNames=new String[flashProfiles.size()];int chosen=0;for(int i=0;i<flashNames.length;i++){flashNames[i]=flashProfiles.get(i).name;if(flashProfiles.get(i).key.equals(flashSession.profileKey))chosen=i;}Spinner profileChoice=new Spinner(this);flashProfileChoice=profileChoice;profileChoice.setMinimumHeight(dp(48));profileChoice.setAdapter(new ArrayAdapter<String>(this,android.R.layout.simple_spinner_dropdown_item,flashNames));profileChoice.setSelection(chosen);profileChoice.setEnabled(!flashSession.busy);body.addView(profileChoice);profileChoice.setOnItemSelectedListener(new android.widget.AdapterView.OnItemSelectedListener(){public void onNothingSelected(android.widget.AdapterView<?> p){}public void onItemSelected(android.widget.AdapterView<?> p,View v,int pos,long id){if(!flashSession.busy)flashSession.selectProfile(flashProfiles.get(pos).key);}});
  CheckBox factoryChoice=new CheckBox(this);flashFactoryChoice=factoryChoice;factoryChoice.setText("清空安装（删除板上程序、数据和绑定）");factoryChoice.setTextSize(12);factoryChoice.setMinHeight(dp(48));factoryChoice.setChecked(flashSession.factory);factoryChoice.setEnabled(!flashSession.busy);factoryChoice.setOnCheckedChangeListener((b,c)->{if(!flashSession.busy)flashSession.factory=c;});body.addView(factoryChoice);
  label(body,"先识别型号与容量，再确认覆盖。本 APK 内置配套固件，不能选择其他 BIN。烧录期间保持前台和供电；旋转可继续，离开前台会中止。",12).setTextColor(MUTED);
  flashProgress=new ProgressBar(this,null,android.R.attr.progressBarStyleHorizontal);flashProgress.setMax(100);flashProgress.setProgress(flashSession.percent);body.addView(flashProgress,new LinearLayout.LayoutParams(-1,dp(12)));
  flashStatus=label(body,flashSession.hasHost()?flashSession.message:"此手机未提供 USB Host / OTG 能力，请使用 Windows 外网端烧录。",13);flashStatus.setTextIsSelectable(true);
  flashStart=button(body,flashSession.inspected?"确认覆盖并烧录…":"识别 USB 开发板",()->{
   if(!flashSession.inspected){chooseUsbDevice();return;}
   new AlertDialog.Builder(this).setTitle(flashSession.factory?"确认清空安装":"确认保留绑定升级").setMessage(flashSession.identity+"\n\n所选板型："+boardCatalog.name(flashSession.selectedBundle.profile)+"\n"+(flashSession.factory?"将清空全部 Flash，板上绑定会丢失。请先加密导出配置；完成后可通过 USB 恢复。":"仅在分区兼容时写入四个固件段，保留 NVS 绑定。")+"\n请确认实物板型、SPI NOR 和原生 USB 口。是否开始？").setNegativeButton("取消",null).setPositiveButton("确认覆盖",(d,w)->{flashSession.write();flashSession.notifyChanged();}).show();
  });styleAction(flashStart,true);flashStart.setEnabled(!flashSession.busy&&flashSession.hasHost());
  button(body,"重新识别",()->{if(!flashSession.busy){flashSession.inspected=false;chooseUsbDevice();}});
  button(body,"中止 / 关闭",()->{if(flashSession.busy){flashSession.cancelIfRunning();setLabel(flashStatus,"正在中止；重新进入下载模式后再完整重试。");}else flashDialog.dismiss();});
  label(body,"需要 0.2.0 或更新固件，已正常绑定的板无需重烧。首次升级请先备份配置。烧录成功后拔插开发板，经“我的开发板”添加或恢复绑定。",12).setTextColor(MUTED);
  ScrollView scroll=new ScrollView(this);scroll.addView(body);flashDialog.setContentView(scroll);flashDialog.setCancelable(!flashSession.busy);flashDialog.show();flashDialog.getWindow().setBackgroundDrawableResource(android.R.color.transparent);flashDialog.getWindow().setLayout(Math.min(getResources().getDisplayMetrics().widthPixels-dp(24),dp(520)),-2);
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
  super.onActivityResult(request,result,data);if(boardsUi!=null&&boardsUi.activityResult(request,result,data))return;if(request!=202||result!=RESULT_OK||data==null)return;
  try(OutputStream out=getContentResolver().openOutputStream(data.getData())){File[] files=logs.listFiles();if(files==null)files=new File[0];Arrays.sort(files);byte[] buffer=new byte[8192];for(File f:files)try(InputStream in=new FileInputStream(f)){int n;while((n=in.read(buffer))>0)out.write(buffer,0,n);}log("日志已导出。");}
  catch(Exception ex){log("日志导出失败。");}
 }
}
