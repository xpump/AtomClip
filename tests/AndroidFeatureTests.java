package dev.atomclip.mobile;

import java.io.*;
import java.nio.*;
import java.nio.charset.StandardCharsets;
import java.util.*;
import java.util.zip.InflaterInputStream;

/** Desktop JVM tests: exercises production ROM framing, checks and compressed writes. */
public final class AndroidFeatureTests {
 static int checks;
 static void require(boolean ok,String why){checks++;if(!ok)throw new AssertionError(why);}
 interface Job {void run()throws Exception;}
 static void fails(Job job,String why)throws Exception{try{job.run();throw new AssertionError(why);}catch(IOException expected){checks++;}}
 static final class Status implements EspRom.Progress {
  int percent;boolean cancel;String phase;
  public void update(String s,int p){phase=s;percent=p;}
  public boolean cancelled(){return cancel;}
 }
 static final class RomDevice implements EspRom.Transport {
  final ArrayDeque<Byte> rx=new ArrayDeque<>();final Map<Integer,Integer> regs=new HashMap<>();final List<Integer> commands=new ArrayList<>();
  final ByteArrayOutputStream compressed=new ByteArrayOutputStream();
  int chip=9,capacity=23,flags,crypt,seq,writes;boolean detached,badMd5,badLength,badEscape,drop,closed;int reject=-1;
  byte[] flashed;byte[] memory=new byte[16777216];int writeOffset,writeLength;
  int spiRead(int addr){if((regs.getOrDefault(0x60002020,0)&255)==159)return (capacity<<16)|0x40ef;int offset=regs.getOrDefault(0x60002004,0)+addr-0x60002058;return (memory[offset]&255)|((memory[offset+1]&255)<<8)|((memory[offset+2]&255)<<16)|((memory[offset+3]&255)<<24);}
  RomDevice(){regs.put(0x3fcef14c,4);}
  byte[] unframe(byte[] wire)throws IOException{
   require((wire[0]&255)==0xc0&&(wire[wire.length-1]&255)==0xc0,"SLIP delimiters");ByteArrayOutputStream raw=new ByteArrayOutputStream();
   for(int i=1;i<wire.length-1;i++){int v=wire[i]&255;if(v==0xdb){v=wire[++i]&255;require(v==0xdc||v==0xdd,"SLIP escape");v=v==0xdc?0xc0:0xdb;}raw.write(v);}return raw.toByteArray();
  }
  void response(int op,int value,byte[] payload,int error){
   ByteBuffer r=ByteBuffer.allocate(12+payload.length).order(ByteOrder.LITTLE_ENDIAN);r.put((byte)1).put((byte)op).putShort((short)(payload.length+4)).putInt(value).put(payload).put((byte)(error==0?0:1)).put((byte)error).putShort((short)0);
   byte[] raw=r.array();if(badLength)raw[2]++;for(byte b:EspRom.slip(raw))rx.add(b);
  }
  public void write(byte[] wire,int timeout)throws IOException{
   if(detached)throw new IOException("detached");byte[] r=unframe(wire);int op=r[1]&255;commands.add(op);require(r[0]==0&&r.length==8+((r[2]&255)|((r[3]&255)<<8)),"request length");
   byte[] data=Arrays.copyOfRange(r,8,r.length);if(drop)return;if(badEscape){for(int b:new int[]{0xc0,1,0xdb,7,0xc0})rx.add((byte)b);return;}
   if(op==reject){response(op,0,new byte[0],5);return;}
   if(op==8){require(data.length==36&&data[0]==7&&data[35]==0x55,"SYNC payload");for(int i=0;i<8;i++)response(op,0x20100707,new byte[0],0);}
   else if(op==0x14){byte[] sec=EspRom.words(flags,crypt,0,chip,1);response(op,0,sec,0);}
   else if(op==0x0a){int addr=EspRom.word(data,0),v=regs.containsKey(addr)?regs.get(addr):0;if(addr==0x60002000)v=0;if(addr>=0x60002058&&addr<0x60002098)v=spiRead(addr);response(op,v,new byte[0],0);}
   else if(op==9){require(data.length==16&&EspRom.word(data,8)==-1,"register write mask");regs.put(EspRom.word(data,0),EspRom.word(data,4));response(op,0,new byte[0],0);}
   else if(op==0x10){writes++;writeLength=EspRom.word(data,0);writeOffset=EspRom.word(data,12);compressed.reset();seq=0;require(data.length==20&&writeLength>0&&EspRom.word(data,8)==1024&&writeOffset>=0&&(long)writeOffset+writeLength<=1<<capacity&&EspRom.word(data,16)==0,"S3 bounded DEFL_BEGIN parameters");response(op,0,new byte[0],0);}
   else if(op==0x11){int n=EspRom.word(data,0);require(EspRom.word(data,4)==seq++&&n==data.length-16&&n<=1024,"compressed sequence");int checksum=0xef;for(int i=16;i<data.length;i++)checksum^=data[i]&255;require(checksum==EspRom.word(r,4),"XOR checksum");compressed.write(data,16,n);response(op,0,new byte[0],0);}
   else if(op==0x13){require(!commands.contains(0x12),"ROM must not exit before verification");require(data.length==16&&EspRom.word(data,0)==writeOffset&&EspRom.word(data,4)==writeLength,"MD5 segment range");try(InflaterInputStream zip=new InflaterInputStream(new ByteArrayInputStream(compressed.toByteArray()));ByteArrayOutputStream out=new ByteArrayOutputStream()){byte[] b=new byte[4096];int n;while((n=zip.read(b))!=-1)out.write(b,0,n);flashed=out.toByteArray();}require(flashed.length==writeLength,"decompressed segment length");System.arraycopy(flashed,0,memory,writeOffset,writeLength);String md5=badMd5?"00000000000000000000000000000000":EspRom.digest("MD5",flashed);response(op,0,md5.getBytes(StandardCharsets.US_ASCII),0);}
   else response(op,0,new byte[0],0);
  }
  public int read(byte[] buffer,int timeout)throws IOException{if(detached)throw new IOException("detached");int n=Math.min(Math.min(7,buffer.length),rx.size());for(int i=0;i<n;i++)buffer[i]=rx.removeFirst();return n;}
  public void close(){closed=true;}
 }
 static void editor(){
  EditorHistory h=new EditorHistory();h.record("",0,0,0,0,1,100,false);h.record("a",1,1,1,0,1,200,false);h.record("ab",2,2,2,0,1,400,false);require(h.undo().value.equals(""),"continuous typing is one undo transaction");
  h.record("abc",1,2,0,3,6,500,true);h.record("中文ABC",5,5,0,5,0,600,true);EditorHistory.Snapshot clear=h.undo();require(clear.value.equals("中文ABC"),"clear restores mixed text");EditorHistory.Snapshot paste=h.undo();require(paste.value.equals("abc")&&paste.start==1&&paste.end==2,"paste undo restores selection");
  h.record("a",1,1,1,0,1,1000,false);h.record("ab",2,2,2,0,1,2200,false);require(h.undo().value.equals("ab"),"pause begins new transaction");
  h.boundary();h.remember("草稿ABC",2,4);int count=h.undo.size();h.remember(h.value,h.start,h.end);require(h.undo.size()==count&&h.value.equals("草稿ABC"),"fullscreen/caret capture does not create undo");
  EditorHistory other=new EditorHistory();other.remember("另一模式",0,0);require(!other.canUndo()&&h.canUndo(),"route histories independent");
  for(int i=0;i<100;i++)h.record("value"+i,0,0,0,5,0,i*2000,true);require(h.undo.size()==40,"history bounded");
 }
 static FirmwareBundle bundle(int mb)throws Exception{
  FirmwareBundle.Segment[] segments=new FirmwareBundle.Segment[4];int[] offsets={0,0x8000,0xe000,0x10000},lengths={18720,3072,8192,77000};
  for(int i=0;i<4;i++){byte[] b=new byte[lengths[i]];for(int j=0;j<b.length;j++)b[j]=(byte)(j*31+i);if(i==0||i==3){b[0]=(byte)233;b[12]=9;b[13]=0;}segments[i]=new FirmwareBundle.Segment(offsets[i],b,EspRom.digest("SHA-256",b));}return new FirmwareBundle(mb==4?260:mb==8?264:272,mb*1048576,"test","",segments);
 }
 static void install(RomDevice io,Status status,FirmwareBundle b,boolean factory)throws Exception{EspRom r=new EspRom(io,status);r.identify();r.writeBundle(b,factory);}
 static void compatible(RomDevice d,FirmwareBundle b){System.arraycopy(b.segments[1].data,0,d.memory,0x8000,3072);Arrays.fill(d.memory,0x9000,0xe000,(byte)90);}
 public static void main(String[] args)throws Exception{
  editor();FirmwareBundle b=bundle(8);RomDevice good=new RomDevice();compatible(good,b);Status ok=new Status();install(good,ok,b,false);require(ok.percent==100&&good.writes==4,"four compressed segments verified");for(int i=0x9000;i<0xe000;i++)require(good.memory[i]==90,"normal upgrade preserves NVS byte");for(FirmwareBundle.Segment s:b.segments)require(Arrays.equals(s.data,Arrays.copyOfRange(good.memory,s.offset,s.offset+s.data.length)),"segment content");require(good.regs.get(0x60008098)==0,"USB watchdog disabled");
  for(int mb:new int[]{4,16}){FirmwareBundle p=bundle(mb);RomDevice d=new RomDevice();d.capacity=mb==4?22:24;compatible(d,p);install(d,new Status(),p,false);require(d.writes==4,"capacity-specific profile writes");}
  RomDevice incompatible=new RomDevice();fails(()->install(incompatible,new Status(),b,false),"incompatible table accepted");require(incompatible.writes==0,"incompatible cannot erase");
  RomDevice factory=new RomDevice();Arrays.fill(factory.memory,0x9000,0xe000,(byte)90);install(factory,new Status(),b,true);require(factory.writes==5&&factory.memory[0x9000]==(byte)255,"explicit factory install clears NVS");
  FirmwareBundle.Segment[] broken=b.segments.clone();broken[0]=new FirmwareBundle.Segment(0,broken[0].data,"bad");fails(()->new FirmwareBundle(264,8388608,"test","",broken),"invalid SHA accepted");
  FirmwareBundle.Segment[] overlap=b.segments.clone();overlap[1]=new FirmwareBundle.Segment(0x9000,overlap[1].data,overlap[1].sha);fails(()->new FirmwareBundle(264,8388608,"test","",overlap),"NVS overlap accepted");
  RomDevice wrongChip=new RomDevice();wrongChip.chip=2;fails(()->install(wrongChip,new Status(),b,false),"wrong chip accepted");require(wrongChip.writes==0,"wrong chip cannot erase");
  RomDevice wrongSize=new RomDevice();wrongSize.capacity=22;fails(()->install(wrongSize,new Status(),b,false),"wrong size accepted");require(wrongSize.writes==0,"size mismatch cannot erase");
  RomDevice octal=new RomDevice();octal.regs.put(0x6000703c,512);fails(()->install(octal,new Status(),b,false),"OPI accepted");require(octal.writes==0,"OPI cannot erase");
  for(int flag:new int[]{1,4}){RomDevice d=new RomDevice();d.flags=flag;fails(()->install(d,new Status(),b,false),"security bypass");require(d.writes==0,"secure board cannot erase");}
  RomDevice encrypted=new RomDevice();encrypted.crypt=1;fails(()->install(encrypted,new Status(),b,false),"encryption accepted");require(encrypted.writes==0,"encrypted cannot erase");
  RomDevice corrupt=new RomDevice();compatible(corrupt,b);corrupt.badMd5=true;Status bad=new Status();fails(()->install(corrupt,bad,b,false),"bad readback accepted");require(bad.percent<100&&corrupt.writes==1,"MD5 fails before next segment");
  RomDevice denied=new RomDevice();compatible(denied,b);denied.reject=0x10;fails(()->install(denied,new Status(),b,false),"ROM rejection ignored");require(denied.seq==0,"no data after erase rejection");
  RomDevice unplug=new RomDevice();unplug.detached=true;fails(()->new EspRom(unplug,new Status()).identify(),"unplug ignored");
  RomDevice malformed=new RomDevice();malformed.badLength=true;fails(()->new EspRom(malformed,new Status()).identify(),"bad frame accepted");
  RomDevice escaping=new RomDevice();escaping.badEscape=true;fails(()->new EspRom(escaping,new Status()).identify(),"bad SLIP accepted");
  RomDevice cancelled=new RomDevice();Status cancel=new Status();cancel.cancel=true;fails(()->new EspRom(cancelled,cancel).identify(),"cancel ignored");require(cancelled.writes==0,"cancel before erase");
  RomDevice timeout=new RomDevice();timeout.drop=true;fails(()->new EspRom(timeout,new Status()).command(0x14,new byte[0],0,20),"timeout ignored");
  RomDevice inspect=new RomDevice();new EspRom(inspect,new Status()).identify();require(inspect.writes==0,"inspection cannot erase");
  System.out.println("Android editing / segmented ROM tests passed: "+checks+" assertions; no hardware accessed.");
 }
}
