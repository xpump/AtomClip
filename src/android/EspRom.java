package dev.atomclip.mobile;

import java.io.*;
import java.nio.*;
import java.nio.charset.StandardCharsets;
import java.security.MessageDigest;
import java.util.*;
import java.util.zip.DeflaterOutputStream;

/** ESP32-S3 ROM protocol. No stub, executable, shell, or third-party USB library.
 * Wire contract: https://docs.espressif.com/projects/esptool/en/latest/esp32s3/advanced-topics/serial-protocol.html
 */
final class EspRom {
 static final int IMAGE_SIZE=8*1024*1024, BLOCK=1024;
 interface Transport extends AutoCloseable {
  void write(byte[] bytes,int timeout)throws IOException;
  int read(byte[] bytes,int timeout)throws IOException;
  void close();
 }
 interface Progress {void update(String phase,int percent);boolean cancelled();}
 final Transport io; final Progress progress;int flashBytes;boolean octal;
 final byte[] input=new byte[4096];int at,count;
 EspRom(Transport io,Progress progress){this.io=io;this.progress=progress;}
 static byte[] words(int... values){ByteBuffer b=ByteBuffer.allocate(values.length*4).order(ByteOrder.LITTLE_ENDIAN);for(int v:values)b.putInt(v);return b.array();}
 static int word(byte[] data,int at){return ByteBuffer.wrap(data,at,4).order(ByteOrder.LITTLE_ENDIAN).getInt();}
 static String digest(String algorithm,byte[] data)throws IOException{try{StringBuilder s=new StringBuilder();for(byte b:MessageDigest.getInstance(algorithm).digest(data))s.append(String.format(Locale.ROOT,"%02x",b&255));return s.toString();}catch(Exception ex){throw new IOException("无法计算固件校验值",ex);}}
 static void validateImage(byte[] image,String sha256)throws IOException{
  if(image.length!=IMAGE_SIZE||(image[0]&255)!=0xe9||(image[12]&255)!=9||image[13]!=0)throw new IOException("内置固件不是 ESP32-S3 8MB 完整镜像");
  if(!digest("SHA-256",image).equalsIgnoreCase(sha256.trim()))throw new IOException("内置固件校验失败，请重新获取 APK");
 }
 static byte[] slip(byte[] raw){ByteArrayOutputStream b=new ByteArrayOutputStream();b.write(0xc0);for(byte v:raw){int c=v&255;if(c==0xc0){b.write(0xdb);b.write(0xdc);}else if(c==0xdb){b.write(0xdb);b.write(0xdd);}else b.write(c);}b.write(0xc0);return b.toByteArray();}
 void checkCancel()throws IOException{if(progress.cancelled()||Thread.currentThread().isInterrupted())throw new IOException("已中止；重新进入下载模式后完整重试");}
 int next(long deadline)throws IOException{
  while(at>=count){checkCancel();long remaining=deadline-System.nanoTime()/1000000;if(remaining<=0)throw new java.net.SocketTimeoutException("开发板响应超时，请重新进入下载模式");count=io.read(input,(int)Math.min(250,remaining));at=0;if(count<0)throw new IOException("USB 已断开");}
  return input[at++]&255;
 }
 byte[] packet(long deadline)throws IOException{
  ByteArrayOutputStream out=new ByteArrayOutputStream();boolean started=false,escape=false;int noise=0;
  while(true){int c=next(deadline);if(!started){if(c==0xc0)started=true;else if(++noise>8192)throw new IOException("不是有效的 ROM 下载数据");continue;}
   if(c==0xc0){if(out.size()==0)continue;if(escape)throw new IOException("USB 数据转义不完整");return out.toByteArray();}
   if(escape){if(c!=0xdc&&c!=0xdd)throw new IOException("USB 数据转义无效");out.write(c==0xdc?0xc0:0xdb);escape=false;}else if(c==0xdb)escape=true;else out.write(c);
   if(out.size()>4096)throw new IOException("USB 响应超长");
  }
 }
 byte[] command(int op,byte[] data,int checksum,int timeout)throws IOException{
  checkCancel();ByteBuffer req=ByteBuffer.allocate(8+data.length).order(ByteOrder.LITTLE_ENDIAN);req.put((byte)0).put((byte)op).putShort((short)data.length).putInt(checksum).put(data);io.write(slip(req.array()),5000);
  long end=System.nanoTime()/1000000+timeout;
  for(int ignored=0;ignored<64;ignored++){
   byte[] r=packet(end);if(r.length<8||r[0]!=1)continue;if((r[1]&255)!=op)continue;
   int len=(r[2]&255)|((r[3]&255)<<8);if(len<4||r.length!=8+len)throw new IOException("ROM 响应长度无效");
   if(r[r.length-4]!=0||r[r.length-3]!=0)throw new IOException("ROM 拒绝命令 0x"+Integer.toHexString(op)+"（错误 "+(r[r.length-3]&255)+"）");
   return r;
  }
  throw new IOException("ROM 响应不匹配，请重新进入下载模式");
 }
 byte[] command(int op,byte[] data)throws IOException{return command(op,data,0,5000);}
 int readReg(int addr)throws IOException{return word(command(0x0a,words(addr)),4);}
 void writeReg(int addr,int value)throws IOException{command(0x09,words(addr,value,-1,0));}
 String identify()throws IOException{
  progress.update("同步 ROM 下载模式…",1);byte[] sync=new byte[36];Arrays.fill(sync,(byte)0x55);sync[0]=7;sync[1]=7;sync[2]=0x12;sync[3]=0x20;
  boolean synced=false;for(int i=0;i<7&&!synced;i++)try{command(0x08,sync,0,800);synced=true;}catch(java.net.SocketTimeoutException ex){at=count=0;}
  if(!synced)throw new IOException("未进入 ROM 下载模式，请长按 RESET 后重试");
  byte[] sec=command(0x14,new byte[0]);if(sec.length!=32||word(sec,20)!=9)throw new IOException("仅支持 ESP32-S3，未执行烧录");
  int flags=word(sec,8);if((flags&5)!=0||sec[12]!=0)throw new IOException("设备启用了安全启动、加密或安全下载，无法写入此固件");
  // ROM on the USB Serial/JTAG port requires its download watchdogs disabled.
  if(readReg(0x3fcef14c)==4){writeReg(0x600080b0,0x50d83aa1);writeReg(0x60008098,0);writeReg(0x600080b0,0);writeReg(0x600080b8,0x8f1d312a);writeReg(0x600080b4,readReg(0x600080b4)|0x80000000);writeReg(0x600080b8,0);}
  octal=(readReg(0x6000703c)&(1<<9))!=0;if(octal)throw new IOException("暂不支持 Octal / OPI Flash，未执行烧录");
  command(0x0d,words(0,0));int id=flashId(),exponent=(id>>>16)&255;if(exponent<22||exponent>24)throw new IOException("仅支持 4 / 8 / 16 MB SPI NOR Flash");flashBytes=1<<exponent;
  String serial=String.format(Locale.ROOT,"%04X%08X",readReg(0x60007048)&65535,readReg(0x60007044));progress.update("已识别 ESP32-S3，请核对板型与原生 USB 口",5);return String.format(Locale.ROOT,"ESP32-S3 · %dMB · MAC %s · Flash ID %06X",flashBytes/1048576,serial,id&0xffffff);
 }
 int flashId()throws IOException{
  int base=0x60002000,usr=readReg(base+0x18),usr2=readReg(base+0x20),miso=readReg(base+0x28);
  try{writeReg(base+0x28,23);writeReg(base+0x18,0x90000000);writeReg(base+0x20,0x7000009f);writeReg(base+0x58,0);writeReg(base,1<<18);
   for(int i=0;i<20;i++){if((readReg(base)&(1<<18))==0)return readReg(base+0x58);}
   throw new IOException("读取 Flash ID 超时");
  }finally{writeReg(base+0x18,usr);writeReg(base+0x20,usr2);writeReg(base+0x28,miso);}
 }
 byte[] readFlash(int address,int length)throws IOException{
  if(address<0||length<1||(long)address+length>flashBytes)throw new IOException("读取范围无效");
  byte[] result=new byte[length];int base=0x60002000;int[] regs={4,0x18,0x1c,0x20,0x28};int[] old=new int[regs.length];for(int i=0;i<regs.length;i++)old[i]=readReg(base+regs[i]);
  try{for(int at=0;at<length;at+=64){checkCancel();int n=Math.min(64,length-at);writeReg(base+4,address+at);writeReg(base+0x1c,23<<26);writeReg(base+0x28,n*8-1);writeReg(base+0x18,0xd0000000);writeReg(base+0x20,0x70000003);writeReg(base,1<<18);boolean done=false;for(int j=0;j<20;j++)if((readReg(base)&(1<<18))==0){done=true;break;}if(!done)throw new IOException("读取分区表超时");for(int off=0;off<n;off+=4){int v=readReg(base+0x58+off);for(int b=0;b<4&&off+b<n;b++)result[at+off+b]=(byte)(v>>>(b*8));}}return result;}
  finally{for(int i=0;i<regs.length;i++)writeReg(base+regs[i],old[i]);}
 }
 void writeBundle(FirmwareBundle bundle,boolean factory)throws IOException{
  bundle.checkTarget(flashBytes,octal,readFlash(0x8000,3072),factory);checkCancel();command(0x0b,words(0,flashBytes,65536,4096,256,65535));
  if(factory){byte[] erased=new byte[flashBytes];Arrays.fill(erased,(byte)255);writeSegment(0,erased,5,20,"清空全部 Flash（含绑定）");}
  for(int i=0;i<bundle.segments.length;i++){FirmwareBundle.Segment seg=bundle.segments[i];writeSegment(seg.offset,seg.data,25+i*18,18,"写入段 "+(i+1)+" / 4");}
  checkCancel();progress.update("各段写入与校验完成；请拔插开发板",100);
 }
 void writeSegment(int offset,byte[] image,int start,int span,String phase)throws IOException{
  checkCancel();ByteArrayOutputStream packed=new ByteArrayOutputStream();try(DeflaterOutputStream zip=new DeflaterOutputStream(packed)){zip.write(image);}byte[] compressed=packed.toByteArray();int blocks=(compressed.length+BLOCK-1)/BLOCK;
  progress.update(phase+" · 擦除对应范围",start);command(0x10,words(image.length,blocks,BLOCK,offset,0),0,300000);
  for(int i=0;i<blocks;i++){checkCancel();int len=Math.min(BLOCK,compressed.length-i*BLOCK);byte[] payload=new byte[16+len];System.arraycopy(words(len,i,0,0),0,payload,0,16);System.arraycopy(compressed,i*BLOCK,payload,16,len);int checksum=0xef;for(int j=16;j<payload.length;j++)checksum^=payload[j]&255;command(0x11,payload,checksum,30000);progress.update(phase,start+(i+1)*span/blocks);}
  // ROM DEFL_END exits download mode; verify before any exit/reset command.
  byte[] reply=command(0x13,words(offset,image.length,0,0),0,90000);if(reply.length!=44||!digest("MD5",image).equalsIgnoreCase(new String(reply,8,32,StandardCharsets.US_ASCII)))throw new IOException("写入段校验失败，未确认烧录成功");
 }
}
