using System.Drawing;
using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace DisplayToggle;

/// <summary>
/// System-tray app. Left-click toggles all secondaries; right-click opens a per-display
/// menu plus peer status/config — the Windows analogue of the Mac menu-bar app.
/// Local toggles broadcast state to the peer; peer state changes are applied inversely
/// (shared-monitor handoff) without echoing. See PROTOCOL.md.
/// </summary>
internal sealed class TrayApp : ApplicationContext
{
    private readonly Settings _settings;
    private readonly DisplayManager _manager;
    private readonly PeerLink _peer = new();
    private readonly PeerDiscovery _discovery = new();
    private readonly NotifyIcon _tray;
    private readonly Control _ui = new();       // marshals background callbacks to the UI thread
    private TrayIcons.Glyph? _current;
    private readonly EventWaitHandle _quitEvent = new(false, EventResetMode.AutoReset, Program.QuitEventName);
    private readonly RegisteredWaitHandle _quitWait;
    private readonly (EventWaitHandle Event, RegisteredWaitHandle Wait)[] _commandWaits;
    private readonly System.Windows.Forms.Timer _settleRefresh = new() { Interval = 2000 };
    private readonly System.Windows.Forms.Timer _iconDebounce = new() { Interval = 400 };

    // Self-update (Updater.cs). Checks and downloads run on the thread pool; results come back via Post.
    private readonly Updater _updater = new();
    private readonly CancellationTokenSource _updateCts = new();
    private const int FirstUpdateCheckMs = 60_000;
    private const int UpdateCheckEveryMs = 6 * 60 * 60 * 1000;
    private readonly System.Windows.Forms.Timer _updateTimer = new() { Interval = FirstUpdateCheckMs };
    private bool _checkingUpdate;
    private bool _reportCheck;              // a "Check for updates" click is waiting for this check's outcome
    private UpdateManifest? _update;        // a verified newer release, once found
    private bool _installingUpdate;

    // Looked up off the UI thread (its fallback is a DNS lookup) at start and on address changes.
    private volatile string _localAddress = "…";

    private bool _peerConnected;
    private string? _peerEndpoint;
    private string? _peerHostName;

    public TrayApp()
    {
        _ = _ui.Handle; // force handle creation on the UI thread

        // Clean-exit request from `display-toggle --quit` or the scripts (see Program.QuitEventName).
        _quitWait = ThreadPool.RegisterWaitForSingleObject(_quitEvent, (_, _) => Post(Quit), null,
            Timeout.Infinite, executeOnlyOnce: true);

        // CLI switches (`--toggle` / `--extend` / `--internal`) handed to us while we run (Program.AskTray).
        _commandWaits = new (string, Action)[] { ("toggle", DoToggle), ("extend", () => DoSet(true)), ("internal", () => DoSet(false)) }
            .Select(c =>
            {
                var ev = new EventWaitHandle(false, EventResetMode.AutoReset, Program.CommandEventName(c.Item1));
                var wait = ThreadPool.RegisterWaitForSingleObject(ev, (_, _) => Post(c.Item2), null, Timeout.Infinite, executeOnlyOnce: false);
                return (ev, wait);
            })
            .ToArray();

        _settings = Settings.Load();
        _settings.EnsureInstanceId();
        _manager = new DisplayManager(_settings);

        _tray = new NotifyIcon
        {
            Visible = true,
            Text = "Display Toggle",
            ContextMenuStrip = new ContextMenuStrip(),
        };
        _tray.MouseClick += OnTrayClick;
        // An empty strip raises Opening with Cancel = true, so the first right-click after launch
        // (before any items exist) would be swallowed. The menu is built here, so un-cancel it.
        _tray.ContextMenuStrip.Opening += (_, e) => { RebuildMenu(); e.Cancel = false; };

        UpdateIcon();

        _peer.PeerStateChanged += OnPeerState;
        _peer.ConnectionChanged += OnPeerConnection;
        _discovery.PeersChanged += () => Post(OnPeersChanged);
        StartPeer();

        RefreshLocalAddress();
        NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;

        // Redraw the icon when the user switches light/dark theme.
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        // Re-render and re-register after RDP connect/disconnect, unlock, and a tray DPI change. Without
        // this, an icon rendered while the session had no (or a different) display stays blank or
        // stale in the tray until the next state change. Refresh now, then again once things settle.
        // Other display changes (including our own toggles) only re-render in place.
        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _settleRefresh.Tick += (_, _) => { _settleRefresh.Stop(); RefreshTray(); };
        _iconDebounce.Tick += (_, _) => OnIconDebounce();

        // Automatic update checks: once shortly after start, then every 6 h — only for the installed
        // copy, whatever its version (`make install` builds 0.0.0-dev, which every release is newer
        // than). A build run from the checkout never checks by itself; "Check for updates" still works.
        _updateTimer.Tick += (_, _) => { _updateTimer.Interval = UpdateCheckEveryMs; CheckForUpdates(manual: false); };
        if (IsInstalledCopy) _updateTimer.Start();

        // This app assumes a laptop (internal panel + external). Warn once if there's no built-in
        // display, since "internal only" would then blank every screen.
        if (!_settings.WarnedNoInternal && _manager.ShouldWarnNoInternal())
        {
            _settings.WarnedNoInternal = true;
            _settings.Save();
            Notify("No built-in display detected. This app is built for laptops — turning the external off would leave no screen.", ToolTipIcon.Warning);
        }
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e) => RefreshLocalAddress();

    private void RefreshLocalAddress() => _ = Task.Run(() => _localAddress = PeerLink.LocalAddress());

    private void Notify(string text, ToolTipIcon icon = ToolTipIcon.Info)
        => _tray.ShowBalloonTip(3500, "Display Toggle", text, icon);

    private void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        // General/Color: theme switch. Desktop/Window: a display-scale change on the same resolution
        // may only arrive as WM_SETTINGCHANGE — re-render at the new tray size.
        // These arrive in bursts (several Desktop changes per topology switch) and never change the
        // on/off state, so reuse the last one: UpdateIcon then only re-renders if size/theme changed.
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color
                       or UserPreferenceCategory.Desktop or UserPreferenceCategory.Window)
            Post(() => DebounceIconUpdate(displayChanged: false));
    }

    // Display and preference changes arrive in bursts (one topology switch → a display change plus
    // several Desktop preference changes). Collapse each burst into one update once it settles.
    private bool _pendingDisplayChange;

    private void DebounceIconUpdate(bool displayChanged)
    {
        _pendingDisplayChange |= displayChanged;
        _iconDebounce.Stop();
        _iconDebounce.Start();
    }

    private void OnIconDebounce()
    {
        _iconDebounce.Stop();
        bool display = _pendingDisplayChange;
        _pendingDisplayChange = false;
        if (display) OnDisplayChanged();
        else UpdateIcon(_rendered?.On);
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.ConsoleConnect or SessionSwitchReason.RemoteConnect
                     or SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon)
            Post(ScheduleRefresh);
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
        => Post(() => DebounceIconUpdate(displayChanged: true));

    // Our own toggles fire this too. Re-adding the tray entry (remove + add) costs ~0.5s on the UI
    // thread and churns the icon, so only do it when the tray's icon size changed (a DPI switch);
    // otherwise an in-place update covers a hot-plugged or released external.
    private void OnDisplayChanged()
    {
        if (_rendered is { } r && r.Size != TrayIcons.CurrentTraySize()) ScheduleRefresh();
        else UpdateIcon();
    }

    private void ScheduleRefresh()
    {
        RefreshTray();
        _settleRefresh.Stop();
        _settleRefresh.Start(); // the session's display/DPI may still be settling; redo it once more
    }

    /// <summary>Re-render the glyph and re-add the tray entry, so the shell drops any blank copy it holds.</summary>
    private void RefreshTray()
    {
        UpdateIcon(force: true);
        _tray.Visible = false;
        _tray.Visible = true;
    }

    // ---- tray interaction ---------------------------------------------------

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return; // right-click → ContextMenuStrip opens itself
        DoToggle();
    }

    // ---- switching (off the UI thread) ----------------------------------------

    // Topology switches run on the thread pool, one at a time in request order, so a slow switch
    // never stalls the tray and a local toggle can't interleave with a peer-driven one. Each job
    // decides from the live state when it runs (not when it was queued); `done` gets its result on
    // the UI thread. Only touched from the UI thread.
    private Task _switches = Task.CompletedTask;

    // A job that throws reports default (null: "didn't switch") instead of vanishing silently.
    private void QueueSwitch<T>(Func<T> job, Action<T?> done)
        => _switches = _switches.ContinueWith(_ =>
        {
            T? result;
            try { result = job(); }
            catch { result = default; }
            Post(() => done(result));
        }, TaskScheduler.Default);

    private void DoToggle() => QueueSwitch(_manager.ToggleAll, LocalSwitchDone);

    private void DoSet(bool on) => QueueSwitch(() => _manager.SetAll(on) ? on : (bool?)null, LocalSwitchDone);

    private void LocalSwitchDone(bool? newState)
    {
        if (newState is bool on)
        {
            _peer.SendState(on);       // local toggle → announce
            UpdateIcon(on);            // known result: don't re-query CCD while the switch settles
        }
        else
        {
            Notify("Couldn't switch the display — the topology change was rejected.", ToolTipIcon.Warning);
        }
    }

    private void RebuildMenu()
    {
        var menu = _tray.ContextMenuStrip!;
        // Clear() doesn't dispose the removed items, and a submenu that has been shown owns a
        // window — so each open would leak a USER handle. Dispose them (submenus recursively).
        var old = menu.Items.Cast<ToolStripItem>().ToList();
        menu.Items.Clear();
        foreach (var item in old) item.Dispose();

        if (_update is { } upd)
        {
            menu.Items.Add(!IsInstalledCopy
                ? new ToolStripMenuItem($"Update available: v{upd.Version} — run make install from a newer checkout") { Enabled = false }
                : _installingUpdate
                ? new ToolStripMenuItem($"Installing v{upd.Version}…") { Enabled = false }
                : new ToolStripMenuItem($"Update available: v{upd.Version} — Install and restart", null, (_, _) => InstallUpdate()));
            menu.Items.Add(new ToolStripSeparator());
        }

        // One checkable row per external monitor, labelled with its name, like the Mac's rows. Unlike
        // the Mac, every row switches the whole topology (extend ⇄ internal): the topology API can't
        // release one external alone. Checked = extended (on).
        var (on, labels) = _manager.MenuState();
        foreach (var name in labels)
        {
            var item = new ToolStripMenuItem(name) { Checked = on, CheckOnClick = false };
            item.Click += (_, _) => DoToggle();
            menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add(new ToolStripMenuItem($"This PC: {_localAddress}:{_settings.PeerPort}") { Enabled = false });
        // Prefer live connection state — a peer that dialed *us* counts as connected even
        // though we never configured a peer host ourselves. Show its hostname when known.
        string who = !string.IsNullOrWhiteSpace(_peerHostName) ? _peerHostName!
                   : !string.IsNullOrWhiteSpace(_peerEndpoint) ? _peerEndpoint! : "connected";
        // A link problem (PROTOCOL.md v2) replaces the offline line; wording shared with the Mac.
        string problemWho = !string.IsNullOrWhiteSpace(_peerHostName) ? _peerHostName!
                          : $"{_settings.PeerHost}:{_settings.PeerPort}";
        string? problem = _peer.CurrentProblem switch
        {
            PeerLink.Problem.NeedsUpdate => $"Peer: {problemWho} — needs update",
            PeerLink.Problem.NotAuthenticated => $"Peer: {problemWho} — not authenticated",
            PeerLink.Problem.RequiresSecret => $"Peer: {problemWho} — requires a secret",
            _ => null,
        };
        string peerLabel = _settings.PeerNone
            ? "Peer: none (standalone)"
            : _peerConnected
            ? $"Peer: {who} ✓"
            : problem
            ?? (!_settings.PeerConfigured
                ? "Peer: not set (peer can still connect to you)"
                : $"Peer: {_settings.PeerHost}:{_settings.PeerPort} (offline)");
        menu.Items.Add(new ToolStripMenuItem(peerLabel) { Enabled = false });
        menu.Items.Add(BuildChoosePeerMenu());

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem($"Version {UpdateLogic.CurrentVersion}") { Enabled = false });
        menu.Items.Add(_checkingUpdate
            ? new ToolStripMenuItem("Checking for updates…") { Enabled = false }
            : new ToolStripMenuItem("Check for updates", null, (_, _) => CheckForUpdates(manual: true)));
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Quit()));
    }

    // ---- self-update (network/disk on the thread pool; results Posted back) ------

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    // Only the copy `make install` put in %LOCALAPPDATA%\display-toggle replaces itself; a dev run
    // (bin\...) just says an update exists.
    private static bool IsInstalledCopy => UpdateLogic.IsInstalledCopy(Environment.ProcessPath, LocalAppData);

    /// <summary>Run one check. <paramref name="manual"/> = from the menu: report the outcome once, whatever it is.</summary>
    private void CheckForUpdates(bool manual)
    {
        _reportCheck |= manual;
        if (_checkingUpdate) return;   // the check already running reports to this click too
        _checkingUpdate = true;
        var current = UpdateLogic.CurrentVersion;
        var ct = _updateCts.Token;
        Task.Run(() => _updater.CheckAsync(current, ct), ct)
            .ContinueWith(t => Post(() => UpdateCheckDone(t.IsCompletedSuccessfully ? t.Result : null)), TaskScheduler.Default);
    }

    private void UpdateCheckDone(UpdateCheckResult? result)   // null: cancelled (we're quitting)
    {
        _checkingUpdate = false;
        bool report = _reportCheck;
        _reportCheck = false;
        if (result is null) return;

        switch (result.Outcome)
        {
            case UpdateCheckResult.Kind.Available:
                var m = result.Manifest!;
                _update = m;
                // One notification per version, unless the user asked just now. Set in memory first: a
                // ReadOnly config doesn't save, but this session still won't repeat the balloon.
                if (report || _settings.LastNotifiedVersion != m.Version.ToString())
                {
                    _settings.LastNotifiedVersion = m.Version.ToString();
                    _settings.Save();
                    Notify(IsInstalledCopy
                        ? $"Update available: v{m.Version}. Right-click the tray icon → Install and restart."
                        : $"Update available: v{m.Version}. Run make install from a newer checkout to update.");
                }
                break;
            case UpdateCheckResult.Kind.UpToDate:
                _update = null;
                if (report) Notify($"You're up to date (v{UpdateLogic.CurrentVersion}).");
                break;
            default:   // a failed automatic check stays quiet; the next one retries
                if (report) Notify($"Couldn't check for updates: {result.Error}.", ToolTipIcon.Warning);
                break;
        }
    }

    /// <summary>
    /// Download, verify and stage the update, start the helper that swaps the files in once we've
    /// exited, then quit the normal way. Any failure before the hand-off leaves the install as it is.
    /// </summary>
    private void InstallUpdate()
    {
        if (_update is not { } m || _installingUpdate || !IsInstalledCopy) return;
        _installingUpdate = true;
        Notify($"Downloading v{m.Version}…");
        string installDir = UpdateLogic.InstallDir(LocalAppData);
        string temp = Path.GetTempPath();
        int pid = Environment.ProcessId;
        var ct = _updateCts.Token;
        Task.Run(async () =>
            {
                string staging = await _updater.StageAsync(m, temp, ct).ConfigureAwait(false);
                try { Updater.LaunchHelper(staging, installDir, pid, Path.Combine(temp, "display-toggle-update.log")); }
                catch { Updater.TryDelete(staging); throw; }
            }, ct)
            .ContinueWith(t => Post(() => InstallStarted(m, t)), TaskScheduler.Default);
    }

    private void InstallStarted(UpdateManifest m, Task staged)
    {
        if (staged.IsCompletedSuccessfully)
        {
            Quit();   // the helper is waiting for us to exit; it then swaps the files and relaunches
            return;
        }
        _installingUpdate = false;
        if (staged.IsCanceled) return;
        var e = staged.Exception?.GetBaseException();
        string why = e is UpdateException ? e.Message : $"{e?.GetType().Name}: {e?.Message}";
        Notify($"Couldn't install v{m.Version}: {why}. Still running v{UpdateLogic.CurrentVersion}.", ToolTipIcon.Warning);
    }

    // "Choose peer ▸": None (standalone), discovered instances (checked = current peer), then the manual dialog.
    private ToolStripMenuItem BuildChoosePeerMenu()
    {
        var choose = new ToolStripMenuItem("Choose peer");
        choose.DropDownItems.Add(new ToolStripMenuItem("None (standalone)", null, (_, _) => ChooseNoPeer())
        {
            Checked = _settings.PeerNone,
        });
        choose.DropDownItems.Add(new ToolStripSeparator());
        var peers = _discovery.Peers;
        if (peers.Count == 0)
        {
            string searching = _discovery.IsPublicNetwork
                ? "Searching… (this network is Public — set it to Private)"
                : "Searching…";
            choose.DropDownItems.Add(new ToolStripMenuItem(searching) { Enabled = false });
        }
        foreach (var p in peers)
        {
            var item = new ToolStripMenuItem($"{p.Name} — {p.Ip}")
            {
                Checked = !_settings.PeerNone && PeerDiscoveryLogic.IsCurrent(p, _settings.PeerHost, _settings.PeerPort, _settings.PeerId),
            };
            item.Click += (_, _) => ChoosePeer(p);
            choose.DropDownItems.Add(item);
        }
        choose.DropDownItems.Add(new ToolStripSeparator());
        choose.DropDownItems.Add(new ToolStripMenuItem("Set peer manually…", null, (_, _) => SetPeer()));
        return choose;
    }

    private void ChoosePeer(DiscoveredPeer p)
    {
        _settings.PeerHost = p.Ip;
        _settings.PeerPort = p.Port;
        _settings.PeerId = p.Id;
        _settings.PeerNone = false;
        _settings.Save();
        StartPeer();
    }

    // "Choose peer ▸ None": forget the peer and stop the link entirely (standalone).
    private void ChooseNoPeer()
    {
        _settings.PeerHost = "";
        _settings.PeerId = "";
        _settings.PeerNone = true;
        _settings.Save();
        StartPeer();
    }

    private void SetPeer()
    {
        using var dlg = new PeerDialog(_settings.PeerHost, _settings.PeerPort, _settings.PeerSecret);
        if (dlg.ShowDialog() != DialogResult.OK) return;
        _settings.PeerHost = dlg.PeerHost;
        _settings.PeerPort = dlg.PeerPort;
        _settings.PeerSecret = dlg.PeerSecret;
        _settings.PeerId = "";   // manual entry — no longer tied to a discovered instance
        _settings.PeerNone = false;
        _settings.Save();
        StartPeer();
    }

    private void StartPeer()
    {
        if (_settings.PeerNone)
        {
            // Standalone: no link and no advertising, but keep browsing so peers still show in the menu.
            _peer.Stop();
            _discovery.Start(_settings.InstanceId, _settings.PeerPort, advertise: false);
            _peerConnected = false;
            _peerEndpoint = null;
            _peerHostName = null;
            UpdateIcon(_rendered?.On);
            return;
        }
        _peer.Start(_settings.PeerHost, _settings.PeerPort, _manager.On, _settings.PeerSecret, _settings.InstanceId);
        _discovery.Start(_settings.InstanceId, _settings.PeerPort);
    }

    // ---- discovery (raised on dnsapi threads → marshalled here via Post) -----

    // Auto-pick the only peer when none is configured, or follow the chosen one to a new address —
    // never while the link is up (see PeerDiscoveryLogic.DecideAdopt).
    private void OnPeersChanged()
    {
        var adopt = PeerDiscoveryLogic.DecideAdopt(_peerConnected || _peer.IsConnected,
            _settings.PeerHost, _settings.PeerPort, _settings.PeerId, _discovery.Peers, _settings.PeerNone);
        if (adopt is null) return;
        bool autoPicked = !_settings.PeerConfigured;
        ChoosePeer(adopt);
        if (autoPicked) Notify($"Found {adopt.Name} on the network — paired with it ({adopt.Ip}).");
    }

    // ---- peer callbacks (background threads → marshal to UI) ----------------

    private void OnPeerState(bool peerOn)
    {
        // Inverse mirror: peer released (off) → we grab (on), and vice-versa. Silent (no re-broadcast).
        bool desired = !peerOn;
        Post(() => QueueSwitch(
            () => _manager.AggregateOn() != desired || _manager.On != desired
                ? (bool?)_manager.SetAll(desired)   // ran: did it work?
                : null,                            // already there: nothing to do
            ran => { if (ran is bool ok) UpdateIcon(ok ? desired : null); }));
    }

    private void OnPeerConnection(bool connected, string? endpoint, string? host)
    {
        Post(() =>
        {
            _peerConnected = connected;
            _peerEndpoint = endpoint;
            if (!connected) _peerHostName = null;
            else if (!string.IsNullOrWhiteSpace(host)) _peerHostName = host;
            UpdateIcon(_rendered?.On);   // only the peer dot changed
            if (!connected) OnPeersChanged();
        });
    }

    private void Post(Action action)
    {
        // The handle is created in the constructor, so no handle means we're tearing down: drop the
        // callback rather than run UI code on a background thread.
        if (!_ui.IsHandleCreated) return;
        if (_ui.InvokeRequired) _ui.BeginInvoke(action);
        else action();
    }

    // ---- icon ---------------------------------------------------------------

    // What the current glyph was rendered for, so repeat calls with nothing changed are free.
    private (bool On, bool Peer, int Size, bool Light)? _rendered;

    /// <summary>
    /// Re-render the glyph if anything it shows changed. <paramref name="on"/> = the known display
    /// state (skips the CCD query, which is slow mid-switch); null = query it. <paramref name="force"/>
    /// re-renders regardless (the tray re-add wants a fresh handle).
    /// </summary>
    private void UpdateIcon(bool? on = null, bool force = false)
    {
        var key = (on ?? _manager.AggregateOn(), _peerConnected, TrayIcons.CurrentTraySize(), TrayIcons.IsLightTaskbar());
        if (!force && _rendered == key) return;
        var next = TrayIcons.Make(key.Item1, key.Item2, key.Item3);
        _tray.Icon = next.Icon;
        _current?.Dispose();
        _current = next;
        _rendered = key;
    }

    // ---- teardown -----------------------------------------------------------

    /// <summary>Remove the tray icon now. Safe from any thread; used by the crash handler.</summary>
    internal void HideTrayIcon()
    {
        try { _tray.Visible = false; } catch { /* already torn down */ }
    }

    private void Quit()
    {
        _tray.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
            NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
            _quitWait.Unregister(null);
            _quitEvent.Dispose();
            foreach (var (ev, wait) in _commandWaits) { wait.Unregister(null); ev.Dispose(); }
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            _settleRefresh.Dispose();
            _iconDebounce.Dispose();
            _updateTimer.Dispose();
            _updateCts.Cancel();   // an in-flight check/download stops; its Post is dropped
            _updater.Dispose();
            _updateCts.Dispose();
            _discovery.Dispose();
            _peer.Dispose();
            _tray.Dispose();
            _current?.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
