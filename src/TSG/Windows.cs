using System.Globalization;
using System.Text.Json;
using TSG.Platform;

namespace TSG;

/// <summary>
/// Window-centric terminal state viewer and restorer.
/// Primary source: SQLite (terminal.db), fallback: JSON files.
/// </summary>
public static class Windows
{
    public static async Task<int> RunAsync(IPlatformHost host, string[] args)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(args);

        var windowsDir = Path.Combine(host.TsgDir, "windows");
        var activePath = Path.Combine(windowsDir, "active-windows.json");

        if (args.Length > 0 && args[0].Equals("--restore", StringComparison.OrdinalIgnoreCase))
            return await RestoreWindowsAsync(host, windowsDir, activePath);

        if (args.Length > 0 && args[0].Equals("--history", StringComparison.OrdinalIgnoreCase))
            return ShowDbHistory(host);

        var interactive = args.Any(a => a.Equals("--interactive", StringComparison.OrdinalIgnoreCase)
                                     || a.Equals("-i", StringComparison.OrdinalIgnoreCase));

        // Capture fresh state to SQLite
        var captureData = await StateCapture.CaptureStateAsync(host);
        if (captureData != null)
        {
            using var db = new TerminalDatabase(host.TsgDir);
            db.SaveCapture(captureData);
        }

        if (interactive)
            return await InteractiveLoop(host, windowsDir, activePath, captureData);

        return ShowFromDb(host, captureData);
    }

    static int ShowFromDb(IPlatformHost host, CaptureData? data)
    {
        if (data == null || data.Windows.Count == 0)
        {
            Console.WriteLine("  ❌ No terminal state captured. Run 'tsg capture' first.");
            return 1;
        }

        var liveWins = data.Windows.Where(w => w.IsLive).ToList();
        var closedWins = data.Windows.Where(w => !w.IsLive).ToList();

        Console.WriteLine($"\n  🪟 Terminal Windows — {liveWins.Count} active, {closedWins.Count} recently closed");
        Console.Write($"  📅 {data.CapturedAt}  📑 {data.TotalTabs} tabs  🤖 {data.CopilotCount}");

        // Quality indicator
        Console.ForegroundColor = data.Quality switch
        {
            "live" => ConsoleColor.Cyan,
            "verified" => ConsoleColor.Green,
            "trimmed" => ConsoleColor.Yellow,
            "stale" => ConsoleColor.Red,
            "suspect" => ConsoleColor.Red,
            _ => ConsoleColor.DarkGray
        };
        Console.Write($"  [{data.Quality}]");
        if (data.LiveWindowCount.HasValue)
            Console.Write($"  👁️ UIA:{data.LiveWindowCount}");
        Console.ResetColor();
        Console.WriteLine("\n");

        if (liveWins.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  ── Active Windows ──");
            Console.ResetColor();
            PrintCaptureWindows(liveWins, isActive: true);
        }

        if (closedWins.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("\n  ── Recently Closed ──");
            Console.ResetColor();
            PrintCaptureWindows(closedWins, isActive: false);
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"\n  📁 DB: {Path.Combine(host.TsgDir, "terminal.db")}");
        Console.ResetColor();

        // Quick action hints
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("\n  💡 tsg windows --restore   Restore closed windows");
        Console.WriteLine("  💡 tsg windows --history   Browse capture history");
        Console.WriteLine("  💡 tsg capture             Take a snapshot now");
        Console.WriteLine("  💡 tsg processes           View dev processes & ports");
        Console.ResetColor();

        Console.WriteLine();
        return 0;
    }

    /// <summary>Interactive dashboard loop for TSG Windows terminal tab.</summary>
    static async Task<int> InteractiveLoop(IPlatformHost host, string windowsDir, string activePath, CaptureData? initialData)
    {
        var data = initialData;
        while (true)
        {
            Console.Clear();
            ShowFromDb(host, data);

            // Interactive menu
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("  📋 Actions:");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("    [R] ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("🔄 Restore closed windows");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("    [S] ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("📸 Take snapshot now");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("    [H] ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("📜 Capture history");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("    [P] ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("🔧 Dev processes & ports");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("    [F] ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("🔃 Refresh");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("    [Q] ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("❌ Quit");

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("\n  Select: ");
            Console.ResetColor();

            var key = Console.ReadKey(intercept: true);
            Console.WriteLine();

            switch (char.ToUpperInvariant(key.KeyChar))
            {
                case 'R':
                    Console.Clear();
                    await RestoreWindowsAsync(host, windowsDir, activePath);
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("\n  Press any key to continue...");
                    Console.ResetColor();
                    Console.ReadKey(intercept: true);
                    break;

                case 'S':
                    Console.Clear();
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine("\n  📸 Capturing snapshot...");
                    Console.ResetColor();
                    data = await StateCapture.CaptureStateAsync(host);
                    if (data != null)
                    {
                        using var db = new TerminalDatabase(host.TsgDir);
                        var captureId = db.SaveCapture(data);
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"  ✅ Snapshot #{captureId} saved!");
                        Console.ResetColor();
                    }
                    await Task.Delay(1000);
                    break;

                case 'H':
                    Console.Clear();
                    ShowDbHistory(host);
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("\n  Press any key to continue...");
                    Console.ResetColor();
                    Console.ReadKey(intercept: true);
                    break;

                case 'P':
                    Console.Clear();
                    await ProcessManager.RunAsync(host, []);
                    Console.ForegroundColor = ConsoleColor.DarkGray;
                    Console.Write("\n  Press any key to continue...");
                    Console.ResetColor();
                    Console.ReadKey(intercept: true);
                    break;

                case 'F':
                    Console.Clear();
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine("\n  🔃 Refreshing...");
                    Console.ResetColor();
                    data = await StateCapture.CaptureStateAsync(host);
                    if (data != null)
                    {
                        using var db = new TerminalDatabase(host.TsgDir);
                        db.SaveCapture(data);
                    }
                    break;

                case 'Q':
                case '\u001b': // ESC
                    return 0;
            }
        }
    }

    static void PrintCaptureWindows(List<CaptureWindow> windows, bool isActive)
    {
        var wi = 0;
        foreach (var w in windows)
        {
            wi++;
            var status = isActive ? "🟢" : "🔴";
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"  {status} Window {wi}");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"  [{w.Id}]");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"  📑 {w.Tabs.Count} tabs");
            if (w.CopilotCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.Write($"  🤖 {w.CopilotCount}");
            }
            Console.ResetColor();
            Console.WriteLine();

            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"     📅 Opened: {w.OpenedAt}");
            if (w.ClosedAt != null)
                Console.Write($"  ❌ Closed: {w.ClosedAt}");
            else
                Console.Write($"  👁️ Last seen: {w.LastSeen}");
            Console.ResetColor();
            Console.WriteLine();

            foreach (var tab in w.Tabs)
            {
                PrintCaptureTab(tab, "     ");
                foreach (var pane in tab.Panes)
                    PrintCaptureTab(pane, "       ├─");
            }
        }
    }

    static void PrintCaptureTab(CaptureTab tab, string indent)
    {
        var icon = tab.HasCopilot ? "🤖" : tab.TabType switch
        {
            "cmd" => "⬛",
            "wsl" => "🐧",
            "copilot" => "🤖",
            _ => "📂"
        };

        string label;
        if (tab.IsLiveDetected)
        {
            // Live-detected: use the real tab title from UI Automation
            label = tab.Title;
        }
        else
        {
            var typeLabel = !string.IsNullOrEmpty(tab.Title) && tab.Title != "PowerShell7" && tab.Title != "Default"
                ? $" [{tab.Title}]" : tab.TabType == "cmd" ? " [CMD]" : "";
            label = $"{tab.Folder}{typeLabel}";
        }

        var existsMark = !tab.IsLiveDetected && !tab.DirExists ? " ❌" : "";

        Console.ForegroundColor = tab.IsLiveDetected ? ConsoleColor.White : ConsoleColor.DarkGray;
        Console.Write($"{indent}{icon} {label}{existsMark}");
        if (!string.IsNullOrEmpty(tab.CopilotSummary))
        {
            Console.ForegroundColor = ConsoleColor.DarkCyan;
            Console.Write($"  💬 {tab.CopilotSummary}");
        }
        Console.ResetColor();
        Console.WriteLine();
    }

    static int ShowDbHistory(IPlatformHost host)
    {
        var dbPath = Path.Combine(host.TsgDir, "terminal.db");
        if (!File.Exists(dbPath))
        {
            Console.WriteLine("  ℹ️  No database history yet. Run 'tsg capture' first.");
            return 0;
        }

        using var db = new TerminalDatabase(host.TsgDir);
        var captures = db.GetCaptureHistory(50);

        if (captures.Count == 0)
        {
            Console.WriteLine("  ℹ️  No captures recorded yet.");
            return 0;
        }

        Console.WriteLine($"\n  📜 Capture History ({captures.Count} entries)\n");

        foreach (var c in captures)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"  #{c.Id,-4}");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($" 📅 {c.CapturedAt}");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"  📺 {c.LiveWindowCount?.ToString(CultureInfo.InvariantCulture) ?? "?"}/{c.StateWindowCount} win");
            Console.Write($"  📑 {c.TotalTabs} tabs");
            if (c.CopilotCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.Write($"  🤖 {c.CopilotCount}");
            }
            Console.ForegroundColor = c.Quality switch
            {
                "live" => ConsoleColor.Cyan,
                "verified" => ConsoleColor.Green,
                "trimmed" => ConsoleColor.Yellow,
                _ => ConsoleColor.Red
            };
            Console.Write($"  [{c.Quality}]");
            Console.ResetColor();
            Console.WriteLine();
        }

        // Show recent events
        var events = db.GetRecentEvents(20);
        if (events.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"\n  ── Recent Events ({events.Count}) ──");
            Console.ResetColor();
            foreach (var e in events)
            {
                var icon = e.EventType switch
                {
                    "window_opened" => "🟢",
                    "window_closed" => "🔴",
                    _ => "📝"
                };
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"  {icon} {e.Timestamp}  {e.EventType}");
                if (e.WindowId != null)
                    Console.Write($"  [{e.WindowId}]");
                if (e.Details != null)
                    Console.Write($"  {e.Details}");
                Console.ResetColor();
                Console.WriteLine();
            }
        }

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine($"\n  📁 {dbPath}\n");
        Console.ResetColor();
        return 0;
    }

    static async Task<int> RestoreWindowsAsync(IPlatformHost host, string windowsDir, string activePath)
    {
        var windows = new List<WindowInfo>();
        var dbPath = Path.Combine(host.TsgDir, "terminal.db");

        if (File.Exists(dbPath))
        {
            // Fresh capture
            var data = await StateCapture.CaptureStateAsync(host);
            if (data != null)
            {
                using var db = new TerminalDatabase(host.TsgDir);
                db.SaveCapture(data);
            }

            using var db2 = new TerminalDatabase(host.TsgDir);
            var latest = db2.GetLatestCapture();

            if (latest != null)
            {
                // Add active windows from latest capture
                foreach (var w in latest.Windows.Where(w => w.IsLive))
                {
                    windows.Add(CaptureWindowToWindowInfo(w));
                }
            }

            // Add closed windows WITH their last known tabs from DB
            var closedWindows = db2.GetClosedWindowsWithTabs(20);
            foreach (var cw in closedWindows)
            {
                var tabs = cw.Tabs.Select(t => new TabInfo(
                    t.Path, t.Folder, t.Title, t.TabType, t.DirExists,
                    t.HasCopilot, t.CopilotSessionId, t.CopilotSummary,
                    t.Panes.Select(p => new TabInfo(
                        p.Path, p.Folder, p.Title, p.TabType, p.DirExists,
                        p.HasCopilot, p.CopilotSessionId, p.CopilotSummary, [])).ToList()
                )).ToList();
                var copilotCount = tabs.Count(t => t.HasCopilot) + tabs.Sum(t => t.Panes.Count(p => p.HasCopilot));
                windows.Add(new WindowInfo(cw.Id, cw.FirstSeen, cw.LastSeen, cw.ClosedAt, tabs, copilotCount));
            }
        }
        else if (File.Exists(activePath))
        {
            using var doc = JsonDocument.Parse(await File.ReadAllTextAsync(activePath));
            if (doc.RootElement.TryGetProperty("Windows", out var wArr))
            {
                foreach (var w in wArr.EnumerateArray())
                    AddWindowInfo(w, windows);
            }
        }

        // Deduplicate by Id — keep most recent, prefer one with more tabs
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<WindowInfo>();
        foreach (var w in windows.OrderByDescending(w => w.Tabs.Count).ThenByDescending(w => w.LastSeen))
        {
            if (seen.Add(w.Id))
                unique.Add(w);
        }

        // Re-sort: active first, then closed by recency
        unique = [.. unique.OrderBy(w => w.IsClosed).ThenByDescending(w => w.LastSeen)];

        if (unique.Count == 0)
        {
            Console.WriteLine("  ❌ No windows available for restore.");
            return 1;
        }

        var activeCount = unique.Count(w => !w.IsClosed);
        var closedCount = unique.Count(w => w.IsClosed);
        Console.WriteLine($"\n  🔄 Window Restore — {activeCount} active, {closedCount} closed\n");

        for (var i = 0; i < unique.Count; i++)
        {
            var w = unique[i];
            var status = w.IsClosed ? "🔴" : "🟢";
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"  [{i + 1}] {status}");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write($"  📑 {w.Tabs.Count} tabs");
            if (w.CopilotCount > 0)
            {
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.Write($"  🤖 {w.CopilotCount}");
            }
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"  📅 {w.OpenedAt}");
            if (w.IsClosed)
                Console.Write($" → ❌ {w.ClosedAt}");
            Console.ResetColor();
            Console.WriteLine();

            foreach (var tab in w.Tabs)
            {
                var icon = tab.HasCopilot ? "🤖" : tab.TabType == "cmd" ? "⬛" : "📂";
                // For live-detected tabs, show the full title
                var label = !string.IsNullOrEmpty(tab.Title) && tab.Title.Length > 1
                    ? tab.Title
                    : !string.IsNullOrEmpty(tab.Folder)
                        ? tab.Folder + (tab.TabType == "cmd" ? " [CMD]" : "")
                        : tab.TabType == "cmd" ? "[CMD]" : "PowerShell";
                var existsMark = !string.IsNullOrEmpty(tab.Path) && !tab.DirExists ? " ❌" : "";
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"      {icon} {label}{existsMark}");
                if (tab.Summary != null)
                {
                    Console.ForegroundColor = ConsoleColor.DarkCyan;
                    Console.Write($"  💬 {tab.Summary}");
                }
                Console.ResetColor();
                Console.WriteLine();
            }
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("\n  Select windows (comma-sep), A=all closed, Q=quit: ");
        Console.ResetColor();
        var input = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(input) || input.Equals("Q", StringComparison.OrdinalIgnoreCase))
            return 0;

        var indices = input.Equals("A", StringComparison.OrdinalIgnoreCase)
            ? unique.Select((_, idx) => idx + 1).Where(idx => unique[idx - 1].IsClosed)
            : input.Split(',').Select(s => int.TryParse(s.Trim(), out var v) ? v : 0);

        var shell = FindPwsh();
        foreach (var idx in indices)
        {
            if (idx < 1 || idx > unique.Count) continue;
            var w = unique[idx - 1];
            RestoreWindow(w, shell);
            await Task.Delay(1500);
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n  ✅ Restore complete!");
        Console.ResetColor();
        return 0;
    }

    static WindowInfo CaptureWindowToWindowInfo(CaptureWindow w)
    {
        var tabs = w.Tabs.Select(t => new TabInfo(
            t.Path, t.Folder, t.Title, t.TabType, t.DirExists,
            t.HasCopilot, t.CopilotSessionId, t.CopilotSummary,
            t.Panes.Select(p => new TabInfo(
                p.Path, p.Folder, p.Title, p.TabType, p.DirExists,
                p.HasCopilot, p.CopilotSessionId, p.CopilotSummary, [])).ToList()
        )).ToList();
        return new WindowInfo(w.Id, w.OpenedAt, w.LastSeen, w.ClosedAt, tabs, w.CopilotCount);
    }

    static void RestoreWindow(WindowInfo window, string shell)
    {
        if (window.Tabs.Count == 0) return;

        var first = window.Tabs[0];
        var firstDir = GetTabDir(first);
        var firstCmd = BuildTabCommand(first, shell);

        var args = new System.Text.StringBuilder();
        args.Append(System.Globalization.CultureInfo.InvariantCulture, $"-d \"{firstDir}\" {firstCmd}");

        // Add panes for first tab
        foreach (var pane in first.Panes)
        {
            var paneCmd = BuildTabCommand(pane, shell);
            var paneDir = GetTabDir(pane);
            args.Append(System.Globalization.CultureInfo.InvariantCulture, $" ; split-pane -d \"{paneDir}\" {paneCmd}");
        }

        for (var i = 1; i < window.Tabs.Count; i++)
        {
            var tab = window.Tabs[i];
            var tabCmd = BuildTabCommand(tab, shell);
            var tabDir = GetTabDir(tab);
            args.Append(System.Globalization.CultureInfo.InvariantCulture, $" ; new-tab -d \"{tabDir}\" {tabCmd}");

            foreach (var pane in tab.Panes)
            {
                var paneCmd = BuildTabCommand(pane, shell);
                var paneDir = GetTabDir(pane);
                args.Append(System.Globalization.CultureInfo.InvariantCulture, $" ; split-pane -d \"{paneDir}\" {paneCmd}");
            }
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.Write($"  🚀 Restoring window: ");
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write(string.Join(", ", window.Tabs.Select(t =>
            !string.IsNullOrEmpty(t.Title) && t.Title.Length > 1 ? t.Title : t.Folder)));
        Console.ResetColor();
        Console.WriteLine();

        var psi = new System.Diagnostics.ProcessStartInfo("wt", args.ToString())
        {
            UseShellExecute = false
        };
        System.Diagnostics.Process.Start(psi);
    }

    static string BuildTabCommand(TabInfo tab, string shell)
    {
        if (tab.HasCopilot && tab.CopilotId != null)
            return $"\"{shell}\" -NoExit -Command \"copilot --resume={tab.CopilotId}\"";
        if (tab.HasCopilot)
            return $"\"{shell}\" -NoExit -Command \"copilot\"";
        return $"\"{shell}\"";
    }

    static string GetTabDir(TabInfo tab)
    {
        if (!string.IsNullOrEmpty(tab.Path) && tab.DirExists)
            return tab.Path;
        return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
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

    static void AddWindowInfo(JsonElement w, List<WindowInfo> list)
    {
        var id = w.TryGetProperty("Id", out var idp) ? idp.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(id)) return;

        var openedAt = w.TryGetProperty("OpenedAt", out var oa) ? oa.GetString() ?? "" : "";
        var lastSeen = w.TryGetProperty("LastSeenAt", out var ls) ? ls.GetString() ?? "" : "";
        var closedAt = w.TryGetProperty("ClosedAt", out var ca) && ca.ValueKind != JsonValueKind.Null ? ca.GetString() : null;
        var tabCount = w.TryGetProperty("TabCount", out var tc) ? tc.GetInt32() : 0;
        var copilotCount = w.TryGetProperty("CopilotCount", out var cc) ? cc.GetInt32() : 0;

        var tabs = new List<TabInfo>();
        if (w.TryGetProperty("Tabs", out var tabsArr))
        {
            foreach (var t in tabsArr.EnumerateArray())
            {
                tabs.Add(ParseTabInfo(t));
            }
        }

        list.Add(new WindowInfo(id, openedAt, lastSeen, closedAt, tabs, copilotCount));
    }

    static TabInfo ParseTabInfo(JsonElement t)
    {
        var path = t.TryGetProperty("Path", out var p) ? p.GetString() ?? "" : "";
        var title = t.TryGetProperty("Title", out var tTitle) ? tTitle.GetString() ?? "" : "";
        var tabType = t.TryGetProperty("TabType", out var tType) ? tType.GetString() ?? "shell" : "shell";
        var dirExists = !t.TryGetProperty("DirExists", out var de) || de.ValueKind != JsonValueKind.False;
        var hasCopilot = t.TryGetProperty("HasCopilot", out var hc) && hc.GetBoolean();
        var copilotId = t.TryGetProperty("CopilotId", out var ci) && ci.ValueKind != JsonValueKind.Null ? ci.GetString() : null;
        var summary = t.TryGetProperty("Summary", out var sm) && sm.ValueKind != JsonValueKind.Null ? sm.GetString() : null;
        var folder = string.IsNullOrEmpty(path) ? "(no dir)" : Path.GetFileName(path);

        var panes = new List<TabInfo>();
        if (t.TryGetProperty("Panes", out var panesArr) && panesArr.GetArrayLength() > 0)
        {
            foreach (var pane in panesArr.EnumerateArray())
            {
                panes.Add(ParseTabInfo(pane));
            }
        }

        return new TabInfo(path, folder, title, tabType, dirExists, hasCopilot, copilotId, summary, panes);
    }

    sealed record TabInfo(string Path, string Folder, string Title, string TabType, bool DirExists, bool HasCopilot, string? CopilotId, string? Summary, List<TabInfo> Panes);
    sealed record WindowInfo(string Id, string OpenedAt, string LastSeen, string? ClosedAt, List<TabInfo> Tabs, int CopilotCount)
    {
        public bool IsClosed => ClosedAt != null;
    }
}
