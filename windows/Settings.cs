using System.Text.Json;
using System.Text.Json.Serialization;

namespace DisplayToggle;

/// <summary>
/// Persisted state, mirroring the Mac app's UserDefaults plist.
/// Stored at %APPDATA%\display-toggle\config.json.
///
/// - <see cref="On"/> is the last toggle *intent* (not a live hardware probe).
/// - Peer link config lives here too. The shared secret is stored in plain text.
/// </summary>
public sealed class Settings
{
    public bool On { get; set; } = true;
    public string PeerHost { get; set; } = "";
    public int PeerPort { get; set; } = 51763;

    /// <summary>Optional shared secret; when set on both machines the peer link is authenticated.</summary>
    public string PeerSecret { get; set; } = "";

    /// <summary>This install's stable discovery id (TXT <c>id</c>), minted once by <see cref="EnsureInstanceId"/>.</summary>
    public string InstanceId { get; set; } = "";

    /// <summary>TXT <c>id</c> of the peer chosen from discovery; empty when the peer was entered manually.</summary>
    public string PeerId { get; set; } = "";

    /// <summary>
    /// True when the user picked "Choose peer ▸ None": no listener, no advertising, no dialling, no
    /// auto-pick — the app runs on its own until a peer is chosen again.
    /// </summary>
    public bool PeerNone { get; set; }

    /// <summary>Last-seen external monitor names, so the toggle row keeps its label while released.</summary>
    public List<string> ExternalNames { get; set; } = new();

    /// <summary>Set once we've warned that there's no built-in panel (non-laptop), so we don't nag.</summary>
    public bool WarnedNoInternal { get; set; }

    /// <summary>The newest release we've shown an "update available" notification for (one per version).</summary>
    public string LastNotifiedVersion { get; set; } = "";

    [JsonIgnore]
    public bool PeerConfigured => !string.IsNullOrWhiteSpace(PeerHost);

    // ---- persistence --------------------------------------------------------

    private static readonly string DefaultPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "display-toggle", "config.json");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    private string _path = DefaultPath;

    /// <summary>
    /// True when the file exists but couldn't be read (locked, access denied). Such a Settings runs on
    /// defaults but never saves, so it can't overwrite the real config — InstanceId, secret, peer.
    /// </summary>
    [JsonIgnore]
    public bool ReadOnly { get; private set; }

    public static Settings Load() => Load(DefaultPath);

    internal static Settings Load(string path)
    {
        // Another process (the CLI, or `make install`'s tray) may be mid-replace: retry briefly.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return new Settings { _path = path };
                var s = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? throw new JsonException("empty config");
                s._path = path;
                return s;
            }
            catch (JsonException)
            {
                // Corrupt: keep a copy for recovery, then start fresh (saving over it is now safe).
                try { File.Copy(path, $"{path}.bad-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true); } catch { }
                return new Settings { _path = path };
            }
            catch (Exception) when (attempt < 4) { Thread.Sleep(100); }
            catch (Exception)
            {
                return new Settings { _path = path, ReadOnly = true };
            }
        }
    }

    /// <summary>Mint and persist <see cref="InstanceId"/> the first time it's missing.</summary>
    public void EnsureInstanceId()
    {
        if (!string.IsNullOrWhiteSpace(InstanceId)) return;
        InstanceId = Guid.NewGuid().ToString();
        Save();
    }

    // Saves come from the UI thread and the display-switch worker; they share one temp file.
    private static readonly object SaveGate = new();

    public void Save()
    {
        if (ReadOnly) return;
        lock (SaveGate)
        {
            try
            {
                // Write a temp file and move it over the config, so a crash mid-write (or a concurrent
                // reader) never sees a truncated file — the move replaces it in one step.
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                string tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, Opts));
                File.Move(tmp, _path, overwrite: true);
            }
            catch { /* best-effort */ }
        }
    }
}
