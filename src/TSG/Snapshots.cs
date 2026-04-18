using System.Text.Json;
using TSG.Platform;

namespace TSG;

/// <summary>
/// Lists all saved terminal snapshots (READ-ONLY).
/// </summary>
public static class Snapshots
{
    public static async Task<int> RunAsync(IPlatformHost host, string[] args)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(args);

        var snapDir = Path.Combine(host.HomeDir, ".copilotAccel", "terminal-snapshots");
        if (!Directory.Exists(snapDir))
        {
            Console.WriteLine("  ❌ No snapshots found. Run 'tsg install' first.");
            return 1;
        }

        var all = args.Length > 0 && args[0].Equals("--all", StringComparison.OrdinalIgnoreCase);
        var files = Directory.GetFiles(snapDir, "202*.json")
            .OrderByDescending(f => f)
            .ToArray();

        if (files.Length == 0)
        {
            Console.WriteLine("  ℹ️  No snapshots saved yet.");
            return 0;
        }

        // Read config for max display limit (--all overrides)
        var maxDisplay = all ? files.Length : await GetMaxSnapshotsAsync(host);

        Console.WriteLine($"\n  📸 Terminal Snapshots ({files.Length} total)\n");

        if (!all && files.Length > maxDisplay)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  Showing {maxDisplay}/{files.Length} — use 'tsg snapshots --all' to show all\n");
            Console.ResetColor();
        }

        var shown = 0;
        foreach (var file in files)
        {
            if (!all && shown >= maxDisplay) break;
            shown++;

            try
            {
                var json = await File.ReadAllTextAsync(file);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var timestamp = root.TryGetProperty("Timestamp", out var ts) ? ts.GetString() : null;
                var totalTabs = root.TryGetProperty("TotalTabs", out var tt) ? tt.GetInt32() : 0;
                var windowCount = root.TryGetProperty("WindowCount", out var wc) ? wc.GetInt32() : 0;
                var copilotCount = root.TryGetProperty("CopilotCount", out var cc) ? cc.GetInt32() : 0;

                var fileInfo = new FileInfo(file);
                var created = fileInfo.CreationTime;
                var modified = fileInfo.LastWriteTime;
                var age = FormatAge(DateTime.Now - modified);

                var displayTime = timestamp ?? modified.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

                var copilotIcon = copilotCount > 0 ? $"🤖 {copilotCount}" : "  0";
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($"  [{shown,3}] ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"📅 {displayTime}");
                Console.ForegroundColor = ConsoleColor.DarkYellow;
                Console.Write($"  ⏱️ {age}");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"  📺 {windowCount} win  📑 {totalTabs} tabs  {copilotIcon}");
                Console.ResetColor();
                Console.WriteLine();

                // Show tab details with --all or if few snapshots
                if (all && root.TryGetProperty("Tabs", out var tabs))
                {
                    foreach (var tab in tabs.EnumerateArray())
                    {
                        var path = tab.TryGetProperty("Path", out var p) ? p.GetString() : "?";
                        var hasCopilot = tab.TryGetProperty("HasCopilot", out var hc) && hc.GetBoolean();
                        var summary = tab.TryGetProperty("Summary", out var sm) ? sm.GetString() : null;
                        var icon = hasCopilot ? "🤖" : "📂";
                        var folder = Path.GetFileName(path);

                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.WriteLine($"        {icon} {folder}");
                        if (!string.IsNullOrEmpty(summary))
                        {
                            Console.ForegroundColor = ConsoleColor.DarkCyan;
                            Console.WriteLine($"           💬 {summary}");
                        }
                        Console.ResetColor();
                    }
                }
            }
            catch (JsonException)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  [{shown,3}] ⚠️  {Path.GetFileName(file)} — corrupt");
                Console.ResetColor();
            }
        }

        // Latest snapshot summary
        var latestPath = Path.Combine(snapDir, "latest.json");
        if (File.Exists(latestPath))
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n  ✅ latest.json → active snapshot");
            Console.ResetColor();
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"  📁 {snapDir}\n");
        Console.ResetColor();

        return 0;
    }

    static async Task<int> GetMaxSnapshotsAsync(IPlatformHost host)
    {
        var envVal = Environment.GetEnvironmentVariable("TSG_MAX_SNAPSHOTS");
        if (envVal is not null && int.TryParse(envVal, out var envMax))
            return envMax;

        var configPath = Path.Combine(host.TsgDir, "tsg-config.json");
        if (File.Exists(configPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
                if (doc.RootElement.TryGetProperty("MaxSnapshots", out var ms))
                    return ms.GetInt32();
            }
            catch (JsonException) { /* use default */ }
        }

        return 50;
    }

    static string FormatAge(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes}m ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours}h {span.Minutes}m ago";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays}d ago";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)}mo ago";
        return $"{(int)(span.TotalDays / 365)}y ago";
    }
}
