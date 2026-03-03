namespace TSG.Platform;

/// <summary>
/// Linux/macOS host with bash/zsh integration.
/// </summary>
public class LinuxHost : IPlatformHost
{
    public string HomeDir { get; } = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    public string TsgDir => Path.Combine(HomeDir, ".tsg");
    public string CopilotSessionDir => Path.Combine(HomeDir, ".copilot", "session-state");
    public string ShellName => DetectShell();

    public string ShellProfilePath => ShellName switch
    {
        "zsh" => Path.Combine(HomeDir, ".zshrc"),
        "fish" => Path.Combine(HomeDir, ".config", "fish", "config.fish"),
        _ => Path.Combine(HomeDir, ".bashrc")
    };

    public string? FindShell()
    {
        string[] shells = ["/usr/bin/bash", "/bin/bash", "/usr/bin/zsh", "/bin/zsh"];
        return shells.FirstOrDefault(File.Exists) ?? "bash";
    }

    public string GetScriptExtension() => ".sh";
    public string GetScriptPrefix(string scriptPath) => $"bash '{scriptPath}'";

    public async Task InstallTerminalIntegrationAsync()
    {
        // Add shell aliases for quick access
        var profilePath = ShellProfilePath;
        var existing = File.Exists(profilePath) ? await File.ReadAllTextAsync(profilePath) : "";

        if (existing.Contains("# TSG START"))
        {
            Console.WriteLine("  ✅ Shell integration already installed");
            return;
        }

        var block = """

            # TSG START — Terminal State Guard
            alias tsg-boost='tsg boost'
            alias tsg-monitor='tsg monitor'
            alias tsg-status='tsg status'
            alias tsg-recover='tsg recover'
            # Keyboard shortcuts (bind if supported)
            if [ -n "$BASH_VERSION" ]; then
                bind -x '"\C-\eb":"tsg boost"' 2>/dev/null
                bind -x '"\C-\em":"tsg monitor"' 2>/dev/null
                bind -x '"\C-\es":"tsg status"' 2>/dev/null
            fi
            # TSG END
            """;

        await File.AppendAllTextAsync(profilePath, block);
        Console.WriteLine($"  ✅ Shell aliases added to {profilePath}");
    }

    public async Task RemoveTerminalIntegrationAsync()
    {
        var profilePath = ShellProfilePath;
        if (!File.Exists(profilePath)) return;

        var content = await File.ReadAllTextAsync(profilePath);
        var start = content.IndexOf("# TSG START");
        var end = content.IndexOf("# TSG END");
        if (start >= 0 && end >= 0)
        {
            content = content[..start] + content[(end + "# TSG END".Length)..];
            await File.WriteAllTextAsync(profilePath, content.Trim() + "\n");
            Console.WriteLine("  ✅ Shell aliases removed");
        }
    }

    public string? GetTerminalStateJson() => null; // No equivalent on Linux

    static string DetectShell()
    {
        var shell = Environment.GetEnvironmentVariable("SHELL") ?? "/bin/bash";
        return Path.GetFileName(shell);
    }
}
