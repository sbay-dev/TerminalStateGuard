namespace TSG.Platform;

/// <summary>
/// Windows-specific host with Windows Terminal Fragment extension support.
/// </summary>
public class WindowsHost : IPlatformHost
{
    public string HomeDir { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string TsgDir => Path.Combine(HomeDir, ".tsg");
    public string CopilotSessionDir => Path.Combine(HomeDir, ".copilot", "session-state");
    public string ShellName => "pwsh";

    public string ShellProfilePath
    {
        get
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            return Path.Combine(docs, "PowerShell", "Microsoft.PowerShell_profile.ps1");
        }
    }

    static readonly string FragmentDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "Windows Terminal", "Fragments", "TerminalStateGuard");

    public string? FindShell()
    {
        string[] paths =
        [
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe"),
        ];
        return paths.FirstOrDefault(File.Exists) ?? TryPathLookup("pwsh");
    }

    public string GetScriptExtension() => ".ps1";
    public string GetScriptPrefix(string scriptPath) => $"& '{scriptPath}'";

    /// <summary>
    /// Install Windows Terminal Fragment — native integration without modifying settings.json.
    /// Adds TSG profile and keyboard shortcuts directly to Windows Terminal.
    /// </summary>
    public async Task InstallTerminalIntegrationAsync()
    {
        Directory.CreateDirectory(FragmentDir);

        var tsgDir = TsgDir.Replace("\\", "\\\\");
        var fragment = $$"""
        {
            "profiles": [
                {
                    "name": "⚡ TSG Monitor",
                    "commandline": "pwsh -NoProfile -ExecutionPolicy Bypass -File \"{{tsgDir}}\\CopilotBoost.ps1\" -Mode Monitor",
                    "icon": "⚡",
                    "startingDirectory": "%USERPROFILE%"
                }
            ],
            "actions": [
                {"keys":"ctrl+alt+b","command":{"action":"sendInput","input":"tsg boost\r\n"},"name":"TSG: Boost"},
                {"keys":"ctrl+alt+m","command":{"action":"sendInput","input":"tsg monitor\r\n"},"name":"TSG: Monitor"},
                {"keys":"ctrl+alt+s","command":{"action":"sendInput","input":"tsg status\r\n"},"name":"TSG: Status"},
                {"keys":"ctrl+alt+f","command":{"action":"sendInput","input":"tsg recover\r\n"},"name":"TSG: Recover"},
                {"keys":"ctrl+alt+r","command":{"action":"sendInput","input":"tsg restore\r\n"},"name":"TSG: Restore"}
            ]
        }
        """;

        await File.WriteAllTextAsync(Path.Combine(FragmentDir, "tsg.json"), fragment);
        Console.WriteLine($"  ✅ Windows Terminal Fragment installed: {FragmentDir}");
        Console.WriteLine("     Shortcuts will appear after reopening Windows Terminal.");
    }

    public async Task RemoveTerminalIntegrationAsync()
    {
        if (Directory.Exists(FragmentDir))
        {
            Directory.Delete(FragmentDir, true);
            Console.WriteLine("  ✅ Windows Terminal Fragment removed");
        }
        await Task.CompletedTask;
    }

    public string? GetTerminalStateJson()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Packages", "Microsoft.WindowsTerminal_8wekyb3d8bbwe", "LocalState", "state.json");
        return File.Exists(path) ? path : null;
    }

    static string? TryPathLookup(string exe)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(exe, "--version")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            var p = System.Diagnostics.Process.Start(psi);
            p?.WaitForExit();
            return p?.ExitCode == 0 ? exe : null;
        }
        catch { return null; }
    }
}
