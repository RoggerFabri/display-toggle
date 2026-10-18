namespace DisplayToggle;

/// <summary>
/// High-level control of the shared external monitor on a laptop (internal panel + external monitor).
/// "On" = extended onto the external monitor (grabbed); "Off" = internal-only (released → the Mac shows).
/// Toggling switches the display topology via the in-process CCD API (SetDisplayConfig),
/// the same extend/internal modes as Win+P, with DisplaySwitch.exe as a fallback.
/// </summary>
public sealed class DisplayManager
{
    private readonly Settings _settings;

    public DisplayManager(Settings settings) => _settings = settings;

    /// <summary>Last toggle intent: true = extended (external grabbed), false = internal-only (released).</summary>
    public bool On => _settings.On;

    public sealed record Entry(string DeviceName, string FriendlyName, bool IsOn, bool IsPrimary);

    /// <summary>Every detected monitor (shown as read-only status in the menu).</summary>
    public List<Entry> AllDisplays() =>
        NativeDisplay.Enumerate()
            .Select(d => new Entry(d.DeviceName, d.FriendlyName, d.IsAttached, d.IsPrimary))
            .ToList();

    /// <summary>
    /// Friendly names of the external monitor(s) to show as toggle rows — the Windows analogue of
    /// the Mac's per-external rows. Cached so the label persists while the external is released
    /// (it drops out of the active display list when off).
    /// </summary>
    public List<string> ExternalLabels() => ExternalLabels(DisplayTargets.All());

    /// <summary>The menu's per-external rows plus the aggregate state, from one CCD query.</summary>
    public (bool On, List<string> Labels) MenuState()
    {
        var targets = DisplayTargets.All();
        return (AggregateOn(targets), ExternalLabels(targets));
    }

    private List<string> ExternalLabels(List<DisplayTargets.Target> targets)
    {
        var live = targets.Where(t => !t.IsInternal).Select(t => t.Name).ToList();
        if (live.Count > 0)
        {
            if (!live.SequenceEqual(_settings.ExternalNames))
            {
                _settings.ExternalNames = live;
                _settings.Save();
            }
            return live;
        }
        return _settings.ExternalNames.Count > 0 ? _settings.ExternalNames : new List<string> { "External display" };
    }

    /// <summary>
    /// Live "on" state: true when any external (non-built-in) monitor is active. Prefers the CCD
    /// target list (robust to docks/phantom displays); falls back to attached-count when CCD is
    /// unavailable, and to the stored intent when nothing is attached.
    /// </summary>
    public bool AggregateOn() => AggregateOn(DisplayTargets.All());

    private bool AggregateOn(List<DisplayTargets.Target> targets)
    {
        if (targets.Count > 0) return targets.Any(t => !t.IsInternal);

        int attached = NativeDisplay.Enumerate().Count(d => d.IsAttached);
        return attached == 0 ? _settings.On : attached > 1;
    }

    /// <summary>True if we can tell there's no built-in panel (a non-laptop) — worth warning about.</summary>
    public bool ShouldWarnNoInternal()
    {
        var targets = DisplayTargets.All();
        return targets.Count > 0 && !targets.Any(t => t.IsInternal);
    }

    /// <summary>Flip between extended and internal-only. Returns the new state, or null on failure.</summary>
    public bool? ToggleAll()
    {
        bool target = !AggregateOn();
        return SetAll(target) ? target : (bool?)null;
    }

    /// <summary>
    /// Drive the topology: on → extend (grab), off → internal-only (release).
    /// Uses the in-process CCD API (SetDisplayConfig); falls back to DisplaySwitch.exe if it fails.
    /// The intent is persisted only when the switch worked, so a failure doesn't leave a wrong state.
    /// </summary>
    public bool SetAll(bool on)
    {
        bool ok = on ? DisplayConfig.Extend() : DisplayConfig.Internal();
        if (!ok && (on ? DisplaySwitch.Extend() : DisplaySwitch.Internal()))
            ok = WaitForState(on, TimeSpan.FromSeconds(3));
        if (ok)
        {
            _settings.On = on;
            _settings.Save();
        }
        return ok;
    }

    // DisplaySwitch.exe exits 0 whether or not the topology changed, often before it applies, so
    // its success is judged by re-reading the live state.
    private bool WaitForState(bool on, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            if (AggregateOn() == on) return true;
            if (DateTime.UtcNow >= deadline) return false;
            Thread.Sleep(250);
        }
    }
}
