namespace TSG;

/// <summary>
/// Emit OSC 8 hyperlinks that Windows Terminal recognizes.
/// Clicking a hyperlink in a supporting terminal opens the URL in the shell's
/// default handler. For copilot sessions, we point at <c>tsg://resume/{id}</c>
/// which the installer registers as a URL protocol handler that runs
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
    public static string Wrap(string url, string text)
    {
        if (!IsSupported || string.IsNullOrEmpty(url)) return text;
        return $"{Osc8}{url}{St}{text}{Osc8}{St}";
    }

    /// <summary>Build a <c>tsg://resume/{sessionId}</c> URL to reopen a copilot session in a new tab.</summary>
    public static string ResumeUrl(string sessionId) => $"tsg://resume/{sessionId}";

    /// <summary>Write a clickable session ID (short prefix) to the console.</summary>
    public static void WriteSessionId(string sessionId, ConsoleColor color = ConsoleColor.DarkYellow)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        var shortId = sessionId.Length > 8 ? sessionId[..8] : sessionId;
        Console.Write(Wrap(ResumeUrl(sessionId), $"[{shortId}]"));
        Console.ForegroundColor = prev;
    }
}
