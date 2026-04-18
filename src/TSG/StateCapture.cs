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

        // Scan running processes for copilot --resume=<sessionId> with parent PID info
        var activeResumeIds = ScanRunningCopilotSessions();
        // Build map: WT process PID → list of resume session IDs (for process-tree matching)
        var wtSessionMap = BuildTerminalSessionMap(activeResumeIds);

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
            var matchedSessionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Build set of active session summaries for detecting idle copilot tabs (no 🤖 in title)
            var activeSessionSummaries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (rid, _) in activeResumeIds)
            {
                var sess = copilotSessions.FirstOrDefault(cs =>
                    cs.SessionId.Equals(rid, StringComparison.OrdinalIgnoreCase));
                if (sess != null && !string.IsNullOrEmpty(sess.Summary))
                    activeSessionSummaries.TryAdd(sess.Summary, rid);
            }

            foreach (var liveWin in liveWindowTabs)
            {
                // Get sessions running in this WT window (by process tree)
                List<string>? windowSessions = null;
                if (liveWin.ProcessId > 0)
                    wtSessionMap.TryGetValue(liveWin.ProcessId, out windowSessions);

                // Build direct tab-position → session-ID map from WT child processes
                Dictionary<int, string>? tabPositionMap = null;
                if (liveWin.ProcessId > 0)
                    tabPositionMap = BuildTabPositionSessionMap(liveWin.ProcessId);

                var tabs = new List<CaptureTab>();
                var tabIndex = 0;
                foreach (var tabName in liveWin.TabNames)
                {
                    var isCopilot = tabName.Contains("🤖", StringComparison.Ordinal);

                    // Check if tab-position map directly assigns a session to this tab
                    string? positionSessionId = null;
                    if (tabPositionMap != null && tabPositionMap.TryGetValue(tabIndex, out var posId))
                    {
                        positionSessionId = posId;
                        if (!isCopilot) isCopilot = true;
                    }

                    // Also detect idle copilot tabs: title matches a running copilot session summary
                    if (!isCopilot && windowSessions != null)
                    {
                        foreach (var sid in windowSessions)
                        {
                            if (matchedSessionIds.Contains(sid)) continue;
                            var session = copilotSessions.FirstOrDefault(cs =>
                                cs.SessionId.Equals(sid, StringComparison.OrdinalIgnoreCase));
                            if (session != null && !string.IsNullOrEmpty(session.Summary)
                                && tabName.Contains(session.Summary, StringComparison.OrdinalIgnoreCase))
                            {
                                isCopilot = true;
                                break;
                            }
                        }
                    }
                    // Also check if tab title exactly matches any active session summary
                    if (!isCopilot && activeSessionSummaries.ContainsKey(tabName.Trim()))
                        isCopilot = true;

                    // Try to match live tab to a state.json tab by title for path info
                    var stateMatch = stateTabs.FirstOrDefault(st =>
                        !string.IsNullOrEmpty(st.Title) && tabName.Contains(st.Title, StringComparison.OrdinalIgnoreCase));

                    string? copilotSessionId = null;
                    string? copilotSummary = null;
                    string? tabPath = stateMatch?.Path ?? "";
                    string? cmdline = stateMatch?.Commandline ?? "";

                    if (isCopilot)
                    {
                        // Strategy 0 (MOST RELIABLE): Direct tab-position mapping from WT child processes
                        if (positionSessionId != null && !matchedSessionIds.Contains(positionSessionId))
                        {
                            copilotSessionId = positionSessionId;
                            matchedSessionIds.Add(positionSessionId);
                            var session = copilotSessions.FirstOrDefault(cs =>
                                cs.SessionId.Equals(positionSessionId, StringComparison.OrdinalIgnoreCase));
                            if (session != null)
                            {
                                copilotSummary = session.Summary;
                                if (string.IsNullOrEmpty(tabPath))
                                    tabPath = session.Cwd;
                            }
                        }

                        // Strategy 1: Extract --resume=<id> from state.json commandline
                        if (copilotSessionId == null && !string.IsNullOrEmpty(cmdline))
                            copilotSessionId = ExtractResumeId(cmdline);

                        // Strategy 2: Match by summary text in the UIA tab name (exact or contained)
                        if (copilotSessionId == null)
                        {
                            var summaryMatch = copilotSessions
                                .Where(cs => !string.IsNullOrEmpty(cs.Summary)
                                    && (tabName.Contains(cs.Summary, StringComparison.OrdinalIgnoreCase)
                                        || cs.Summary.Contains(tabName.Trim(), StringComparison.OrdinalIgnoreCase)))
                                .OrderByDescending(cs => cs.Updated)
                                .FirstOrDefault();
                            if (summaryMatch != null)
                            {
                                copilotSessionId = summaryMatch.SessionId;
                                copilotSummary = summaryMatch.Summary;
                                if (string.IsNullOrEmpty(tabPath))
                                    tabPath = summaryMatch.Cwd;
                            }
                        }

                        // Strategy 3: Process-tree matching — use WT PID to find copilot sessions
                        if (copilotSessionId == null && windowSessions != null)
                        {
                            var cleanTabName = tabName.Replace("🤖", "", StringComparison.Ordinal).Trim();

                            // 3a: Match by summary (full or partial/truncated)
                            foreach (var sid in windowSessions)
                            {
                                if (matchedSessionIds.Contains(sid)) continue;
                                var session = copilotSessions.FirstOrDefault(cs =>
                                    cs.SessionId.Equals(sid, StringComparison.OrdinalIgnoreCase));
                                if (session == null || string.IsNullOrEmpty(session.Summary)) continue;

                                if (tabName.Contains(session.Summary, StringComparison.OrdinalIgnoreCase)
                                    || session.Summary.Contains(cleanTabName, StringComparison.OrdinalIgnoreCase)
                                    || (cleanTabName.Length >= 4 && session.Summary.StartsWith(cleanTabName, StringComparison.OrdinalIgnoreCase)))
                                {
                                    copilotSessionId = sid;
                                    copilotSummary = session.Summary;
                                    tabPath = session.Cwd;
                                    matchedSessionIds.Add(sid);
                                    break;
                                }
                            }

                            // 3b: Match by CWD — if tab has a known path, find session with same CWD
                            if (copilotSessionId == null && !string.IsNullOrEmpty(tabPath))
                            {
                                foreach (var sid in windowSessions)
                                {
                                    if (matchedSessionIds.Contains(sid)) continue;
                                    var session = copilotSessions.FirstOrDefault(cs =>
                                        cs.SessionId.Equals(sid, StringComparison.OrdinalIgnoreCase));
                                    if (session != null && !string.IsNullOrEmpty(session.Cwd)
                                        && tabPath.Equals(session.Cwd, StringComparison.OrdinalIgnoreCase))
                                    {
                                        copilotSessionId = sid;
                                        copilotSummary = session.Summary;
                                        matchedSessionIds.Add(sid);
                                        break;
                                    }
                                }
                            }

                            // 3c: Elimination — only 1 unclaimed copilot tab + 1 unclaimed session
                            if (copilotSessionId == null)
                            {
                                var unclaimed = windowSessions.Where(s => !matchedSessionIds.Contains(s)).ToList();
                                if (unclaimed.Count == 1)
                                {
                                    var sid = unclaimed[0];
                                    var session = copilotSessions.FirstOrDefault(cs =>
                                        cs.SessionId.Equals(sid, StringComparison.OrdinalIgnoreCase));
                                    copilotSessionId = sid;
                                    copilotSummary = session?.Summary;
                                    tabPath = session?.Cwd ?? tabPath;
                                    matchedSessionIds.Add(sid);
                                }
                            }
                        }

                        // Strategy 4: Fallback — try all active resume IDs with summary matching
                        if (copilotSessionId == null)
                        {
                            var cleanTabName = tabName.Replace("🤖", "", StringComparison.Ordinal).Trim();
                            foreach (var (rid, _) in activeResumeIds)
                            {
                                if (matchedSessionIds.Contains(rid)) continue;
                                var session = copilotSessions.FirstOrDefault(cs =>
                                    cs.SessionId.Equals(rid, StringComparison.OrdinalIgnoreCase));
                                if (session != null && !string.IsNullOrEmpty(session.Summary))
                                {
                                    if (tabName.Contains(session.Summary, StringComparison.OrdinalIgnoreCase)
                                        || session.Summary.Contains(cleanTabName, StringComparison.OrdinalIgnoreCase)
                                        || (cleanTabName.Length >= 4 && session.Summary.StartsWith(cleanTabName, StringComparison.OrdinalIgnoreCase)))
                                    {
                                        copilotSessionId = rid;
                                        copilotSummary = session.Summary;
                                        tabPath = session.Cwd;
                                        matchedSessionIds.Add(rid);
                                        break;
                                    }
                                }
                            }
                        }

                        // Strategy 5: Match by CWD directory against all sessions
                        if (copilotSessionId == null && !string.IsNullOrEmpty(tabPath))
                        {
                            var cwdMatch = FindBestCopilotSession(tabPath, copilotSessions);
                            if (cwdMatch != null)
                            {
                                copilotSessionId = cwdMatch.SessionId;
                                copilotSummary = cwdMatch.Summary;
                            }
                        }

                        // Strategy 6: Match all state.json copilot tabs' commandlines
                        if (copilotSessionId == null)
                        {
                            foreach (var st in stateTabs.Where(s => s.HasCopilot && !string.IsNullOrEmpty(s.Commandline)))
                            {
                                var rid = ExtractResumeId(st.Commandline);
                                if (rid != null)
                                {
                                    var session = copilotSessions.FirstOrDefault(cs =>
                                        cs.SessionId.Equals(rid, StringComparison.OrdinalIgnoreCase));
                                    if (session != null && !string.IsNullOrEmpty(session.Summary)
                                        && tabName.Contains(session.Summary, StringComparison.OrdinalIgnoreCase))
                                    {
                                        copilotSessionId = rid;
                                        copilotSummary = session.Summary;
                                        if (string.IsNullOrEmpty(tabPath))
                                            tabPath = session.Cwd;
                                        break;
                                    }
                                }
                            }
                        }

                        // Get summary from session if we have an ID but no summary yet
                        if (copilotSessionId != null && string.IsNullOrEmpty(copilotSummary))
                        {
                            var session = copilotSessions.FirstOrDefault(cs =>
                                cs.SessionId.Equals(copilotSessionId, StringComparison.OrdinalIgnoreCase));
                            copilotSummary = session?.Summary;
                        }
                    }

                    tabs.Add(new CaptureTab
                    {
                        Title = tabName,
                        Path = tabPath ?? "",
                        TabType = isCopilot ? "copilot" : "shell",
                        HasCopilot = isCopilot,
                        CopilotSessionId = copilotSessionId,
                        CopilotSummary = copilotSummary,
                        Commandline = cmdline ?? "",
                        IsLiveDetected = true,
                        Panes = []
                    });
                    tabIndex++;
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

    public sealed record LiveWindowInfo(string Title, List<string> TabNames, int ProcessId);

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
            var folder = string.IsNullOrEmpty(dir) ? "(no dir)" : Path.GetFileName(dir);

            // Extract session ID from commandline first (most reliable)
            var resumeId = ExtractResumeId(cmdline);
            CopilotSession? copilot = null;
            var hasCopilot = tabType == "copilot" || resumeId != null;

            if (resumeId != null)
            {
                // Direct match by session ID from --resume=
                copilot = copilotSessions.FirstOrDefault(s =>
                    s.SessionId.Equals(resumeId, StringComparison.OrdinalIgnoreCase));
                hasCopilot = true;
            }

            // Fallback to CWD matching
            copilot ??= FindBestCopilotSession(dir, copilotSessions);

            var tab = new CaptureTab
            {
                Path = dir,
                Folder = folder,
                Title = title,
                TabType = hasCopilot ? "copilot" : tabType,
                Commandline = cmdline,
                DirExists = string.IsNullOrEmpty(dir) || dirExists,
                HasCopilot = hasCopilot || copilot != null,
                CopilotSessionId = resumeId ?? copilot?.SessionId,
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

    /// <summary>Extract session ID from commandline like: copilot --resume=abc-def-123</summary>
    static string? ExtractResumeId(string cmdline)
    {
        if (string.IsNullOrEmpty(cmdline)) return null;

        // Match --resume=<guid-like-id>
        var idx = cmdline.IndexOf("--resume=", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var start = idx + "--resume=".Length;
        if (start >= cmdline.Length) return null;

        // Session IDs are UUIDs: read until whitespace or quote
        var end = start;
        while (end < cmdline.Length && !char.IsWhiteSpace(cmdline[end]) && cmdline[end] != '"' && cmdline[end] != '\'')
            end++;

        var id = cmdline[start..end];
        return id.Length > 4 ? id : null; // Sanity: must be meaningful
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

    /// <summary>
    /// Scan running pwsh processes for copilot --resume=&lt;sessionId&gt; commandlines.
    /// Returns list of (SessionId, ProcessId) pairs for process-tree matching.
    /// </summary>
    static List<(string SessionId, int Pid)> ScanRunningCopilotSessions()
    {
        var results = new List<(string SessionId, int Pid)>();
        try
        {
            var shell = FindPwshPath();
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = shell,
                Arguments = "-NoProfile -Command \"Get-CimInstance Win32_Process -Filter \\\"Name = 'pwsh.exe'\\\" | Where-Object { $_.CommandLine -like '*copilot*--resume*' } | ForEach-Object { '{0}|{1}' -f $_.ProcessId, $_.CommandLine }\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return results;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);

            if (string.IsNullOrWhiteSpace(output)) return results;

            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                var sep = trimmed.IndexOf('|', StringComparison.Ordinal);
                if (sep <= 0) continue;
                if (!int.TryParse(trimmed[..sep], out var pid)) continue;
                var id = ExtractResumeId(trimmed[(sep + 1)..]);
                if (id != null)
                    results.Add((id, pid));
            }
        }
        catch (Exception) { /* Non-critical */ }
        return results;
    }

    /// <summary>
    /// Build a map from Windows Terminal PID → list of copilot session IDs
    /// by tracing each copilot pwsh process's parent chain up to WindowsTerminal.exe.
    /// </summary>
    static Dictionary<int, List<string>> BuildTerminalSessionMap(List<(string SessionId, int Pid)> copilotProcesses)
    {
        var map = new Dictionary<int, List<string>>();
        foreach (var (sessionId, pid) in copilotProcesses)
        {
            var wtPid = FindParentTerminalPid(pid);
            if (wtPid > 0)
            {
                if (!map.TryGetValue(wtPid, out var list))
                {
                    list = [];
                    map[wtPid] = list;
                }
                list.Add(sessionId);
            }
        }
        return map;
    }

    /// <summary>
    /// Build a per-tab-position map of copilot session IDs for a specific WT window.
    /// Enumerates WT child shell processes (pwsh/cmd) sorted by creation time,
    /// matching tab order in the terminal. Each shell process with --resume=ID
    /// gives a direct tab-position → session-ID mapping.
    /// </summary>
    static Dictionary<int, string> BuildTabPositionSessionMap(int wtPid)
    {
        var map = new Dictionary<int, string>();
        try
        {
            var shell = FindPwshPath();
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = shell,
                Arguments = $"-NoProfile -Command \"Get-CimInstance Win32_Process -Filter 'ParentProcessId={wtPid}' | Where-Object {{ $_.Name -match 'pwsh|cmd|powershell' }} | Sort-Object CreationDate | ForEach-Object {{ '{{0}}|{{1}}' -f $_.ProcessId, $_.CommandLine }}\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return map;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);
            if (string.IsNullOrWhiteSpace(output)) return map;

            var tabIndex = 0;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                var sep = trimmed.IndexOf('|', StringComparison.Ordinal);
                if (sep <= 0) { tabIndex++; continue; }
                var cmdLine = trimmed[(sep + 1)..];
                var sessionId = ExtractResumeId(cmdLine);
                if (sessionId != null)
                    map[tabIndex] = sessionId;
                tabIndex++;
            }
        }
        catch (Exception) { /* Non-critical */ }
        return map;
    }

    /// <summary>Trace parent PID chain from a process up to WindowsTerminal.exe</summary>
    static int FindParentTerminalPid(int pid)
    {
        try
        {
            var current = pid;
            for (var depth = 0; depth < 10; depth++)
            {
                using var proc = System.Diagnostics.Process.GetProcessById(current);
                var parentPid = GetParentPid(current);
                if (parentPid <= 0) break;

                try
                {
                    using var parent = System.Diagnostics.Process.GetProcessById(parentPid);
                    if (parent.ProcessName.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase))
                        return parentPid;
                }
                catch (Exception) { break; }

                current = parentPid;
            }
        }
        catch (Exception) { }
        return 0;
    }

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    static int GetParentPid(int pid)
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetProcessById(pid);
            var pbi = new PROCESS_BASIC_INFORMATION();
            var status = NtQueryInformationProcess(proc.Handle, 0, ref pbi, Marshal.SizeOf(pbi), out _);
            return status == 0 ? checked((int)pbi.InheritedFromUniqueProcessId) : 0;
        }
        catch (Exception) { return 0; }
    }

    static string FindPwshPath()
    {
        var paths = new[] { @"C:\Program Files\PowerShell\7\pwsh.exe", @"C:\Program Files (x86)\PowerShell\7\pwsh.exe" };
        return paths.FirstOrDefault(File.Exists) ?? "pwsh.exe";
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
