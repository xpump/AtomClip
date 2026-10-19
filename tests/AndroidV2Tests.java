package dev.atomclip.mobile;

import java.nio.file.*;
import java.util.*;
import java.io.*;
import java.security.*;

public final class AndroidV2Tests {
 static int checks;static Properties vectors=new Properties();
 static byte[] v(String key){String s=vectors.getProperty(key);byte[] b=new byte[s.length()/2];for(int i=0;i<b.length;i++)b[i]=(byte)Integer.parseInt(s.substring(i*2,i*2+2),16);return b;}
 static void check(boolean good,String name){checks++;if(!good)throw new AssertionError(name);}
 interface Action {void run()throws Exception;}
 static void reject(Action a,String name)throws Exception{try{a.run();throw new AssertionError(name);}catch(GeneralSecurityException|IOException|IllegalArgumentException expected){checks++;}}
 static V2Protocol.Binding binding(){return V2Protocol.Binding.decode(v("binding"));}
 static V2Protocol.Session session(boolean client)throws Exception{return new V2Protocol.Session(v("controlKey"),new V2Protocol.Info(v("info")),v("clientNonce"),v("serverNonce"),client);}
 public static void main(String[] args)throws Exception{
  try(InputStream in=Files.newInputStream(Paths.get(args[0]))){vectors.load(in);}Path out=Paths.get(args[1]);Files.createDirectories(out);
  V2Protocol.Client c=new V2Protocol.Client(binding(),v("clientNonce"));check(Arrays.equals(c.hello(),v("hello")),"hello reference");check(!c.ready(),"not ready before proof");check(Arrays.equals(c.accept(v("serverHello")),v("clientProof")),"server proof / client proof reference");c.accept(v("finish"));check(c.ready(),"mutually authenticated finish");check(Arrays.equals(c.session.seal(v("plain")),v("clientFrame")),"HKDF nonce/AAD/GCM reference c2d");check(Arrays.equals(c.session.open(v("serverFrame")),v("plain")),"reference d2c");
  for(boolean auth:new boolean[]{true,false})for(int fragment:new int[]{1,7,20,180}){byte[] frame=v(auth?"serverHello":"clientFrame");V2Protocol.Stream stream=new V2Protocol.Stream(auth);ArrayList<byte[]> got=new ArrayList<>();for(int i=0;i<frame.length;i+=fragment)got.addAll(stream.feed(Arrays.copyOfRange(frame,i,Math.min(frame.length,i+fragment)),100));check(got.size()==1&&Arrays.equals(got.get(0),frame),"MTU23/arbitrary boundaries");}
  byte[] maximum=new byte[V2Protocol.MAX_INNER];for(int i=0;i<maximum.length;i++)maximum[i]=(byte)i;V2Protocol.Session tx=session(true),rx=session(false);byte[] big=tx.seal(maximum);V2Protocol.Stream split=new V2Protocol.Stream(false);ArrayList<byte[]> assembled=new ArrayList<>();for(int i=0;i<big.length;i+=20)assembled.addAll(split.feed(Arrays.copyOfRange(big,i,Math.min(big.length,i+20)),i));check(assembled.size()==1&&Arrays.equals(rx.open(assembled.get(0)),maximum),"maximum protected frame at MTU23");
  V2Protocol.Stream multi=new V2Protocol.Stream(true);check(multi.feed(V2Protocol.join(v("serverHello"),v("finish")),1).size()==2,"coalesced frames");
  V2Protocol.Stream timed=new V2Protocol.Stream(false);timed.feed(new byte[]{65},0);reject(()->timed.feed(new byte[]{67},5001),"fragment idle timeout");
  byte[] badLength=V2Protocol.join(new byte[]{65,67,83,50},new byte[8],V2Protocol.le32(Integer.MAX_VALUE));reject(()->new V2Protocol.Stream(false).feed(badLength,0),"bounded untrusted lengths");
  byte[] wrong=v("controlKey");wrong[0]^=1;V2Protocol.Binding wrongBinding=new V2Protocol.Binding(new V2Protocol.Info(v("info")),wrong,v("dataKey"),"wrong");V2Protocol.Client wrongClient=new V2Protocol.Client(wrongBinding,v("clientNonce"));wrongClient.hello();reject(()->wrongClient.accept(v("serverHello")),"wrong key");check(!wrongClient.ready(),"wrong key cannot enable sends");
  byte[] otherNonce=v("clientNonce");otherNonce[0]^=1;V2Protocol.Client replay=new V2Protocol.Client(binding(),otherNonce);replay.hello();reject(()->replay.accept(v("serverHello")),"old challenge replay");
  byte[] changedInfo=v("serverHello");changedInfo[7+8]^=1;reject(()->{V2Protocol.Client altered=new V2Protocol.Client(binding(),v("clientNonce"));altered.hello();altered.accept(changedInfo);},"forged capabilities/CRC");
  reject(()->{V2Protocol.Client reflection=new V2Protocol.Client(binding(),v("clientNonce"));reflection.hello();reflection.accept(V2Protocol.control(2,V2Protocol.join(v("info"),v("serverNonce"),V2Protocol.proof(v("controlKey"),"client",new V2Protocol.Info(v("info")),v("clientNonce"),v("serverNonce")))));},"role reflection");
  V2Protocol.Session replayData=session(false);replayData.open(v("clientFrame"));reject(()->replayData.open(v("clientFrame")),"old data replay");check(replayData.closed,"bad session terminal");
  V2Protocol.Session skipTx=session(true);skipTx.seal(v("plain"));byte[] skipped=skipTx.seal(v("plain"));reject(()->session(false).open(skipped),"sequence gap");
  byte[] tamper=v("clientFrame");tamper[tamper.length-1]^=1;reject(()->session(false).open(tamper),"tag tampering");reject(()->session(false).open(v("serverFrame")),"direction reflection");
  byte[] newNonce=v("serverNonce");newNonce[0]^=1;reject(()->new V2Protocol.Session(v("controlKey"),new V2Protocol.Info(v("info")),v("clientNonce"),newNonce,false).open(v("clientFrame")),"previous-session ciphertext");
  byte[] otherInfo=v("info");otherInfo[22]^=1;V2Protocol.Info other=new V2Protocol.Info(otherInfo);check(other.shortId().equals(binding().info.shortId()),"same short ID fixture");
  ArrayList<V2Protocol.Candidate> boards=new ArrayList<>();boards.add(new V2Protocol.Candidate("AA:BB:CC:DD:EE:01",other,-10));boards.add(new V2Protocol.Candidate("AA:BB:CC:DD:EE:02",binding().info,-90));check(V2Protocol.selected(boards,binding().info.id).address.endsWith("02"),"fullID wins over RSSI/same short/name");
  check(V2Protocol.selected(Collections.singletonList(boards.get(0)),binding().info.id)==null,"missing bound target does not switch");boards.add(new V2Protocol.Candidate("AABBCCDDEE03",binding().info,-1));reject(()->V2Protocol.selected(boards,binding().info.id),"duplicate complete IDs stop selection");
  String password="跨平台配置密码-12345";check(Arrays.equals(V2Protocol.importBinding(v("exportFixture"),password.toCharArray()).encode(),v("binding")),"independent Unicode password PBKDF2/GCM export reference");reject(()->V2Protocol.importBinding(v("exportFixture"),"wrong-password-123".toCharArray()),"wrong import password");
  byte[] alteredExport=v("exportFixture");alteredExport[alteredExport.length-1]^=1;reject(()->V2Protocol.importBinding(alteredExport,password.toCharArray()),"export tampering");
  Path csharp=out.resolve("csharp.acbind");if(Files.exists(csharp))check(Arrays.equals(V2Protocol.importBinding(Files.readAllBytes(csharp),password.toCharArray()).encode(),v("binding")),"C# exported binding -> Java");
  Path registryFile=out.resolve("csharp-registry.bin");if(Files.exists(registryFile)){V2Registry local=V2Registry.decode(Files.readAllBytes(registryFile));check(local.pending.size()==1&&local.confirmed.size()==1&&local.active()==null,"C# registry pending/confirmed format -> Java");check(Arrays.equals(local.confirmed.get(binding().info.number()).controlKey,binding().controlKey),"retained original key matches across registry formats");}
  Files.write(out.resolve("java.acbind"),V2Protocol.exportBinding(binding(),password.toCharArray()));Files.write(out.resolve("java-session.bin"),session(true).seal(v("plain")));
  byte[] invalidName=binding().encode();invalidName[111]=(byte)0xc0;reject(()->V2Protocol.Binding.decode(invalidName),"malformed UTF8 alias rejected");
  reject(()->new V2Protocol.Binding(binding().info,v("controlKey"),v("dataKey"),"line\nbreak"),"control-character alias rejected");
  V2Protocol.Binding wide=new V2Protocol.Binding(binding().info,v("controlKey"),v("dataKey"),"　中文　");check(wide.alias.equals("　中文　"),"Unicode spacing preserved consistently");
  V2Protocol.Binding bound=binding();BoardPresence.Observation near=new BoardPresence.Observation();near.address="AABBCCDDEE02";near.shortId=bound.info.shortId();near.info=bound.info;near.rssi=-50;
  List<BoardPresence.Row> merged=BoardPresence.merge(Collections.singletonList(bound),Arrays.asList(near));check(merged.size()==1&&merged.get(0).binding==bound&&merged.get(0).nearby==near&&!merged.get(0).conflict,"USB binding and BLE full ID merge once");
  BoardPresence.Observation collision=new BoardPresence.Observation();collision.address="AABBCCDDEE03";collision.shortId=bound.info.shortId();collision.info=other;collision.rssi=-1;
  merged=BoardPresence.merge(Collections.singletonList(bound),Arrays.asList(near,collision));check(merged.size()==2&&merged.get(0).nearby==near&&merged.get(1).binding==null,"same short and stronger signal cannot claim different full ID");
  BoardPresence.Observation unread=new BoardPresence.Observation();unread.address="AABBCCDDEE04";unread.shortId=bound.info.shortId();merged=BoardPresence.merge(Collections.singletonList(bound),Arrays.asList(unread));check(merged.size()==2&&merged.get(0).nearby==null,"unreadable Info remains separate");
  BoardPresence.Observation repeat=new BoardPresence.Observation();repeat.address="aabbccddee02";repeat.info=bound.info;repeat.rssi=-65;merged=BoardPresence.merge(Collections.singletonList(bound),Arrays.asList(near,repeat));check(merged.size()==1&&merged.get(0).nearby.rssi==-65&&!merged.get(0).conflict,"same address deduplicated case-insensitively");
  BoardPresence.Observation impostor=new BoardPresence.Observation();impostor.address="AABBCCDDEE05";impostor.info=bound.info;merged=BoardPresence.merge(Collections.singletonList(bound),Arrays.asList(near,impostor));check(merged.size()==3&&merged.stream().allMatch(row->row.conflict)&&merged.get(0).nearby==null,"duplicate full IDs stay separate conflict rows");
  merged=BoardPresence.merge(Collections.emptyList(),Arrays.asList(near,impostor));check(merged.size()==2&&merged.stream().allMatch(row->row.conflict),"unbound duplicate full IDs conflict too");
  V2Registry saved=new V2Registry();saved.confirmed.put(bound.info.number(),bound);saved.select(bound.info.number());byte[] beforeScan=saved.encode();BoardPresence.merge(saved.confirmed.values(),Arrays.asList(near,collision));check(Arrays.equals(beforeScan,saved.encode()),"scan never changes keys or target");
  V2Protocol.Binding staged=saved.stage(new V2Protocol.Binding(bound.info,V2Protocol.random(32),bound.dataKey,"pending"));merged=BoardPresence.merge(Arrays.asList(staged),Arrays.asList(near));check(merged.size()==1&&merged.get(0).binding.pending&&saved.active()==null,"merged pending remains unconnectable");
  byte[] oldRegistry=saved.encode();oldRegistry[5]=1;int addressAt=7+(oldRegistry[6]&255);oldRegistry=V2Protocol.join(V2Protocol.part(oldRegistry,0,addressAt),new byte[]{12},"AABBCCDDEE01".getBytes(java.nio.charset.StandardCharsets.US_ASCII),V2Protocol.part(oldRegistry,addressAt+1,oldRegistry.length-addressAt-1));
  V2Registry migrated=V2Registry.decode(oldRegistry);check(migrated.selectedId.isEmpty()&&migrated.active()==null&&migrated.confirmed.size()==1&&migrated.pending.size()==1,"old legacy target retired and V2 records kept");check(migrated.encode()[5]==0,"retired mode not written again");
  System.out.println("V2 Java identity/auth/session/config tests passed: "+checks+" checks; no devices or user keys used.");
 }
}
