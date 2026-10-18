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
        menu.Items.Add(new ToolStripMenuItem("Quit", null, (_, _) => Quit()));
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
            _discovery.Dispose();
            _peer.Dispose();
            _tray.Dispose();
            _current?.Dispose();
            _ui.Dispose();
        }
        base.Dispose(disposing);
    }
}
