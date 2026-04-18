using System.Globalization;
using Microsoft.Data.Sqlite;

namespace TSG;

/// <summary>
/// SQLite database for terminal state tracking with temporal captures.
/// DB location: ~/.tsg/terminal.db
/// </summary>
public sealed class TerminalDatabase : IDisposable
{
    readonly string _dbPath;
    readonly SqliteConnection _conn;
    static readonly Mutex DbMutex = new(false, "Global\\TSG_TerminalDB");

    public TerminalDatabase(string tsgDir)
    {
        _dbPath = Path.Combine(tsgDir, "terminal.db");
        Directory.CreateDirectory(tsgDir);
        _conn = new SqliteConnection($"Data Source={_dbPath}");
        _conn.Open();
        InitSchema();
    }

    void InitSchema()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS windows (
                id TEXT PRIMARY KEY,
                first_seen TEXT NOT NULL,
                last_seen TEXT NOT NULL,
                closed_at TEXT,
                is_active INTEGER DEFAULT 1
            );

            CREATE TABLE IF NOT EXISTS captures (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                captured_at TEXT NOT NULL,
                source_mtime TEXT,
                live_window_count INTEGER,
                state_window_count INTEGER,
                total_tabs INTEGER DEFAULT 0,
                copilot_count INTEGER DEFAULT 0,
                quality TEXT DEFAULT 'unknown'
            );

            CREATE TABLE IF NOT EXISTS capture_windows (
                capture_id INTEGER NOT NULL,
                window_id TEXT NOT NULL,
                tab_count INTEGER DEFAULT 0,
                copilot_count INTEGER DEFAULT 0,
                is_live INTEGER DEFAULT 1,
                PRIMARY KEY (capture_id, window_id),
                FOREIGN KEY (capture_id) REFERENCES captures(id),
                FOREIGN KEY (window_id) REFERENCES windows(id)
            );

            CREATE TABLE IF NOT EXISTS capture_tabs (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                capture_id INTEGER NOT NULL,
                window_id TEXT NOT NULL,
                ordinal INTEGER NOT NULL,
                path TEXT,
                folder TEXT,
                title TEXT,
                tab_type TEXT DEFAULT 'shell',
                commandline TEXT,
                dir_exists INTEGER DEFAULT 1,
                has_copilot INTEGER DEFAULT 0,
                copilot_session_id TEXT,
                copilot_summary TEXT,
                is_pane INTEGER DEFAULT 0,
                parent_ordinal INTEGER,
                FOREIGN KEY (capture_id) REFERENCES captures(id)
            );

            CREATE TABLE IF NOT EXISTS events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                event_type TEXT NOT NULL,
                window_id TEXT,
                details TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_captures_at ON captures(captured_at);
            CREATE INDEX IF NOT EXISTS idx_events_ts ON events(timestamp);
            CREATE INDEX IF NOT EXISTS idx_capture_tabs_cw ON capture_tabs(capture_id, window_id);
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Record a full capture with windows and tabs.
    /// Uses a named mutex to prevent concurrent writes.
    /// </summary>
    public long SaveCapture(CaptureData data)
    {
        ArgumentNullException.ThrowIfNull(data);
        bool acquired = false;
        try
        {
            acquired = DbMutex.WaitOne(TimeSpan.FromSeconds(5));
            return SaveCaptureInternal(data);
        }
        finally
        {
            if (acquired) DbMutex.ReleaseMutex();
        }
    }

    long SaveCaptureInternal(CaptureData data)
    {
        using var tx = _conn.BeginTransaction();
        try
        {
            // Insert capture record
            using var captureCmd = _conn.CreateCommand();
            captureCmd.CommandText = """
                INSERT INTO captures (captured_at, source_mtime, live_window_count, state_window_count, total_tabs, copilot_count, quality)
                VALUES ($at, $mtime, $live, $state, $tabs, $copilot, $quality);
                SELECT last_insert_rowid();
                """;
            captureCmd.Parameters.AddWithValue("$at", data.CapturedAt);
            captureCmd.Parameters.AddWithValue("$mtime", (object?)data.SourceMtime ?? DBNull.Value);
            captureCmd.Parameters.AddWithValue("$live", data.LiveWindowCount.HasValue ? data.LiveWindowCount.Value : DBNull.Value);
            captureCmd.Parameters.AddWithValue("$state", data.StateWindowCount);
            captureCmd.Parameters.AddWithValue("$tabs", data.TotalTabs);
            captureCmd.Parameters.AddWithValue("$copilot", data.CopilotCount);
            captureCmd.Parameters.AddWithValue("$quality", data.Quality);
            var captureId = (long)captureCmd.ExecuteScalar()!;

            foreach (var win in data.Windows)
            {
                // Upsert window identity
                using var winCmd = _conn.CreateCommand();
                winCmd.CommandText = """
                    INSERT INTO windows (id, first_seen, last_seen, closed_at, is_active)
                    VALUES ($id, $now, $now, $closed, $active)
                    ON CONFLICT(id) DO UPDATE SET
                        last_seen = $now,
                        closed_at = $closed,
                        is_active = $active;
                    """;
                winCmd.Parameters.AddWithValue("$id", win.Id);
                winCmd.Parameters.AddWithValue("$now", data.CapturedAt);
                winCmd.Parameters.AddWithValue("$closed", (object?)win.ClosedAt ?? DBNull.Value);
                winCmd.Parameters.AddWithValue("$active", win.IsLive ? 1 : 0);
                winCmd.ExecuteNonQuery();

                // Insert capture_windows
                using var cwCmd = _conn.CreateCommand();
                cwCmd.CommandText = """
                    INSERT INTO capture_windows (capture_id, window_id, tab_count, copilot_count, is_live)
                    VALUES ($cid, $wid, $tabs, $copilot, $live);
                    """;
                cwCmd.Parameters.AddWithValue("$cid", captureId);
                cwCmd.Parameters.AddWithValue("$wid", win.Id);
                cwCmd.Parameters.AddWithValue("$tabs", win.Tabs.Count);
                cwCmd.Parameters.AddWithValue("$copilot", win.CopilotCount);
                cwCmd.Parameters.AddWithValue("$live", win.IsLive ? 1 : 0);
                cwCmd.ExecuteNonQuery();

                // Insert tabs
                var ordinal = 0;
                foreach (var tab in win.Tabs)
                {
                    InsertTab(captureId, win.Id, ordinal, tab, parentOrdinal: null);
                    var parentOrd = ordinal;
                    ordinal++;
                    foreach (var pane in tab.Panes)
                    {
                        InsertTab(captureId, win.Id, ordinal, pane, parentOrdinal: parentOrd);
                        ordinal++;
                    }
                }
            }

            // Log events for state changes
            foreach (var evt in data.Events)
            {
                using var evtCmd = _conn.CreateCommand();
                evtCmd.CommandText = """
                    INSERT INTO events (timestamp, event_type, window_id, details)
                    VALUES ($ts, $type, $wid, $details);
                    """;
                evtCmd.Parameters.AddWithValue("$ts", data.CapturedAt);
                evtCmd.Parameters.AddWithValue("$type", evt.Type);
                evtCmd.Parameters.AddWithValue("$wid", (object?)evt.WindowId ?? DBNull.Value);
                evtCmd.Parameters.AddWithValue("$details", (object?)evt.Details ?? DBNull.Value);
                evtCmd.ExecuteNonQuery();
            }

            tx.Commit();
            return captureId;
        }
        catch
        {
            tx.Rollback();
            throw;
        }
    }

    void InsertTab(long captureId, string windowId, int ordinal, CaptureTab tab, int? parentOrdinal)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO capture_tabs (capture_id, window_id, ordinal, path, folder, title, tab_type, commandline, dir_exists, has_copilot, copilot_session_id, copilot_summary, is_pane, parent_ordinal)
            VALUES ($cid, $wid, $ord, $path, $folder, $title, $type, $cmd, $exists, $copilot, $sid, $summary, $pane, $parent);
            """;
        cmd.Parameters.AddWithValue("$cid", captureId);
        cmd.Parameters.AddWithValue("$wid", windowId);
        cmd.Parameters.AddWithValue("$ord", ordinal);
        cmd.Parameters.AddWithValue("$path", (object?)tab.Path ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$folder", (object?)tab.Folder ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", (object?)tab.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$type", tab.TabType);
        cmd.Parameters.AddWithValue("$cmd", (object?)tab.Commandline ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$exists", tab.DirExists ? 1 : 0);
        cmd.Parameters.AddWithValue("$copilot", tab.HasCopilot ? 1 : 0);
        cmd.Parameters.AddWithValue("$sid", (object?)tab.CopilotSessionId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$summary", (object?)tab.CopilotSummary ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$pane", parentOrdinal.HasValue ? 1 : 0);
        cmd.Parameters.AddWithValue("$parent", parentOrdinal.HasValue ? parentOrdinal.Value : DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Get the latest capture with full window/tab data.</summary>
    public CaptureData? GetLatestCapture()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT id, captured_at, source_mtime, live_window_count, state_window_count, total_tabs, copilot_count, quality FROM captures ORDER BY id DESC LIMIT 1;";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return null;

        var captureId = reader.GetInt64(0);
        var data = new CaptureData
        {
            CapturedAt = reader.GetString(1),
            SourceMtime = reader.IsDBNull(2) ? null : reader.GetString(2),
            LiveWindowCount = reader.IsDBNull(3) ? null : reader.GetInt32(3),
            StateWindowCount = reader.GetInt32(4),
            TotalTabs = reader.GetInt32(5),
            CopilotCount = reader.GetInt32(6),
            Quality = reader.GetString(7)
        };

        // Load windows
        using var winCmd = _conn.CreateCommand();
        winCmd.CommandText = """
            SELECT cw.window_id, w.first_seen, w.last_seen, w.closed_at, cw.tab_count, cw.copilot_count, cw.is_live
            FROM capture_windows cw
            JOIN windows w ON w.id = cw.window_id
            WHERE cw.capture_id = $cid
            ORDER BY cw.is_live DESC, cw.tab_count DESC;
            """;
        winCmd.Parameters.AddWithValue("$cid", captureId);
        using var winReader = winCmd.ExecuteReader();

        while (winReader.Read())
        {
            var winId = winReader.GetString(0);
            var win = new CaptureWindow
            {
                Id = winId,
                OpenedAt = winReader.GetString(1),
                LastSeen = winReader.GetString(2),
                ClosedAt = winReader.IsDBNull(3) ? null : winReader.GetString(3),
                CopilotCount = winReader.GetInt32(5),
                IsLive = winReader.GetInt32(6) == 1
            };

            // Load tabs for this window
            using var tabCmd = _conn.CreateCommand();
            tabCmd.CommandText = """
                SELECT ordinal, path, folder, title, tab_type, commandline, dir_exists, has_copilot, copilot_session_id, copilot_summary, is_pane, parent_ordinal
                FROM capture_tabs WHERE capture_id = $cid AND window_id = $wid
                ORDER BY ordinal;
                """;
            tabCmd.Parameters.AddWithValue("$cid", captureId);
            tabCmd.Parameters.AddWithValue("$wid", winId);
            using var tabReader = tabCmd.ExecuteReader();

            var allTabs = new List<(int Ordinal, CaptureTab Tab, int? ParentOrdinal)>();
            while (tabReader.Read())
            {
                var tab = new CaptureTab
                {
                    Path = tabReader.IsDBNull(1) ? "" : tabReader.GetString(1),
                    Folder = tabReader.IsDBNull(2) ? "" : tabReader.GetString(2),
                    Title = tabReader.IsDBNull(3) ? "" : tabReader.GetString(3),
                    TabType = tabReader.GetString(4),
                    Commandline = tabReader.IsDBNull(5) ? "" : tabReader.GetString(5),
                    DirExists = tabReader.GetInt32(6) == 1,
                    HasCopilot = tabReader.GetInt32(7) == 1,
                    CopilotSessionId = tabReader.IsDBNull(8) ? null : tabReader.GetString(8),
                    CopilotSummary = tabReader.IsDBNull(9) ? null : tabReader.GetString(9)
                };
                var parentOrd = tabReader.IsDBNull(11) ? (int?)null : tabReader.GetInt32(11);
                allTabs.Add((tabReader.GetInt32(0), tab, parentOrd));
            }

            // Rebuild tab hierarchy (panes nested under parent tabs)
            var tabMap = new Dictionary<int, CaptureTab>();
            foreach (var (ord, tab, parentOrd) in allTabs)
            {
                tabMap[ord] = tab;
                if (parentOrd.HasValue && tabMap.TryGetValue(parentOrd.Value, out var parent))
                    parent.Panes.Add(tab);
                else
                    win.Tabs.Add(tab);
            }

            data.Windows.Add(win);
        }

        return data;
    }

    /// <summary>Get capture history for snapshots view.</summary>
    public List<CaptureInfo> GetCaptureHistory(int limit = 100)
    {
        var result = new List<CaptureInfo>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, captured_at, live_window_count, state_window_count, total_tabs, copilot_count, quality
            FROM captures ORDER BY id DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new CaptureInfo(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetInt32(4),
                reader.GetInt32(5),
                reader.GetString(6)));
        }
        return result;
    }

    /// <summary>Get recent events.</summary>
    public List<EventRecord> GetRecentEvents(int limit = 50)
    {
        var result = new List<EventRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT timestamp, event_type, window_id, details FROM events ORDER BY id DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            result.Add(new EventRecord(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }
        return result;
    }

    /// <summary>
    /// Get the last known tabs for a window from the most recent capture where it had tabs.
    /// Enriches tabs with copilot session IDs from earlier captures if the last capture lost them
    /// (copilot process may have ended before window closed).
    /// </summary>
    public List<CaptureTab> GetLastKnownTabs(string windowId, string? referenceTime = null)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT ct.ordinal, ct.path, ct.title, ct.tab_type, ct.has_copilot,
                   ct.copilot_session_id, ct.copilot_summary, ct.commandline, ct.parent_ordinal
            FROM capture_tabs ct
            WHERE ct.window_id = $wid
              AND ct.capture_id = (
                  SELECT cw.capture_id FROM capture_windows cw
                  WHERE cw.window_id = $wid AND cw.is_live = 1
                  ORDER BY cw.capture_id DESC LIMIT 1
              )
            ORDER BY ct.ordinal;
            """;
        cmd.Parameters.AddWithValue("$wid", windowId);
        using var reader = cmd.ExecuteReader();

        var tabs = new List<CaptureTab>();

        while (reader.Read())
        {
            var parentOrdinal = reader.IsDBNull(8) ? (int?)null : reader.GetInt32(8);
            var path = reader.IsDBNull(1) ? "" : reader.GetString(1);
            var tab = new CaptureTab
            {
                Path = path,
                Folder = string.IsNullOrEmpty(path) ? "" : Path.GetFileName(path),
                Title = reader.IsDBNull(2) ? "" : reader.GetString(2),
                TabType = reader.IsDBNull(3) ? "shell" : reader.GetString(3),
                HasCopilot = reader.GetBoolean(4),
                CopilotSessionId = reader.IsDBNull(5) ? null : reader.GetString(5),
                CopilotSummary = reader.IsDBNull(6) ? null : reader.GetString(6),
                Commandline = reader.IsDBNull(7) ? "" : reader.GetString(7),
                DirExists = !string.IsNullOrEmpty(path) && Directory.Exists(path),
                Panes = []
            };

            if (parentOrdinal != null)
            {
                var parent = tabs.ElementAtOrDefault(parentOrdinal.Value);
                if (parent != null)
                    parent.Panes.Add(tab);
            }
            else
            {
                tabs.Add(tab);
            }
        }

        // Enrich: if any tab lost its copilot session ID (copilot ended before window closed),
        // recover it from earlier captures for the same window
        EnrichTabsWithHistoricalSessionIds(windowId, tabs, referenceTime);

        return tabs;
    }

    /// <summary>
    /// Recover copilot session IDs from earlier captures when the last capture lost them.
    /// Matches tabs by ordinal position within the same window.
    /// </summary>
    void EnrichTabsWithHistoricalSessionIds(string windowId, List<CaptureTab> tabs, string? referenceTime = null)
    {
        // Find all distinct copilot session IDs ever seen for this window, with their ordinal and path
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT ct.ordinal, ct.copilot_session_id, ct.copilot_summary, ct.path, ct.title
            FROM capture_tabs ct
            WHERE ct.window_id = $wid
              AND ct.copilot_session_id IS NOT NULL
              AND ct.parent_ordinal IS NULL
            ORDER BY ct.capture_id DESC;
            """;
        cmd.Parameters.AddWithValue("$wid", windowId);
        using var reader = cmd.ExecuteReader();

        // Build a map: ordinal → best (most recent) session info
        var sessionByOrdinal = new Dictionary<int, (string SessionId, string? Summary, string Path, string Title)>();
        // Also track all session IDs seen (for fallback matching by title)
        var sessionByTitle = new Dictionary<string, (string SessionId, string? Summary, string Path)>();

        while (reader.Read())
        {
            var ordinal = reader.GetInt32(0);
            var sessionId = reader.GetString(1);
            var summary = reader.IsDBNull(2) ? null : reader.GetString(2);
            var path = reader.IsDBNull(3) ? "" : reader.GetString(3);
            var title = reader.IsDBNull(4) ? "" : reader.GetString(4);

            sessionByOrdinal.TryAdd(ordinal, (sessionId, summary, path, title));

            // Track by copilot summary for title-based matching
            if (!string.IsNullOrEmpty(summary))
                sessionByTitle.TryAdd(summary, (sessionId, summary, path));
        }

        if (sessionByOrdinal.Count == 0)
        {
            // No history for this window — fall back to scanning all copilot sessions on disk
            EnrichFromCopilotSessionStore(tabs, referenceTime);
            return;
        }

        // Enrich tabs that are missing session IDs
        for (var i = 0; i < tabs.Count; i++)
        {
            var tab = tabs[i];
            if (tab.CopilotSessionId != null) continue;

            // Strategy 1: match by ordinal position
            if (sessionByOrdinal.TryGetValue(i, out var byOrdinal))
            {
                tab.HasCopilot = true;
                tab.CopilotSessionId = byOrdinal.SessionId;
                tab.CopilotSummary = byOrdinal.Summary;
                if (string.IsNullOrEmpty(tab.Path) && !string.IsNullOrEmpty(byOrdinal.Path))
                {
                    tab.Path = byOrdinal.Path;
                    tab.Folder = Path.GetFileName(byOrdinal.Path);
                    tab.DirExists = Directory.Exists(byOrdinal.Path);
                }
                continue;
            }

            // Strategy 2: match by title containing copilot summary
            if (!string.IsNullOrEmpty(tab.Title))
            {
                foreach (var (summary, info) in sessionByTitle)
                {
                    if (tab.Title.Contains(summary, StringComparison.OrdinalIgnoreCase) ||
                        summary.Contains(tab.Title, StringComparison.OrdinalIgnoreCase))
                    {
                        tab.HasCopilot = true;
                        tab.CopilotSessionId = info.SessionId;
                        tab.CopilotSummary = info.Summary;
                        if (string.IsNullOrEmpty(tab.Path) && !string.IsNullOrEmpty(info.Path))
                        {
                            tab.Path = info.Path;
                            tab.Folder = Path.GetFileName(info.Path);
                            tab.DirExists = Directory.Exists(info.Path);
                        }
                        break;
                    }
                }
            }
        }

        // Final fallback: scan disk for any unmatched tabs
        EnrichFromCopilotSessionStore(tabs, referenceTime);
    }

    /// <summary>
    /// Last-resort enrichment: scan all copilot session-state directories on disk
    /// and match unmatched tabs by PATH (CWD) and TITLE, using nearest TIMESTAMP
    /// to the window's reference time as tie-breaker. Restores session continuity
    /// for closed windows captured before reliable session ID tracking.
    /// </summary>
    static void EnrichFromCopilotSessionStore(List<CaptureTab> tabs, string? referenceTime = null)
    {
        var sessionsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot", "session-state");
        if (!Directory.Exists(sessionsDir)) return;

        // Check if any tabs need enrichment
        if (!tabs.Any(t => t.CopilotSessionId == null)) return;

        DateTime? refTime = null;
        if (!string.IsNullOrEmpty(referenceTime)
            && DateTime.TryParse(referenceTime, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeLocal, out var parsedRef))
        {
            refTime = parsedRef;
        }

        // Collect already-claimed session IDs to avoid double-assignment
        var claimed = new HashSet<string>(
            tabs.Where(t => t.CopilotSessionId != null).Select(t => t.CopilotSessionId!),
            StringComparer.OrdinalIgnoreCase);

        // Build sessions catalog from disk
        var diskSessions = new List<(string SessionId, string Summary, string Cwd, DateTime Updated)>();
        foreach (var dir in Directory.EnumerateDirectories(sessionsDir))
        {
            var ws = Path.Combine(dir, "workspace.yaml");
            if (!File.Exists(ws)) continue;
            try
            {
                var id = Path.GetFileName(dir);
                string? summary = null;
                string? cwd = null;
                var updated = File.GetLastWriteTime(ws);
                foreach (var line in File.ReadLines(ws))
                {
                    var t = line.TrimStart();
                    if (t.StartsWith("summary: ", StringComparison.Ordinal))
                        summary = t["summary: ".Length..].Trim();
                    else if (t.StartsWith("cwd: ", StringComparison.Ordinal))
                        cwd = t["cwd: ".Length..].Trim();
                }
                if (!string.IsNullOrEmpty(id))
                    diskSessions.Add((id, summary ?? "", cwd ?? "", updated));
            }
            catch (IOException) { }
        }

        if (diskSessions.Count == 0) return;

        foreach (var tab in tabs)
        {
            if (tab.CopilotSessionId != null) continue;

            var cleanTitle = (tab.Title ?? "")
                .Replace("🤖", "", StringComparison.Ordinal).Trim();
            var tabPath = tab.Path ?? "";
            var hasUsefulTitle = cleanTitle.Length >= 4;
            var hasUsefulPath = !string.IsNullOrEmpty(tabPath);

            if (!hasUsefulTitle && !hasUsefulPath) continue;

            // Score every candidate session
            (string SessionId, string Summary, string Cwd, DateTime Updated, double Score)? best = null;
            foreach (var s in diskSessions)
            {
                if (claimed.Contains(s.SessionId)) continue;

                double score = 0;

                // Path match (strongest signal)
                if (hasUsefulPath && !string.IsNullOrEmpty(s.Cwd))
                {
                    if (tabPath.Equals(s.Cwd, StringComparison.OrdinalIgnoreCase))
                        score += 100;
                    else if (tabPath.StartsWith(s.Cwd, StringComparison.OrdinalIgnoreCase)
                          || s.Cwd.StartsWith(tabPath, StringComparison.OrdinalIgnoreCase))
                        score += 50;
                }

                // Title/summary match
                if (hasUsefulTitle && !string.IsNullOrEmpty(s.Summary))
                {
                    if (cleanTitle.Equals(s.Summary, StringComparison.OrdinalIgnoreCase))
                        score += 80;
                    else if (cleanTitle.Contains(s.Summary, StringComparison.OrdinalIgnoreCase)
                          || s.Summary.Contains(cleanTitle, StringComparison.OrdinalIgnoreCase))
                        score += 40;
                }

                if (score == 0) continue;

                // Timestamp proximity bonus (closer to refTime = higher bonus, max +20)
                if (refTime.HasValue)
                {
                    var deltaHours = Math.Abs((s.Updated - refTime.Value).TotalHours);
                    // 0h delta → +20, 24h → +10, 1week → 0
                    score += Math.Max(0, 20 - (deltaHours * 20.0 / 168.0));
                }
                else
                {
                    // No reference time — prefer most recently updated
                    score += Math.Max(0, 10 - (DateTime.Now - s.Updated).TotalDays * 0.1);
                }

                if (best == null || score > best.Value.Score)
                    best = (s.SessionId, s.Summary, s.Cwd, s.Updated, score);
            }

            // Require minimum score to avoid garbage matches
            if (best == null || best.Value.Score < 40) continue;

            tab.HasCopilot = true;
            tab.CopilotSessionId = best.Value.SessionId;
            if (!string.IsNullOrEmpty(best.Value.Summary))
                tab.CopilotSummary = best.Value.Summary;
            if (string.IsNullOrEmpty(tab.Path) && !string.IsNullOrEmpty(best.Value.Cwd))
            {
                tab.Path = best.Value.Cwd;
                tab.Folder = Path.GetFileName(best.Value.Cwd);
                tab.DirExists = Directory.Exists(best.Value.Cwd);
            }
            claimed.Add(best.Value.SessionId);
        }
    }

    /// <summary>
    /// Get all closed windows that had tabs, with their last known tab data.
    /// </summary>
    public List<ClosedWindowRecord> GetClosedWindowsWithTabs(int limit = 20)
    {
        var result = new List<ClosedWindowRecord>();
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT w.id, w.first_seen, w.last_seen, w.closed_at
            FROM windows w
            WHERE w.closed_at IS NOT NULL
            ORDER BY w.closed_at DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            var firstSeen = reader.GetString(1);
            var lastSeen = reader.GetString(2);
            var closedAt = reader.IsDBNull(3) ? null : reader.GetString(3);
            var tabs = GetLastKnownTabs(id, closedAt ?? lastSeen);
            if (tabs.Count > 0)
                result.Add(new ClosedWindowRecord(id, firstSeen, lastSeen, closedAt, tabs));
        }
        return result;
    }

    /// <summary>Execute a read-only SQL query and return results as formatted text.</summary>
    public string ExecuteQuery(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();

        var sb = new System.Text.StringBuilder();
        var cols = reader.FieldCount;

        // Header
        for (var i = 0; i < cols; i++)
        {
            if (i > 0) sb.Append(" │ ");
            sb.Append(reader.GetName(i).PadRight(20));
        }
        sb.AppendLine();
        sb.AppendLine(new string('─', Math.Min(cols * 23, 120)));

        var rows = 0;
        while (reader.Read() && rows < 200)
        {
            for (var i = 0; i < cols; i++)
            {
                if (i > 0) sb.Append(" │ ");
                var val = reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToString() ?? "";
                sb.Append(val.PadRight(20)[..Math.Min(val.Length, 20)].PadRight(20));
            }
            sb.AppendLine();
            rows++;
        }

        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"\n({rows} rows)"));
        return sb.ToString();
    }

    public string DbPath => _dbPath;

    public void Dispose() => _conn.Dispose();
}

// Data models for captures
public sealed class CaptureData
{
    public string CapturedAt { get; set; } = "";
    public string? SourceMtime { get; set; }
    public int? LiveWindowCount { get; set; }
    public int StateWindowCount { get; set; }
    public int TotalTabs { get; set; }
    public int CopilotCount { get; set; }
    public string Quality { get; set; } = "unknown";
    public List<CaptureWindow> Windows { get; set; } = [];
    public List<CaptureEvent> Events { get; set; } = [];
}

public sealed class CaptureWindow
{
    public string Id { get; set; } = "";
    public string OpenedAt { get; set; } = "";
    public string LastSeen { get; set; } = "";
    public string? ClosedAt { get; set; }
    public int CopilotCount { get; set; }
    public bool IsLive { get; set; } = true;
    public List<CaptureTab> Tabs { get; set; } = [];
}

public sealed class CaptureTab
{
    public string Path { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Title { get; set; } = "";
    public string TabType { get; set; } = "shell";
    public string Commandline { get; set; } = "";
    public bool DirExists { get; set; } = true;
    public bool HasCopilot { get; set; }
    public string? CopilotSessionId { get; set; }
    public string? CopilotSummary { get; set; }
    public bool IsLiveDetected { get; set; }
    public List<CaptureTab> Panes { get; set; } = [];
}

public sealed record CaptureEvent(string Type, string? WindowId = null, string? Details = null);
public sealed record CaptureInfo(long Id, string CapturedAt, int? LiveWindowCount, int StateWindowCount, int TotalTabs, int CopilotCount, string Quality);
public sealed record EventRecord(string Timestamp, string EventType, string? WindowId, string? Details);
public sealed record ClosedWindowRecord(string Id, string FirstSeen, string LastSeen, string? ClosedAt, List<CaptureTab> Tabs);
