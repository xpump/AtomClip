package dev.atomclip.mobile;

import android.content.Context;
import android.security.keystore.*;
import android.util.AtomicFile;
import java.io.*;
import java.security.*;
import java.util.*;
import javax.crypto.*;
import javax.crypto.spec.GCMParameterSpec;

/** Android Keystore-backed encryption, atomic app-private file, backup disabled in manifest. */
final class DeviceBindings {
 static final String ALIAS="AtomClip.BindingStore.v2";
 final AtomicFile file;V2Registry registry=new V2Registry();String loadError;
 DeviceBindings(Context app){file=new AtomicFile(new File(app.getFilesDir(),"devices-v2.bin"));try{load();}catch(Exception ex){loadError="设备配置无法解密；请恢复已导出的加密配置。不会改连其他板。";}}
 SecretKey key()throws Exception{
  KeyStore ks=KeyStore.getInstance("AndroidKeyStore");ks.load(null);if(ks.containsAlias(ALIAS))return (SecretKey)ks.getKey(ALIAS,null);
  if(file.getBaseFile().exists())throw new GeneralSecurityException("系统密钥缺失");
  KeyGenerator g=KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES,"AndroidKeyStore");g.init(new KeyGenParameterSpec.Builder(ALIAS,KeyProperties.PURPOSE_ENCRYPT|KeyProperties.PURPOSE_DECRYPT).setKeySize(256).setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).setRandomizedEncryptionRequired(true).build());return g.generateKey();
 }
 void load()throws Exception{
  if(!file.getBaseFile().exists())return;byte[] b=file.readFully();if(b.length<33||b.length>32800||!Arrays.equals(V2Protocol.part(b,0,5),new byte[]{65,67,65,83,50}))throw new IOException("配置文件无效");
  Cipher c=Cipher.getInstance("AES/GCM/NoPadding");c.init(Cipher.DECRYPT_MODE,key(),new GCMParameterSpec(128,V2Protocol.part(b,5,12)));c.updateAAD(V2Protocol.part(b,0,17));byte[] plain=c.doFinal(V2Protocol.part(b,17,b.length-17));try{registry=V2Registry.decode(plain);}finally{Arrays.fill(plain,(byte)0);}
 }
 void save()throws Exception{
  if(loadError!=null)throw new IOException(loadError);byte[] plain=registry.encode();try{
   Cipher c=Cipher.getInstance("AES/GCM/NoPadding");c.init(Cipher.ENCRYPT_MODE,key());byte[] header=V2Protocol.join(new byte[]{65,67,65,83,50},c.getIV());if(header.length!=17)throw new GeneralSecurityException("系统 GCM nonce 长度异常");c.updateAAD(header);byte[] blob=V2Protocol.join(header,c.doFinal(plain));FileOutputStream out=null;
   try{out=file.startWrite();out.write(blob);file.finishWrite(out);}catch(Exception ex){if(out!=null)file.failWrite(out);throw ex;}
  }finally{Arrays.fill(plain,(byte)0);}
 }
 interface Change {void apply(V2Registry registry)throws Exception;}
 synchronized void change(Change action)throws Exception{if(loadError!=null)throw new IOException(loadError);byte[] before=registry.encode();try{action.apply(registry);save();}catch(Exception ex){registry=V2Registry.decode(before);throw ex;}finally{Arrays.fill(before,(byte)0);}}
 void resetUnreadable()throws Exception{
  if(loadError==null)throw new IllegalStateException();file.delete();KeyStore ks=KeyStore.getInstance("AndroidKeyStore");ks.load(null);if(ks.containsAlias(ALIAS))ks.deleteEntry(ALIAS);registry=new V2Registry();loadError=null;save();
 }
}
