package dev.atomclip.mobile;

import java.io.*;
import java.nio.*;
import java.nio.charset.StandardCharsets;
import java.security.*;
import java.util.*;
import java.util.zip.CRC32;
import javax.crypto.*;
import javax.crypto.spec.*;

/** Versioned device authentication and encrypted BLE session; no Android dependencies. */
final class V2Protocol {
 static byte[] strictUtf8(String value){try{ByteBuffer b=StandardCharsets.UTF_8.newEncoder().onMalformedInput(java.nio.charset.CodingErrorAction.REPORT).onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT).encode(java.nio.CharBuffer.wrap(value));byte[] out=new byte[b.remaining()];b.get(out);return out;}catch(java.nio.charset.CharacterCodingException e){throw new IllegalArgumentException("UTF-8 文本无效",e);}}
 static String strictUtf8(byte[] bytes){try{return StandardCharsets.UTF_8.newDecoder().onMalformedInput(java.nio.charset.CodingErrorAction.REPORT).onUnmappableCharacter(java.nio.charset.CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes)).toString();}catch(java.nio.charset.CharacterCodingException e){throw new IllegalArgumentException("UTF-8 文本无效",e);}}
 static final int INFO_SIZE=40, MAX_INNER=16525, USB=7, IDENTIFY=8;
 static final String SERVICE="a29bc201-12a5-4ea1-81a0-2c4dd9cd0001",INFO="a29bc202-12a5-4ea1-81a0-2c4dd9cd0001",AUTH="a29bc203-12a5-4ea1-81a0-2c4dd9cd0001",RX="a29bc204-12a5-4ea1-81a0-2c4dd9cd0001",TX="a29bc205-12a5-4ea1-81a0-2c4dd9cd0001";
 static final SecureRandom RANDOM=new SecureRandom();
 static byte[] random(int n){byte[] b=new byte[n];RANDOM.nextBytes(b);return b;}
 static byte[] part(byte[] b,int at,int n){if(at<0||n<0||at>b.length-n)throw new IllegalArgumentException("数据边界无效");return Arrays.copyOfRange(b,at,at+n);}
 static byte[] join(byte[]... arrays){int n=0;for(byte[] a:arrays)n+=a.length;byte[] r=new byte[n];int p=0;for(byte[] a:arrays){System.arraycopy(a,0,r,p,a.length);p+=a.length;}return r;}
 static byte[] domain(String value){return (value+"\0").getBytes(StandardCharsets.US_ASCII);}
 static int u16(byte[] b,int p){return (b[p]&255)|((b[p+1]&255)<<8);}
 static int u32(byte[] b,int p){return ByteBuffer.wrap(b,p,4).order(ByteOrder.LITTLE_ENDIAN).getInt();}
 static byte[] le32(int value){return ByteBuffer.allocate(4).order(ByteOrder.LITTLE_ENDIAN).putInt(value).array();}
 static byte[] le64(long value){return ByteBuffer.allocate(8).order(ByteOrder.LITTLE_ENDIAN).putLong(value).array();}
 static String hex(byte[] value){StringBuilder r=new StringBuilder();for(byte b:value)r.append(String.format(Locale.ROOT,"%02X",b&255));return r.toString();}
 static byte[] sha(byte[] data)throws GeneralSecurityException{return MessageDigest.getInstance("SHA-256").digest(data);}
 static byte[] mac(byte[] key,byte[] data)throws GeneralSecurityException{Mac m=Mac.getInstance("HmacSHA256");m.init(new SecretKeySpec(key,"HmacSHA256"));return m.doFinal(data);}
 static boolean equal(byte[] a,byte[] b){return MessageDigest.isEqual(a,b);}
 static void length(byte[] data,int n){if(data==null||data.length!=n)throw new IllegalArgumentException("数据长度无效");}
 static final class Info {
  final byte[] bytes,id;final int profile,capabilities,flash;final boolean configured;
  Info(byte[] value){
   length(value,40);if(!Arrays.equals(part(value,0,4),"ACI2".getBytes(StandardCharsets.US_ASCII))||value[4]!=2||value[5]!=1||value[19]!=0||value[37]!=0||value[38]!=0||value[39]!=0)throw new IllegalArgumentException("不支持的设备信息版本");
   bytes=value.clone();id=part(value,20,16);profile=u16(value,6);capabilities=u32(value,8);flash=u32(value,12);configured=(value[36]&1)!=0;
   if(profile==0||flash<=0||equal(id,new byte[16]))throw new IllegalArgumentException("设备信息无效");
  }
  String number(){return hex(id);}
  String shortId(){return hex(part(id,0,2));}
  String version(){return (bytes[16]&255)+"."+(bytes[17]&255)+"."+(bytes[18]&255);}
 }
 static final class Binding {
  Info info;final byte[] controlKey,dataKey;String alias;boolean pending;
  Binding(Info info,byte[] controlKey,byte[] dataKey,String alias){length(controlKey,32);length(dataKey,32);if(equal(controlKey,new byte[32])||equal(dataKey,new byte[32]))throw new IllegalArgumentException("绑定密钥无效");this.info=info;this.controlKey=controlKey.clone();this.dataKey=dataKey.clone();this.alias=alias==null?"我的开发板":alias.replaceAll("^ +| +$","");if(this.alias.isEmpty()||this.alias.matches("(?s).*[\\x00-\\x1f\\x7f].*")||strictUtf8(this.alias).length>160)throw new IllegalArgumentException("别名需为 1–160 UTF-8 字节");}
  byte[] encode(){byte[] name=strictUtf8(alias);return join("ACBK2".getBytes(StandardCharsets.US_ASCII),info.bytes,controlKey,dataKey,new byte[]{(byte)name.length,(byte)(name.length>>>8)},name);}
  static Binding decode(byte[] bytes){if(bytes.length<111||bytes.length>271||!Arrays.equals(part(bytes,0,5),"ACBK2".getBytes(StandardCharsets.US_ASCII))||u16(bytes,109)!=bytes.length-111)throw new IllegalArgumentException("绑定配置格式错误");return new Binding(new Info(part(bytes,5,40)),part(bytes,45,32),part(bytes,77,32),strictUtf8(part(bytes,111,bytes.length-111)));}
 }
 static final class Candidate {
  final String address;final Info info;final int rssi;
  Candidate(String address,Info info,int rssi){this.address=address.replace(":","").toUpperCase(Locale.ROOT);if(!this.address.matches("[0-9A-F]{12}"))throw new IllegalArgumentException("蓝牙地址无效");this.info=info;this.rssi=rssi;}
 }
 static Candidate selected(List<Candidate> candidates,byte[] target)throws GeneralSecurityException{
  length(target,16);LinkedHashMap<String,Candidate> matches=new LinkedHashMap<>();
  for(Candidate c:candidates)if(equal(c.info.id,target))matches.put(c.address,c);
  if(matches.size()>1)throw new GeneralSecurityException("发现重复完整设备编号，已停止自动连接，请通过 USB 核对");
  return matches.isEmpty()?null:matches.values().iterator().next();
 }
 static byte[] proof(byte[] key,String role,Info info,byte[] client,byte[] server)throws GeneralSecurityException{return mac(key,join(domain("AtomClip/v2/"+role),info.bytes,client,server));}
 static byte[] control(int kind,byte[] payload){
  if(kind<1||kind>4||payload.length>256)throw new IllegalArgumentException("认证消息无效");
  byte[] b=join(new byte[]{65,67,86,50,(byte)kind,(byte)payload.length,(byte)(payload.length>>>8)},payload,new byte[4]);CRC32 crc=new CRC32();crc.update(b,4,b.length-8);System.arraycopy(le32((int)crc.getValue()),0,b,b.length-4,4);return b;
 }
 static byte[] controlPayload(byte[] frame,int kind)throws GeneralSecurityException{
  if(frame.length<11||frame[4]!=kind||frame.length!=11+u16(frame,5)||u16(frame,5)>256||!Arrays.equals(part(frame,0,4),new byte[]{65,67,86,50}))throw new GeneralSecurityException("认证消息格式错误");
  CRC32 c=new CRC32();c.update(frame,4,frame.length-8);if((int)c.getValue()!=u32(frame,frame.length-4))throw new GeneralSecurityException("认证消息校验失败");return part(frame,7,frame.length-11);
 }
 static final class Client implements AutoCloseable {
  final Binding binding;final byte[] client;byte[] server;Info info;int stage;Session session;
  Client(Binding binding){this(binding,random(32));}
  Client(Binding binding,byte[] nonce){this.binding=binding;length(nonce,32);client=nonce.clone();}
  byte[] hello(){if(stage!=0)throw new IllegalStateException("认证已开始");stage=1;return control(1,join(binding.info.id,client));}
  byte[] accept(byte[] frame)throws GeneralSecurityException{
   try{
    if(stage==1){byte[] p=controlPayload(frame,2);length(p,104);info=new Info(part(p,0,40));if(!info.configured||(info.capabilities&128)==0||!equal(info.id,binding.info.id))throw new GeneralSecurityException("设备编号不匹配或尚未绑定");server=part(p,40,32);if(!equal(part(p,72,32),proof(binding.controlKey,"server",info,client,server)))throw new GeneralSecurityException("开发板身份认证失败；不会改连其他板");stage=2;return control(3,proof(binding.controlKey,"client",info,client,server));}
    if(stage==2){byte[] p=controlPayload(frame,4);if(!equal(p,proof(binding.controlKey,"finish",info,client,server)))throw new GeneralSecurityException("连接完成证明无效");session=new Session(binding.controlKey,info,client,server,true);stage=3;return null;}
    throw new GeneralSecurityException("认证消息顺序错误");
   }catch(GeneralSecurityException|IllegalArgumentException ex){close();throw new GeneralSecurityException("设备认证未完成："+ex.getMessage(),ex);}
  }
  boolean ready(){return stage==3&&session!=null&&!session.closed;}
  public void close(){stage=-1;Arrays.fill(client,(byte)0);if(server!=null)Arrays.fill(server,(byte)0);if(session!=null)session.close();}
 }
 static final class Session implements AutoCloseable {
  final byte[] sendKey,receiveKey,sendPrefix,receivePrefix;long sendSequence,receiveSequence;boolean closed;
  Session(byte[] key,Info info,byte[] client,byte[] server,boolean isClient)throws GeneralSecurityException{
   length(key,32);length(client,32);length(server,32);byte[] salt=sha(join(domain("AtomClip/v2/session"),info.bytes,client,server)),prk=mac(salt,key);
   String send=isClient?"c2d":"d2c",receive=isClient?"d2c":"c2d";
   sendKey=expand(prk,send+"/key",32);receiveKey=expand(prk,receive+"/key",32);sendPrefix=expand(prk,send+"/nonce",4);receivePrefix=expand(prk,receive+"/nonce",4);Arrays.fill(prk,(byte)0);
  }
  static byte[] expand(byte[] prk,String label,int n)throws GeneralSecurityException{return part(mac(prk,join(domain("AtomClip/v2/"+label),new byte[]{1})),0,n);}
  synchronized byte[] seal(byte[] plain)throws GeneralSecurityException{
   if(closed||sendSequence==Long.MAX_VALUE)throw new GeneralSecurityException("会话已结束");if(plain.length<13||plain.length>MAX_INNER)throw new GeneralSecurityException("会话正文长度无效");
   byte[] header=join(new byte[]{65,67,83,50},le64(sendSequence),le32(plain.length));Cipher c=Cipher.getInstance("AES/GCM/NoPadding");c.init(Cipher.ENCRYPT_MODE,new SecretKeySpec(sendKey,"AES"),new GCMParameterSpec(128,join(sendPrefix,le64(sendSequence))));c.updateAAD(header);byte[] result=join(header,c.doFinal(plain));sendSequence++;return result;
  }
  synchronized byte[] open(byte[] frame)throws GeneralSecurityException{
   try{
    if(closed||receiveSequence==Long.MAX_VALUE||frame.length<45||frame.length>MAX_INNER+32||!Arrays.equals(part(frame,0,4),new byte[]{65,67,83,50})||u32(frame,12)!=frame.length-32||ByteBuffer.wrap(frame,4,8).order(ByteOrder.LITTLE_ENDIAN).getLong()!=receiveSequence)throw new GeneralSecurityException("会话序号、长度或版本无效");
    Cipher c=Cipher.getInstance("AES/GCM/NoPadding");c.init(Cipher.DECRYPT_MODE,new SecretKeySpec(receiveKey,"AES"),new GCMParameterSpec(128,join(receivePrefix,le64(receiveSequence))));c.updateAAD(part(frame,0,16));byte[] plain=c.doFinal(part(frame,16,frame.length-16));receiveSequence++;return plain;
   }catch(GeneralSecurityException|IllegalArgumentException ex){close();throw new GeneralSecurityException("会话数据认证失败",ex);}
  }
  public synchronized void close(){closed=true;Arrays.fill(sendKey,(byte)0);Arrays.fill(receiveKey,(byte)0);Arrays.fill(sendPrefix,(byte)0);Arrays.fill(receivePrefix,(byte)0);}
 }
 /** Strict bounded reassembly. ATT fragments may be as small as 1 byte. */
 static final class Stream {
  final boolean auth;final ByteArrayOutputStream pending=new ByteArrayOutputStream();int expected;long first,last;
  Stream(boolean auth){this.auth=auth;}
  void reset(){pending.reset();expected=0;}
  List<byte[]> feed(byte[] input,long now)throws IOException{
   ArrayList<byte[]> result=new ArrayList<>();if(pending.size()>0&&(now-last>(auth?3000:5000)||now-first>(auth?15000:120000))){reset();throw new IOException("分片接收超时");}
   for(byte b:input){if(pending.size()==0)first=now;pending.write(b);int n=pending.size();
    if(n==4&&!Arrays.equals(pending.toByteArray(),auth?new byte[]{65,67,86,50}:new byte[]{65,67,83,50})){reset();throw new IOException("分片版本无效");}
    if(n==(auth?7:16)){byte[] h=pending.toByteArray();int length=auth?u16(h,5):u32(h,12);if(length<(auth?0:13)||length>(auth?256:MAX_INNER)){reset();throw new IOException("分片长度超限");}expected=length+(auth?11:32);}
    if(expected!=0&&n==expected){result.add(pending.toByteArray());reset();}
   }
   last=now;return result;
  }
 }
 static byte[] exportBinding(Binding binding,char[] password)throws GeneralSecurityException{
  if(password.length<12||password.length>128)throw new GeneralSecurityException("配置口令需为12–128字符");byte[] salt=random(16),nonce=random(12),plain=binding.encode();byte[] header=join("ACBF2".getBytes(StandardCharsets.US_ASCII),salt,le32(200000),nonce,le32(plain.length));byte[] k=derivePassword(password,salt);
  try{Cipher c=Cipher.getInstance("AES/GCM/NoPadding");c.init(Cipher.ENCRYPT_MODE,new SecretKeySpec(k,"AES"),new GCMParameterSpec(128,nonce));c.updateAAD(header);return join(header,c.doFinal(plain));}finally{Arrays.fill(k,(byte)0);Arrays.fill(plain,(byte)0);}
 }
 static Binding importBinding(byte[] data,char[] password)throws GeneralSecurityException{
  if(data.length<168||data.length>328||!Arrays.equals(part(data,0,5),"ACBF2".getBytes(StandardCharsets.US_ASCII))||u32(data,21)!=200000||u32(data,37)!=data.length-57)throw new GeneralSecurityException("不支持的加密配置格式");byte[] k=derivePassword(password,part(data,5,16)),plain=null;
  try{Cipher c=Cipher.getInstance("AES/GCM/NoPadding");c.init(Cipher.DECRYPT_MODE,new SecretKeySpec(k,"AES"),new GCMParameterSpec(128,part(data,25,12)));c.updateAAD(part(data,0,41));plain=c.doFinal(part(data,41,data.length-41));return Binding.decode(plain);}catch(IllegalArgumentException ex){throw new GeneralSecurityException("配置内容无效",ex);}finally{Arrays.fill(k,(byte)0);if(plain!=null)Arrays.fill(plain,(byte)0);}
 }
 static byte[] derivePassword(char[] password,byte[] salt)throws GeneralSecurityException{if(password.length<12||password.length>128)throw new GeneralSecurityException("配置口令需为12–128字符");PBEKeySpec spec=new PBEKeySpec(password,salt,200000,256);try{return SecretKeyFactory.getInstance("PBKDF2WithHmacSHA256").generateSecret(spec).getEncoded();}finally{spec.clearPassword();}}
 static byte[] usbFrame(byte[] payload){byte[] b=join(new byte[]{65,67,66,49,7},le32(payload.length),payload,new byte[4]);CRC32 crc=new CRC32();crc.update(b,4,b.length-8);System.arraycopy(le32((int)crc.getValue()),0,b,b.length-4,4);return b;}
 static boolean validInner(byte[] b){if(b.length<13||b.length>MAX_INNER||b[0]!=65||b[1]!=67||b[2]!=66||b[3]!=49||u32(b,5)!=b.length-13||!((b[4]>=1&&b[4]<=6)||b[4]==8))return false;CRC32 crc=new CRC32();crc.update(b,4,b.length-8);return (int)crc.getValue()==u32(b,b.length-4);}
}
