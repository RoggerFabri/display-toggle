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

    /// <summary>
    /// The connected targets' keys, compared case-insensitively — the set Windows keys its stored
    /// layouts on. Inactive targets count: they're still connected, so our own switches and the user's
    /// flips in Windows Settings never change this set; only a plug or unplug does.
    /// </summary>
    public static HashSet<string> ConnectedKeys(IReadOnlyList<DisplayTargets.Target> targets)
        => new(targets.Select(t => t.Key), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// After a display change we didn't cause, the state to put the shared monitor back in, or null
    /// for nothing to do. Windows re-applies its stored layout for the new set of connected displays
    /// (e.g. a SudoVDA virtual monitor appearing or disappearing), which can turn the shared monitor on
    /// or off against <paramref name="intentOn"/>, the handoff state the peer already knows.
    /// Only a change of the connected set counts (<paramref name="previousConnected"/> null = first
    /// look: never), so manual activation changes aren't fought. Never in all mode, whose topology
    /// switch would turn other displays off; nothing when the shared monitor isn't connected or is
    /// already right.
    /// </summary>
    public static bool? HotplugTarget(IReadOnlySet<string>? previousConnected,
        IReadOnlyList<DisplayTargets.Target> targets, string configuredKey, bool intentOn)
    {
        if (previousConnected is null) return null;
        if (ConnectedKeys(targets).SetEquals(previousConnected)) return null;
        if (ResolveSharedKey(configuredKey, targets) is not { } sharedKey) return null;
        var shared = targets.FirstOrDefault(t => string.Equals(t.Key, sharedKey, StringComparison.OrdinalIgnoreCase));
        if (shared is null || shared.IsActive == intentOn) return null;
        return intentOn;
    }
}
