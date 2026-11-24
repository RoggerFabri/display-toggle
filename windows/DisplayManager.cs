namespace DisplayToggle;

/// <summary>
/// High-level control of the shared external monitor on a laptop (internal panel + external monitors).
/// "On" = the shared monitor is driven by this PC (grabbed); "Off" = released (→ the Mac shows).
/// The shared monitor is the user's choice (<see cref="Settings.SharedDisplayKey"/>), else the only
/// connected external; it is switched alone with a CCD per-path edit, so other displays (a virtual
/// monitor, a second external) stay as they are. With several externals and no choice ("all mode"),
/// the whole topology switches, extend ⇄ internal like Win+P, with DisplaySwitch.exe as a fallback.
/// </summary>
public sealed class DisplayManager
{
    private readonly Settings _settings;

    public DisplayManager(Settings settings) => _settings = settings;

    /// <summary>Last toggle intent: true = grabbed, false = released.</summary>
    public bool On => _settings.On;

    public sealed record Entry(string DeviceName, string FriendlyName, bool IsOn, bool IsPrimary);

    /// <summary>A connected external monitor (or the configured shared one while disconnected) as a menu row.</summary>
    public sealed record ExternalRow(string Key, string Name, bool IsActive, bool IsShared);

    /// <summary>Every detected monitor (shown as read-only status in the menu).</summary>
    public List<Entry> AllDisplays() =>
        NativeDisplay.Enumerate()
            .Select(d => new Entry(d.DeviceName, d.FriendlyName, d.IsAttached, d.IsPrimary))
            .ToList();

    /// <summary>
    /// The menu's per-external rows plus the aggregate state, from one CCD query. A configured shared
    /// monitor that isn't connected still gets a (released) row under its saved name, so the user
    /// can see — and change — what's shared.
    /// </summary>
    public (bool On, List<ExternalRow> Rows, bool AllMode) MenuState()
    {
        var targets = DisplayTargets.All();
        string? shared = SharedKey(targets);
        var rows = targets.Where(t => !t.IsInternal)
            .Select(t => new ExternalRow(t.Key, t.Name, t.IsActive, t.Key == shared))
            .ToList();
        if (shared is not null && rows.All(r => r.Key != shared))
        {
            string name = string.IsNullOrWhiteSpace(_settings.SharedDisplayName) ? "External display" : _settings.SharedDisplayName;
            rows.Add(new ExternalRow(shared, name, false, true));
        }
        return (AggregateOn(targets), rows, shared is null);
    }

    /// <summary>The resolved shared target key (see <see cref="SharedDisplayLogic.ResolveSharedKey"/>); null = all mode.</summary>
    public string? SharedKey() => SharedKey(DisplayTargets.All());

    private string? SharedKey(List<DisplayTargets.Target> targets)
        => SharedDisplayLogic.ResolveSharedKey(_settings.SharedDisplayKey, targets);

    /// <summary>
    /// Live "on" state of the shared monitor (all mode: any external active). Falls back to the
    /// attached-display count when CCD is unavailable, and to the stored intent when nothing is attached.
    /// </summary>
    public bool AggregateOn() => AggregateOn(DisplayTargets.All());

    private bool AggregateOn(List<DisplayTargets.Target> targets)
    {
        if (targets.Count > 0)
            return SharedDisplayLogic.AggregateOn(SharedKey(targets), targets, _settings.On);

        int attached = NativeDisplay.Enumerate().Count(d => d.IsAttached);
        return attached == 0 ? _settings.On : attached > 1;
    }

    /// <summary>True if we can tell there's no built-in panel (a non-laptop) — worth warning about.</summary>
    public bool ShouldWarnNoInternal()
    {
        var targets = DisplayTargets.All();
        return targets.Count > 0 && !targets.Any(t => t.IsInternal);
    }

    /// <summary>Flip the shared monitor. Returns the new state, or null on failure.</summary>
    public bool? ToggleShared()
    {
        bool target = !AggregateOn();
        return SetShared(target) ? target : (bool?)null;
    }

    /// <summary>
    /// Grab (on) or release (off) the shared monitor: one CCD path edit in shared mode; in all mode
    /// the topology switch (extend / internal-only), falling back to DisplaySwitch.exe. The intent is
    /// persisted only when the switch worked, so a failure doesn't leave a wrong state.
    /// </summary>
    public bool SetShared(bool on)
    {
        bool ok;
        if (SharedKey() is { } key)
        {
            ok = DisplayConfig.SetTargetActive(key, on);
        }
        else
        {
            ok = on ? DisplayConfig.Extend() : DisplayConfig.Internal();
            if (!ok && (on ? DisplaySwitch.Extend() : DisplaySwitch.Internal()))
                ok = WaitForState(on, TimeSpan.FromSeconds(3));
        }
        if (ok)
        {
            _settings.On = on;
            _settings.Save();
        }
        return ok;
    }

    /// <summary>Switch one non-shared monitor on or off. Local only: not the handoff state, so <see cref="On"/> is untouched.</summary>
    public bool SetOne(string key, bool on) => DisplayConfig.SetTargetActive(key, on);

    /// <summary>Make <paramref name="key"/> the shared monitor; <paramref name="name"/> labels it while disconnected.</summary>
    public void ChooseShared(string key, string name)
    {
        _settings.SharedDisplayKey = key;
        _settings.SharedDisplayName = name;
        _settings.Save();
    }

    /// <summary>Forget the choice: back to auto (the only external, or all externals).</summary>
    public void ClearShared()
    {
        _settings.SharedDisplayKey = "";
        _settings.SharedDisplayName = "";
        _settings.Save();
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
