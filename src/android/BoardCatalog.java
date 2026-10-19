package dev.atomclip.mobile;

import android.content.Context;
import org.json.*;
import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.*;

final class BoardCatalog {
 static final class Profile {
  final int id,flashBytes,buttonPin,ledPin;final String key,name,chip,backend,usbPort;
  Profile(JSONObject p)throws JSONException{id=p.getInt("id");flashBytes=p.getInt("flashBytes");buttonPin=p.getInt("buttonPin");ledPin=p.getInt("rgbLedPin");key=p.getString("key");name=p.getString("name");chip=p.getString("chip");backend=p.getString("backend");usbPort=p.getString("usbPort");}
 }
 final LinkedHashMap<Integer,Profile> profiles=new LinkedHashMap<>();
 BoardCatalog(Context context)throws Exception{
  ByteArrayOutputStream out=new ByteArrayOutputStream();try(InputStream in=context.getAssets().open("boards.json")){byte[] b=new byte[4096];int n;while((n=in.read(b))!=-1){if(out.size()+n>131072)throw new IOException("开发板配置过大");out.write(b,0,n);}}
  JSONObject root=new JSONObject(new String(out.toByteArray(),StandardCharsets.UTF_8));if(root.getInt("schema")!=1)throw new IOException("开发板配置版本不支持");JSONArray list=root.getJSONArray("profiles");for(int i=0;i<list.length();i++){Profile p=new Profile(list.getJSONObject(i));if(p.id<=0||p.flashBytes<=0||profiles.put(p.id,p)!=null)throw new IOException("开发板配置无效");}
 }
 String name(int id){Profile p=profiles.get(id);return p==null?"未知板型 #"+id:p.name;}
 String capabilities(int caps){return (caps&1)==0?"缺少 USB CDC 桥接能力":(caps&2)==0?"仅剪贴板桥接；不支持 HID 自动输入":"剪贴板桥接、USB 键盘";}
}
