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
    /// Install Windows Terminal Fragment with all TSG profiles and shortcuts.
    /// </summary>
    public async Task InstallTerminalIntegrationAsync()
    {
        Directory.CreateDirectory(FragmentDir);

        var tsgDir = TsgDir.Replace(@"\", @"\\", StringComparison.Ordinal);
        var pwsh = (FindShell() ?? @"C:\Program Files\PowerShell\7\pwsh.exe")
            .Replace(@"\", @"\\", StringComparison.Ordinal);

        var fragment = $$"""
        {
            "profiles": [
                {
                    "name": "\u26a1 TSG Boost [Admin]",
                    "commandline": "{{pwsh}} -NoProfile -ExecutionPolicy Bypass -Command \"Start-Process '{{pwsh}}' -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \\\"{{tsgDir}}\\CopilotBoost.ps1\\\" -Mode Boost'\"",
                    "startingDirectory": "%USERPROFILE%"
                },
                {
                    "name": "\ud83d\udcca TSG Monitor",
                    "commandline": "{{pwsh}} -NoProfile -ExecutionPolicy Bypass -File \"{{tsgDir}}\\CopilotBoost.ps1\" -Mode Monitor",
                    "startingDirectory": "%USERPROFILE%"
                },
                {
                    "name": "\ud83d\udccb TSG Status",
                    "commandline": "{{pwsh}} -NoProfile -ExecutionPolicy Bypass -File \"{{tsgDir}}\\CopilotBoost.ps1\" -Mode Status",
                    "startingDirectory": "%USERPROFILE%"
                },
                {
                    "name": "\ud83d\udd04 TSG Recover",
                    "commandline": "{{pwsh}} -NoProfile -ExecutionPolicy Bypass -File \"{{tsgDir}}\\RecoverSessions.ps1\"",
                    "startingDirectory": "%USERPROFILE%"
                },
                {
                    "name": "\ud83d\udd04 TSG Restore [Admin]",
                    "commandline": "{{pwsh}} -NoProfile -ExecutionPolicy Bypass -Command \"Start-Process '{{pwsh}}' -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \\\"{{tsgDir}}\\CopilotBoost.ps1\\\" -Mode Restore'\"",
                    "startingDirectory": "%USERPROFILE%"
                },
                {
                    "name": "\ud83c\udfaf TSG Focus [Admin]",
                    "commandline": "{{pwsh}} -NoProfile -ExecutionPolicy Bypass -File \"{{tsgDir}}\\Focus.ps1\"",
                    "startingDirectory": "%USERPROFILE%"
                },
                {
                    "name": "\ud83e\ude7a TSG Doctor",
                    "commandline": "{{pwsh}} -NoProfile -ExecutionPolicy Bypass -Command \"tsg doctor; Read-Host 'Press Enter to close'\"",
                    "startingDirectory": "%USERPROFILE%"
                }
            ],
            "actions": [
                {"keys":"ctrl+alt+b","command":{"action":"sendInput","input":"tsg boost\r\n"},"name":"TSG: Boost"},
                {"keys":"ctrl+alt+m","command":{"action":"sendInput","input":"tsg monitor\r\n"},"name":"TSG: Monitor"},
                {"keys":"ctrl+alt+s","command":{"action":"sendInput","input":"tsg status\r\n"},"name":"TSG: Status"},
                {"keys":"ctrl+alt+f","command":{"action":"sendInput","input":"tsg recover\r\n"},"name":"TSG: Recover"},
                {"keys":"ctrl+alt+r","command":{"action":"sendInput","input":"tsg restore\r\n"},"name":"TSG: Restore"},
                {"keys":"ctrl+alt+g","command":{"action":"sendInput","input":"tsg focus\r\n"},"name":"TSG: Focus"}
            ]
        }
        """;

        await File.WriteAllTextAsync(Path.Combine(FragmentDir, "tsg.json"), fragment);
        Console.WriteLine($"  \u2705 Windows Terminal Fragment installed: {FragmentDir}");
        Console.WriteLine("     All TSG profiles added to terminal dropdown.");
    }

    public async Task RemoveTerminalIntegrationAsync()
    {
        if (Directory.Exists(FragmentDir))
        {
            Directory.Delete(FragmentDir, true);
            Console.WriteLine("  \u2705 Windows Terminal Fragment removed");
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
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException)
        { return null; }
    }
}
