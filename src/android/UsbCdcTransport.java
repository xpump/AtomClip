package dev.atomclip.mobile;

import android.hardware.usb.*;
import java.io.IOException;

/** Single CDC ACM function of an Espressif native USB device. */
final class UsbCdcTransport implements EspRom.Transport {
 final UsbManager manager;final UsbDevice device;final UsbDeviceConnection connection;
 final UsbInterface control,data;final UsbEndpoint in,out;volatile boolean closed;
 static boolean candidate(UsbDevice d){
  if(d.getVendorId()!=0x303a)return false;
  int controls=0,datas=0;for(int i=0;i<d.getInterfaceCount();i++){int c=d.getInterface(i).getInterfaceClass();if(c==2)controls++;if(c==10)datas++;}
  return controls==1&&datas==1;
 }
 UsbCdcTransport(UsbManager manager,UsbDevice device)throws IOException{
  this.manager=manager;this.device=device;
  if(!candidate(device))throw new IOException("请选择下载模式下的 Espressif 原生 USB CDC 设备");
  if(!manager.hasPermission(device))throw new IOException("USB 权限未授予，请重新授权");
  UsbInterface ctl=null,dat=null;UsbEndpoint rx=null,tx=null;
  for(int i=0;i<device.getInterfaceCount();i++){UsbInterface f=device.getInterface(i);if(f.getInterfaceClass()==2)ctl=f;
   if(f.getInterfaceClass()==10){dat=f;for(int j=0;j<f.getEndpointCount();j++){UsbEndpoint e=f.getEndpoint(j);if(e.getType()==UsbConstants.USB_ENDPOINT_XFER_BULK){if(e.getDirection()==UsbConstants.USB_DIR_IN)rx=e;else tx=e;}}}
  }
  if(ctl==null||dat==null||rx==null||tx==null)throw new IOException("USB CDC 端点不完整");
  control=ctl;data=dat;in=rx;out=tx;connection=manager.openDevice(device);if(connection==null)throw new IOException("无法打开 USB，请确认授权和数据连接");
  try{
   if(!connection.claimInterface(control,true)||!connection.claimInterface(data,true))throw new IOException("USB 正被其他程序使用");
   // 115200, 8 data bits, no parity, 1 stop bit. Native USB is not baud limited.
   byte[] line={(byte)0x00,(byte)0xc2,1,0,0,0,8};
   if(connection.controlTransfer(0x21,0x20,0,control.getId(),line,7,2000)!=7)throw new IOException("设置 USB CDC 线路失败");
   if(connection.controlTransfer(0x21,0x22,3,control.getId(),null,0,2000)<0)throw new IOException("设置 USB CDC 控制信号失败");
  }catch(IOException ex){close();throw ex;}
 }
 void alive()throws IOException{if(closed||!manager.getDeviceList().containsKey(device.getDeviceName()))throw new IOException("USB 已断开，请重新进入下载模式");}
 public void write(byte[] bytes,int timeout)throws IOException{
  int at=0;long deadline=System.nanoTime()/1000000+timeout;
  while(at<bytes.length){alive();int remaining=(int)(deadline-System.nanoTime()/1000000);if(remaining<=0)throw new IOException("USB 写入超时");int n=connection.bulkTransfer(out,bytes,at,bytes.length-at,Math.min(remaining,1000));if(n<=0)throw new IOException("USB 写入失败或设备已拔出");at+=n;}
 }
 public int read(byte[] bytes,int timeout)throws IOException{alive();int n=connection.bulkTransfer(in,bytes,bytes.length,timeout);if(n<0){alive();return 0;}return n;}
 public synchronized void close(){if(closed)return;closed=true;try{connection.releaseInterface(data);connection.releaseInterface(control);}finally{connection.close();}}
}
