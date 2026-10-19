package dev.atomclip.mobile;

import java.io.*;
import java.util.*;

/** Validated, sector-separated firmware. Normal upgrades never touch NVS. */
final class FirmwareBundle {
 static final class Segment {final int offset;final byte[] data;final String sha;Segment(int offset,byte[] data,String sha){this.offset=offset;this.data=data;this.sha=sha;}}
 final int profile,flashBytes;final String key,pairingHash;final Segment[] segments;
 FirmwareBundle(int profile,int flashBytes,String key,String pairingHash,Segment[] segments)throws IOException{
  this.profile=profile;this.flashBytes=flashBytes;this.key=key;this.pairingHash=pairingHash;this.segments=segments;
  if(!(profile==1&&flashBytes==8388608||profile==260&&flashBytes==4194304||profile==264&&flashBytes==8388608||profile==272&&flashBytes==16777216))throw new IOException("不支持的板型 / Flash 容量");
  if(segments==null||segments.length!=4)throw new IOException("固件段数量无效");int[] offsets={0,0x8000,0xe000,0x10000};
  for(int i=0;i<4;i++){Segment s=segments[i];long end=(long)s.offset+((s.data.length+4095L)&~4095L);if(s.offset!=offsets[i]||s.data.length==0||end>flashBytes||(i<3&&end>offsets[i+1])||s.offset<0xe000&&end>0x9000)throw new IOException("固件段越界或覆盖绑定区");if(!EspRom.digest("SHA-256",s.data).equalsIgnoreCase(s.sha))throw new IOException("内置固件段校验失败");}
  if(segments[3].data.length>(flashBytes==4194304?0x140000:flashBytes==8388608?0x330000:0x640000))throw new IOException("应用固件超过分区容量");
  if(segments[1].data.length!=3072||segments[2].data.length!=8192||segments[0].data.length<24||segments[3].data.length<24)throw new IOException("固件段长度无效");
  for(int i:new int[]{0,3}){byte[] b=segments[i].data;if((b[0]&255)!=0xe9||b[12]!=9||b[13]!=0)throw new IOException("内置固件不是 ESP32-S3");}
 }
 boolean compatible(byte[] table){return Arrays.equals(segments[1].data,table);}
 void checkTarget(int capacity,boolean octal,byte[] table,boolean factory)throws IOException{
  if(octal||capacity!=flashBytes)throw new IOException("芯片 Flash 类型 / 容量与所选固件不同，未写入");
  if(!compatible(table)&&!factory)throw new IOException("分区不兼容或首次安装。请先备份加密绑定配置，明确选择清空安装；随后可通过 USB 恢复绑定。");
 }
}
