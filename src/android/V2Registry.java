package dev.atomclip.mobile;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;

/** Pending writes coexist with old confirmed keys until a USB commit is verified. */
final class V2Registry {
 final LinkedHashMap<String,V2Protocol.Binding> confirmed=new LinkedHashMap<>(),pending=new LinkedHashMap<>();
 String selectedId="";
 V2Protocol.Binding active(){return pending.containsKey(selectedId)?null:confirmed.get(selectedId);}
 V2Protocol.Binding stage(V2Protocol.Binding binding){V2Protocol.Binding old=pending.get(binding.info.number());if(old!=null){if(!V2Protocol.equal(old.controlKey,binding.controlKey)||!V2Protocol.equal(old.dataKey,binding.dataKey))throw new IllegalArgumentException("请先处理此板的待确认绑定");return old;}V2Protocol.Binding copy=V2Protocol.Binding.decode(binding.encode());copy.pending=true;pending.put(copy.info.number(),copy);return copy;}
 void confirm(String id){V2Protocol.Binding binding=pending.remove(id);if(binding==null)throw new IllegalArgumentException("没有待确认配置");binding.pending=false;confirmed.put(id,binding);selectedId=id;}
 void select(String id){if(!confirmed.containsKey(id)||pending.containsKey(id))throw new IllegalArgumentException("请先通过 USB 确认此绑定");selectedId=id;}
 void forget(String id){confirmed.remove(id);pending.remove(id);if(selectedId.equals(id))selectedId="";}
 byte[] encode()throws IOException{
  if(confirmed.size()+pending.size()>64)throw new IOException("最多保存64条设备配置");ByteArrayOutputStream out=new ByteArrayOutputStream();DataOutputStream d=new DataOutputStream(out);d.writeBytes("ACBS2");d.writeByte(0);writeString(d,selectedId);writeString(d,"");writeU16(d,confirmed.size()+pending.size());
  for(Map<String,V2Protocol.Binding> map:Arrays.asList(confirmed,pending))for(V2Protocol.Binding binding:map.values()){byte[] b=binding.encode();writeU16(d,b.length);d.writeByte(map==pending?1:0);d.write(b);}return out.toByteArray();
 }
 static void writeU16(DataOutputStream out,int n)throws IOException{out.writeByte(n);out.writeByte(n>>>8);}
 static int readU16(DataInputStream in)throws IOException{return in.readUnsignedByte()|(in.readUnsignedByte()<<8);}
 static void writeString(DataOutputStream out,String s)throws IOException{byte[] bytes=s.getBytes(StandardCharsets.US_ASCII);if(bytes.length>32)throw new IOException("设备标识过长");out.writeByte(bytes.length);out.write(bytes);}
 static String readString(DataInputStream in)throws IOException{int n=in.readUnsignedByte();if(n>32)throw new IOException("设备标识过长");byte[] b=new byte[n];in.readFully(b);return new String(b,StandardCharsets.US_ASCII);}
 static V2Registry decode(byte[] bytes)throws IOException{
  if(bytes.length>32768)throw new IOException("本地配置过大");DataInputStream in=new DataInputStream(new ByteArrayInputStream(bytes));byte[] magic=new byte[5];in.readFully(magic);if(!Arrays.equals(magic,"ACBS2".getBytes(StandardCharsets.US_ASCII)))throw new IOException("本地配置版本无效");
  V2Registry r=new V2Registry();int legacy=in.readUnsignedByte();if(legacy>1)throw new IOException("本地模式无效");r.selectedId=readString(in);String retiredAddress=readString(in);
  if(!r.selectedId.matches("(?:[0-9A-F]{32})?")||!retiredAddress.matches("(?:[0-9A-F]{12})?"))throw new IOException("本地设备标识无效");int count=readU16(in);if(count>64)throw new IOException("本地配置数量无效");
  for(int i=0;i<count;i++){int length=readU16(in),state=in.readUnsignedByte();if(length<111||length>271||state>1)throw new IOException("本地设备记录无效");byte[] b=new byte[length];in.readFully(b);try{V2Protocol.Binding binding=V2Protocol.Binding.decode(b);binding.pending=state==1;Map<String,V2Protocol.Binding> map=state==1?r.pending:r.confirmed;if(map.put(binding.info.number(),binding)!=null)throw new IOException("重复设备记录");}catch(IllegalArgumentException ex){throw new IOException("本地设备记录错误",ex);}finally{Arrays.fill(b,(byte)0);}}
  if(in.available()!=0)throw new IOException("本地配置存在多余内容");if(legacy==1)r.selectedId="";return r;
 }
}
