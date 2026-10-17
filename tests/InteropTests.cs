using System.Diagnostics;
using System.Security.Cryptography;
using AtomClip;

// Public deterministic test key, independent of the user's private pairing key.
byte[] key = Enumerable.Range(0, 32).Select(n => (byte)n).ToArray();
string folder = Path.GetFullPath(args[0]);
Directory.CreateDirectory(folder);
string[] texts = { "", "ASCII 123", "中文双向：你好！", "第一行\r\n第二行 😀🚀\nThird line", new string('中', 5461) + "a" };
for (int i = 0; i < texts.Length; i++)
{
    byte[] payload = Protocol.Encrypt(texts[i], key);
    File.WriteAllText(Path.Combine(folder, i + ".txt"), texts[i], Protocol.Utf8);
    File.WriteAllBytes(Path.Combine(folder, i + ".data"), payload);
    File.WriteAllBytes(Path.Combine(folder, i + ".frame"), Protocol.Frame(Protocol.Data, payload));
    File.WriteAllBytes(Path.Combine(folder, i + ".ack"), Protocol.MakeAck(payload[..16], key));
}
var start = new ProcessStartInfo(args[1]) { UseShellExecute = false };
start.ArgumentList.Add(folder);
using var process = Process.Start(start)!;
await process.WaitForExitAsync();
if (process.ExitCode != 0) throw new Exception("Framework test failed: " + process.ExitCode);
for (int i = 0; i < texts.Length; i++)
{
    byte[] payload = File.ReadAllBytes(Path.Combine(folder, i + ".compact"));
    if (Protocol.Decrypt(payload, key).Text != texts[i]) throw new Exception("Compact -> original text mismatch");
    var packets = new PacketParser().Feed(File.ReadAllBytes(Path.Combine(folder, i + ".compact-frame")));
    if (packets.Count != 1 || !packets[0].Payload.SequenceEqual(payload)) throw new Exception("Compact -> original frame mismatch");
}
Console.WriteLine("PASS: actual .NET Framework 4.x compact EXE protocol <-> actual original .NET 8 protocol, 5 Unicode/size cases in both directions; ACK/CRC/tamper/limits/log checks passed.");

var javaStart = new ProcessStartInfo(args[2]) { UseShellExecute = false };
javaStart.ArgumentList.Add("-cp"); javaStart.ArgumentList.Add(args[3]);
javaStart.ArgumentList.Add("dev.atomclip.mobile.AndroidProtocolTests"); javaStart.ArgumentList.Add(folder);
using var java = Process.Start(javaStart)!; await java.WaitForExitAsync();
if (java.ExitCode != 0) throw new Exception("Android protocol tests failed");
byte[] opener = File.ReadAllBytes(Path.Combine(folder, "android-open.data"));
byte[] openPlain = new byte[opener.Length - 44];
using (var aes = new AesGcm(key, 16)) aes.Decrypt(opener.AsSpan(16,12), opener.AsSpan(28,openPlain.Length), opener.AsSpan(28+openPlain.Length,16), openPlain, opener.AsSpan(0,16));
if (!openPlain.SequenceEqual(new byte[]{2,0,4})) throw new Exception("Android fixed receiver opener mismatch");
string header = File.ReadAllText(args[4]);
string quoted = System.Text.RegularExpressions.Regex.Match(header, "BOOTSTRAP\\[\\] = (\".*\");").Groups[1].Value;
string bootstrap = System.Text.Json.JsonSerializer.Deserialize<string>(quoted)!;
if (bootstrap.Length >= 259) throw new Exception("Run dialog command too long");
for (int i = 0; i < texts.Length; i++)
{
    byte[] mobile = File.ReadAllBytes(Path.Combine(folder, i + ".android"));
    if (Protocol.Decrypt(mobile, key).Text != texts[i]) throw new Exception("Android -> original decrypt mismatch");
    foreach (string kind in new[] { "job", "android-job" })
    {
        byte[] b = File.ReadAllBytes(Path.Combine(folder, i + "." + kind));
        byte[] plain = new byte[b.Length - 44];
        using (var aes = new AesGcm(key, 16)) aes.Decrypt(b.AsSpan(16,12), b.AsSpan(28,plain.Length), b.AsSpan(28+plain.Length,16), plain, b.AsSpan(0,16));
        if (plain[0] != 1 || plain[1] != 0 || plain[2] != 4) throw new Exception("Job header mismatch");
        byte[] gzip = plain[3..];
        using (var zipped = new System.IO.Compression.GZipStream(new MemoryStream(gzip), System.IO.Compression.CompressionMode.Decompress))
        using (var reader = new StreamReader(zipped, Protocol.Utf8))
            if (reader.ReadToEnd() != texts[i]) throw new Exception("Job compression mismatch");
        // Exercise the exact firmware bootstrap decoder over redirected standard
        // input. Replace only its final clipboard command so this test never
        // touches the real clipboard. This does not test physical HID timing.
        string arguments = bootstrap.Substring(bootstrap.IndexOf(' ') + 1)
            .Replace("Set-Clipboard", "Write-Output")
            .Replace("$s='';", "[Console]::OutputEncoding=[Text.Encoding]::UTF8;$s='';");
        var psStart = new ProcessStartInfo("powershell.exe", arguments) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8 };
        using var ps = Process.Start(psStart)!;
        Task<string> output = ps.StandardOutput.ReadToEndAsync(), error = ps.StandardError.ReadToEndAsync();
        string base64 = Convert.ToBase64String(gzip);
        for (int p=0; p<base64.Length; p+=120) await ps.StandardInput.WriteLineAsync(base64.Substring(p,Math.Min(120,base64.Length-p)));
        await ps.StandardInput.WriteLineAsync(""); ps.StandardInput.Close();
        await ps.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        string actual = await output, errors = await error;
        if (ps.ExitCode != 0 || errors.Length != 0 || actual != texts[i] + Environment.NewLine)
            throw new Exception("PowerShell decoding mismatch: " + kind + ", case " + i + ", stderr=" + errors);
    }
}
Console.WriteLine("PASS: Android <-> original C# encryption; Windows/Android compressed typing jobs; exact PowerShell bootstrap decoder, 10 cases. Physical HID/phone runtime not covered.");
foreach (string platform in new[]{"windows","android"})
foreach (string kind in new[]{"direct","delay","fast","pinyin","pinyin-compatible","probe","gbk","gbk-compatible"})
{
    byte[] payload=File.ReadAllBytes(Path.Combine(folder,platform+"-"+kind+".data"));
    byte[] plain=new byte[payload.Length-44];
    using(var aes=new AesGcm(key,16))aes.Decrypt(payload.AsSpan(16,12),payload.AsSpan(28,plain.Length),payload.AsSpan(28+plain.Length,16),plain,payload.AsSpan(0,16));
    if(kind.StartsWith("gbk")) {
        bool compatible=kind.EndsWith("compatible");byte[] expected=Convert.FromHexString("B2A1414243313233A3ACD6D0CEC4210A4E657874096C696E65");
        if(plain[0]!=6||plain[1]!=(compatible?8:2)||plain[2]!=plain[1]||plain[3]!=(compatible?15:3)||plain[4]!=(compatible?30:6)||!plain[5..].SequenceEqual(expected))throw new Exception("GBK mixed ASCII/Chinese byte stream or timing mismatch");
    } else if(kind=="probe") {
        if(plain[0]!=3 || plain[1]!=0 || plain[2]!=35 || System.Text.Encoding.ASCII.GetString(plain[3..])!="vuc4e2d vuc6587 ")throw new Exception("Pinyin probe must be legacy raw HID with conservative pacing");
    } else if(kind.StartsWith("pinyin")) {
        bool compatible=kind.EndsWith("compatible");
        if(plain[0]!=5 || plain[1]!=(compatible?80:35) || plain[2]!=(compatible?4:1) || System.Text.Encoding.UTF8.GetString(plain[3..])!="中文Ab123，。\nNext\tline")throw new Exception("Pinyin pacing/text encoding mismatch");
    } else if(kind=="direct") {
        if(plain[0]!=3 || plain[1]!=0 || plain[2]!=4 || System.Text.Encoding.ASCII.GetString(plain[3..])!="ASCII 123!\nNext\tline")throw new Exception("Direct-key encoding mismatch");
    } else {
        if(plain[0]!=4 || plain[1]!=0 || plain[2]!=(kind=="fast"?1:4) || plain[3]!=(kind=="fast"?10:30))throw new Exception("Startup-delay/speed header mismatch");
        using(var gzip=new System.IO.Compression.GZipStream(new MemoryStream(plain[4..]),System.IO.Compression.CompressionMode.Decompress))
        using(var reader=new StreamReader(gzip,Protocol.Utf8))if(reader.ReadToEnd()!="中文启动等待 😀")throw new Exception("Delayed job text mismatch");
    }
}
Console.WriteLine("PASS: Windows/Android GBK mixed Chinese/English/digits/punctuation/newline/tab jobs and fast/compatible timing independently decoded to canonical GBK bytes; ASCII/VUC/legacy probe/startup compatibility and unsupported-character/size rejection passed.");
