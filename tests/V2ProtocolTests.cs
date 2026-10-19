using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using AtomClipUsb;

static class V2ProtocolTests
{
    static int checks; static readonly Dictionary<string, byte[]> vectors = new Dictionary<string, byte[]>();
    static byte[] V(string key) { return (byte[])vectors[key].Clone(); }
    static void Check(bool ok, string label) { checks++; if (!ok) throw new Exception(label); }
    static void Reject(Action action, string label) { try { action(); } catch (CryptographicException) { checks++; return; } catch (InvalidDataException) { checks++; return; } catch (IOException) { checks++; return; } catch (ArgumentException) { checks++; return; } throw new Exception("accepted: " + label); }
    static V2Protocol.Binding Binding() { return V2Protocol.Binding.Decode(V("binding")); }
    static V2Protocol.Session Session(bool client) { return new V2Protocol.Session(V("controlKey"), new V2Protocol.Info(V("info")), V("clientNonce"), V("serverNonce"), client); }
    static int Main(string[] args)
    {
        try
        {
            foreach (string line in File.ReadAllLines(args[0])) { if (line.Length == 0 || line[0] == '#') continue; int p = line.IndexOf('='); string hex = line.Substring(p + 1); byte[] b = new byte[hex.Length / 2]; for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16); vectors.Add(line.Substring(0, p), b); }
            Directory.CreateDirectory(args[1]); string password = "跨平台配置密码-12345";
            if (args.Length > 2 && args[2] == "check-java")
            {
                Check(V2Protocol.Equal(V2Protocol.ImportBinding(File.ReadAllBytes(Path.Combine(args[1], "java.acbind")), password).Encode(), V("binding")), "Java exported binding -> Framework");
                Check(V2Protocol.Equal(Session(false).Open(File.ReadAllBytes(Path.Combine(args[1], "java-session.bin"))), V("plain")), "Java encrypted session -> Framework CNG");
                Console.WriteLine("PASS: Java/Framework encrypted configuration and session interoperability."); return 0;
            }
            var c = new V2Protocol.Client(Binding(), V("clientNonce")); Check(V2Protocol.Equal(c.Hello(), V("hello")), "hello reference"); Check(!c.Ready, "pre-auth blocked"); Check(V2Protocol.Equal(c.Accept(V("serverHello")), V("clientProof")), "mutual HMAC reference"); c.Accept(V("finish")); Check(c.Ready, "finish proof"); Check(V2Protocol.Equal(c.Session.Seal(V("plain")), V("clientFrame")), "HKDF/AAD/nonce/GCM reference c2d"); Check(V2Protocol.Equal(c.Session.Open(V("serverFrame")), V("plain")), "reference d2c");
            foreach (bool auth in new[] { true, false }) foreach (int fragment in new[] { 1, 7, 20, 180 })
            {
                byte[] f = V(auth ? "serverHello" : "clientFrame"); var stream = new V2Protocol.Stream(auth); var frames = new List<byte[]>(); for (int p = 0; p < f.Length; p += fragment) frames.AddRange(stream.Feed(V2Protocol.Part(f, p, Math.Min(fragment, f.Length - p)), 100)); Check(frames.Count == 1 && V2Protocol.Equal(frames[0], f), "MTU23 / arbitrary boundaries");
            }
            byte[] maximum = new byte[V2Protocol.MaxInner]; for (int i = 0; i < maximum.Length; i++) maximum[i] = (byte)i; byte[] wire = Session(true).Seal(maximum); var bigStream = new V2Protocol.Stream(false); var big = new List<byte[]>(); for (int p = 0; p < wire.Length; p += 20) big.AddRange(bigStream.Feed(V2Protocol.Part(wire, p, Math.Min(20, wire.Length - p)), p)); Check(big.Count == 1 && V2Protocol.Equal(Session(false).Open(big[0]), maximum), "maximum inner frame MTU23");
            Check(new V2Protocol.Stream(true).Feed(V2Protocol.Join(V("serverHello"), V("finish")), 0).Count == 2, "coalesced controls");
            var timed = new V2Protocol.Stream(false); timed.Feed(new byte[] { 65 }, 0); Reject(() => timed.Feed(new byte[] { 67 }, 5001), "partial idle timeout");
            Reject(() => new V2Protocol.Stream(false).Feed(V2Protocol.Join(new byte[] { 65, 67, 83, 50 }, new byte[8], V2Protocol.Le32(uint.MaxValue)), 0), "bounded untrusted length");
            byte[] wrong = V("controlKey"); wrong[0] ^= 1; var wrongClient = new V2Protocol.Client(new V2Protocol.Binding(new V2Protocol.Info(V("info")), wrong, V("dataKey"), "wrong"), V("clientNonce")); wrongClient.Hello(); Reject(() => wrongClient.Accept(V("serverHello")), "wrong key"); Check(!wrongClient.Ready, "wrong key cannot enable transport");
            byte[] nonce = V("clientNonce"); nonce[0] ^= 1; var replay = new V2Protocol.Client(Binding(), nonce); replay.Hello(); Reject(() => replay.Accept(V("serverHello")), "challenge replay");
            Reject(() => { var r = new V2Protocol.Client(Binding(), V("clientNonce")); r.Hello(); r.Accept(V2Protocol.Control(2, V2Protocol.Join(V("info"), V("serverNonce"), V2Protocol.Proof(V("controlKey"), "client", new V2Protocol.Info(V("info")), V("clientNonce"), V("serverNonce"))))); }, "role reflection");
            byte[] corruptInfo = V("serverHello"); corruptInfo[15] ^= 1; Reject(() => { var r = new V2Protocol.Client(Binding(), V("clientNonce")); r.Hello(); r.Accept(corruptInfo); }, "forged advertisement/capabilities");
            var rx = Session(false); rx.Open(V("clientFrame")); Reject(() => rx.Open(V("clientFrame")), "old data replay"); Check(rx.Closed, "authentication error poisons session");
            var tx = Session(true); tx.Seal(V("plain")); byte[] skipped = tx.Seal(V("plain")); Reject(() => Session(false).Open(skipped), "sequence gap"); byte[] tag = V("clientFrame"); tag[tag.Length - 1] ^= 1; Reject(() => Session(false).Open(tag), "bad GCM tag"); Reject(() => Session(false).Open(V("serverFrame")), "direction reflection");
            byte[] nextNonce = V("serverNonce"); nextNonce[0] ^= 1; Reject(() => new V2Protocol.Session(V("controlKey"), new V2Protocol.Info(V("info")), V("clientNonce"), nextNonce, false).Open(V("clientFrame")), "old session");
            byte[] alternate = V("info"); alternate[22] ^= 1; var other = new V2Protocol.Info(alternate); Check(other.ShortId == Binding().DeviceInfo.ShortId, "short collision fixture"); var candidates = new List<V2Protocol.Candidate> { new V2Protocol.Candidate("AABBCCDDEE01", other, -10), new V2Protocol.Candidate("AABBCCDDEE02", Binding().DeviceInfo, -90) }; Check(V2Protocol.Selected(candidates, Binding().DeviceInfo.Id).Address.EndsWith("02"), "full identity rather than RSSI/name"); Check(V2Protocol.Selected(new[] { candidates[0] }, Binding().DeviceInfo.Id) == null, "no fallback for missing target"); candidates.Add(new V2Protocol.Candidate("AABBCCDDEE03", Binding().DeviceInfo, -1)); Reject(() => V2Protocol.Selected(candidates, Binding().DeviceInfo.Id), "duplicate full identity");
            Check(V2Protocol.Equal(V2Protocol.ImportBinding(V("exportFixture"), password).Encode(), V("binding")), "independent Unicode PBKDF2/GCM reference"); Reject(() => V2Protocol.ImportBinding(V("exportFixture"), "wrong-password-123"), "wrong password"); byte[] modified = V("exportFixture"); modified[modified.Length - 1] ^= 1; Reject(() => V2Protocol.ImportBinding(modified, password), "modified export");
            File.WriteAllBytes(Path.Combine(args[1], "csharp.acbind"), V2Protocol.ExportBinding(Binding(), password));
            var registry = new V2Registry(); var original = Binding(); registry.Confirmed[original.DeviceInfo.Number] = original; registry.Select(original.DeviceInfo.Number);
            var pending = registry.Stage(new V2Protocol.Binding(original.DeviceInfo, V2Protocol.Random(32), original.DataKey, "replacement"));
            Check(registry.Active == null && !original.Pending && registry.Confirmed.Count == 1, "pending update retains original key and blocks auto-connect");
            Reject(() => registry.Stage(new V2Protocol.Binding(original.DeviceInfo, V2Protocol.Random(32), original.DataKey, "overwrite pending")), "cannot silently replace pending key");
            byte[] registryBlob = registry.Encode(); var decoded = V2Registry.Decode(registryBlob); Check(decoded.Pending.Count == 1 && decoded.Confirmed.Count == 1 && decoded.Active == null, "pending and confirmed survive serialization");
            File.WriteAllBytes(Path.Combine(args[1], "csharp-registry.bin"), registryBlob);
            var protectedStore = new DeviceBindings(Path.Combine(args[1], "protected-store")); protectedStore.Registry = registry; protectedStore.Save();
            var reloaded = new DeviceBindings(Path.Combine(args[1], "protected-store")); Check(reloaded.LoadError == null && reloaded.Registry.Pending.Count == 1 && reloaded.Registry.Confirmed.Count == 1, "actual Windows DPAPI store roundtrip");
            string storePath = Path.Combine(args[1], "protected-store", "devices-v2.bin"); byte[] badStore = File.ReadAllBytes(storePath); badStore[badStore.Length / 2] ^= 1; File.WriteAllBytes(storePath, badStore); var failedStore = new DeviceBindings(Path.GetDirectoryName(storePath)); Check(failedStore.LoadError != null && failedStore.Registry.Active == null, "corrupt OS-protected store never auto-selects or silently resets");
            decoded.Confirm(original.DeviceInfo.Number); Check(V2Protocol.Equal(decoded.Active.ControlKey, pending.ControlKey), "USB-confirmed candidate becomes selected"); decoded.Forget(original.DeviceInfo.Number); Check(decoded.Active == null && decoded.Pending.Count == 0 && decoded.Confirmed.Count == 0, "forget local device");
            byte[] invalidName=Binding().Encode();invalidName[111]=0xc0;Reject(()=>V2Protocol.Binding.Decode(invalidName),"malformed UTF8 alias rejected");
            Reject(()=>new V2Protocol.Binding(Binding().DeviceInfo,V("controlKey"),V("dataKey"),"line\nbreak"),"control-character alias rejected");
            var bound=Binding();var near=new BoardPresence.Observation{Address="AABBCCDDEE02",ShortId=bound.DeviceInfo.ShortId,Info=bound.DeviceInfo,Rssi=-50};
            var merged=BoardPresence.Merge(new[]{bound},new[]{near});Check(merged.Count==1&&merged[0].Binding==bound&&merged[0].Nearby==near&&!merged[0].Conflict,"USB binding and full-ID BLE observation merge into one row");
            var collision=new BoardPresence.Observation{Address="AABBCCDDEE03",ShortId=bound.DeviceInfo.ShortId,Info=other,Rssi=-1};
            merged=BoardPresence.Merge(new[]{bound},new[]{near,collision});Check(merged.Count==2&&merged[0].Nearby==near&&merged[1].Binding==null,"same short ID and stronger signal do not claim another board");
            var unread=new BoardPresence.Observation{Address="AABBCCDDEE04",ShortId=bound.DeviceInfo.ShortId};
            merged=BoardPresence.Merge(new[]{bound},new[]{unread});Check(merged.Count==2&&merged[0].Nearby==null,"unreadable Info cannot merge by short ID");
            var repeat=new BoardPresence.Observation{Address="aabbccddee02",Info=bound.DeviceInfo,Rssi=-65};merged=BoardPresence.Merge(new[]{bound},new[]{near,repeat});Check(merged.Count==1&&merged[0].Nearby.Rssi==-65&&!merged[0].Conflict,"repeat address deduplicated case-insensitively");
            var impostor=new BoardPresence.Observation{Address="AABBCCDDEE05",Info=bound.DeviceInfo};merged=BoardPresence.Merge(new[]{bound},new[]{near,impostor});Check(merged.Count==3&&merged.TrueForAll(row=>row.Conflict)&&merged[0].Nearby==null,"duplicate full ID across addresses stays explicit conflict");
            merged=BoardPresence.Merge(new V2Protocol.Binding[0],new[]{near,impostor});Check(merged.Count==2&&merged.TrueForAll(row=>row.Conflict),"unbound duplicate full IDs conflict too");
            var saved=new V2Registry();saved.Confirmed[bound.DeviceInfo.Number]=bound;saved.Select(bound.DeviceInfo.Number);byte[] beforeScan=saved.Encode();BoardPresence.Merge(saved.Confirmed.Values,new[]{near,collision});Check(V2Protocol.Equal(beforeScan,saved.Encode()),"discovery does not modify keys or current target");
            var staged=saved.Stage(new V2Protocol.Binding(bound.DeviceInfo,V2Protocol.Random(32),bound.DataKey,"pending"));merged=BoardPresence.Merge(new[]{staged},new[]{near});Check(merged.Count==1&&merged[0].Binding.Pending&&saved.Active==null,"pending merged row remains blocked from normal connection");
            byte[] oldRegistry=saved.Encode();oldRegistry[5]=1;int addressAt=7+oldRegistry[6];oldRegistry=V2Protocol.Join(V2Protocol.Part(oldRegistry,0,addressAt),new byte[]{12},System.Text.Encoding.ASCII.GetBytes("AABBCCDDEE01"),V2Protocol.Part(oldRegistry,addressAt+1,oldRegistry.Length-addressAt-1));
            var migrated=V2Registry.Decode(oldRegistry);Check(migrated.SelectedId==""&&migrated.Active==null&&migrated.Confirmed.Count==1&&migrated.Pending.Count==1,"old legacy target retired while V2 records survive");Check(migrated.Encode()[5]==0,"retired mode never written again");
            Console.WriteLine("V2 Framework identity/auth/session/config tests passed: " + checks + " checks; no devices or user keys used."); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
}
