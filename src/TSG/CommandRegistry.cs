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
            ["doctor"]    = async _ => { await Diagnostics.RunDoctorAsync(host); return 0; },
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
          ⚡ Terminal State Guard v1.0 ⚡
          Platform: {os} | .NET {Environment.Version}

          USAGE:
            tsg install       Setup scripts, shortcuts & terminal integration
            tsg uninstall     Remove configuration
            tsg boost         Elevate Copilot priority (Admin/sudo)
            tsg monitor       Safe live monitor with diagnostics
            tsg status        Quick health check
            tsg recover       Recover terminal sessions
            tsg restore       Restore original priorities
            tsg doctor        Diagnose environment issues
            tsg version       Show version

          SHORTCUTS (after install):
            Ctrl+Alt+B  Boost    Ctrl+Alt+M  Monitor
            Ctrl+Alt+S  Status   Ctrl+Alt+F  Recover
            Ctrl+Alt+R  Restore  Ctrl+Alt+H  Help

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
