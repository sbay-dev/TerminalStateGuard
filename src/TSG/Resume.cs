using System.Diagnostics;
using TSG.Platform;

namespace TSG;

/// <summary>
/// Resume a copilot session in a new Windows Terminal tab.
/// Reads the session's CWD from <c>~/.copilot/session-state/&lt;id&gt;/workspace.yaml</c>
/// and launches a new tab in the active Windows Terminal window using the same
/// shell application that invoked TSG.
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
        if (sessionId == null)
        {
            Console.WriteLine($"  ❌ Invalid session ID: {args[0]}");
            return Task.FromResult(1);
        }

        var explicitShell = GetOption(args, "--shell");
        var shell = ShellContext.Resolve(explicitShell);
        var cwd = GetSessionCwd(sessionId);
        var startDir = !string.IsNullOrEmpty(cwd) && Directory.Exists(cwd)
            ? cwd
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        try
        {
            var psi = new ProcessStartInfo("wt") { UseShellExecute = false };
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add("0");
            psi.ArgumentList.Add("new-tab");
            psi.ArgumentList.Add("-d");
            psi.ArgumentList.Add(startDir);
            psi.ArgumentList.Add(shell.Executable);
            foreach (var argument in shell.BuildResumeArguments(sessionId))
                psi.ArgumentList.Add(argument);
            Process.Start(psi);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  🚀 Resuming session {sessionId[..Math.Min(8, sessionId.Length)]}…");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"     📂 {startDir}");
            Console.WriteLine($"     🖥️ {Path.GetFileName(shell.Executable)} · current Terminal window");
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
    static string? ExtractSessionId(string input)
    {
        var s = input.Trim().Trim('/');
        const string prefix = "tsg://resume/";
        if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            s = s[prefix.Length..].Trim('/');
        return Guid.TryParse(s, out var g) ? g.ToString("D") : null;
    }

    static string? GetOption(string[] args, string name)
    {
        for (var i = 1; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return null;
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

}
