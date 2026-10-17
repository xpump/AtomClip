package dev.atomclip.mobile;
import java.nio.ByteBuffer;
import java.nio.ByteOrder;
import java.nio.charset.StandardCharsets;
import java.nio.charset.CodingErrorAction;
import java.security.SecureRandom;
import java.security.MessageDigest;
import java.util.*;
import java.util.zip.*;
import java.io.*;
import javax.crypto.*;
import javax.crypto.spec.*;

public final class Protocol {
 public static final int MAX_TEXT=16384, MAX_PAYLOAD=16512;
 public static final int DATA=1, ACK=2, PING=3, STATUS=4, TYPE_JOB=5, JOB_RESULT=6;
 public static class Packet { public final int kind; public final byte[] payload; Packet(int k,byte[] b){kind=k;payload=b;} }
 public static byte[] utf8(String s) throws Exception {
  if(s.indexOf(0)>=0)throw new IllegalArgumentException("文本含空字符");
  ByteBuffer b=StandardCharsets.UTF_8.newEncoder().onMalformedInput(CodingErrorAction.REPORT).encode(java.nio.CharBuffer.wrap(s));
  byte[] r=new byte[b.remaining()];b.get(r);if(r.length>MAX_TEXT)throw new IllegalArgumentException("文本超过 16 KiB");return r;
 }
 public static String id(byte[] b){StringBuilder s=new StringBuilder();for(int i=0;i<16;i++)s.append(String.format(java.util.Locale.ROOT,"%02X",b[i]&255));return s.toString();}
 static byte[] range(byte[] b,int start,int length){return Arrays.copyOfRange(b,start,start+length);}
 public static byte[] encrypt(String text,byte[] key)throws Exception{return encryptBytes(utf8(text),key);}
 public static byte[] encryptBytes(byte[] plain,byte[] key)throws Exception{
  try{
   if(plain.length+44>MAX_PAYLOAD)throw new IllegalArgumentException("数据过长");
   byte[] prefix=new byte[28];new SecureRandom().nextBytes(prefix);
   Cipher c=Cipher.getInstance("AES/GCM/NoPadding");c.init(Cipher.ENCRYPT_MODE,new SecretKeySpec(key,"AES"),new GCMParameterSpec(128,range(prefix,16,12)));c.updateAAD(range(prefix,0,16));
   byte[] encrypted=c.doFinal(plain),b=new byte[prefix.length+encrypted.length];System.arraycopy(prefix,0,b,0,28);System.arraycopy(encrypted,0,b,28,encrypted.length);return b;
  }finally{Arrays.fill(plain,(byte)0);}
 }
 public static String decrypt(byte[] b,byte[] key)throws Exception{
  if(b.length<44||b.length>MAX_TEXT+44)throw new GeneralSecurityException("数据长度错误");
  Cipher c=Cipher.getInstance("AES/GCM/NoPadding");c.init(Cipher.DECRYPT_MODE,new SecretKeySpec(key,"AES"),new GCMParameterSpec(128,range(b,16,12)));c.updateAAD(range(b,0,16));
  byte[] plain=c.doFinal(range(b,28,b.length-28));
  try{String text=StandardCharsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(plain)).toString();if(text.indexOf(0)>=0)throw new GeneralSecurityException("文本格式错误");return text;}finally{Arrays.fill(plain,(byte)0);}
 }
 static byte[] hmac(byte[] b,byte[] key)throws Exception{Mac mac=Mac.getInstance("HmacSHA256");mac.init(new SecretKeySpec(key,"HmacSHA256"));return mac.doFinal(b);}
 public static byte[] ack(byte[] payload,byte[] key)throws Exception{
  byte[] signed=new byte[17];signed[0]=ACK;System.arraycopy(payload,0,signed,1,16);
  byte[] result=new byte[32];System.arraycopy(payload,0,result,0,16);System.arraycopy(hmac(signed,key),0,result,16,16);return result;
 }
 public static String readAck(byte[] b,byte[] key)throws Exception{return b.length==32&&MessageDigest.isEqual(range(ack(b,key),16,16),range(b,16,16))?id(b):null;}
 public static int readResult(byte[] b,String id,byte[] key)throws Exception{
  if(b.length!=33||!id(b).equals(id))return -1;byte[] signed=new byte[18];signed[0]=JOB_RESULT;System.arraycopy(b,0,signed,1,17);
  return MessageDigest.isEqual(range(hmac(signed,key),0,16),range(b,17,16))?(b[16]&255):-1;
 }
 public static byte[] job(String text,boolean paste,byte[] key)throws Exception{
  return job(text,paste,0,key);
 }
 public static byte[] job(String text,boolean paste,int startup,byte[] key)throws Exception{
  return job(text,paste,startup,4,key);
 }
 public static byte[] job(String text,boolean paste,int startup,int interval,byte[] key)throws Exception{
  byte[] raw=utf8(text),gzip;
  try{ByteArrayOutputStream out=new ByteArrayOutputStream();try(GZIPOutputStream g=new GZIPOutputStream(out)){g.write(raw);}gzip=out.toByteArray();}finally{Arrays.fill(raw,(byte)0);}
  if(startup!=0&&(startup<3||startup>60))throw new IllegalArgumentException("Startup delay");
  if(interval<1||interval>50)throw new IllegalArgumentException("Typing interval");
  int header=startup==0?3:4;byte[] plain=new byte[header+gzip.length];plain[0]=(byte)(startup==0?1:4);plain[1]=(byte)(paste?1:0);plain[2]=(byte)interval;if(header==4)plain[3]=(byte)startup;System.arraycopy(gzip,0,plain,header,gzip.length);return encryptBytes(plain,key);
 }
 public static byte[] directKeys(String text,byte[] key)throws Exception{
  text=normalizeDirectText(text);
  byte[] plain=new byte[text.length()+3];plain[0]=3;plain[2]=4;System.arraycopy(text.getBytes(StandardCharsets.US_ASCII),0,plain,3,text.length());return encryptBytes(plain,key);
 }
 public static String normalizeDirectText(String text){
  text=text.replace("\r\n","\n");if(text.isEmpty()||text.length()>MAX_TEXT)throw new IllegalArgumentException("ASCII text required");
  for(int i=0;i<text.length();i++){char c=text.charAt(i);if((c<32||c>126)&&c!='\n'&&c!='\t')throw new IllegalArgumentException("ASCII only");}
  return text;
 }
 public static byte[] receiverOpenJob(byte[] key)throws Exception{
  return encryptBytes(new byte[]{2,0,4},key);
 }
 public static final String PINYIN_PROBE_KEYS="vuc4e2d vuc6587 ";
 public static byte[] pinyinProbe(byte[] key)throws Exception{byte[] raw=PINYIN_PROBE_KEYS.getBytes(StandardCharsets.US_ASCII),plain=new byte[raw.length+3];plain[0]=3;plain[2]=35;System.arraycopy(raw,0,plain,3,raw.length);return encryptBytes(plain,key);}
 public static String normalizePinyinText(String text)throws Exception{
  text=text.replace("\r\n","\n");if(text.isEmpty())throw new IllegalArgumentException("Empty text");
  for(int i=0;i<text.length();i++){char c=text.charAt(i);if(Character.isSurrogate(c)||c>=0xfffe||(Character.isISOControl(c)&&c!='\n'&&c!='\t'))throw new IllegalArgumentException("BMP text only");}
  utf8(text);return text;
 }
 public static byte[] pinyinJob(String text,boolean compatible,byte[] key)throws Exception{
  byte[] raw=utf8(normalizePinyinText(text)),plain=new byte[raw.length+3];plain[0]=5;plain[1]=(byte)(compatible?80:35);plain[2]=(byte)(compatible?4:1);System.arraycopy(raw,0,plain,3,raw.length);Arrays.fill(raw,(byte)0);return encryptBytes(plain,key);
 }
 public static final String KEYBOARD_PROBE_TEXT="病ABC123，中文!";
 public static byte[] encodeGbkText(String text)throws Exception{
  text=normalizePinyinText(text);for(int i=0;i<text.length();i++){char c=text.charAt(i);if(c=='\u20ac'||(c>='\ue000'&&c<='\uf8ff'))throw new IllegalArgumentException("GBK 不支持此字符，请改用 VUC。");}java.nio.charset.Charset codec=java.nio.charset.Charset.forName("GBK");
  ByteBuffer buffer;
  try{buffer=codec.newEncoder().onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT).encode(java.nio.CharBuffer.wrap(text));}
  catch(java.nio.charset.CharacterCodingException ex){throw new IllegalArgumentException("GBK 不支持部分字符，请改用 VUC。");}
  byte[] raw=new byte[buffer.remaining()];buffer.get(raw);
  if(!codec.newDecoder().onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(raw)).toString().equals(text))throw new IllegalArgumentException("GBK 无法无损表示文字，请改用 VUC。");
  for(int i=0;i<raw.length;i++){int first=raw[i]&255;if(first<128)continue;if(first<0x81||first>0xfe||++i>=raw.length||(raw[i]&255)<0x40||(raw[i]&255)>0xfe||(raw[i]&255)==0x7f)throw new IllegalArgumentException("GBK 暂不支持此字符，请改用 VUC。");}
  return raw;
 }
 public static byte[] gbkJob(String text,boolean compatible,byte[] key)throws Exception{
  byte[] raw=encodeGbkText(text),plain=new byte[raw.length+5];plain[0]=6;plain[1]=(byte)(compatible?8:2);plain[2]=plain[1];plain[3]=(byte)(compatible?15:3);plain[4]=(byte)(compatible?30:6);System.arraycopy(raw,0,plain,5,raw.length);Arrays.fill(raw,(byte)0);return encryptBytes(plain,key);
 }
 public static byte[] frame(int kind,byte[] payload){
  if(payload.length>MAX_PAYLOAD||kind<1||kind>6)throw new IllegalArgumentException("Frame bounds");
  ByteBuffer b=ByteBuffer.allocate(13+payload.length).order(ByteOrder.LITTLE_ENDIAN);b.put(new byte[]{65,67,66,49}).put((byte)kind).putInt(payload.length).put(payload);
  CRC32 crc=new CRC32();crc.update(b.array(),4,5+payload.length);b.putInt((int)crc.getValue());return b.array();
 }
 public static class Parser {
  byte[] buffer=new byte[0];
  public List<Packet> feed(byte[] input){
   byte[] next=Arrays.copyOf(buffer,buffer.length+input.length);System.arraycopy(input,0,next,buffer.length,input.length);buffer=next;
   List<Packet> result=new ArrayList<>();int p=0;
   while(buffer.length-p>=4){
    if(buffer[p]!=65||buffer[p+1]!=67||buffer[p+2]!=66||buffer[p+3]!=49){p++;continue;}
    if(buffer.length-p<9)break;int size=ByteBuffer.wrap(buffer,p+5,4).order(ByteOrder.LITTLE_ENDIAN).getInt(),kind=buffer[p+4]&255;
    if(size<0||size>MAX_PAYLOAD||kind<1||kind>6){p++;continue;}
    int total=13+size;if(buffer.length-p<total)break;
    CRC32 crc=new CRC32();crc.update(buffer,p+4,5+size);
    if((int)crc.getValue()!=ByteBuffer.wrap(buffer,p+9+size,4).order(ByteOrder.LITTLE_ENDIAN).getInt()){p++;continue;}
    result.add(new Packet(kind,range(buffer,p+9,size)));p+=total;
   }
   buffer=range(buffer,p,buffer.length-p);return result;
  }
 }
 private static class GeneralSecurityException extends Exception {GeneralSecurityException(String s){super(s);}}
}
