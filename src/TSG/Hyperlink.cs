namespace TSG;

/// <summary>
/// Emit OSC 8 hyperlinks that Windows Terminal recognizes.
/// Clicking a hyperlink in a supporting terminal opens the URL in the shell's
/// default handler. Windows Terminal can reject custom OSC 8 URI schemes, so
/// session links point at a local <c>file://</c> command launcher under
/// <c>~/.tsg/session-links/</c>. The launcher invokes
/// <c>tsg resume &lt;id&gt;</c>.
/// </summary>
public static class Hyperlink
{
    const string Esc = "\x1b";
    const string Osc8 = Esc + "]8;;";
    const string St = Esc + "\\";

    /// <summary>True when the current process appears to run inside Windows Terminal or another OSC 8-aware host.</summary>
    public static bool IsSupported =>
        !Console.IsOutputRedirected
        && (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"))
            || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_PROFILE_ID"))
            || string.Equals(Environment.GetEnvironmentVariable("TERM_PROGRAM"), "vscode", StringComparison.OrdinalIgnoreCase));

    /// <summary>Wrap <paramref name="text"/> in an OSC 8 hyperlink pointing at <paramref name="url"/>.</summary>
    public static string Wrap(Uri url, string text)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (!IsSupported) return text;
        return $"{Osc8}{url.AbsoluteUri}{St}{text}{Osc8}{St}";
    }

    /// <summary>Create a local file launcher URL that reopens a copilot session in a new tab.</summary>
    public static Uri? CreateResumeLauncherUrl(string sessionId)
    {
        if (!Guid.TryParse(sessionId, out var sessionGuid))
            return null;

        var tsgPath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(tsgPath) || !File.Exists(tsgPath))
            return null;

        var linksDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".tsg", "session-links");
        var launcherPath = Path.Combine(linksDir, $"resume-{sessionGuid:D}.cmd");
        var callerShell = ShellContext.Resolve();
        var launcher = string.Join("\r\n",
            "@echo off",
            $"\"{tsgPath}\" resume \"{sessionGuid:D}\" --shell \"{callerShell.Executable}\"",
            "exit /b %errorlevel%",
            "");

        try
        {
            Directory.CreateDirectory(linksDir);
            if (!File.Exists(launcherPath)
                || !File.ReadAllText(launcherPath).Equals(launcher, StringComparison.Ordinal))
            {
                File.WriteAllText(launcherPath, launcher, System.Text.Encoding.ASCII);
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        return new Uri(launcherPath);
    }

    /// <summary>Write a clickable session ID (short prefix) to the console.</summary>
    public static void WriteSessionId(string sessionId, ConsoleColor color = ConsoleColor.DarkYellow)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        var shortId = sessionId.Length > 8 ? sessionId[..8] : sessionId;
        var launcherUrl = CreateResumeLauncherUrl(sessionId);
        if (launcherUrl != null && IsSupported)
        {
            Console.Write("🔗 ");
            Console.Write(Wrap(launcherUrl, $"[{shortId}]"));
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write(" Ctrl+click");
        }
        else
        {
            Console.Write($"[{shortId}]");
        }
        Console.ForegroundColor = prev;
    }
}
