# Windows: per-display toggles and a chosen shared monitor

Status: approved 2026-11-23.

## Why

The Windows app switched the whole topology (`SDC_TOPOLOGY_INTERNAL` / `EXTEND`, the Win+P modes).
"Off" therefore dropped **every** non-internal display, not just the monitor shared with the Mac.
With a second external — a SudoVDA virtual monitor used by Tessera (iPad as a monitor) — releasing
the Dell also killed the iPad's monitor, and every menu row did the same whole-topology toggle.

## Goals

- Turn one external off (or on) without touching any other display.
- Left-click, the peer handoff and `--toggle/--extend/--internal` act on **the shared monitor** only.
- No change to the peer protocol or the Mac app.

Non-goals: identifying virtual monitors automatically (SudoVDA reports itself as HDMI, the same
output technology as the Dell, so the app can't tell them apart).

## Approaches considered

1. Auto-pick the shared monitor by output technology (skip virtual displays). Rejected: SudoVDA
   declares `DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI`, indistinguishable from a real HDMI monitor.
2. **Chosen:** per-display rows like the Mac, plus a user-chosen shared monitor.

## Design

- **Per-path switching (CCD).** Off: query active paths, drop the target's path(s), apply the rest
  with `SDC_USE_SUPPLIED_DISPLAY_CONFIG`. If the dropped display held the desktop origin (primary),
  translate the remaining sources so another one (the internal panel if active) sits at (0,0).
  Refuse to turn off the last active display. On: activate one of the target's paths on a free
  source; restore the layout from the topology database (`SDC_TOPOLOGY_SUPPLIED`), else place it
  right of the current desktop at its preferred mode.
- **Rows.** One row per connected external (active or not), checked when active; each toggles only
  that display. The shared monitor's row behaves like a left-click (tells the peer); other rows are
  local only.
- **Shared monitor ▸** submenu: the externals plus *All external monitors*. Stored in
  `config.json` as `SharedDisplayKey` (the CCD monitor device path) and `SharedDisplayName` (label).
  Unset: with exactly one external connected it is the shared one; with several, left-click/peer
  act on all externals via the old topology switch (and its `DisplaySwitch.exe` fallback).
- **State.** "On" = the shared monitor is active (all-mode: any external active). If the shared
  monitor isn't connected, the stored intent is used.
- The obsolete `ExternalNames` label cache goes: inactive externals are still enumerated by CCD.

## Hot-plug (added 2026-11-24)

Windows remembers a layout per set of connected displays and re-applies it when one is plugged in
or removed; when the Tessera virtual monitor (SudoVDA) came or went, that flipped the Dell against
the user's intent. Approved by the user:

- After a display change the app didn't cause, if the set of connected targets changed (a key
  appeared or disappeared), the tray puts the shared monitor back to the stored intent
  (`Settings.On`), switching only that monitor.
- Only on a connected-set change: activation-only changes (manual changes in Windows Settings, our
  own switches) never trigger it. Only in shared mode (a resolved shared key); never in
  all-externals mode, where the topology switch would turn other displays off. Nothing happens if
  the shared monitor isn't connected or is already in the intended state.
- It runs ~1.5 s after the last display-change event of a burst (separate from the 400 ms icon
  debounce), through the FIFO `QueueSwitch`, so it decides from live state and can't interleave
  with other switches.
- Silent: no peer message (the peer already knows the intent) and no notification on success; one
  warning notification on failure.

## Left to the builder

Exact CCD flag combinations and the fallback when the topology database has no entry; both are
verified on real hardware (ASUS laptop panel + Dell P2422HE + SudoVDA).
