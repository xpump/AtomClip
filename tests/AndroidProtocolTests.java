package dev.atomclip.mobile;
import java.nio.file.*;
import java.util.*;
import java.nio.charset.StandardCharsets;

public class AndroidProtocolTests {
 static void require(boolean b,String message){if(!b)throw new AssertionError(message);}
 public static void main(String[] args)throws Exception{
  Path root=Paths.get(args[0]);byte[] key=new byte[32];for(int i=0;i<32;i++)key[i]=(byte)i;
  for(int i=0;i<5;i++){
   String text=new String(Files.readAllBytes(root.resolve(i+".txt")),StandardCharsets.UTF_8);
   byte[] data=Files.readAllBytes(root.resolve(i+".data"));
   require(Protocol.decrypt(data,key).equals(text),"Original -> Android decrypt");
   require(Arrays.equals(Protocol.ack(data,key),Files.readAllBytes(root.resolve(i+".ack"))),"ACK compatibility");
   byte[] encrypted=Protocol.encrypt(text,key);Files.write(root.resolve(i+".android"),encrypted);
   Files.write(root.resolve(i+".android-job"),Protocol.job(text,false,key));
   byte[] frame=Files.readAllBytes(root.resolve(i+".frame"));
   for(int step:new int[]{1,20,180,4096}){Protocol.Parser parser=new Protocol.Parser();int found=0;for(int p=0;p<frame.length;p+=step)for(Protocol.Packet packet:parser.feed(Arrays.copyOfRange(frame,p,Math.min(p+step,frame.length)))){require(Arrays.equals(packet.payload,data),"Frame compatibility");found++;}require(found==1,"Exactly one frame");}
   data[data.length-1]^=1;try{Protocol.decrypt(data,key);throw new AssertionError("Bad tag accepted");}catch(javax.crypto.AEADBadTagException expected){}
  }
  try{Protocol.utf8(new String(new char[16385]).replace('\0','a'));throw new AssertionError("Oversize accepted");}catch(IllegalArgumentException expected){}
  byte[] open=Protocol.receiverOpenJob(key);Files.write(root.resolve("android-open.data"),open);
  Files.write(root.resolve("android-direct.data"),Protocol.directKeys("ASCII 123!\r\nNext\tline",key));
  Files.write(root.resolve("android-pinyin.data"),Protocol.pinyinJob("中文Ab123，。\r\nNext\tline",key));
  for(int[] speed:new int[][]{{7,120},{1,20},{50,200}})Files.write(root.resolve("android-pinyin-"+speed[0]+"-"+speed[1]+".data"),Protocol.pinyinJob("中文Ab123，。\r\nNext\tline",speed[0],speed[1],key));
  for(int[] speed:new int[][]{{0,80},{51,80},{4,19},{4,201},{-1,80},{4,Integer.MAX_VALUE}})try{Protocol.pinyinJob("中文",speed[0],speed[1],key);throw new AssertionError("Invalid VUC speed accepted");}catch(IllegalArgumentException expected){}
  require(Protocol.pinyinMilliseconds("中A文",4,80)==570&&Protocol.pinyinMilliseconds("A",4,80)==194&&Protocol.pinyinMilliseconds("中文",50,200)==1500,"VUC duration includes commit, key and Shift waits, including custom maximum");
  Files.write(root.resolve("android-gbk.data"),Protocol.gbkJob("病ABC123，中文!\r\nNext\tline",false,key));
  Files.write(root.resolve("android-gbk-compatible.data"),Protocol.gbkJob("病ABC123，中文!\r\nNext\tline",true,key));
  require(Arrays.equals(Protocol.encodeGbkText(Protocol.KEYBOARD_PROBE_TEXT),new byte[]{(byte)0xb2,(byte)0xa1,65,66,67,49,50,51,(byte)0xa3,(byte)0xac,(byte)0xd6,(byte)0xd0,(byte)0xce,(byte)0xc4,33}),"GBK canonical mixed probe bytes");
  require(Protocol.encodeGbkText(new String(new char[16384]).replace('\0','a')).length==16384,"GBK exact ASCII maximum without expansion");
  for(String invalid:new String[]{"😀","𠀀","\u2603","\u20ac","\ue000","bad\rtext",new String(new char[5462]).replace('\0','中')})try{Protocol.gbkJob(invalid,false,key);throw new AssertionError("Unsupported GBK text accepted");}catch(IllegalArgumentException expected){}
  Files.write(root.resolve("android-probe.data"),Protocol.pinyinProbe(key));
  require(Protocol.normalizePinyinText(new String(new char[5461]).replace('\0','中')+"a").length()==5462,"Pinyin exact 16KiB limit");
  for(String invalid:new String[]{"","😀","𠀀","bad\0text","bad\rtext","bad\u0085","\uffff","\ud800",new String(new char[5462]).replace('\0','中')})try{Protocol.pinyinJob(invalid,key);throw new AssertionError("Unsupported pinyin text accepted");}catch(IllegalArgumentException expected){}
  Files.write(root.resolve("android-delay.data"),Protocol.job("中文启动等待 😀",false,30,key));
  Files.write(root.resolve("android-fast.data"),Protocol.job("中文启动等待 😀",false,10,1,key));
  for(int interval:new int[]{0,51,255})try{Protocol.job("test",false,10,interval,key);throw new AssertionError("Invalid interval accepted");}catch(IllegalArgumentException expected){}
  for(String invalid:new String[]{"中文","😀","bad\0text","bad\rtext",new String(new char[16385]).replace('\0','a')})try{Protocol.directKeys(invalid,key);throw new AssertionError("Invalid direct text accepted");}catch(IllegalArgumentException expected){}
  for(int delay:new int[]{1,2,61,255})try{Protocol.job("test",false,delay,key);throw new AssertionError("Invalid startup delay accepted");}catch(IllegalArgumentException expected){}
  javax.crypto.Cipher cipher=javax.crypto.Cipher.getInstance("AES/GCM/NoPadding");
  cipher.init(javax.crypto.Cipher.DECRYPT_MODE,new javax.crypto.spec.SecretKeySpec(key,"AES"),new javax.crypto.spec.GCMParameterSpec(128,Arrays.copyOfRange(open,16,28)));
  cipher.updateAAD(Arrays.copyOfRange(open,0,16));
  require(Arrays.equals(cipher.doFinal(Arrays.copyOfRange(open,28,open.length)),new byte[]{2,0,4}),"Fixed opener, no arbitrary command or auto-paste");
  byte[] ack=Protocol.ack(open,key);ack[31]^=1;require(Protocol.readAck(ack,key)==null,"Forged ACK rejected");
  byte[] broken=Protocol.frame(Protocol.DATA,open);broken[broken.length-1]^=1;require(new Protocol.Parser().feed(broken).isEmpty(),"Bad CRC rejected");
  System.out.println("PASS: Android Java AES-GCM/ACK/CRC/fragment/UTF8/limit and fixed receiver opener (desktop JVM, not phone runtime).");
 }
}
