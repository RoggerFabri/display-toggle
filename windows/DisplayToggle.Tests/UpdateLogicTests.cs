using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace DisplayToggle.Tests;

// Self-update's pure logic. The signature vector is shared with the Mac suite (release spec).
public class UpdateLogicTests
{
    // ---- shared test vector -------------------------------------------------

    private const string TestPublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE4BCU0nciMFrT1xf2Bvr7A0YsNFDqrUW0bxlnWkYIrYPS6mrw5s10jb9M2Q1zwykDT1dTdgie/skqbI6b01mdPQ==";
    private const string VectorManifest =
        """{"version":"1.2.3","assets":{"windows-x64":{"name":"display-toggle-windows-x64.zip","sha256":"0000000000000000000000000000000000000000000000000000000000000000","size":1}}}""";
    private const string VectorSignature =
        "MEUCIQCTIpk4IoOVhz1mcxTcXIg4G9wHzkivQxm/oxmYJFup0gIgJ2Jmq27uUHoOg3w3HW0+P1IxUjlLZiVwo1fTum552os=";

    private static byte[] VectorBytes => Encoding.UTF8.GetBytes(VectorManifest);
    private static byte[] VectorSig => Convert.FromBase64String(VectorSignature);

    [Fact]
    public void Vector_Verifies() => Assert.True(UpdateLogic.VerifySignature(VectorBytes, VectorSig, TestPublicKey));

    [Fact]
    public void Vector_FailsOnAnyChangedByte()
    {
        var bytes = VectorBytes;
        for (int i = 0; i < bytes.Length; i++)
        {
            var changed = (byte[])bytes.Clone();
            changed[i] ^= 0x01;
            Assert.False(UpdateLogic.VerifySignature(changed, VectorSig, TestPublicKey), $"byte {i}");
        }
        Assert.False(UpdateLogic.VerifySignature(Encoding.UTF8.GetBytes(VectorManifest + "\n"), VectorSig, TestPublicKey));
    }

    [Fact]
    public void Vector_FailsWithReleaseKey()
        => Assert.False(UpdateLogic.VerifySignature(VectorBytes, VectorSig, UpdateLogic.ReleasePublicKey));

    [Fact]
    public void Vector_ParsesAfterVerification()
    {
        var m = UpdateLogic.VerifyAndParse(VectorBytes, VectorSig, TestPublicKey);
        Assert.NotNull(m);
        Assert.Equal(new SemVer(1, 2, 3, null), m!.Version);
        Assert.Equal("v1.2.3", m.Tag);
        Assert.Equal(new UpdateAsset("display-toggle-windows-x64.zip", new string('0', 64), 1), m.Windows);
    }

    [Fact]
    public void VerifyAndParse_BadSignature_ReturnsNullWithoutParsing()
    {
        // Garbage that would throw if it were parsed: a bad signature must stop before that.
        Assert.Null(UpdateLogic.VerifyAndParse(Encoding.UTF8.GetBytes("not json"), VectorSig, TestPublicKey));
    }

    [Theory]
    [InlineData("")]
    [InlineData("AAAA")]
    [InlineData("not base64!")]
    public void MalformedSignatureOrKey_IsInvalid_NotAnException(string b64)
    {
        byte[] sig;
        try { sig = Convert.FromBase64String(b64); } catch (FormatException) { sig = new byte[] { 1, 2, 3 }; }
        Assert.False(UpdateLogic.VerifySignature(VectorBytes, sig, TestPublicKey));
        Assert.False(UpdateLogic.VerifySignature(VectorBytes, VectorSig, b64));
    }

    [Fact]
    public void P384Key_IsRejected()
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var sig = k.SignData(VectorBytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        Assert.False(UpdateLogic.VerifySignature(VectorBytes, sig, Convert.ToBase64String(k.ExportSubjectPublicKeyInfo())));
    }

    [Fact]
    public void ReleaseKey_IsAValidP256Key()
    {
        using var k = ECDsa.Create();
        k.ImportSubjectPublicKeyInfo(Convert.FromBase64String(UpdateLogic.ReleasePublicKey), out _);
        Assert.Equal(256, k.KeySize);
    }

    // ---- versions -----------------------------------------------------------

    [Theory]
    [InlineData("1.2.3", 1, 2, 3, null)]
    [InlineData("0.0.0-dev", 0, 0, 0, "dev")]
    [InlineData("10.20.30", 10, 20, 30, null)]
    [InlineData("1.0.0-rc.1", 1, 0, 0, "rc.1")]
    public void SemVer_Parses(string text, int major, int minor, int patch, string? suffix)
        => Assert.Equal(new SemVer(major, minor, patch, suffix), SemVer.TryParse(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("v1.2.3")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3+meta")]
    [InlineData("99999999999.0.0")]
    [InlineData("1.2.x")]
    public void SemVer_Rejects(string? text) => Assert.Null(SemVer.TryParse(text));

    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.1.0", "1.0.9", true)]
    [InlineData("2.0.0", "1.99.99", true)]
    [InlineData("1.10.0", "1.9.0", true)]     // numeric, not lexical
    [InlineData("1.0.0", "1.0.0", false)]
    [InlineData("1.0.0", "1.0.1", false)]
    [InlineData("0.9.9", "1.0.0", false)]
    [InlineData("1.0.0", "0.0.0-dev", true)]  // dev builds are older than everything
    [InlineData("0.0.1", "5.0.0-dev", true)]
    [InlineData("0.0.0", "0.0.0-dev", true)]
    [InlineData("2.0.0-rc.1", "1.0.0", false)] // never offer a dev build
    public void SemVer_IsNewer(string candidate, string current, bool newer)
        => Assert.Equal(newer, SemVer.IsNewer(SemVer.TryParse(candidate)!, SemVer.TryParse(current)!));

    [Theory]
    [InlineData("1.2.3", "1.2.3")]
    [InlineData("1.2.3+abcdef0", "1.2.3")]
    [InlineData("0.0.0-dev+abcdef0", "0.0.0-dev")]
    [InlineData(null, "0.0.0-dev")]
    [InlineData("garbage", "0.0.0-dev")]
    public void NormalizeVersion_StripsBuildMetadata(string? informational, string expected)
        => Assert.Equal(expected, UpdateLogic.NormalizeVersion(informational).ToString());

    [Fact]
    public void CurrentVersion_OfThisBuild_IsTheDevDefault()
    {
        // Tests build without -p:Version, so the csproj default applies.
        Assert.Equal("0.0.0-dev", UpdateLogic.CurrentVersion.ToString());
        Assert.True(UpdateLogic.CurrentVersion.IsDev);
    }

    // ---- manifest -----------------------------------------------------------

    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string Manifest(string version = "\"1.4.0\"", string? tag = "\"v1.4.0\"", string name = "\"display-toggle-windows-x64.zip\"",
                                   string sha = "\"" + Sha + "\"", string size = "1234", string extra = "")
        => "{\"version\":" + version + (tag is null ? "" : ",\"tag\":" + tag)
           + ",\"published\":\"2026-01-01T00:00:00Z\",\"notes_url\":\"https://example.invalid\""
           + ",\"assets\":{\"windows-x64\":{\"name\":" + name + ",\"sha256\":" + sha + ",\"size\":" + size + "}"
           + ",\"macos\":{\"name\":\"display-toggle-macos.tar.gz\",\"sha256\":\"" + Sha + "\",\"size\":5}}" + extra + "}";

    private static UpdateManifest Parse(string json) => UpdateLogic.ParseManifest(Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Manifest_FullExample_Parses()
    {
        var m = Parse(Manifest(extra: ",\"future\":{\"x\":[1,2]}"));   // unknown fields ignored
        Assert.Equal("1.4.0", m.Version.ToString());
        Assert.Equal("v1.4.0", m.Tag);
        Assert.Equal(new UpdateAsset("display-toggle-windows-x64.zip", Sha, 1234), m.Windows);
    }

    // Each app needs only its own asset: a release without a Windows build is "no update here", not an error.
    [Theory]
    [InlineData("{\"version\":\"1.0.0\",\"assets\":{}}")]
    [InlineData("{\"version\":\"1.0.0\",\"assets\":{\"macos\":{\"name\":\"a\",\"sha256\":\"" + Sha + "\",\"size\":1}}}")]
    public void Manifest_WithoutWindowsAsset_ParsesToNullAsset(string json)
    {
        var m = Parse(json);
        Assert.Equal("1.0.0", m.Version.ToString());
        Assert.Null(m.Windows);
    }

    [Fact]
    public void Manifest_WithoutTag_DerivesIt() => Assert.Equal("v1.4.0", Parse(Manifest(tag: null)).Tag);

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"version\":\"1.0.0\"}")]
    [InlineData("{\"version\":\"1.0.0\",\"assets\":[]}")]
    [InlineData("{\"version\":\"1.0.0\",\"assets\":{\"windows-x64\":\"a.zip\"}}")]
    public void Manifest_Malformed_Throws(string json) => Assert.Throws<FormatException>(() => Parse(json));

    [Theory]
    [InlineData("\"1.4\"")]
    [InlineData("\"v1.4.0\"")]
    [InlineData("\"1.4.0-dev\"")]
    [InlineData("140")]
    [InlineData("null")]
    public void Manifest_BadVersion_Throws(string version) => Assert.Throws<FormatException>(() => Parse(Manifest(version: version, tag: null)));

    [Theory]
    [InlineData("\"v1.4.1\"")]
    [InlineData("\"1.4.0\"")]
    [InlineData("1")]
    public void Manifest_TagMismatch_Throws(string tag) => Assert.Throws<FormatException>(() => Parse(Manifest(tag: tag)));

    [Theory]
    [InlineData("\"../evil.zip\"")]
    [InlineData("\"..\\\\evil.zip\"")]
    [InlineData("\"dir/evil.zip\"")]
    [InlineData("\"C:evil.zip\"")]
    [InlineData("\"..\"")]
    [InlineData("\"\"")]
    [InlineData("\" evil.zip\"")]
    [InlineData("\"evil\\u0000.zip\"")]
    [InlineData("5")]
    public void Manifest_BadAssetName_Throws(string name) => Assert.Throws<FormatException>(() => Parse(Manifest(name: name)));

    [Theory]
    [InlineData("\"0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef\"")] // uppercase
    [InlineData("\"0123456789abcdef\"")]                                                     // short
    [InlineData("\"0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef00\"")] // long
    [InlineData("\"g123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef\"")]
    [InlineData("null")]
    public void Manifest_BadSha_Throws(string sha) => Assert.Throws<FormatException>(() => Parse(Manifest(sha: sha)));

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("\"12\"")]
    [InlineData("104857601")]                 // > 100 MB
    [InlineData("99999999999999999999999")]
    public void Manifest_BadSize_Throws(string size) => Assert.Throws<FormatException>(() => Parse(Manifest(size: size)));

    [Fact]
    public void Manifest_MaxSize_Parses() => Assert.Equal(UpdateLogic.MaxAssetBytes, Parse(Manifest(size: "104857600")).Windows!.Size);

    // ---- asset --------------------------------------------------------------

    [Fact]
    public void AssetMatches_ChecksSizeAndHash()
    {
        var content = Encoding.ASCII.GetBytes("hello update");
        var asset = new UpdateAsset("a.zip", Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), content.Length);
        Assert.True(UpdateLogic.AssetMatches(asset, content));
        Assert.False(UpdateLogic.AssetMatches(asset with { Size = content.Length + 1 }, content));
        var changed = (byte[])content.Clone();
        changed[0] ^= 1;
        Assert.False(UpdateLogic.AssetMatches(asset, changed));
    }

    // ---- install helpers ----------------------------------------------------

    [Theory]
    [InlineData(@"C:\Users\x\AppData\Local\display-toggle\display-toggle.exe", true)]
    [InlineData(@"c:\users\X\appdata\local\DISPLAY-TOGGLE\Display-Toggle.EXE", true)]
    [InlineData(@"C:\Users\x\AppData\Local\display-toggle\..\display-toggle\display-toggle.exe", true)]
    [InlineData(@"C:\source\display-toggle\windows\bin\Release\net8.0-windows\display-toggle.exe", false)]
    [InlineData(@"C:\Users\x\AppData\Local\display-toggle\sub\display-toggle.exe", false)]
    [InlineData(@"C:\Users\x\AppData\Local\display-toggle\display-toggle.dll", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsInstalledCopy(string? path, bool expected)
        => Assert.Equal(expected, UpdateLogic.IsInstalledCopy(path, @"C:\Users\x\AppData\Local"));

    [Theory]
    [InlineData("plain", "'plain'")]
    [InlineData("it's", "'it''s'")]
    [InlineData("a\u2019b", "'a\u2019\u2019b'")]
    [InlineData("$(evil) `x` \"q\"", "'$(evil) `x` \"q\"'")]
    public void PsQuote_EscapesQuotesOnly(string input, string expected) => Assert.Equal(expected, UpdateLogic.PsQuote(input));

    [Fact]
    public void HelperScript_EmbedsQuotedArguments()
    {
        string s = UpdateLogic.HelperScript(1234, @"C:\T\it's\files", @"C:\Users\O'Neil\AppData\Local\display-toggle", @"C:\T\it's", @"C:\T\log.txt");
        Assert.Contains("$waitPid = 1234\n", s.Replace("\r", ""));
        Assert.Contains(@"$staged  = 'C:\T\it''s\files'", s);
        Assert.Contains(@"$install = 'C:\Users\O''Neil\AppData\Local\display-toggle'", s);
        Assert.Contains(@"$staging = 'C:\T\it''s'", s);
        Assert.Contains("WaitForExit(60000)", s);
        Assert.Contains("Start-Process -FilePath $exe", s);
        Assert.DoesNotContain("{{", s);
    }

    [Fact]
    public void HelperScript_KeepsBackupWhenRestoreFails()
    {
        // The backup lives in the staging dir; if restoring it failed it's the only intact copy.
        var lines = UpdateLogic.HelperScript(1, @"C:\a\files", @"C:\i", @"C:\a", @"C:\a\log")
            .Replace("\r", "").Split('\n').Select(l => l.Trim()).ToList();
        int failed = lines.FindIndex(l => l.Contains("RESTORE FAILED"));
        Assert.True(failed > 0);
        Assert.Contains("$backup", lines[failed]);   // the log names where the backup is

        // The else-branch that logs RESTORE FAILED sets the flag and doesn't clean up itself...
        int start = lines.FindLastIndex(failed, l => l == "} else {");
        int end = lines.FindIndex(failed, l => l == "}");
        var branch = lines.GetRange(start, end - start + 1);
        Assert.Contains("$keepBackup = $true", branch);
        Assert.DoesNotContain(branch, l => l.Contains("Cleanup"));

        // ...and from there on, the only Cleanup is the one the flag skips.
        var after = lines.Skip(end).Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"\bCleanup\b")).ToList();
        Assert.Equal(new[] { "if ($keepBackup) { Log \"not cleaning up; backup kept in $backup\" } else { Cleanup }" }, after);
    }

    [Fact]
    public void HelperScript_ParsesAsPowerShell()
    {
        // Syntax-check the generated script with the in-box Windows PowerShell parser (no execution).
        string ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        if (!File.Exists(ps)) return;
        string dir = Path.Combine(Path.GetTempPath(), "dt-helper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string script = Path.Combine(dir, "update.ps1");
            File.WriteAllText(script, UpdateLogic.HelperScript(1, @"C:\a b\files", @"C:\it's", @"C:\a b", @"C:\a b\log"), new UTF8Encoding(true));
            string check = Path.Combine(dir, "check.ps1");
            File.WriteAllText(check,
                "$e = $null; $null = [System.Management.Automation.Language.Parser]::ParseFile($args[0], [ref]$null, [ref]$e); " +
                "if ($e.Count) { $e | ForEach-Object { Write-Output $_.Message }; exit 1 } else { exit 0 }");
            var psi = new System.Diagnostics.ProcessStartInfo(ps) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", check, script }) psi.ArgumentList.Add(a);
            using var p = System.Diagnostics.Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            Assert.True(p.WaitForExit(60_000));
            Assert.True(p.ExitCode == 0, output);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
