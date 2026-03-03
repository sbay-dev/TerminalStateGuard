namespace TSG.Platform;

/// <summary>
/// Cross-platform abstraction for OS-specific operations.
/// </summary>
public interface IPlatformHost
{
    string HomeDir { get; }
    string TsgDir { get; }
    string CopilotSessionDir { get; }
    string ShellProfilePath { get; }
    string ShellName { get; }

    string? FindShell();
    string GetScriptExtension();
    string GetScriptPrefix(string scriptPath);
    Task InstallTerminalIntegrationAsync();
    Task RemoveTerminalIntegrationAsync();
    string? GetTerminalStateJson();
}

/// <summary>
/// Factory to detect and create the right platform host.
/// </summary>
public static class PlatformHost
{
    public static IPlatformHost Detect() => OperatingSystem.IsWindows()
        ? new WindowsHost()
        : new LinuxHost();
}
