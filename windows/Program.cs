using System.Reflection;
using System.Runtime.InteropServices;

namespace DisplayToggle;

internal static class Program
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int dwProcessId);
    private const int ATTACH_PARENT_PROCESS = -1;

    /// <summary>
    /// Signalled by <c>display-toggle --quit</c> (and the install/run scripts) to ask the running tray
    /// app to exit cleanly, which removes its tray icon. A forced kill (Stop-Process -Force, Task
    /// Manager) can't: the process gets no chance to run, so the shell keeps a ghost icon until the
    /// mouse passes over it. Global, like the single-instance mutex, so stop.ps1 / `make install` run
    /// from another session (SSH, a second logon) can still reach it instead of force-killing.
    /// </summary>
    internal const string QuitEventName = @"Global\display-toggle.quit";

    /// <summary>
    /// Signalled by <c>--toggle</c> / <c>--extend</c> / <c>--internal</c> when the tray app is running, so
    /// the tray does the switch: it then also tells the peer, and nothing writes config.json behind
    /// its back (its next save would undo the change). <paramref name="command"/> is the option name.
    /// </summary>
    internal static string CommandEventName(string command) => $@"Global\display-toggle.{command}";

    [STAThread]
    private static int Main(string[] args)
    {
        // CLI subcommands (write to the launching console, then exit — no tray).
        if (args.Length > 0)
        {
            AttachConsole(ATTACH_PARENT_PROCESS);
            switch (args[0].ToLowerInvariant())
            {
                case "--list": return CmdList();
                case "--discover": return CmdDiscover(args.Length > 1 && int.TryParse(args[1], out var s) ? s : 20);
                case "--toggle": return CmdToggle();
                case "--quit": return CmdQuit();
                case "--extend": case "--on": return CmdSwitch(true);
                case "--internal": case "--off": return CmdSwitch(false);
                case "--version": case "-v":
                    Console.WriteLine($"display-toggle {Assembly.GetExecutingAssembly().GetName().Version}");
                    return 0;
                case "--help": case "-h":
                    Console.WriteLine("display-toggle [--list | --discover [secs] | --toggle | --extend | --internal | --quit | --version]");
                    Console.WriteLine("  (no args)     run the system-tray app");
                    Console.WriteLine("  --list        print detected displays and exit");
                    Console.WriteLine("  --discover    browse for peers on the LAN for N seconds (default 20) and print them");
                    Console.WriteLine("  --toggle      flip between extended (external on) and internal-only");
                    Console.WriteLine("  --extend      extend onto the external monitor (grab)");
                    Console.WriteLine("  --internal    internal panel only (release)");
                    Console.WriteLine("  --quit        ask the running tray app to exit cleanly (removes its tray icon)");
                    return 0;
                default:
                    Console.Error.WriteLine($"unknown option: {args[0]}");
                    return 2;
            }
        }

        // Single-instance guard (analogue of the Mac flock).
        using var mutex = new Mutex(initiallyOwned: true, "Global\\display-toggle.singleton", out bool isNew);
        if (!isNew) return 0;

        ApplicationConfiguration.Initialize();
        var tray = new TrayApp();
        // Last chance on a crash: remove the tray icon so it isn't left behind as a ghost.
        AppDomain.CurrentDomain.UnhandledException += (_, _) => tray.HideTrayIcon();
        Application.Run(tray);
        return 0;
    }

    private static int CmdQuit()
    {
        if (!EventWaitHandle.TryOpenExisting(QuitEventName, out var quit))
        {
            Console.WriteLine("display-toggle is not running");
            return 0;
        }
        using (quit) quit.Set();
        return 0;
    }

    private static int CmdList()
    {
        var displays = NativeDisplay.Enumerate();
        Console.WriteLine($"{"Device",-16} {"On",-4} {"Primary",-8} Name");
        Console.WriteLine(new string('-', 60));
        foreach (var d in displays)
            Console.WriteLine($"{d.DeviceName,-16} {(d.IsAttached ? "on" : "off"),-4} {(d.IsPrimary ? "yes" : ""),-8} {d.FriendlyName}");
        Console.WriteLine();
        Console.WriteLine($"secondaries: {displays.Count(d => !d.IsPrimary)}");
        return 0;
    }

    private static int CmdDiscover(int seconds)
    {
        // Browse only (a throwaway id, so a running tray instance isn't filtered or shadowed).
        var settings = Settings.Load();
        PeerDiscovery.Trace = msg => Console.WriteLine($"[{DateTime.Now:HH:mm:ss}]   · {msg}");
        using var discovery = new PeerDiscovery();
        discovery.PeersChanged += () =>
        {
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] peers:");
            foreach (var p in discovery.Peers)
                Console.WriteLine($"  {p.Name} — {p.Ip}:{p.Port}  os={p.Os}  id={p.Id}{(p.Id == settings.PeerId ? "  (current)" : "")}");
        };
        discovery.Start(Guid.NewGuid().ToString(), settings.PeerPort, advertise: false);
        System.Threading.Thread.Sleep(TimeSpan.FromSeconds(Math.Max(1, seconds)));
        Console.WriteLine($"found {discovery.Peers.Count} peer(s)");
        return 0;
    }

    private static void PrintDisplays(DisplayManager mgr, string when)
    {
        Console.WriteLine($"{when} (extended={mgr.AggregateOn()}):");
        foreach (var e in mgr.AllDisplays())
            Console.WriteLine($"  {e.DeviceName}  {(e.IsOn ? "on " : "off")}  {(e.IsPrimary ? "[primary] " : "")}{e.FriendlyName}");
    }

    /// <summary>Hand a switch to the running tray app, if there is one; then report the result.</summary>
    private static bool AskTray(string command)
    {
        if (!EventWaitHandle.TryOpenExisting(CommandEventName(command), out var ev)) return false;
        using (ev) ev.Set();
        Console.WriteLine($"asked the running tray app to {command.TrimStart('-')}");
        System.Threading.Thread.Sleep(2000); // let the switch land before re-reading
        Console.WriteLine($"extended-now={new DisplayManager(Settings.Load()).AggregateOn()}");
        return true;
    }

    private static int CmdToggle()
    {
        if (AskTray("toggle")) return 0;
        var mgr = new DisplayManager(Settings.Load());
        PrintDisplays(mgr, "before");
        var result = mgr.ToggleAll();
        Console.WriteLine(result is bool b ? $"toggled → {(b ? "extended (on)" : "internal only (off)")}" : "toggle failed");
        System.Threading.Thread.Sleep(1500); // let the topology change land before re-reading
        PrintDisplays(mgr, "after");
        return result is null ? 1 : 0;
    }

    private static int CmdSwitch(bool on)
    {
        if (AskTray(on ? "extend" : "internal")) return 0;
        var mgr = new DisplayManager(Settings.Load());
        bool ok = mgr.SetAll(on);
        System.Threading.Thread.Sleep(1500);
        Console.WriteLine($"{(on ? "extend" : "internal")}: ok={ok}  extended-now={mgr.AggregateOn()}");
        return ok ? 0 : 1;
    }
}
