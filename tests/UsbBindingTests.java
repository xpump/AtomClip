package dev.atomclip.mobile;

import java.io.*;
import java.nio.file.*;
import java.security.*;
import java.util.*;
import java.util.zip.CRC32;

final class UsbBindingTests {
 static int checks;static void check(boolean ok,String label){checks++;if(!ok)throw new AssertionError(label);}
 interface Job {void run()throws Exception;}
 static void reject(Job j,String label)throws Exception{try{j.run();throw new AssertionError(label);}catch(IOException|GeneralSecurityException|IllegalArgumentException expected){checks++;}}
 static final class Board implements EspRom.Transport {
  byte[] info,data,control=new byte[32];final ArrayDeque<Byte> incoming=new ArrayDeque<>();boolean bound,loseAck;int writes;
  Board(byte[] info,byte[] data){this.info=info.clone();this.info[36]=2;this.data=data.clone();}
  byte[] currentInfo(){byte[] i=info.clone();i[36]=(byte)(bound?3:2);return i;}
  public void write(byte[] bytes,int timeout)throws IOException{
   int op=bytes[9]&255;byte[] nonce=V2Protocol.part(bytes,10,16),args=V2Protocol.part(bytes,26,bytes.length-30);CRC32 crc=new CRC32();crc.update(bytes,4,bytes.length-8);check(bytes[4]==7&&(int)crc.getValue()==V2Protocol.u32(bytes,bytes.length-4),"USB CRC and kind");byte error=0;byte[] result=new byte[0];
   try{
    if(op==4)result=V2Protocol.join(currentInfo(),data);
    else if(op==1)result=currentInfo();
    else if(op==2){if(bound&&args[0]==0)error=2;else{control=V2Protocol.part(args,1,32);data=V2Protocol.part(args,33,32);bound=true;writes++;result=V2Protocol.join(currentInfo(),V2Protocol.mac(control,V2Protocol.join(V2Protocol.domain("AtomClip/v2/usb-bind"),currentInfo(),nonce)));if(loseAck){loseAck=false;throw new IOException("power-loss after NVS commit");}}}
    else if(op==5){if(!bound)error=3;else result=V2Protocol.join(currentInfo(),V2Protocol.mac(control,V2Protocol.join(V2Protocol.domain("AtomClip/v2/usb-proof"),currentInfo(),nonce)));}
    else if(op==3){bound=false;control=new byte[32];data=V2Protocol.random(32);result=currentInfo();}
    byte[] response=V2Protocol.usbFrame(V2Protocol.join(new byte[]{(byte)(op|0x80)},nonce,new byte[]{error},result));for(byte b:response)incoming.add(b);
   }catch(GeneralSecurityException e){throw new IOException(e);}
  }
  public int read(byte[] out,int timeout){int n=Math.min(Math.min(3,incoming.size()),out.length);for(int i=0;i<n;i++)out[i]=incoming.removeFirst();return n;}
  public void close(){}
 }
 public static void main(String[] args)throws Exception{
  Properties p=new Properties();try(InputStream in=Files.newInputStream(Paths.get(args[0]))){p.load(in);}byte[] raw=new byte[40];String hex=p.getProperty("info");for(int i=0;i<raw.length;i++)raw[i]=(byte)Integer.parseInt(hex.substring(i*2,i*2+2),16);
  Board device=new Board(raw,V2Protocol.random(32));UsbBoardClient usb=new UsbBoardClient(device);UsbBoardClient.Material material=usb.read();check(!material.info.configured,"unclaimed board stays USB-only");
  V2Protocol.Binding candidate=new V2Protocol.Binding(material.info,V2Protocol.random(32),material.dataKey,"USB test");V2Registry registry=new V2Registry();V2Protocol.Binding staged=registry.stage(candidate);byte[] durable=registry.encode();check(registry.active()==null&&registry.pending.size()==1,"pending not selectable before commit");
  device.loseAck=true;reject(()->usb.provision(candidate,false),"commit acknowledgement loss");check(device.bound&&device.writes==1,"device committed once");V2Registry restored=V2Registry.decode(durable);V2Protocol.Binding recovery=restored.pending.get(candidate.info.number());recovery.info=usb.confirm(recovery);restored.confirm(recovery.info.number());check(restored.active()!=null&&V2Protocol.equal(restored.active().controlKey,device.control),"pending key survives power-loss and USB recovery");
  V2Protocol.Binding old=restored.active();byte[] savedData=device.data.clone(),savedControl=device.control.clone();usb.confirm(old);check(device.writes==1&&V2Protocol.equal(savedControl,device.control),"reusing a confirmed USB binding verifies without reprovisioning");check(V2Protocol.equal(savedData,material.dataKey),"first binding preserved board data pairing");
  V2Protocol.Binding replacement=new V2Protocol.Binding(old.info,V2Protocol.random(32),old.dataKey,"new control");restored.stage(replacement);check(restored.active()==null&&V2Protocol.equal(restored.confirmed.get(old.info.number()).controlKey,old.controlKey),"old confirmed key retained during replacement");
  reject(()->restored.stage(new V2Protocol.Binding(old.info,V2Protocol.random(32),old.dataKey,"overwrite pending")),"pending secret cannot be silently replaced");
  reject(()->usb.provision(replacement,false),"no JustWorks overwrite");check(device.writes==1,"nonexplicit claim cannot alter existing control key");
  replacement.info=usb.provision(replacement,true);check(device.writes==2&&V2Protocol.equal(device.control,replacement.controlKey),"explicit physical replacement");
  byte[] another=raw.clone();another[20]^=1;V2Protocol.Binding wrongTarget=new V2Protocol.Binding(new V2Protocol.Info(another),V2Protocol.random(32),old.dataKey,"wrong board");reject(()->usb.provision(wrongTarget,true),"swapped USB target");check(device.writes==2,"identity checked before changing wrong board");
  byte[] reset=usb.call(3,new byte[0]);check(!new V2Protocol.Info(reset).configured&&!device.bound,"factory reset invalidates control");reject(()->usb.confirm(replacement),"old binding invalid after reset");
  restored.forget(old.info.number());check(restored.active()==null&&restored.confirmed.isEmpty()&&restored.pending.isEmpty(),"forget clears only local records");
  System.out.println("USB provisioning/pending recovery tests passed: "+checks+" checks; simulated USB only.");
 }
}
