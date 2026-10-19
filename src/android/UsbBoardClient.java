package dev.atomclip.mobile;

import java.io.*;
import java.security.*;
import java.util.*;
import java.util.zip.CRC32;

/** Physical USB management only. Never constructed from a BLE transport. */
final class UsbBoardClient implements AutoCloseable {
 final EspRom.Transport io;final ByteArrayOutputStream pending=new ByteArrayOutputStream();
 UsbBoardClient(EspRom.Transport io){this.io=io;}
 byte[] call(int opcode,byte[] arguments)throws IOException{
  if(opcode<1||opcode>5||arguments.length>65)throw new IllegalArgumentException("USB 请求无效");byte[] id=correlatedRequest==null?V2Protocol.random(16):correlatedRequest.clone();io.write(V2Protocol.usbFrame(V2Protocol.join(new byte[]{(byte)opcode},id,arguments)),3000);
  long deadline=System.nanoTime()/1000000+4000;byte[] buffer=new byte[512];
  while(System.nanoTime()/1000000<deadline){int n=io.read(buffer,250);if(n<0)throw new IOException("USB 已断开");
   for(int i=0;i<n;i++){pending.write(buffer[i]);byte[] p=pending.toByteArray();
    while(p.length>=9){int length=V2Protocol.u32(p,5);if(p[0]!=65||p[1]!=67||p[2]!=66||p[3]!=49||p[4]!=7||length<18||length>256){p=V2Protocol.part(p,1,p.length-1);continue;}
     if(p.length<length+13)break;CRC32 c=new CRC32();c.update(p,4,length+5);if((int)c.getValue()!=V2Protocol.u32(p,9+length)){p=V2Protocol.part(p,1,p.length-1);continue;}
     byte[] body=V2Protocol.part(p,9,length);p=V2Protocol.part(p,length+13,p.length-length-13);
     if((body[0]&255)==(opcode|0x80)&&V2Protocol.equal(V2Protocol.part(body,1,16),id)){if(body[17]!=0)throw new IOException("USB 管理未完成，代码 "+(body[17]&255));return V2Protocol.part(body,18,body.length-18);}
    }
    pending.reset();pending.write(p,0,p.length);
   }
  }
  throw new IOException("USB 管理响应超时；确认正在运行 0.2.0 固件，而非 ROM 下载模式");
 }
 static final class Material {final V2Protocol.Info info;final byte[] dataKey;Material(byte[] b)throws IOException{if(b.length!=72)throw new IOException("USB 配对信息长度错误");try{info=new V2Protocol.Info(V2Protocol.part(b,0,40));dataKey=V2Protocol.part(b,40,32);}catch(IllegalArgumentException ex){throw new IOException("USB 配对信息无效",ex);}finally{Arrays.fill(b,(byte)0);}}}
 Material read()throws IOException{return new Material(call(4,new byte[0]));}
 // The caller must persist the candidate as Pending before invoking this method.
 V2Protocol.Info provision(V2Protocol.Binding binding,boolean replace)throws IOException,GeneralSecurityException{
  checkPhysical(binding,false);
  byte[] args=V2Protocol.join(new byte[]{(byte)(replace?1:0)},binding.controlKey,binding.dataKey);
  V2Protocol.Info result=writeWithProof(2,args,binding,"usb-bind");checkPhysical(binding,true);return result;
 }
 V2Protocol.Info confirm(V2Protocol.Binding pending)throws IOException,GeneralSecurityException{checkPhysical(pending,true);return writeWithProof(5,new byte[0],pending,"usb-proof");}
 void checkPhysical(V2Protocol.Binding binding,boolean compareData)throws IOException,GeneralSecurityException{Material actual=read();try{if(!V2Protocol.equal(actual.info.id,binding.info.id))throw new GeneralSecurityException("USB 目标已改变；未写入绑定");if(compareData&&!V2Protocol.equal(actual.dataKey,binding.dataKey))throw new GeneralSecurityException("USB 正文配对不一致；请保留待确认配置并核对");}finally{Arrays.fill(actual.dataKey,(byte)0);}}
 V2Protocol.Info writeWithProof(int opcode,byte[] arguments,V2Protocol.Binding binding,String role)throws IOException,GeneralSecurityException{
  // A fresh correlated request and proof are required; no public BLE read exposes keys.
  byte[] request=V2Protocol.random(16);byte[] response=callWithRequest(opcode,arguments,request);
  if(response.length!=72)throw new GeneralSecurityException("USB 绑定证明长度无效");V2Protocol.Info info=new V2Protocol.Info(V2Protocol.part(response,0,40));
  byte[] expected=V2Protocol.mac(binding.controlKey,V2Protocol.join(V2Protocol.domain("AtomClip/v2/"+role),info.bytes,request));
  if(!info.configured||!V2Protocol.equal(info.id,binding.info.id)||!V2Protocol.equal(expected,V2Protocol.part(response,40,32)))throw new GeneralSecurityException("USB 绑定未确认；待确认密钥已保留，请重新接入 USB 核对");return info;
 }
 byte[] callWithRequest(int opcode,byte[] arguments,byte[] request)throws IOException{
  // Reuse the strict packet reader while making the nonce available to HMAC verification.
  correlatedRequest=request;
  try{return call(opcode,arguments);}finally{correlatedRequest=null;Arrays.fill(arguments,(byte)0);}
 }
 byte[] correlatedRequest;
 V2Protocol.Info reset(V2Protocol.Info expected)throws IOException,GeneralSecurityException{Material actual=read();try{if(!V2Protocol.equal(actual.info.id,expected.id))throw new GeneralSecurityException("USB 目标已改变，未清除配对");}finally{Arrays.fill(actual.dataKey,(byte)0);}V2Protocol.Info result=new V2Protocol.Info(call(3,new byte[0]));if(result.configured||!V2Protocol.equal(result.id,expected.id))throw new GeneralSecurityException("设备未确认清除配对");return result;}
 public void close(){io.close();pending.reset();}
}
