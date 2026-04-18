# TerminalStateGuard v3.0 - Window+Tab state tracking with UUID-based identity
$script:SnapshotDir = Join-Path $env:USERPROFILE ".copilotAccel\terminal-snapshots"
$script:StatePath = "$env:LOCALAPPDATA\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\state.json"
$script:WindowsDir = Join-Path $env:USERPROFILE ".tsg\windows"
$script:ActiveWindowsPath = Join-Path $script:WindowsDir "active-windows.json"
$script:WindowHistoryDir = Join-Path $script:WindowsDir "history"
$script:MissCount = @{} # Track consecutive misses per window ID for reliable close detection
$script:LastGoodState = $null
$script:MaxSnapshots = if ($env:TSG_MAX_SNAPSHOTS) { [int]$env:TSG_MAX_SNAPSHOTS } else {
    $cfgPath = Join-Path $env:USERPROFILE ".tsg\tsg-config.json"
    if (Test-Path $cfgPath) { try { (Get-Content $cfgPath -Raw | ConvertFrom-Json).MaxSnapshots } catch { 50 } } else { 50 }
}

function Get-CopilotSessions {
    $all = @(); $sr = Join-Path $env:USERPROFILE ".copilot\session-state"
    if (-not (Test-Path $sr)) { return $all }
    Get-ChildItem $sr -Directory | ForEach-Object {
        $ws = Join-Path $_.FullName "workspace.yaml"
        if (Test-Path $ws) {
            $ct = Get-Content $ws -Raw
            $cwd = if ($ct -match 'cwd: (.+)') { $Matches[1].Trim() } else { $null }
            $sum = if ($ct -match 'summary: (.+)') { $Matches[1].Trim() } else { "" }
            $upd = if ($ct -match 'updated_at: (.+)') { $Matches[1].Trim() } else { "" }
            if ($cwd) { $all += @{ SessionId = $_.Name; Cwd = $cwd; Summary = $sum; Updated = $upd } }
        }
    }
    return $all
}

function Find-BestCopilotSession { param([string]$Dir, [array]$All)
    $m = $All | Where-Object { $_.Cwd -eq $Dir -or $Dir.StartsWith($_.Cwd + "\") -or $_.Cwd.StartsWith($Dir + "\") }
    if (-not $m) { return $null }
    $ws = $m | Where-Object { $_.Summary -ne "" } | Sort-Object { $_.Updated } -Descending
    if ($ws) { return ($ws | Select-Object -First 1) }
    return ($m | Sort-Object { $_.Updated } -Descending | Select-Object -First 1)
}

function Get-WindowSimilarity { param($ExistingTabs, $NewPaths)
    # Jaccard similarity between existing window tabs and new tab paths
    if (-not $ExistingTabs -or $ExistingTabs.Count -eq 0 -or -not $NewPaths -or $NewPaths.Count -eq 0) { return 0.0 }
    $existPaths = @($ExistingTabs | ForEach-Object { $_.Path.ToLower() })
    $newLower = @($NewPaths | ForEach-Object { $_.ToLower() })
    $intersection = @($existPaths | Where-Object { $_ -in $newLower }).Count
    $union = @(($existPaths + $newLower) | Select-Object -Unique).Count
    if ($union -eq 0) { return 0.0 }
    return [double]$intersection / [double]$union
}

function Read-ActiveWindows {
    if (-not (Test-Path $script:ActiveWindowsPath)) { return @{ Windows = @(); LastUpdated = $null } }
    try { return (Get-Content $script:ActiveWindowsPath -Raw | ConvertFrom-Json) }
    catch { return @{ Windows = @(); LastUpdated = $null } }
}

function Write-ActiveWindowsAtomic { param($Data)
    if (-not (Test-Path $script:WindowsDir)) { New-Item $script:WindowsDir -ItemType Directory -Force | Out-Null }
    $tmpPath = Join-Path $script:WindowsDir "active-windows.tmp.$PID.json"
    $Data | ConvertTo-Json -Depth 6 | Set-Content $tmpPath -Encoding UTF8
    Move-Item $tmpPath $script:ActiveWindowsPath -Force
}

function Save-WindowHistory { param($Window)
    if (-not (Test-Path $script:WindowHistoryDir)) { New-Item $script:WindowHistoryDir -ItemType Directory -Force | Out-Null }
    $ts = Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'
    $shortId = if ($Window.Id.Length -gt 8) { $Window.Id.Substring(0, 8) } else { $Window.Id }
    $histFile = Join-Path $script:WindowHistoryDir "${shortId}_${ts}.json"
    $Window | ConvertTo-Json -Depth 6 | Set-Content $histFile -Encoding UTF8
    # Cleanup: keep max history entries
    Get-ChildItem $script:WindowHistoryDir -Filter "*.json" | Sort-Object Name -Descending | Select-Object -Skip ($script:MaxSnapshots * 2) | Remove-Item -Force
}

function Save-TerminalState {
    if (-not (Test-Path $script:SnapshotDir)) { New-Item $script:SnapshotDir -ItemType Directory -Force | Out-Null }
    if (-not (Test-Path $script:StatePath)) { return }

    # Retry-safe read of state.json
    $state = $null
    for ($retry = 0; $retry -lt 3; $retry++) {
        try {
            $raw = Get-Content $script:StatePath -Raw
            $state = $raw | ConvertFrom-Json
            $script:LastGoodState = $state
            break
        } catch {
            if ($retry -lt 2) { Start-Sleep -Milliseconds 200 }
        }
    }
    if (-not $state) { $state = $script:LastGoodState }
    if (-not $state -or -not $state.persistedWindowLayouts) { return }

    # Live window count via UI Automation — detect actual open terminal windows
    $liveWindowCount = $null
    try {
        Add-Type -AssemblyName UIAutomationClient -ErrorAction SilentlyContinue
        $root = [System.Windows.Automation.AutomationElement]::RootElement
        $cond = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ClassNameProperty, "CASCADIA_HOSTING_WINDOW_CLASS"
        )
        $liveWindowCount = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond).Count
    } catch { }

    # If state.json has more windows than actually open, trim to match live count
    $layouts = @($state.persistedWindowLayouts)
    if ($liveWindowCount -ne $null -and $liveWindowCount -lt $layouts.Count) {
        # Keep only windows that have tabs (non-empty), prefer those with more tabs
        $withTabs = @($layouts | Where-Object {
            $tabCount = @($_.tabLayout | Where-Object { $_.action -eq 'newTab' }).Count
            $tabCount -gt 0
        } | Sort-Object { @($_.tabLayout | Where-Object { $_.action -eq 'newTab' }).Count } -Descending)
        if ($withTabs.Count -gt $liveWindowCount -and $liveWindowCount -gt 0) {
            $layouts = @($withTabs | Select-Object -First $liveWindowCount)
        }
    }

    $copilotAll = Get-CopilotSessions
    $now = Get-Date
    $nowStr = $now.ToString("yyyy-MM-dd HH:mm:ss")

    # Build current window state from state.json — include ALL tabs
    # tabLayout is a flat list of replay actions: newTab, splitPane, switchToTab
    # newTab = actual tab; splitPane = pane within the preceding tab; switchToTab = focus command (skip)
    $currentWindows = @()
    $totalTabs = 0
    $wi = 0
    foreach ($window in $layouts) {
        $wi++
        $winTabs = @()
        if ($window.tabLayout) {
            $currentTab = $null
            foreach ($entry in $window.tabLayout) {
                $action = $entry.action
                if ($action -eq 'switchToTab') { continue }

                $dir = $entry.startingDirectory
                $title = if ($entry.tabTitle) { $entry.tabTitle } else { "" }
                $cmdline = if ($entry.commandline) { $entry.commandline } else { "" }

                # Detect tab type from commandline and title
                $tabType = "shell"
                if ($cmdline -match 'copilot') { $tabType = "copilot" }
                elseif ($cmdline -match 'cmd\.exe' -or $title -eq 'Command Prompt') { $tabType = "cmd" }
                elseif ($cmdline -match 'pwsh|powershell') { $tabType = "pwsh" }
                elseif ($cmdline -match 'wsl|bash') { $tabType = "wsl" }

                # Check if directory exists
                $dirExists = if ($dir) { Test-Path $dir } else { $true }

                $ci = $null
                if ($dir) { $ci = Find-BestCopilotSession $dir $copilotAll }

                $paneInfo = @{
                    Path       = if ($dir) { $dir } else { "" }
                    Title      = $title
                    Commandline = $cmdline
                    TabType    = $tabType
                    DirExists  = $dirExists
                    HasCopilot = $null -ne $ci
                    CopilotId  = if ($ci) { $ci.SessionId } else { $null }
                    Summary    = if ($ci) { $ci.Summary } else { $null }
                }

                if ($action -eq 'newTab') {
                    # Start a new tab with optional Panes array
                    $currentTab = $paneInfo
                    $currentTab.Panes = @()
                    $winTabs += $currentTab
                }
                elseif ($action -eq 'splitPane' -and $currentTab) {
                    # Add as a pane within the current tab
                    $currentTab.Panes += $paneInfo
                }
            }
        }
        $totalTabs += $winTabs.Count
        if ($winTabs.Count -gt 0) {
            $currentWindows += @{
                Index = $wi
                Paths = @($winTabs | Where-Object { $_.Path -ne "" } | ForEach-Object { $_.Path })
                Tabs = $winTabs
            }
        }
    }

    # --- Window Identity Tracking ---
    $activeData = Read-ActiveWindows
    $existingWindows = @()
    if ($activeData.Windows) { $existingWindows = @($activeData.Windows) }

    $matchedExistingIds = @()
    $updatedWindows = @()

    # Match current windows to existing by similarity
    foreach ($cw in $currentWindows) {
        $bestMatch = $null; $bestScore = 0.0
        foreach ($ew in $existingWindows) {
            if ($ew.Id -in $matchedExistingIds) { continue }
            if ($ew.ClosedAt) { continue }
            $score = Get-WindowSimilarity $ew.Tabs $cw.Paths
            if ($score -gt $bestScore -and $score -ge 0.3) {
                $bestScore = $score; $bestMatch = $ew
            }
        }

        if ($bestMatch) {
            $matchedExistingIds += $bestMatch.Id
            $script:MissCount[$bestMatch.Id] = 0
            $updatedWindows += @{
                Id = $bestMatch.Id
                OpenedAt = $bestMatch.OpenedAt
                LastSeenAt = $nowStr
                ClosedAt = $null
                TabCount = $cw.Tabs.Count
                CopilotCount = @($cw.Tabs | Where-Object { $_.HasCopilot }).Count
                Tabs = $cw.Tabs
            }
        } else {
            # New window — assign UUID
            $newId = [guid]::NewGuid().ToString("N").Substring(0, 12)
            $updatedWindows += @{
                Id = $newId
                OpenedAt = $nowStr
                LastSeenAt = $nowStr
                ClosedAt = $null
                TabCount = $cw.Tabs.Count
                CopilotCount = @($cw.Tabs | Where-Object { $_.HasCopilot }).Count
                Tabs = $cw.Tabs
            }
        }
    }

    # Handle windows that disappeared — close immediately if live count confirms, else 3-miss threshold
    $useLiveClose = ($liveWindowCount -ne $null -and $liveWindowCount -lt $existingWindows.Count)
    foreach ($ew in $existingWindows) {
        if ($ew.Id -in $matchedExistingIds) { continue }
        if ($ew.ClosedAt) {
            # Already closed, keep in list briefly for recovery
            $updatedWindows += $ew
            continue
        }
        $missKey = $ew.Id
        if (-not $script:MissCount.ContainsKey($missKey)) { $script:MissCount[$missKey] = 0 }
        $script:MissCount[$missKey]++

        # Close immediately when live window count confirms fewer windows, else wait for 3 misses
        $shouldClose = $useLiveClose -or ($script:MissCount[$missKey] -ge 3)
        if ($shouldClose) {
            # Confirmed closed — save to history and mark closed
            $closedWin = @{
                Id = $ew.Id; OpenedAt = $ew.OpenedAt; LastSeenAt = $ew.LastSeenAt
                ClosedAt = $nowStr; TabCount = $ew.TabCount; CopilotCount = $ew.CopilotCount; Tabs = $ew.Tabs
            }
            Save-WindowHistory $closedWin
            $updatedWindows += $closedWin
            $script:MissCount.Remove($missKey)
        } else {
            # Not yet confirmed closed — keep as active
            $updatedWindows += $ew
        }
    }

    # Remove windows closed more than 24h ago from active list
    $cutoff = $now.AddHours(-24).ToString("yyyy-MM-dd HH:mm:ss")
    $updatedWindows = @($updatedWindows | Where-Object { -not $_.ClosedAt -or $_.ClosedAt -gt $cutoff })

    # Save active windows atomically
    $activeState = @{
        Windows = $updatedWindows
        LastUpdated = $nowStr
        TotalTabs = $totalTabs
        TotalWindows = ($updatedWindows | Where-Object { -not $_.ClosedAt }).Count
    }
    Write-ActiveWindowsAtomic $activeState

    # --- Legacy Snapshot (backward compatible) ---
    $allTabs = @()
    foreach ($cw in $currentWindows) {
        foreach ($t in $cw.Tabs) {
            $allTabs += @{ Path = $t.Path; Window = $cw.Index; Title = $t.Title; TabType = $t.TabType; HasCopilot = $t.HasCopilot; CopilotId = $t.CopilotId; Summary = $t.Summary }
        }
    }
    $latestPath = Join-Path $script:SnapshotDir "latest.json"
    $snap = @{
        Timestamp = $nowStr; TotalTabs = $totalTabs
        WindowCount = $layouts.Count
        CopilotCount = @($allTabs | Where-Object { $_.HasCopilot }).Count; Tabs = $allTabs
    }
    $snap | ConvertTo-Json -Depth 4 | Set-Content $latestPath -Encoding UTF8
    # Always save timestamped snapshot (removed the totalTabs >= previousTabs guard)
    $snap | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $script:SnapshotDir "$(Get-Date -Format 'yyyy-MM-dd_HH-mm-ss').json") -Encoding UTF8
    Get-ChildItem $script:SnapshotDir -Filter "202*.json" | Sort-Object Name -Descending | Select-Object -Skip $script:MaxSnapshots | Remove-Item -Force
}

function Start-TerminalWatcher {
    if (-not (Test-Path $script:StatePath)) { Write-Host "  ❌ state.json not found" -ForegroundColor Red; return }
    Write-Host "  🛡️ Terminal State Guard v4.0 — SQLite Event-driven Tracking" -ForegroundColor Green
    Write-Host "  📁 Snapshots: $script:SnapshotDir" -ForegroundColor DarkGray
    Write-Host "  📁 Database:  $(Join-Path $env:USERPROFILE '.tsg\terminal.db')" -ForegroundColor DarkGray
    Write-Host "  ⚡ Updates on tab/window open/close (FileSystemWatcher)" -ForegroundColor DarkGray
    Write-Host "  Ctrl+C to stop" -ForegroundColor Yellow

    # Initial capture via tsg capture (writes to SQLite + JSON)
    Save-TerminalState
    & tsg capture -q 2>$null
    Write-Host "  ✅ Initial snapshot + DB capture saved" -ForegroundColor Green

    # Event-driven: react to state.json changes (tab/window open or close)
    $dir = Split-Path $script:StatePath
    $file = Split-Path $script:StatePath -Leaf
    $watcher = New-Object System.IO.FileSystemWatcher $dir, $file
    $watcher.NotifyFilter = [System.IO.NotifyFilters]::LastWrite
    $watcher.EnableRaisingEvents = $true

    $debounceMs = 500
    $script:LastEventTime = [datetime]::MinValue

    $action = {
        $now = [datetime]::UtcNow
        $elapsed = ($now - $script:LastEventTime).TotalMilliseconds
        if ($elapsed -lt $debounceMs) { return }
        $script:LastEventTime = $now

        try {
            Start-Sleep -Milliseconds 300  # Let WT finish writing
            Save-TerminalState
            & tsg capture -q 2>$null
            $aw = Read-ActiveWindows
            $openWins = @($aw.Windows | Where-Object { -not $_.ClosedAt })
            $closedWins = @($aw.Windows | Where-Object { $_.ClosedAt })
            Write-Host "  💾 $(Get-Date -Format 'HH:mm:ss') | $($openWins.Count) win | $($aw.TotalTabs) tabs | closed: $($closedWins.Count)" -ForegroundColor Cyan
        } catch {}
    }

    Register-ObjectEvent $watcher 'Changed' -Action $action | Out-Null

    try {
        while ($true) { Start-Sleep 60 }
    } finally {
        $watcher.EnableRaisingEvents = $false
        $watcher.Dispose()
        Get-EventSubscriber | Where-Object { $_.SourceObject -eq $watcher } | Unregister-Event -ErrorAction SilentlyContinue
    }
}
