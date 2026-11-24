using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using static DisplayToggle.Ccd;

namespace DisplayToggle;

/// <summary>
/// The full active display configuration as it was just before a display was switched off, kept
/// per display key in %APPDATA%\display-toggle\released-layouts.json, so switching it back on can
/// restore it exactly — position, primary, mode — instead of guessing. Windows' own topology
/// database can't be relied on for that (SDC_TOPOLOGY_SUPPLIED fails with ERROR_GEN_FAILURE on real
/// hardware). Best-effort throughout: a missing or unreadable file just means "nothing saved".
/// </summary>
internal static class ReleasedLayouts
{
    /// <summary>
    /// One saved configuration: the CCD path and mode arrays as raw struct bytes (base64), and the
    /// keys of the other displays that were active with it.
    /// </summary>
    public sealed class Entry
    {
        public string Paths { get; set; } = "";
        public string Modes { get; set; } = "";
        public List<string> Others { get; set; } = [];
    }

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "display-toggle", "released-layouts.json");

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };

    // The tray's switch worker and a direct CLI switch in this process share one temp file.
    private static readonly object Gate = new();

    /// <summary>Save the configuration <paramref name="key"/> was active in, replacing any older one.</summary>
    public static void Remember(string key, DisplayPathPlan.Config config, IEnumerable<string> others)
    {
        lock (Gate)
        {
            var all = Load();
            all[key] = new Entry { Paths = Encode(config.Paths), Modes = Encode(config.Modes), Others = others.ToList() };
            Store(all);
        }
    }

    /// <summary>The configuration saved for <paramref name="key"/>; false if none, or it doesn't decode.</summary>
    public static bool TryGet(string key, out DisplayPathPlan.Config config, out List<string> others)
    {
        config = default;
        others = [];
        Entry? entry;
        lock (Gate)
        {
            if (!Load().TryGetValue(key, out entry)) return false;
        }
        if (Decode<DISPLAYCONFIG_PATH_INFO>(entry.Paths) is not { Length: > 0 } paths
            || Decode<DISPLAYCONFIG_MODE_INFO>(entry.Modes) is not { } modes)
            return false;
        config = new DisplayPathPlan.Config(paths, modes);
        others = entry.Others;
        return true;
    }

    /// <summary>Drop the configuration saved for <paramref name="key"/> (it has been restored).</summary>
    public static void Forget(string key)
    {
        lock (Gate)
        {
            var all = Load();
            if (all.Remove(key)) Store(all);
        }
    }

    /// <summary>Raw bytes of blittable CCD structs, base64 — the same bytes SetDisplayConfig takes.</summary>
    internal static string Encode<T>(T[] items) where T : unmanaged =>
        Convert.ToBase64String(MemoryMarshal.AsBytes(items.AsSpan()));

    /// <summary>The inverse of <see cref="Encode{T}"/>; null if it isn't base64 or not a whole number of structs.</summary>
    internal static T[]? Decode<T>(string base64) where T : unmanaged
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }
        if (bytes.Length % Unsafe.SizeOf<T>() != 0) return null;
        return MemoryMarshal.Cast<byte, T>(bytes).ToArray();
    }

    private static Dictionary<string, Entry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { /* unreadable or corrupt → nothing saved */ }
        return new();
    }

    private static void Store(Dictionary<string, Entry> all)
    {
        try
        {
            // Temp file + move, as Settings.Save: a crash mid-write never leaves a truncated file.
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(all, Opts));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* best-effort */ }
    }
}
