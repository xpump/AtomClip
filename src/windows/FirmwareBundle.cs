using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace AtomClipUsb
{
    public sealed class FirmwareSegment { public int offset, length; public string file, sha256; public FirmwareSegment() { } }
    public sealed class FirmwareBuild {
        public int schema, profile, flashBytes; public string key, chip, pairingMode, pairingHash, version; public FirmwareSegment[] segments; public FirmwareBuild() { }
        public void Validate(string folder) {
            if(schema!=2||chip!="esp32s3"||!(profile==1&&flashBytes==8388608||profile==260&&flashBytes==4194304||profile==264&&flashBytes==8388608||profile==272&&flashBytes==16777216)||segments==null||segments.Length!=4)throw new IOException("固件板型 / 容量不支持");
            int[] offsets={0,32768,57344,65536};for(int i=0;i<4;i++){var s=segments[i];long end=(long)s.offset+((s.length+4095L)&~4095L);if(s.offset!=offsets[i]||s.length<1||end>flashBytes||i<3&&end>offsets[i+1]||s.offset<57344&&end>36864||!Regex.IsMatch(s.file??"",@"^[a-z0-9-]+\.bin$"))throw new IOException("固件段越界或覆盖绑定区");byte[] bytes=File.ReadAllBytes(Path.Combine(folder,s.file));if(bytes.Length!=s.length||!Hash(bytes).Equals(s.sha256,StringComparison.OrdinalIgnoreCase))throw new IOException("固件段校验失败");if((i==0||i==3)&&(bytes.Length<24||bytes[0]!=233||bytes[12]!=9||bytes[13]!=0))throw new IOException("固件芯片不是 ESP32-S3");}
            if(segments[1].length!=3072||segments[2].length!=8192||segments[3].length>(flashBytes==4194304?0x140000:flashBytes==8388608?0x330000:0x640000))throw new IOException("固件分区长度无效");
        }
        public static string Hash(byte[] b){using(var h=SHA256.Create())return BitConverter.ToString(h.ComputeHash(b)).Replace("-","").ToLowerInvariant();}
        public bool Compatible(string folder,byte[] existing){return Hash(existing)==Hash(File.ReadAllBytes(Path.Combine(folder,segments[1].file)));}
    }
    public sealed class FirmwareBundle {
        public int schema;public FirmwareBuild[] builds;public FirmwareBundle() { }
        public static FirmwareBundle Load(string folder){var b=new JavaScriptSerializer().Deserialize<FirmwareBundle>(File.ReadAllText(Path.Combine(folder,"bundle.json")));if(b.schema!=2||b.builds==null||b.builds.Length!=4)throw new IOException("固件目录无效");var ids=new System.Collections.Generic.HashSet<int>();foreach(var p in b.builds){if(!ids.Add(p.profile))throw new IOException("重复板型");p.Validate(folder);}return b;}
        public FirmwareBuild Select(int profile){foreach(var b in builds)if(b.profile==profile)return b;throw new IOException("缺少所选板型固件");}
    }
}
