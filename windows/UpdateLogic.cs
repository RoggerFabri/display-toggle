using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DisplayToggle;

/// <summary>
/// A version as the release process uses it: <c>X.Y.Z</c>, or <c>X.Y.Z-suffix</c> for a dev build
/// (local builds report <c>0.0.0-dev</c>). Only releases are compared by number; any dev build is
/// older than every release, so a dev build always sees a published release as newer.
/// </summary>
public sealed record SemVer(int Major, int Minor, int Patch, string? Suffix)
{
    public bool IsDev => Suffix is not null;

    private static readonly Regex Pattern = new(
        @"^(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})\.(0|[1-9][0-9]{0,8})(?:-([0-9A-Za-z.-]+))?$",
        RegexOptions.CultureInvariant);

    public static SemVer? TryParse(string? text)
    {
        if (text is null) return null;
        var m = Pattern.Match(text);
        if (!m.Success) return null;
        int N(int g) => int.Parse(m.Groups[g].Value, CultureInfo.InvariantCulture);
        return new SemVer(N(1), N(2), N(3), m.Groups[4].Success ? m.Groups[4].Value : null);
    }

    /// <summary>True when <paramref name="candidate"/> is a release newer than <paramref name="current"/>.</summary>
    public static bool IsNewer(SemVer candidate, SemVer current)
    {
        if (candidate.IsDev) return false;     // never offer a dev build
        if (current.IsDev) return true;        // a dev build is older than every release
        return (candidate.Major, candidate.Minor, candidate.Patch).CompareTo((current.Major, current.Minor, current.Patch)) > 0;
    }

    public override string ToString() => IsDev ? $"{Major}.{Minor}.{Patch}-{Suffix}" : $"{Major}.{Minor}.{Patch}";
}

/// <summary>One platform's asset in the signed manifest.</summary>
public sealed record UpdateAsset(string Name, string Sha256, long Size);

/// <summary>
/// The fields of a verified <c>manifest.json</c> the Windows app needs (see the release spec).
/// <see cref="Windows"/> is null when the release has no Windows build: no update for this platform.
/// </summary>
public sealed record UpdateManifest(SemVer Version, string Tag, UpdateAsset? Windows);

/// <summary>
/// The pure part of self-update: version handling, signature check, manifest parse, asset check and
/// the helper script. No I/O here (the network and the file system live in <see cref="Updater"/>).
/// </summary>
public static class UpdateLogic
{
    /// <summary>Release signing key (SubjectPublicKeyInfo DER, base64). CI signs manifest.json with its private half.</summary>
    public const string ReleasePublicKey =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE5xd31x3RYUrMiD/ld6+vIkio1hh7R8fu6XABL+jGXPZE3wAiAF5U5ZcvOTAcozHlDUc6VFFaPt7e1uVFK9a0zg==";

    public const string WindowsAssetKey = "windows-x64";
    public const long MaxAssetBytes = 100L * 1024 * 1024;
    public const int MaxManifestAndSigBytes = 64 * 1024;
    public const string ExeName = "display-toggle.exe";

    // ---- versions -------------------------------------------------------------

    /// <summary>
    /// The version string from AssemblyInformationalVersion, minus any <c>+build</c> metadata (the SDK
    /// appends the commit). Unparseable or missing → <c>0.0.0-dev</c>.
    /// </summary>
    public static SemVer NormalizeVersion(string? informational)
    {
        string v = (informational ?? "").Trim();
        int plus = v.IndexOf('+');
        if (plus >= 0) v = v[..plus];
        return SemVer.TryParse(v) ?? new SemVer(0, 0, 0, "dev");
    }

    private static readonly Lazy<SemVer> Current = new(() => NormalizeVersion(
        typeof(UpdateLogic).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion));

    /// <summary>This build's version (<c>-p:Version=X.Y.Z</c>; the csproj default is <c>0.0.0-dev</c>).</summary>
    public static SemVer CurrentVersion => Current.Value;

    // ---- signature ------------------------------------------------------------

    /// <summary>
    /// ECDSA P-256 / SHA-256 over the exact manifest bytes, signature in DER (what <c>openssl dgst
    /// -sha256 -sign</c> writes). Any malformed key or signature is simply "not valid".
    /// </summary>
    public static bool VerifySignature(byte[] data, byte[] signatureDer, string publicKeyBase64)
    {
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            if (ecdsa.KeySize != 256) return false;
            return ecdsa.VerifyData(data, signatureDer, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    // ---- manifest -------------------------------------------------------------

    private static readonly Regex Sha256Hex = new("^[0-9a-f]{64}$", RegexOptions.CultureInvariant);

    /// <summary>
    /// Parse a manifest whose signature has ALREADY been verified. Strict about what we use: a release
    /// <c>version</c>, a <c>tag</c> (if present) equal to <c>v&lt;version&gt;</c>, an <c>assets</c> object, and
    /// — if the release has one — the Windows asset with a plain file name, 64 lowercase hex <c>sha256</c>
    /// and 0 &lt; <c>size</c> ≤ 100 MB (no Windows asset → <see cref="UpdateManifest.Windows"/> is null).
    /// Other platforms' assets and unknown fields are ignored. Throws <see cref="FormatException"/> on anything else.
    /// </summary>
    public static UpdateManifest ParseManifest(byte[] json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException e) { throw new FormatException("manifest is not valid JSON", e); }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("manifest is not an object");

            string versionText = RequiredString(root, "version");
            var version = SemVer.TryParse(versionText) ?? throw new FormatException($"bad version '{versionText}'");
            if (version.IsDev) throw new FormatException("manifest version is not a release");

            string tag = "v" + version;
            if (root.TryGetProperty("tag", out var tagEl))
            {
                if (tagEl.ValueKind != JsonValueKind.String || tagEl.GetString() != tag)
                    throw new FormatException("tag does not match version");
            }

            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Object)
                throw new FormatException("missing assets");
            if (!assets.TryGetProperty(WindowsAssetKey, out var win))
                return new UpdateManifest(version, tag, null);   // a release without a Windows build
            if (win.ValueKind != JsonValueKind.Object)
                throw new FormatException($"assets.{WindowsAssetKey} is not an object");

            string name = RequiredString(win, "name");
            if (!IsPlainFileName(name)) throw new FormatException($"bad asset name '{name}'");
            string sha = RequiredString(win, "sha256");
            if (!Sha256Hex.IsMatch(sha)) throw new FormatException("sha256 must be 64 lowercase hex digits");
            if (!win.TryGetProperty("size", out var sizeEl) || sizeEl.ValueKind != JsonValueKind.Number
                || !sizeEl.TryGetInt64(out long size))
                throw new FormatException("missing or non-integer size");
            if (size <= 0 || size > MaxAssetBytes) throw new FormatException($"asset size {size} out of range");

            return new UpdateManifest(version, tag, new UpdateAsset(name, sha, size));
        }
    }

    private static string RequiredString(JsonElement obj, string prop)
    {
        if (!obj.TryGetProperty(prop, out var el) || el.ValueKind != JsonValueKind.String)
            throw new FormatException($"missing or non-string '{prop}'");
        return el.GetString()!;
    }

    /// <summary>A bare file name: non-empty, no separators, no <c>..</c>, no drive or invalid characters.</summary>
    public static bool IsPlainFileName(string name)
        => name.Length is > 0 and <= 128
           && name != "." && name != ".."
           && name.IndexOfAny(new[] { '/', '\\', ':' }) < 0
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
           && name.Trim() == name;

    /// <summary>Verify signature, then parse. Returns null when the signature doesn't verify.</summary>
    public static UpdateManifest? VerifyAndParse(byte[] manifest, byte[] signature, string publicKeyBase64)
        => VerifySignature(manifest, signature, publicKeyBase64) ? ParseManifest(manifest) : null;

    // ---- asset ----------------------------------------------------------------

    /// <summary>Does a downloaded asset (its byte count and SHA-256) match the signed manifest entry?</summary>
    public static bool AssetMatches(UpdateAsset expected, long size, byte[] sha256)
        => size == expected.Size && Convert.ToHexString(sha256).ToLowerInvariant() == expected.Sha256;

    public static bool AssetMatches(UpdateAsset expected, byte[] content)
        => AssetMatches(expected, content.LongLength, SHA256.HashData(content));

    // ---- install --------------------------------------------------------------

    /// <summary>Where <c>make install</c> puts the app: %LOCALAPPDATA%\display-toggle.</summary>
    public static string InstallDir(string localAppData) => Path.Combine(localAppData, "display-toggle");

    /// <summary>Is <paramref name="processPath"/> the installed copy (so self-update may replace it)?</summary>
    public static bool IsInstalledCopy(string? processPath, string localAppData)
    {
        if (string.IsNullOrEmpty(processPath) || string.IsNullOrEmpty(localAppData)) return false;
        try
        {
            string expected = Path.GetFullPath(Path.Combine(InstallDir(localAppData), ExeName));
            return string.Equals(Path.GetFullPath(processPath), expected, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>A PowerShell single-quoted literal. PowerShell also treats the curly quotes as quote characters.</summary>
    public static string PsQuote(string s)
    {
        var sb = new StringBuilder("'");
        foreach (char c in s)
        {
            sb.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛') sb.Append(c);
        }
        return sb.Append('\'').ToString();
    }

    /// <summary>
    /// The detached helper that finishes an update after the app has quit: wait (≤ 60 s) for
    /// <paramref name="pid"/> to exit, back up the install dir, copy the staged files over it (restoring
    /// the backup if that fails), relaunch the app, delete the staging dir — unless that restore failed too,
    /// when the staging dir (and its backup) is kept and its path logged. Logs to <paramref name="logPath"/>.
    /// If the app never exits it changes nothing. Written as UTF-8 with BOM (Windows PowerShell 5.1).
    /// </summary>
    public static string HelperScript(int pid, string stagedFiles, string installDir, string stagingRoot, string logPath)
        => $$"""
        # display-toggle update helper (generated; runs detached after the app quits).
        $ErrorActionPreference = 'Stop'
        $waitPid = {{pid.ToString(CultureInfo.InvariantCulture)}}
        $staged  = {{PsQuote(stagedFiles)}}
        $install = {{PsQuote(installDir)}}
        $staging = {{PsQuote(stagingRoot)}}
        $log     = {{PsQuote(logPath)}}
        $backup  = Join-Path $staging 'backup'
        $exe     = Join-Path $install '{{ExeName}}'
        $keepBackup = $false

        function Log([string]$msg) {
            try { Add-Content -LiteralPath $log -Value ('[' + (Get-Date).ToString('s') + '] ' + $msg) } catch { }
        }
        function Cleanup { try { Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction Stop } catch { Log "cleanup failed: $_" } }
        # robocopy exit codes 0-7 are success (8+ = some file failed); /R /W retry files still locked.
        function Copy-Tree([string]$from, [string]$to, [string[]]$extra) {
            & robocopy $from $to /E /R:10 /W:1 /NFL /NDL /NJH /NJS /NP @extra | Out-Null
            return ($LASTEXITCODE -lt 8)
        }

        Log "update helper started; waiting for PID $waitPid"
        $p = Get-Process -Id $waitPid -ErrorAction SilentlyContinue
        if ($p -and $p.ProcessName -eq 'display-toggle' -and -not $p.WaitForExit(60000)) {
            Log 'the app did not exit within 60 s; update abandoned, nothing changed'
            Cleanup
            exit 1
        }

        if (-not (Test-Path -LiteralPath (Join-Path $staged '{{ExeName}}'))) {
            Log 'staged files are missing; update abandoned, nothing changed'
        } elseif (-not (Copy-Tree $install $backup @())) {
            Log 'could not back up the install; update abandoned, nothing changed'
        } elseif (Copy-Tree $staged $install @()) {
            Log 'update installed'
        } else {
            Log 'copying the new files failed; restoring the previous install'
            if (Copy-Tree $backup $install @('/MIR')) {
                Log 'previous install restored'
            } else {
                # Keep the staging dir: its backup is the only intact copy of the previous install.
                $keepBackup = $true
                Log "RESTORE FAILED; the previous install is kept in $backup (copy it back, or run make install)"
            }
        }

        try { Start-Process -FilePath $exe -WorkingDirectory $install; Log 'relaunched' } catch { Log "relaunch failed: $_" }
        if ($keepBackup) { Log "not cleaning up; backup kept in $backup" } else { Cleanup }
        """;
}
