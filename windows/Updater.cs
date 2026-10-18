using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace DisplayToggle;

/// <summary>Outcome of one update check.</summary>
public sealed record UpdateCheckResult(UpdateCheckResult.Kind Outcome, UpdateManifest? Manifest = null, string? Error = null)
{
    public enum Kind { UpToDate, Available, Failed }
}

/// <summary>
/// The network and disk half of self-update (the pure half is <see cref="UpdateLogic"/>). Fetches
/// the signed manifest from the latest GitHub Release, and on request downloads, verifies and
/// extracts the Windows asset into a temp staging dir, then hands off to a detached PowerShell
/// helper that swaps the files in once the app has quit. Every method here blocks on I/O — call it
/// off the UI thread. Never throws from <see cref="CheckAsync"/>; <see cref="StageAsync"/> throws
/// <see cref="UpdateException"/> (or cancellation) and leaves nothing behind.
/// </summary>
public sealed class Updater : IDisposable
{
    public const string DefaultManifestUrl = "https://github.com/RoggerFabri/display-toggle/releases/latest/download/manifest.json";
    public const string DefaultDownloadBase = "https://github.com/RoggerFabri/display-toggle/releases/download/";

    /// <summary>Per request: until the headers arrive, and between reads of the body (a stalled transfer).</summary>
    internal TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    private readonly HttpClient _http;
    private readonly Uri _manifestUri;
    private readonly Uri _sigUri;
    private readonly Uri _downloadBase;
    private readonly string _publicKey;
    private readonly bool _requireHttps;

    public Updater() : this(new Uri(DefaultManifestUrl), new Uri(DefaultDownloadBase), UpdateLogic.ReleasePublicKey) { }

    /// <param name="requireHttps">Tests serve over plain http on loopback; production never turns this off.</param>
    internal Updater(Uri manifestUri, Uri downloadBase, string publicKey, bool requireHttps = true, HttpMessageHandler? handler = null)
    {
        _manifestUri = manifestUri;
        _sigUri = new Uri(manifestUri + ".sig");
        _downloadBase = downloadBase;
        _publicKey = publicKey;
        _requireHttps = requireHttps;
        // .NET never follows an https → http redirect; https → other https hosts (GitHub's CDN) is fine.
        _http = new HttpClient(handler ?? new SocketsHttpHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 10 })
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,   // our own per-phase timeouts below
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("display-toggle", UpdateLogic.CurrentVersion.ToString()));
    }

    public void Dispose() => _http.Dispose();

    // ---- check ----------------------------------------------------------------

    /// <summary>Fetch + verify + parse the manifest and compare it with <paramref name="current"/>.</summary>
    public async Task<UpdateCheckResult> CheckAsync(SemVer current, CancellationToken ct)
    {
        try
        {
            byte[] manifest = await GetBytesAsync(_manifestUri, UpdateLogic.MaxManifestAndSigBytes, ct).ConfigureAwait(false);
            byte[] sig = await GetBytesAsync(_sigUri, UpdateLogic.MaxManifestAndSigBytes - manifest.Length, ct).ConfigureAwait(false);
            if (!UpdateLogic.VerifySignature(manifest, sig, _publicKey))
                return new(UpdateCheckResult.Kind.Failed, Error: "the release manifest's signature didn't verify");
            var m = UpdateLogic.ParseManifest(manifest);
            return SemVer.IsNewer(m.Version, current)
                ? new(UpdateCheckResult.Kind.Available, m)
                : new(UpdateCheckResult.Kind.UpToDate, m);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception e)
        {
            return new(UpdateCheckResult.Kind.Failed, Error: Describe(e));
        }
    }

    // ---- stage ----------------------------------------------------------------

    /// <summary>
    /// Download the Windows asset of <paramref name="m"/> into a new staging dir under
    /// <paramref name="tempRoot"/>, check its size and SHA-256 against the signed manifest, and extract
    /// it to <c>&lt;staging&gt;\files</c>. Returns the staging dir. On any failure the staging dir is deleted.
    /// </summary>
    public async Task<string> StageAsync(UpdateManifest m, string tempRoot, CancellationToken ct)
    {
        string staging = Path.Combine(tempRoot, "display-toggle-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        try
        {
            string zip = Path.Combine(staging, m.Windows.Name);   // a plain file name (ParseManifest checked)
            var uri = new Uri(_downloadBase, Uri.EscapeDataString(m.Tag) + "/" + Uri.EscapeDataString(m.Windows.Name));
            await DownloadVerifiedAsync(uri, zip, m.Windows, ct).ConfigureAwait(false);

            string files = Path.Combine(staging, "files");
            try { ZipFile.ExtractToDirectory(zip, files); }   // rejects entries that escape the dir
            catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
            { throw new UpdateException($"couldn't unpack the update ({e.Message})", e); }
            if (!File.Exists(Path.Combine(files, UpdateLogic.ExeName)))
                throw new UpdateException($"the update has no {UpdateLogic.ExeName}");
            File.Delete(zip);
            return staging;
        }
        catch
        {
            TryDelete(staging);
            throw;
        }
    }

    private async Task DownloadVerifiedAsync(Uri uri, string path, UpdateAsset expected, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            cts.CancelAfter(Timeout);
            using var resp = await SendAsync(uri, cts.Token).ConfigureAwait(false);
            if (resp.Content.Headers.ContentLength is long len && len != expected.Size)
                throw new UpdateException($"the download is {len} bytes, expected {expected.Size}");

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long total = 0;
            await using (var body = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false))
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                var buf = new byte[81920];
                while (true)
                {
                    cts.CancelAfter(Timeout);   // a stall timeout, not a cap on the whole transfer
                    int n = await body.ReadAsync(buf, cts.Token).ConfigureAwait(false);
                    if (n == 0) break;
                    total += n;
                    if (total > expected.Size) throw new UpdateException($"the download is larger than the expected {expected.Size} bytes");
                    hash.AppendData(buf, 0, n);
                    await file.WriteAsync(buf.AsMemory(0, n), cts.Token).ConfigureAwait(false);
                }
            }
            if (!UpdateLogic.AssetMatches(expected, total, hash.GetHashAndReset()))
                throw new UpdateException("the download doesn't match the signed manifest (size or SHA-256)");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException("the download timed out");
        }
        catch (HttpRequestException e)
        {
            throw new UpdateException($"the download failed ({e.Message})", e);
        }
    }

    // ---- http -----------------------------------------------------------------

    private async Task<byte[]> GetBytesAsync(Uri uri, int max, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        try
        {
            using var resp = await SendAsync(uri, cts.Token).ConfigureAwait(false);
            if (resp.Content.Headers.ContentLength > max) throw new UpdateException("the release manifest is too large");
            await using var body = await resp.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            var ms = new MemoryStream();
            var buf = new byte[8192];
            int n;
            while ((n = await body.ReadAsync(buf, cts.Token).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + n > max) throw new UpdateException("the release manifest is too large");
                ms.Write(buf, 0, n);
            }
            return ms.ToArray();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UpdateException("the update server timed out");
        }
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken ct)
    {
        RequireHttps(uri);
        var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        try
        {
            RequireHttps(resp.RequestMessage?.RequestUri ?? uri);   // where the redirects ended up
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
                throw new UpdateException("no release found");
            if (!resp.IsSuccessStatusCode)
                throw new UpdateException($"the update server returned HTTP {(int)resp.StatusCode}");
            return resp;
        }
        catch
        {
            resp.Dispose();
            throw;
        }
    }

    private void RequireHttps(Uri uri)
    {
        if (_requireHttps && uri.Scheme != Uri.UriSchemeHttps)
            throw new UpdateException("refusing a non-HTTPS update URL");
    }

    private static string Describe(Exception e) => e switch
    {
        UpdateException u => u.Message,
        HttpRequestException h => $"network error ({h.Message})",
        FormatException f => $"the release manifest is invalid ({f.Message})",
        _ => e.Message,
    };

    // ---- hand-off ---------------------------------------------------------------

    /// <summary>
    /// Write the helper script into <paramref name="staging"/> and start it detached (hidden Windows
    /// PowerShell). It waits for <paramref name="pid"/> to exit, so the caller must quit next.
    /// </summary>
    public static void LaunchHelper(string staging, string installDir, int pid, string logPath)
    {
        string script = Path.Combine(staging, "update.ps1");
        File.WriteAllText(script,
            UpdateLogic.HelperScript(pid, Path.Combine(staging, "files"), installDir, staging, logPath),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        StartHelper(script);
    }

    internal static Process StartHelper(string script)
    {
        // Full path, not PATH lookup: the in-box Windows PowerShell 5.1.
        string ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
        var psi = new ProcessStartInfo(ps)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetTempPath(),   // not the install dir, which it overwrites
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script })
            psi.ArgumentList.Add(a);
        return Process.Start(psi) ?? throw new UpdateException("couldn't start the update helper");
    }

    internal static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { /* best-effort: it's in %TEMP% */ }
    }
}

/// <summary>An update step failed; the message is shown to the user.</summary>
public sealed class UpdateException : Exception
{
    public UpdateException(string message, Exception? inner = null) : base(message, inner) { }
}
