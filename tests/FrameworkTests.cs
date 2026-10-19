using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using AtomClipUsb;

class FrameworkTests
{
    static void Require(bool condition, string name) { if (!condition) throw new Exception(name); }
    static int Main(string[] args)
    {
        try
        {
            string folder = args[0]; byte[] key = Enumerable.Range(0, 32).Select(n => (byte)n).ToArray();
            for (int i = 0; i < 5; i++)
            {
                string text = File.ReadAllText(Path.Combine(folder, i + ".txt"), Protocol.Utf8);
                byte[] payload = File.ReadAllBytes(Path.Combine(folder, i + ".data"));
                Require(Protocol.Decrypt(payload, key) == text, "Original -> compact decrypt");
                byte[] originalAck = File.ReadAllBytes(Path.Combine(folder, i + ".ack"));
                Require(Protocol.MakeAck(Protocol.Slice(payload, 0, 16), key).SequenceEqual(originalAck), "ACK wire compatibility");
                Require(Protocol.ReadAck(originalAck, key) == Protocol.Id(payload), "Valid ACK");
                originalAck[31] ^= 1; Require(Protocol.ReadAck(originalAck, key) == null, "Forged ACK rejection");
                byte[] compact = Protocol.Encrypt(text, key);
                Require(Protocol.Decrypt(compact, key) == text, "Compact roundtrip");
                File.WriteAllBytes(Path.Combine(folder, i + ".compact"), compact);
                File.WriteAllBytes(Path.Combine(folder, i + ".compact-frame"), Protocol.Frame(Protocol.Data, compact));
                File.WriteAllBytes(Path.Combine(folder, i + ".job"), Protocol.CreateTypeJob(text, false, 4, key));
                byte[] frame = File.ReadAllBytes(Path.Combine(folder, i + ".frame"));
                foreach (int step in new[] { 1, 20, 180, 4096 })
                {
                    var parser = new PacketParser(); int found = 0;
                    for (int p = 0; p < frame.Length; p += step)
                        foreach (var packet in parser.Feed(Protocol.Slice(frame, p, Math.Min(step, frame.Length - p)), Math.Min(step, frame.Length - p)))
                        { Require(packet.Kind == Protocol.Data && packet.Payload.SequenceEqual(payload), "Fragment wire compatibility"); found++; }
                    Require(found == 1, "Exactly one packet");
                }
                frame[frame.Length - 1] ^= 1;
                Require(new PacketParser().Feed(frame, frame.Length).Count == 0, "Corrupt CRC rejection");
                payload[payload.Length - 1] ^= 1;
                try { Protocol.Decrypt(payload, key); throw new Exception("Modified tag accepted"); } catch (CryptographicException) { }
                Require(Protocol.Decrypt(File.ReadAllBytes(Path.Combine(folder, i + ".data")), key) == text, "CNG usable after authentication failure");
            }
            try { Protocol.Encrypt(new string('a', 16385), key); throw new Exception("Oversize accepted"); } catch (ArgumentException) { }
            try { Protocol.Encrypt("bad\0text", key); throw new Exception("NUL accepted"); } catch (ArgumentException) { }
            File.WriteAllBytes(Path.Combine(folder,"windows-direct.data"),Protocol.CreateDirectKeyJob("ASCII 123!\r\nNext\tline",key));
            File.WriteAllBytes(Path.Combine(folder,"windows-pinyin.data"),Protocol.CreatePinyinJob("中文Ab123，。\r\nNext\tline",key));
            foreach (var speed in new[]{new[]{7,120},new[]{1,20},new[]{50,200}})
                File.WriteAllBytes(Path.Combine(folder,"windows-pinyin-"+speed[0]+"-"+speed[1]+".data"),Protocol.CreatePinyinJob("中文Ab123，。\r\nNext\tline",speed[0],speed[1],key));
            foreach (var speed in new[]{new[]{0,80},new[]{51,80},new[]{4,19},new[]{4,201},new[]{-1,80},new[]{4,int.MaxValue}})
                try { Protocol.CreatePinyinJob("中文",speed[0],speed[1],key);throw new Exception("Invalid VUC speed accepted"); } catch (ArgumentOutOfRangeException) { }
            Require(Protocol.PinyinMilliseconds("中A文",4,80)==570&&Protocol.PinyinMilliseconds("A",4,80)==194&&Protocol.PinyinMilliseconds("中文",50,200)==1500,"VUC duration includes commit, key and Shift waits, including custom maximum");
            File.WriteAllBytes(Path.Combine(folder,"windows-gbk.data"),Protocol.CreateGbkJob("病ABC123，中文!\r\nNext\tline",false,key));
            File.WriteAllBytes(Path.Combine(folder,"windows-gbk-compatible.data"),Protocol.CreateGbkJob("病ABC123，中文!\r\nNext\tline",true,key));
            Require(BitConverter.ToString(Protocol.EncodeGbkText(Protocol.KeyboardProbeText))=="B2-A1-41-42-43-31-32-33-A3-AC-D6-D0-CE-C4-21","GBK canonical mixed probe bytes");
            Require(Protocol.EncodeGbkText(new string('a',16384)).Length==16384,"GBK maximum ASCII length without record expansion");
            foreach(string invalid in new[]{"😀","𠀀","\u2603","\u20ac","\ue000","bad\rtext",new string('中',5462)})
                try { Protocol.CreateGbkJob(invalid,false,key);throw new Exception("Unsupported GBK text accepted"); } catch(ArgumentException) {}
            File.WriteAllBytes(Path.Combine(folder,"windows-probe.data"),Protocol.CreatePinyinProbeJob(key));
            Require(Protocol.NormalizePinyinText(new string('中',5461)+"a").Length==5462,"Pinyin exact 16KiB limit");
            foreach(string invalid in new[]{"","😀","𠀀","bad\0text","bad\rtext","bad\u0085", "\uffff", "\ud800",new string('中',5462)})
                try { Protocol.CreatePinyinJob(invalid,key);throw new Exception("Unsupported pinyin text accepted"); } catch(ArgumentException) {}
            File.WriteAllBytes(Path.Combine(folder,"windows-delay.data"),Protocol.CreateTypeJob("中文启动等待 😀",false,4,30,key));
            File.WriteAllBytes(Path.Combine(folder,"windows-fast.data"),Protocol.CreateTypeJob("中文启动等待 😀",false,1,10,key));
            foreach(byte interval in new byte[]{0,51,255})
                try { Protocol.CreateTypeJob("test",false,interval,10,key);throw new Exception("Invalid interval accepted"); } catch(ArgumentException) {}
            foreach(string invalid in new[]{"中文","😀","bad\0text","bad\rtext",new string('a',16385)})
                try { Protocol.CreateDirectKeyJob(invalid,key);throw new Exception("Unsupported direct key text accepted"); } catch(ArgumentException) {}
            foreach(byte delay in new byte[]{1,2,61,255})
                try { Protocol.CreateTypeJob("test",false,4,delay,key);throw new Exception("Invalid startup delay accepted"); } catch(ArgumentException) {}
            byte[] vector = Encoding.ASCII.GetBytes("123456789");
            Require(Protocol.Crc(vector, 0, vector.Length) == 0xcbf43926, "CRC standard vector");
            string logs = Path.Combine(folder, "logs-" + Guid.NewGuid().ToString("N")); var logger = new FileLog(logs);
            logger.Write("接收成功：71 UTF-8 字节；已写入剪贴板。"); Require(logger.LastError == null, "Log persistence");
            string current = Directory.GetFiles(logs, "*.log").Single();
            Require(File.ReadAllText(current, Encoding.UTF8).Contains("接收成功"), "UTF8 log");
            File.WriteAllBytes(current, new byte[2 * 1024 * 1024]); logger.Write("rotation");
            Require(File.Exists(current + ".1"), "Log rotation");
            string old = Path.Combine(logs, "AtomClipUSB-old.log"); File.WriteAllText(old, "old");
            File.SetLastWriteTime(old, DateTime.Now.AddDays(-8)); logger.Cleanup(); Require(!File.Exists(old), "Log retention");
            string blocked = Path.Combine(folder, "blocked"); File.WriteAllText(blocked, "file");
            Require(new FileLog(blocked).LastError != null, "Log permission/path errors reported");
            Console.WriteLine("PASS: Framework runtime CNG/UTF8/framing/ACK/limits/rotating logs."); return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}
