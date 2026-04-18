using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using TSG.Platform;

namespace TSG;

/// <summary>
/// Captures terminal state from state.json + UI Automation and writes to SQLite.
/// Called by: tsg capture (manual), PowerShell watcher (on state.json change), tsg windows (auto-refresh).
/// </summary>
public static class StateCapture
{
    static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        @"Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\state.json");

    static readonly string CopilotSessionDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        @".copilot\session-state");

    public static async Task<int> RunAsync(IPlatformHost host, string[] args)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(args);

        var quiet = args.Any(a => a.Equals("--quiet", StringComparison.OrdinalIgnoreCase) || a == "-q");

        var data = await CaptureStateAsync(host);
        if (data == null)
        {
            if (!quiet) Console.WriteLine("  ❌ No terminal state available.");
            return 1;
        }

        using var db = new TerminalDatabase(host.TsgDir);
        var captureId = db.SaveCapture(data);

        if (!quiet)
        {
            var liveWins = data.Windows.Count(w => w.IsLive);
            var closedWins = data.Windows.Count(w => !w.IsLive);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"  💾 Capture #{captureId} | {data.Quality} | {liveWins} live, {closedWins} closed | {data.TotalTabs} tabs | 🤖 {data.CopilotCount}"));
        }

        return 0;
    }

    /// <summary>Perform a full state capture — reads state.json, UI Automation, copilot sessions.</summary>
    public static async Task<CaptureData?> CaptureStateAsync(IPlatformHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (!File.Exists(StatePath)) return null;

        // Read state.json with retry
        JsonDocument? stateDoc = null;
        string? sourceMtime = null;
        for (var retry = 0; retry < 3; retry++)
        {
            try
            {
                var raw = await File.ReadAllTextAsync(StatePath);
                stateDoc = JsonDocument.Parse(raw);
                sourceMtime = File.GetLastWriteTime(StatePath).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                break;
            }
            catch (IOException) when (retry < 2) { await Task.Delay(200); }
            catch (JsonException) when (retry < 2) { await Task.Delay(200); }
        }

        if (stateDoc == null) return null;

        using var doc = stateDoc;
        var root = doc.RootElement;

        if (!root.TryGetProperty("persistedWindowLayouts", out var layoutsEl))
            return null;

        // Live window detection via UI Automation
        int? liveWindowCount = GetLiveWindowCount();
        var liveWindowTabs = GetLiveWindowTabs();

        // Parse layouts from state.json (stale but has directory info)
        var layouts = new List<JsonElement>();
        foreach (var layout in layoutsEl.EnumerateArray())
            layouts.Add(layout);

        var stateWindowCount = layouts.Count;

        // Trim stale layouts if live count is lower
        if (liveWindowCount.HasValue && liveWindowCount.Value < layouts.Count && liveWindowCount.Value > 0)
        {
            layouts = [.. layouts
                .OrderByDescending(l => CountNewTabs(l))
                .Take(liveWindowCount.Value)];
        }

        // Load copilot sessions
        var copilotSessions = LoadCopilotSessions();
        var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        // Read existing window identities from DB for matching
        var existingWindows = LoadExistingWindows(host.TsgDir);
        var events = new List<CaptureEvent>();

        // Parse windows and tabs
        var captureWindows = new List<CaptureWindow>();
        var totalTabs = 0;
        var totalCopilot = 0;
        var matchedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // PRIMARY: Use live UIA tab data when available (real-time accurate)
        if (liveWindowTabs != null && liveWindowTabs.Count > 0)
        {
            // state.json tabs indexed for supplementary path/session info
            var stateTabs = layouts.Count > 0 ? ParseTabs(layouts[0], copilotSessions) : [];

            foreach (var liveWin in liveWindowTabs)
            {
                var tabs = new List<CaptureTab>();
                foreach (var tabName in liveWin.TabNames)
                {
                    var isCopilot = tabName.Contains("🤖", StringComparison.Ordinal);

                    // Try to match live tab to a state.json tab by title for path info
                    var stateMatch = stateTabs.FirstOrDefault(st =>
                        !string.IsNullOrEmpty(st.Title) && tabName.Contains(st.Title, StringComparison.OrdinalIgnoreCase));

                    // Also try path-based matching from copilot sessions
                    CopilotSession? copilotMatch = null;
                    if (isCopilot)
                    {
                        // Find copilot session that might match this tab's directory
                        copilotMatch = stateMatch != null && !string.IsNullOrEmpty(stateMatch.Path)
                            ? copilotSessions.FirstOrDefault(cs =>
                                cs.Cwd.Equals(stateMatch.Path, StringComparison.OrdinalIgnoreCase))
                            : null;
                    }

                    tabs.Add(new CaptureTab
                    {
                        Title = tabName,
                        Path = stateMatch?.Path ?? "",
                        TabType = isCopilot ? "copilot" : "shell",
                        HasCopilot = isCopilot,
                        CopilotSessionId = copilotMatch?.SessionId,
                        Commandline = stateMatch?.Commandline ?? "",
                        IsLiveDetected = true,
                        Panes = []
                    });
                }

                totalTabs += tabs.Count;
                var copilotCount = tabs.Count(t => t.HasCopilot);
                totalCopilot += copilotCount;

                var currentPaths = tabs.Where(t => !string.IsNullOrEmpty(t.Path)).Select(t => t.Path).ToList();
                string? matchedId = null;
                double bestScore = 0;

                foreach (var ew in existingWindows)
                {
                    if (matchedIds.Contains(ew.Id) || ew.ClosedAt != null) continue;
                    var score = JaccardSimilarity(ew.TabPaths, currentPaths);
                    if (score > bestScore && score >= 0.3)
                    {
                        bestScore = score;
                        matchedId = ew.Id;
                    }
                }

                if (matchedId != null)
                {
                    matchedIds.Add(matchedId);
                    captureWindows.Add(new CaptureWindow
                    {
                        Id = matchedId,
                        OpenedAt = existingWindows.First(w => w.Id == matchedId).FirstSeen,
                        LastSeen = now,
                        IsLive = true,
                        CopilotCount = copilotCount,
                        Tabs = tabs
                    });
                }
                else
                {
                    var newId = Guid.NewGuid().ToString("N")[..12];
                    captureWindows.Add(new CaptureWindow
                    {
                        Id = newId,
                        OpenedAt = now,
                        LastSeen = now,
                        IsLive = true,
                        CopilotCount = copilotCount,
                        Tabs = tabs
                    });
                    events.Add(new CaptureEvent("window_opened", newId));
                }
            }
        }
        else
        {
            // FALLBACK: Use state.json layouts (may be stale)
            foreach (var layout in layouts)
            {
                var tabs = ParseTabs(layout, copilotSessions);
                if (tabs.Count == 0) continue;

                totalTabs += tabs.Count;
                var copilotCount = tabs.Count(t => t.HasCopilot) + tabs.Sum(t => t.Panes.Count(p => p.HasCopilot));
                totalCopilot += copilotCount;

                var currentPaths = tabs.Where(t => !string.IsNullOrEmpty(t.Path)).Select(t => t.Path).ToList();
                string? matchedId = null;
                double bestScore = 0;

                foreach (var ew in existingWindows)
                {
                    if (matchedIds.Contains(ew.Id) || ew.ClosedAt != null) continue;
                    var score = JaccardSimilarity(ew.TabPaths, currentPaths);
                    if (score > bestScore && score >= 0.3)
                    {
                        bestScore = score;
                        matchedId = ew.Id;
                    }
                }

                if (matchedId != null)
                {
                    matchedIds.Add(matchedId);
                    captureWindows.Add(new CaptureWindow
                    {
                        Id = matchedId,
                        OpenedAt = existingWindows.First(w => w.Id == matchedId).FirstSeen,
                        LastSeen = now,
                        IsLive = true,
                        CopilotCount = copilotCount,
                        Tabs = tabs
                    });
                }
                else
                {
                    var newId = Guid.NewGuid().ToString("N")[..12];
                    captureWindows.Add(new CaptureWindow
                    {
                        Id = newId,
                        OpenedAt = now,
                        LastSeen = now,
                        IsLive = true,
                        CopilotCount = copilotCount,
                        Tabs = tabs
                    });
                    events.Add(new CaptureEvent("window_opened", newId));
                }
            }
        }

        // Handle closed windows
        foreach (var ew in existingWindows.Where(w => w.ClosedAt == null && !matchedIds.Contains(w.Id)))
        {
            var shouldClose = liveWindowCount.HasValue && liveWindowCount.Value <= captureWindows.Count;
            if (shouldClose)
            {
                captureWindows.Add(new CaptureWindow
                {
                    Id = ew.Id,
                    OpenedAt = ew.FirstSeen,
                    LastSeen = ew.LastSeen,
                    ClosedAt = now,
                    IsLive = false,
                    CopilotCount = 0,
                    Tabs = []
                });
                events.Add(new CaptureEvent("window_closed", ew.Id));
            }
        }

        // Determine quality — live UIA data is always "verified"
        var quality = liveWindowTabs != null && liveWindowTabs.Count > 0
            ? "live"
            : DetermineQuality(liveWindowCount, captureWindows.Count(w => w.IsLive), stateWindowCount, sourceMtime);

        return new CaptureData
        {
            CapturedAt = now,
            SourceMtime = sourceMtime,
            LiveWindowCount = liveWindowCount,
            StateWindowCount = stateWindowCount,
            TotalTabs = totalTabs,
            CopilotCount = totalCopilot,
            Quality = quality,
            Windows = captureWindows,
            Events = events
        };
    }

    static string DetermineQuality(int? liveCount, int capturedLive, int stateCount, string? sourceMtime)
    {
        if (!liveCount.HasValue) return "no-uia";
        if (liveCount.Value == capturedLive && liveCount.Value == stateCount) return "verified";
        if (liveCount.Value == capturedLive) return "trimmed";

        // Check staleness — if state.json is old
        if (sourceMtime != null &&
            DateTime.TryParse(sourceMtime, CultureInfo.InvariantCulture, DateTimeStyles.None, out var mtime) &&
            (DateTime.Now - mtime).TotalMinutes > 5)
            return "stale";

        return "suspect";
    }

    static int? GetLiveWindowCount()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        try
        {
            var count = 0;
            NativeMethods.EnumWindows((hWnd, _) =>
            {
                var className = new char[256];
                var len = NativeMethods.GetClassName(hWnd, className, className.Length);
                if (len > 0 && new string(className, 0, len) == "CASCADIA_HOSTING_WINDOW_CLASS")
                    count++;
                return true;
            }, IntPtr.Zero);
            return count;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Use COM IUIAutomation to get actual live tab names from each terminal window.
    /// This is the TRUE source of data — state.json is stale and unreliable.
    /// </summary>
    static List<LiveWindowInfo>? GetLiveWindowTabs()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        try
        {
            return UiaComHelper.GetTerminalWindowTabs();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public sealed record LiveWindowInfo(string Title, List<string> TabNames);

    static List<CaptureTab> ParseTabs(JsonElement layout, List<CopilotSession> copilotSessions)
    {
        var tabs = new List<CaptureTab>();
        if (!layout.TryGetProperty("tabLayout", out var tabLayout)) return tabs;

        CaptureTab? currentTab = null;
        foreach (var entry in tabLayout.EnumerateArray())
        {
            if (!entry.TryGetProperty("action", out var actionEl)) continue;
            var action = actionEl.GetString();

            if (action == "switchToTab") continue;

            var dir = entry.TryGetProperty("startingDirectory", out var sd) ? sd.GetString() ?? "" : "";
            var title = entry.TryGetProperty("tabTitle", out var tt) ? tt.GetString() ?? "" : "";
            var cmdline = entry.TryGetProperty("commandline", out var cl) ? cl.GetString() ?? "" : "";

            var tabType = DetectTabType(cmdline, title);
            var dirExists = !string.IsNullOrEmpty(dir) && Directory.Exists(dir);
            var copilot = FindBestCopilotSession(dir, copilotSessions);
            var folder = string.IsNullOrEmpty(dir) ? "(no dir)" : Path.GetFileName(dir);

            var tab = new CaptureTab
            {
                Path = dir,
                Folder = folder,
                Title = title,
                TabType = tabType,
                Commandline = cmdline,
                DirExists = string.IsNullOrEmpty(dir) || dirExists,
                HasCopilot = copilot != null,
                CopilotSessionId = copilot?.SessionId,
                CopilotSummary = copilot?.Summary
            };

            if (action == "newTab")
            {
                currentTab = tab;
                tabs.Add(tab);
            }
            else if (action == "splitPane" && currentTab != null)
            {
                currentTab.Panes.Add(tab);
            }
        }

        return tabs;
    }

    static string DetectTabType(string cmdline, string title)
    {
        if (cmdline.Contains("copilot", StringComparison.OrdinalIgnoreCase)) return "copilot";
        if (cmdline.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase) || title == "Command Prompt") return "cmd";
        if (cmdline.Contains("pwsh", StringComparison.OrdinalIgnoreCase) || cmdline.Contains("powershell", StringComparison.OrdinalIgnoreCase)) return "pwsh";
        if (cmdline.Contains("wsl", StringComparison.OrdinalIgnoreCase) || cmdline.Contains("bash", StringComparison.OrdinalIgnoreCase)) return "wsl";
        return "shell";
    }

    static List<CopilotSession> LoadCopilotSessions()
    {
        var sessions = new List<CopilotSession>();
        if (!Directory.Exists(CopilotSessionDir)) return sessions;

        foreach (var dir in Directory.GetDirectories(CopilotSessionDir))
        {
            var wsFile = Path.Combine(dir, "workspace.yaml");
            if (!File.Exists(wsFile)) continue;

            try
            {
                var content = File.ReadAllText(wsFile);
                var cwd = ExtractYamlField(content, "cwd");
                var summary = ExtractYamlField(content, "summary") ?? "";
                var updated = ExtractYamlField(content, "updated_at") ?? "";
                if (!string.IsNullOrEmpty(cwd))
                    sessions.Add(new CopilotSession(Path.GetFileName(dir), cwd, summary, updated));
            }
            catch (IOException) { }
        }

        return sessions;
    }

    static string? ExtractYamlField(string content, string field)
    {
        var prefix = field + ": ";
        foreach (var line in content.Split('\n'))
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith(prefix, StringComparison.Ordinal))
                return trimmed[prefix.Length..].Trim();
        }
        return null;
    }

    static CopilotSession? FindBestCopilotSession(string dir, List<CopilotSession> sessions)
    {
        if (string.IsNullOrEmpty(dir)) return null;
        var matches = sessions
            .Where(s => s.Cwd.Equals(dir, StringComparison.OrdinalIgnoreCase)
                     || dir.StartsWith(s.Cwd + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                     || s.Cwd.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0) return null;
        return matches.Where(m => !string.IsNullOrEmpty(m.Summary))
                      .OrderByDescending(m => m.Updated)
                      .FirstOrDefault() ?? matches.OrderByDescending(m => m.Updated).First();
    }

    static double JaccardSimilarity(List<string> set1, List<string> set2)
    {
        if (set1.Count == 0 || set2.Count == 0) return 0;
        var s1 = new HashSet<string>(set1, StringComparer.OrdinalIgnoreCase);
        var s2 = new HashSet<string>(set2, StringComparer.OrdinalIgnoreCase);
        var intersection = s1.Count(s1.Contains);
        // Correct: intersection of s1 and s2
        var inter = s1.Intersect(s2, StringComparer.OrdinalIgnoreCase).Count();
        var union = s1.Union(s2, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 0 : (double)inter / union;
    }

    static int CountNewTabs(JsonElement layout)
    {
        if (!layout.TryGetProperty("tabLayout", out var tl)) return 0;
        return tl.EnumerateArray().Count(e =>
            e.TryGetProperty("action", out var a) && a.GetString() == "newTab");
    }

    static List<ExistingWindow> LoadExistingWindows(string tsgDir)
    {
        var result = new List<ExistingWindow>();
        var dbPath = Path.Combine(tsgDir, "terminal.db");
        if (!File.Exists(dbPath)) return result;

        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT w.id, w.first_seen, w.last_seen, w.closed_at,
                       GROUP_CONCAT(ct.path, '|') as tab_paths
                FROM windows w
                LEFT JOIN capture_tabs ct ON ct.window_id = w.id
                    AND ct.capture_id = (SELECT MAX(id) FROM captures)
                    AND ct.is_pane = 0
                WHERE w.is_active = 1 OR w.closed_at IS NULL
                GROUP BY w.id;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var paths = reader.IsDBNull(4) ? [] : reader.GetString(4)
                    .Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
                result.Add(new ExistingWindow(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    paths));
            }
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) { /* First run — no DB yet */ }

        // Also try JSON fallback for migration
        if (result.Count == 0)
        {
            var jsonPath = Path.Combine(tsgDir, "windows", "active-windows.json");
            if (File.Exists(jsonPath))
            {
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                    if (doc.RootElement.TryGetProperty("Windows", out var windows))
                    {
                        foreach (var w in windows.EnumerateArray())
                        {
                            var id = w.TryGetProperty("Id", out var idp) ? idp.GetString() ?? "" : "";
                            if (string.IsNullOrEmpty(id)) continue;
                            var opened = w.TryGetProperty("OpenedAt", out var oa) ? oa.GetString() ?? "" : "";
                            var lastSeen = w.TryGetProperty("LastSeenAt", out var ls) ? ls.GetString() ?? "" : "";
                            var closedAt = w.TryGetProperty("ClosedAt", out var ca) && ca.ValueKind != JsonValueKind.Null ? ca.GetString() : null;
                            var paths = new List<string>();
                            if (w.TryGetProperty("Tabs", out var tabs))
                            {
                                foreach (var t in tabs.EnumerateArray())
                                {
                                    var p = t.TryGetProperty("Path", out var pp) ? pp.GetString() : null;
                                    if (!string.IsNullOrEmpty(p)) paths.Add(p);
                                }
                            }
                            result.Add(new ExistingWindow(id, opened, lastSeen, closedAt, paths));
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or JsonException) { /* Corrupt JSON — skip */ }
            }
        }

        return result;
    }

    sealed record CopilotSession(string SessionId, string Cwd, string Summary, string Updated);
    sealed record ExistingWindow(string Id, string FirstSeen, string LastSeen, string? ClosedAt, List<string> TabPaths);
}

file static class NativeMethods
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);
}
