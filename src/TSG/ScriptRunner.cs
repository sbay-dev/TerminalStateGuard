using System.Diagnostics;
using System.Reflection;
using TSG.Platform;

namespace TSG;

/// <summary>
/// Runs platform-specific scripts (PowerShell on Windows, Bash on Linux).
/// </summary>
public class ScriptRunner(IPlatformHost host)
{
    static readonly Dictionary<string, (string Script, string Args)> ScriptMap = new()
    {
        ["boost"] = ("CopilotBoost.ps1", "-Mode Boost"),
        ["monitor"] = ("CopilotBoost.ps1", "-Mode Monitor"),
        ["status"] = ("CopilotBoost.ps1", "-Mode Status"),
        ["restore"] = ("CopilotBoost.ps1", "-Mode Restore"),
        ["recover"] = ("RecoverSessions.ps1", ""),
        ["focus"] = ("Focus.ps1", ""),
    };

    public async Task<int> RunAsync(string command, string[] extraArgs)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(extraArgs);

        if (!ScriptMap.TryGetValue(command, out var info))
        {
            Console.WriteLine($"  ❌ Unknown command: {command}");
            return 1;
        }

        var scriptPath = Path.Combine(host.TsgDir, info.Script);
        if (!File.Exists(scriptPath))
        {
            Console.WriteLine($"  ❌ Script not found: {scriptPath}");
            Console.WriteLine("  Run 'tsg install' first.");
            return 1;
        }

        var shell = host.FindShell();
        if (shell is null)
        {
            Console.WriteLine($"  ❌ {host.ShellName} not found.");
            return 1;
        }

        var normalizedArgs = command.Equals("recover", StringComparison.OrdinalIgnoreCase)
            ? NormalizeRecoveryArgs(extraArgs)
            : extraArgs;
        var allArgs = string.Join(" ", info.Args, string.Join(" ", normalizedArgs)).Trim();

        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(shell, $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {allArgs}")
            : new ProcessStartInfo(shell, $"\"{scriptPath}\" {allArgs}");

        psi.UseShellExecute = false;
        psi.Environment["TSG_VERSION"] =
            Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown";

        var process = Process.Start(psi)!;
        await process.WaitForExitAsync();
        return process.ExitCode;
    }

    static string[] NormalizeRecoveryArgs(string[] args)
    {
        var result = new List<string>(args.Length);
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i].Equals("--all", StringComparison.OrdinalIgnoreCase))
            {
                result.Add("-All");
                continue;
            }

            if (args[i].Equals("--limit", StringComparison.OrdinalIgnoreCase)
                || args[i].Equals("-n", StringComparison.OrdinalIgnoreCase))
            {
                result.Add("-Limit");
                if (i + 1 < args.Length)
                    result.Add(args[++i]);
                continue;
            }

            result.Add(args[i]);
        }

        return [.. result];
    }
}
