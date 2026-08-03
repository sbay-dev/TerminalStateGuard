using System.Diagnostics;
using TSG.Platform;

namespace TSG;

/// <summary>
/// Resume a copilot session in a new Windows Terminal tab.
/// Reads the session's CWD from <c>~/.copilot/session-state/&lt;id&gt;/workspace.yaml</c>
/// and launches <c>wt new-tab -d &lt;cwd&gt; pwsh -NoExit -Command "copilot --resume=&lt;id&gt;"</c>.
/// Also handles the <c>tsg://resume/&lt;id&gt;</c> URL scheme registered during install.
/// </summary>
public static class SessionResume
{
    public static Task<int> RunAsync(IPlatformHost host, string[] args)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            Console.WriteLine("  Usage: tsg resume <sessionId>");
            Console.WriteLine("         tsg resume tsg://resume/<sessionId>");
            return Task.FromResult(1);
        }

        var sessionId = ExtractSessionId(args[0]);
        if (string.IsNullOrEmpty(sessionId))
        {
            Console.WriteLine($"  ❌ Invalid session ID: {args[0]}");
            return Task.FromResult(1);
        }

        var cwd = GetSessionCwd(sessionId);
        var shell = FindPwsh();
        var startDir = !string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)
            ? cwd
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var command = $"Set-Location '{startDir}'; copilot --resume={sessionId}";
        var wtArgs = $"new-tab -d \"{startDir}\" \"{shell}\" -NoExit -Command \"{command}\"";

        try
        {
            var psi = new ProcessStartInfo("wt", wtArgs) { UseShellExecute = false };
            Process.Start(psi);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  🚀 Resuming session {sessionId[..Math.Min(8, sessionId.Length)]}…");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"     📂 {startDir}");
            Console.ResetColor();
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ❌ Failed to open new tab: {ex.Message}");
            return Task.FromResult(1);
        }
    }

    /// <summary>Extract the session ID from either a bare GUID or a <c>tsg://resume/&lt;id&gt;</c> URL.</summary>
    static string ExtractSessionId(string input)
    {
        var s = input.Trim().Trim('/');
        const string prefix = "tsg://resume/";
        if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            s = s[prefix.Length..].Trim('/');
        // Accept just the raw id if it looks like a GUID or hex string
        return Guid.TryParse(s, out var g) ? g.ToString("D") : s;
    }

    static string? GetSessionCwd(string sessionId)
    {
        var ws = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot", "session-state", sessionId, "workspace.yaml");
        if (!File.Exists(ws)) return null;
        try
        {
            foreach (var line in File.ReadLines(ws))
            {
                var t = line.TrimStart();
                if (t.StartsWith("cwd: ", StringComparison.Ordinal))
                    return t["cwd: ".Length..].Trim();
            }
        }
        catch (IOException) { }
        return null;
    }

    static string FindPwsh()
    {
        var paths = new[]
        {
            @"C:\Program Files\PowerShell\7\pwsh.exe",
            @"C:\Program Files (x86)\PowerShell\7\pwsh.exe",
        };
        return paths.FirstOrDefault(File.Exists) ?? "pwsh.exe";
    }
}
