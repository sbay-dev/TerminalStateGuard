using System.Diagnostics;
using TSG.Platform;

namespace TSG;

/// <summary>
/// Runs platform-specific scripts (PowerShell on Windows, Bash on Linux).
/// </summary>
public class ScriptRunner(IPlatformHost host)
{
    static readonly Dictionary<string, (string Script, string Args)> ScriptMap = new()
    {
        ["boost"]   = ("CopilotBoost.ps1", "-Mode Boost"),
        ["monitor"] = ("CopilotBoost.ps1", "-Mode Monitor"),
        ["status"]  = ("CopilotBoost.ps1", "-Mode Status"),
        ["restore"] = ("CopilotBoost.ps1", "-Mode Restore"),
        ["recover"] = ("RecoverSessions.ps1", ""),
    };

    public async Task<int> RunAsync(string command, string[] extraArgs)
    {
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

        var allArgs = string.Join(" ", info.Args, string.Join(" ", extraArgs)).Trim();

        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(shell, $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" {allArgs}")
            : new ProcessStartInfo(shell, $"\"{scriptPath}\" {allArgs}");

        psi.UseShellExecute = false;

        var process = Process.Start(psi)!;
        await process.WaitForExitAsync();
        return process.ExitCode;
    }
}
