using System.Diagnostics;
using System.Runtime.InteropServices;

namespace TSG;

/// <summary>Detect and describe the shell process that invoked TSG.</summary>
public sealed record ShellContext(string Executable, ShellKind Kind)
{
    static readonly string[] PowerShellCorePaths =
    [
        @"C:\Program Files\PowerShell\7\pwsh.exe",
        @"C:\Program Files (x86)\PowerShell\7\pwsh.exe",
    ];

    public static ShellContext Resolve(string? explicitShell = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitShell) && File.Exists(explicitShell))
            return FromExecutable(explicitShell);

        var inherited = Environment.GetEnvironmentVariable("TSG_CALLER_SHELL");
        if (!string.IsNullOrWhiteSpace(inherited) && File.Exists(inherited))
            return FromExecutable(inherited);

        if (OperatingSystem.IsWindows())
        {
            var parentPath = GetParentExecutablePath();
            if (!string.IsNullOrEmpty(parentPath))
            {
                var parent = FromExecutable(parentPath);
                if (parent.Kind != ShellKind.Unknown)
                    return parent;
            }
        }

        var pwsh = PowerShellCorePaths.FirstOrDefault(File.Exists);

        if (pwsh != null)
            return new ShellContext(pwsh, ShellKind.PowerShellCore);

        var comSpec = Environment.GetEnvironmentVariable("COMSPEC");
        return !string.IsNullOrEmpty(comSpec) && File.Exists(comSpec)
            ? new ShellContext(comSpec, ShellKind.CommandPrompt)
            : new ShellContext("pwsh.exe", ShellKind.PowerShellCore);
    }

    public static ShellContext FromExecutable(string executable)
    {
        var name = Path.GetFileNameWithoutExtension(executable);
        var kind = name.ToLowerInvariant() switch
        {
            "pwsh" => ShellKind.PowerShellCore,
            "powershell" => ShellKind.WindowsPowerShell,
            "cmd" => ShellKind.CommandPrompt,
            _ => ShellKind.Unknown
        };
        return new ShellContext(executable, kind);
    }

    public IReadOnlyList<string> BuildResumeArguments(string sessionId)
    {
        ArgumentNullException.ThrowIfNull(sessionId);

        return Kind switch
        {
            ShellKind.CommandPrompt =>
            [
                "/K",
                $"copilot --resume={sessionId}"
            ],
            ShellKind.PowerShellCore or ShellKind.WindowsPowerShell =>
            [
                "-NoExit",
                "-Command",
                $"copilot --resume={sessionId}"
            ],
            _ => []
        };
    }

    static string? GetParentExecutablePath()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            var info = new ProcessBasicInformation();
            var status = NtQueryInformationProcess(
                current.Handle,
                0,
                ref info,
                Marshal.SizeOf<ProcessBasicInformation>(),
                out _);
            if (status != 0 || info.InheritedFromUniqueProcessId == IntPtr.Zero)
                return null;

            using var parent = Process.GetProcessById(info.InheritedFromUniqueProcessId.ToInt32());
            return parent.MainModule?.FileName;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(
        IntPtr processHandle,
        int processInformationClass,
        ref ProcessBasicInformation processInformation,
        int processInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessBasicInformation
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }
}

public enum ShellKind
{
    Unknown,
    PowerShellCore,
    WindowsPowerShell,
    CommandPrompt
}
