using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace DisplayToggle.Tests;

// The updater's network + disk half against a real HTTP server on loopback (plain http there, so
// requireHttps is off except in the test that checks it), signed with a throwaway key.
public sealed class UpdaterTests : IDisposable
{
    private readonly HttpListener _listener;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, Func<HttpListenerResponse, Task>> _routes = new();
    private readonly string _base;
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "dt-updater-" + Guid.NewGuid().ToString("N"));
    private readonly CancellationTokenSource _stop = new();

    public UpdaterTests()
    {
        Directory.CreateDirectory(_temp);
        // HttpListener on a "localhost" prefix needs no URL ACL / elevation. Pick a free port.
        for (int attempt = 0; ; attempt++)
        {
            int port = Random.Shared.Next(20000, 60000);
            _base = $"http://localhost:{port}/";
            _listener = new HttpListener();   // a failed Start disposes the listener
            _listener.Prefixes.Add(_base);
            try { _listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 20) { }
        }
        _ = Task.Run(Serve);
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _listener.Stop(); _listener.Close(); } catch { }
        _key.Dispose();
        try { Directory.Delete(_temp, true); } catch { }
    }

    private async Task Serve()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); } catch { return; }
            _ = Task.Run(async () =>
            {
                var resp = ctx.Response;
                try
                {
                    if (_routes.TryGetValue(ctx.Request.Url!.AbsolutePath, out var handler)) await handler(resp);
                    else resp.StatusCode = 404;
                }
                catch { }
                try { resp.Close(); } catch { }
            });
        }
    }

    private void Serve(string path, byte[] body) => _routes[path] = async r =>
    {
        r.ContentLength64 = body.Length;
        await r.OutputStream.WriteAsync(body);
    };

    private void Redirect(string path, string to) => _routes[path] = r =>
    {
        r.StatusCode = 302;
        r.RedirectLocation = _base.TrimEnd('/') + to;
        return Task.CompletedTask;
    };

    private string PublicKey => Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo());

    private byte[] Sign(byte[] data) => _key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);

    private Updater NewUpdater(bool requireHttps = false) =>
        new(new Uri(_base + "latest/download/manifest.json"), new Uri(_base + "download/"), PublicKey, requireHttps)
        { Timeout = TimeSpan.FromSeconds(5) };

    private static byte[] MakeZip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var (name, content) in entries)
            {
                using var w = new StreamWriter(zip.CreateEntry(name).Open());
                w.Write(content);
            }
        return ms.ToArray();
    }

    private static string Hex(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>Publish a release: the asset at download/vX/name, a signed manifest at latest/download/.</summary>
    private byte[] Publish(string version, byte[] asset, string? sha = null, long? size = null)
    {
        const string name = "display-toggle-windows-x64.zip";
        var manifest = Encoding.UTF8.GetBytes(
            $$"""{"version":"{{version}}","tag":"v{{version}}","assets":{"windows-x64":{"name":"{{name}}","sha256":"{{sha ?? Hex(asset)}}","size":{{size ?? asset.Length}} """
            + "}}}");
        Serve("/latest/download/manifest.json", manifest);
        Serve("/latest/download/manifest.json.sig", Sign(manifest));
        Serve($"/download/v{version}/{name}", asset);
        return manifest;
    }

    private static readonly SemVer V100 = new(1, 0, 0, null);

    // ---- check ----------------------------------------------------------------

    [Fact]
    public async Task Check_FindsNewerRelease()
    {
        Publish("1.2.0", MakeZip(("display-toggle.exe", "new")));
        using var u = NewUpdater();
        var r = await u.CheckAsync(V100, CancellationToken.None);
        Assert.Equal(UpdateCheckResult.Kind.Available, r.Outcome);
        Assert.Equal("1.2.0", r.Manifest!.Version.ToString());
    }

    [Fact]
    public async Task Check_SameVersion_IsUpToDate_DevIsOlder()
    {
        Publish("1.0.0", MakeZip(("display-toggle.exe", "x")));
        using var u = NewUpdater();
        Assert.Equal(UpdateCheckResult.Kind.UpToDate, (await u.CheckAsync(V100, default)).Outcome);
        Assert.Equal(UpdateCheckResult.Kind.Available, (await u.CheckAsync(new SemVer(0, 0, 0, "dev"), default)).Outcome);
    }

    [Fact]
    public async Task Check_FollowsRedirects()
    {
        // Like GitHub: releases/latest/download/… redirects to the tagged release, then to a CDN.
        var manifest = Encoding.UTF8.GetBytes(
            """{"version":"2.0.0","assets":{"windows-x64":{"name":"a.zip","sha256":"0000000000000000000000000000000000000000000000000000000000000000","size":1}}}""");
        Serve("/cdn/m", manifest);
        Serve("/cdn/s", Sign(manifest));
        Redirect("/latest/download/manifest.json", "/cdn/m");
        Redirect("/latest/download/manifest.json.sig", "/cdn/s");
        using var u = NewUpdater();
        Assert.Equal(UpdateCheckResult.Kind.Available, (await u.CheckAsync(V100, default)).Outcome);
    }

    [Fact]
    public async Task Check_TamperedManifest_Fails()
    {
        var manifest = Publish("1.2.0", MakeZip(("display-toggle.exe", "x")));
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(manifest).Replace("1.2.0", "1.3.0"));
        Serve("/latest/download/manifest.json", tampered);
        using var u = NewUpdater();
        var r = await u.CheckAsync(V100, default);
        Assert.Equal(UpdateCheckResult.Kind.Failed, r.Outcome);
        Assert.Contains("signature", r.Error);
    }

    [Fact]
    public async Task Check_SignedByAnotherKey_Fails()
    {
        var manifest = Publish("1.2.0", MakeZip(("display-toggle.exe", "x")));
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Serve("/latest/download/manifest.json.sig", other.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        using var u = NewUpdater();
        Assert.Equal(UpdateCheckResult.Kind.Failed, (await u.CheckAsync(V100, default)).Outcome);
    }

    [Fact]
    public async Task Check_OversizedManifest_Fails()
    {
        Serve("/latest/download/manifest.json", new byte[UpdateLogic.MaxManifestAndSigBytes + 1]);
        Serve("/latest/download/manifest.json.sig", new byte[10]);
        using var u = NewUpdater();
        var r = await u.CheckAsync(V100, default);
        Assert.Equal(UpdateCheckResult.Kind.Failed, r.Outcome);
        Assert.Contains("too large", r.Error);
    }

    [Fact]
    public async Task Check_NoRelease_Fails()
    {
        using var u = NewUpdater();
        var r = await u.CheckAsync(V100, default);
        Assert.Equal(UpdateCheckResult.Kind.Failed, r.Outcome);
        Assert.Equal("no release found", r.Error);
    }

    [Fact]
    public async Task Check_RequiresHttps()
    {
        Publish("1.2.0", MakeZip(("display-toggle.exe", "x")));
        using var u = NewUpdater(requireHttps: true);
        var r = await u.CheckAsync(V100, default);
        Assert.Equal(UpdateCheckResult.Kind.Failed, r.Outcome);
        Assert.Contains("HTTPS", r.Error);
    }

    [Fact]
    public async Task Check_Stalled_TimesOut()
    {
        _routes["/latest/download/manifest.json"] = async r => await Task.Delay(TimeSpan.FromSeconds(10), _stop.Token);
        using var u = new Updater(new Uri(_base + "latest/download/manifest.json"), new Uri(_base + "download/"), PublicKey, requireHttps: false)
        { Timeout = TimeSpan.FromMilliseconds(500) };
        var r = await u.CheckAsync(V100, default);
        Assert.Equal(UpdateCheckResult.Kind.Failed, r.Outcome);
        Assert.Contains("timed out", r.Error);
    }

    // ---- stage ----------------------------------------------------------------

    private async Task<UpdateManifest> CheckedManifest(Updater u)
    {
        var r = await u.CheckAsync(V100, default);
        Assert.Equal(UpdateCheckResult.Kind.Available, r.Outcome);
        return r.Manifest!;
    }

    [Fact]
    public async Task Stage_DownloadsVerifiesAndExtracts()
    {
        Publish("1.2.0", MakeZip(("display-toggle.exe", "new exe"), ("display-toggle.dll", "new dll")));
        using var u = NewUpdater();
        string staging = await u.StageAsync(await CheckedManifest(u), _temp, default);

        Assert.StartsWith(_temp, staging);
        Assert.Equal("new exe", File.ReadAllText(Path.Combine(staging, "files", "display-toggle.exe")));
        Assert.Equal("new dll", File.ReadAllText(Path.Combine(staging, "files", "display-toggle.dll")));
        Assert.False(File.Exists(Path.Combine(staging, "display-toggle-windows-x64.zip")));
    }

    [Fact]
    public async Task Stage_HashMismatch_Throws_AndCleansUp()
    {
        Publish("1.2.0", MakeZip(("display-toggle.exe", "x")), sha: new string('a', 64));
        using var u = NewUpdater();
        var ex = await Assert.ThrowsAsync<UpdateException>(async () => await u.StageAsync(await CheckedManifest(u), _temp, default));
        Assert.Contains("SHA-256", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(_temp));
    }

    [Fact]
    public async Task Stage_SizeMismatch_Throws_AndCleansUp()
    {
        var zip = MakeZip(("display-toggle.exe", "x"));
        Publish("1.2.0", zip, size: zip.Length - 1);
        using var u = NewUpdater();
        await Assert.ThrowsAsync<UpdateException>(async () => await u.StageAsync(await CheckedManifest(u), _temp, default));
        Assert.Empty(Directory.GetFileSystemEntries(_temp));
    }

    [Fact]
    public async Task Stage_ZipWithoutExe_Throws()
    {
        Publish("1.2.0", MakeZip(("readme.txt", "x")));
        using var u = NewUpdater();
        var ex = await Assert.ThrowsAsync<UpdateException>(async () => await u.StageAsync(await CheckedManifest(u), _temp, default));
        Assert.Contains("display-toggle.exe", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(_temp));
    }

    [Fact]
    public async Task Stage_ZipSlip_Throws()
    {
        Publish("1.2.0", MakeZip(("display-toggle.exe", "x"), ("../escaped.txt", "evil")));
        using var u = NewUpdater();
        await Assert.ThrowsAsync<UpdateException>(async () => await u.StageAsync(await CheckedManifest(u), _temp, default));
        Assert.Empty(Directory.GetFileSystemEntries(_temp));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(_temp)!, "escaped.txt")));
    }

    [Fact]
    public async Task Stage_NotAZip_Throws()
    {
        Publish("1.2.0", Encoding.ASCII.GetBytes("this is not a zip file"));
        using var u = NewUpdater();
        await Assert.ThrowsAsync<UpdateException>(async () => await u.StageAsync(await CheckedManifest(u), _temp, default));
        Assert.Empty(Directory.GetFileSystemEntries(_temp));
    }

    [Fact]
    public async Task Stage_MissingAsset_Throws()
    {
        Publish("1.2.0", MakeZip(("display-toggle.exe", "x")));
        using var u = NewUpdater();
        var m = await CheckedManifest(u);
        _routes.TryRemove("/download/v1.2.0/display-toggle-windows-x64.zip", out _);
        await Assert.ThrowsAsync<UpdateException>(() => u.StageAsync(m, _temp, default));
        Assert.Empty(Directory.GetFileSystemEntries(_temp));
    }
}
