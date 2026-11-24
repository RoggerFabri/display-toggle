namespace DisplayToggle;

/// <summary>
/// Pure decisions about the shared monitor (the one left-click, the CLI and the peer handoff switch),
/// kept apart from the CCD calls so they can be tested without hardware.
/// </summary>
internal static class SharedDisplayLogic
{
    /// <summary>
    /// The shared target's key: the configured one (even while disconnected, so a replugged monitor
    /// stays shared); else the only connected external; else null = "all externals" mode, which
    /// switches the whole topology as before. Internal panels are never picked.
    /// </summary>
    public static string? ResolveSharedKey(string configuredKey, IReadOnlyList<DisplayTargets.Target> targets)
    {
        if (!string.IsNullOrEmpty(configuredKey)) return configuredKey;
        var externals = targets.Where(t => !t.IsInternal).Take(2).ToList();
        return externals.Count == 1 ? externals[0].Key : null;
    }

    /// <summary>
    /// Aggregate "on" (grabbed). Shared mode: the shared target is active, or the stored intent
    /// <paramref name="storedOn"/> when it isn't connected (there's nothing live to read). All mode:
    /// any external is active; the stored intent when CCD returned no targets at all.
    /// </summary>
    public static bool AggregateOn(string? sharedKey, IReadOnlyList<DisplayTargets.Target> targets, bool storedOn)
    {
        if (sharedKey is not null)
            return targets.FirstOrDefault(t => t.Key == sharedKey) is { } shared ? shared.IsActive : storedOn;
        if (targets.Count == 0) return storedOn;
        return targets.Any(t => !t.IsInternal && t.IsActive);
    }
}
