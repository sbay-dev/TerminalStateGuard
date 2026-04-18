using TSG.Platform;

namespace TSG;

/// <summary>
/// Read-only SQL query interface: tsg db "SELECT * FROM windows"
/// </summary>
public static class DbQuery
{
    public static Task<int> RunAsync(IPlatformHost host, string[] args)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0 || args[0].Equals("--help", StringComparison.OrdinalIgnoreCase))
        {
            PrintHelp();
            return Task.FromResult(0);
        }

        var sql = string.Join(' ', args);

        // Safety: only allow read-only queries
        var upper = sql.TrimStart().ToUpperInvariant();
        if (!upper.StartsWith("SELECT", StringComparison.Ordinal) &&
            !upper.StartsWith("PRAGMA", StringComparison.Ordinal) &&
            !upper.StartsWith("EXPLAIN", StringComparison.Ordinal) &&
            !upper.StartsWith("WITH", StringComparison.Ordinal))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  ❌ Only SELECT/PRAGMA/EXPLAIN/WITH queries allowed (read-only).");
            Console.ResetColor();
            return Task.FromResult(1);
        }

        var dbPath = Path.Combine(host.TsgDir, "terminal.db");
        if (!File.Exists(dbPath))
        {
            Console.WriteLine("  ❌ No database found. Run 'tsg capture' first.");
            return Task.FromResult(1);
        }

        try
        {
            using var db = new TerminalDatabase(host.TsgDir);
            var result = db.ExecuteQuery(sql);
            Console.WriteLine();
            Console.WriteLine(result);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  ❌ SQL Error: {ex.Message}");
            Console.ResetColor();
            return Task.FromResult(1);
        }

        return Task.FromResult(0);
    }

    static void PrintHelp()
    {
        Console.WriteLine("""

          📊 TSG Database Query (read-only)

          USAGE:
            tsg db "SELECT * FROM windows"
            tsg db "SELECT * FROM captures ORDER BY id DESC LIMIT 10"
            tsg db "SELECT * FROM events ORDER BY id DESC LIMIT 20"

          TABLES:
            windows          — Window identity tracking (id, first_seen, last_seen, closed_at)
            captures         — State capture history (timestamp, quality, live/state counts)
            capture_windows  — Windows per capture (tab_count, copilot_count, is_live)
            capture_tabs     — Tabs per capture (path, title, tab_type, copilot info)
            events           — State change events (window_opened, window_closed, etc.)

          QUALITY FLAGS:
            verified   — UI Automation confirms window count matches state.json
            trimmed    — Stale windows removed based on live count
            stale      — state.json is >5 minutes old
            suspect    — Live count doesn't match captured count
            no-uia     — UI Automation not available

        """);
    }
}
