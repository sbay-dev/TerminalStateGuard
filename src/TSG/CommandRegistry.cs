namespace TSG;

using TSG.Platform;

/// <summary>
/// Registry of all CLI commands using C# 14 lambda delegates.
/// </summary>
public static class CommandRegistry
{
    public static Dictionary<string, Func<string[], Task<int>>> Build(IPlatformHost host)
    {
        var installer = new Installer(host);
        var runner = new ScriptRunner(host);

        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["install"]   = async _ => { await installer.InstallAsync(); return 0; },
            ["uninstall"] = async _ => { await installer.UninstallAsync(); return 0; },
            ["boost"]     = args => runner.RunAsync("boost", args),
            ["monitor"]   = args => runner.RunAsync("monitor", args),
            ["status"]    = args => runner.RunAsync("status", args),
            ["recover"]   = args => runner.RunAsync("recover", args),
            ["restore"]   = args => runner.RunAsync("restore", args),
            ["focus"]     = args => runner.RunAsync("focus", args),
            ["snapshots"] = args => Snapshots.RunAsync(host, args),
            ["windows"]   = args => Windows.RunAsync(host, args),
            ["capture"]   = args => StateCapture.RunAsync(host, args),
            ["processes"] = args => ProcessManager.RunAsync(host, args),
            ["ps"]        = args => ProcessManager.RunAsync(host, args),
            ["db"]        = args => DbQuery.RunAsync(host, args),
            ["doctor"]    = async _ => { await Diagnostics.RunDoctorAsync(host); return 0; },
            ["config"]    = args => Configuration.RunAsync(host, args),
            ["version"]   = _ => { Console.WriteLine($"tsg {Assembly.GetExecutingAssembly().GetName().Version}"); return Task.FromResult(0); },
            ["help"]      = _ => { PrintHelp(); return Task.FromResult(0); },
            ["--help"]    = _ => { PrintHelp(); return Task.FromResult(0); },
            ["-h"]        = _ => { PrintHelp(); return Task.FromResult(0); },
            ["-v"]        = _ => { Console.WriteLine($"tsg {Assembly.GetExecutingAssembly().GetName().Version}"); return Task.FromResult(0); },
        };
    }

    static void PrintHelp()
    {
        var os = OperatingSystem.IsWindows() ? "Windows" : "Linux/macOS";
        Console.WriteLine($"""

         ████████╗███████╗ ██████╗ 
         ╚══██╔══╝██╔════╝██╔════╝ 
            ██║   ███████╗██║  ███╗
            ██║   ╚════██║██║   ██║
            ██║   ███████║╚██████╔╝
            ╚═╝   ╚══════╝ ╚═════╝ 
          ⚡ Terminal State Guard v2.0 ⚡
          Platform: {os} | .NET {Environment.Version}

          USAGE:
            tsg install       Setup scripts, shortcuts & terminal integration
            tsg uninstall     Remove configuration
            tsg boost         Elevate Copilot priority (Admin/sudo)
            tsg monitor       Safe live monitor with diagnostics
            tsg status        Quick health check
            tsg recover       Recover terminal sessions
            tsg restore       Restore original priorities
            tsg focus         Focus ALL resources on one stuck process (Admin)
            tsg focus <PID>   Focus on specific PID
            tsg focus --undo  Restore normal priorities
            tsg snapshots     List all saved terminal snapshots
            tsg snapshots --all  Show all with tab details
            tsg windows       Show active & recently closed windows with tabs
            tsg windows -i    Interactive dashboard (restore, snapshot, processes)
            tsg windows --history  Browse window history
            tsg windows --restore  Restore windows with all their tabs
            tsg capture       Capture current terminal state to SQLite
            tsg capture -q    Capture quietly (for automation)
            tsg processes     Show dev processes with ports & resource usage
            tsg ps            Alias for tsg processes
            tsg ps -i         Interactive process manager with navigation
            tsg ps --orphans  Show only orphaned background processes
            tsg ps --ports    Show only port-binding processes
            tsg ps --kill <PID>       Kill a process (with confirmation)
            tsg ps --kill-tree <PID>  Kill process tree
            tsg db "<SQL>"    Query terminal database (read-only)
            tsg config        Show/set configuration (max-snapshots)
            tsg doctor        Diagnose environment issues
            tsg version       Show version

          SHORTCUTS (after install):
            Ctrl+Alt+B  Boost    Ctrl+Alt+M  Monitor
            Ctrl+Alt+S  Status   Ctrl+Alt+F  Recover
            Ctrl+Alt+R  Restore  Ctrl+Alt+H  Help
            Ctrl+Alt+P  Processes

          SAFETY:
            ⛔ Monitor is READ-ONLY — never modifies Copilot files
            ⛔ NEVER delete events.jsonl — destroys session history

        """);
    }
}

file static class Assembly
{
    public static System.Reflection.Assembly GetExecutingAssembly()
        => System.Reflection.Assembly.GetExecutingAssembly();
}
