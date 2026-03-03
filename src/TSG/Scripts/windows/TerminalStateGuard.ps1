# TerminalStateGuard v2.0 - Watches state.json for ALL shells (READ-ONLY on copilot files)
$script:SnapshotDir = Join-Path $env:USERPROFILE ".copilotAccel\terminal-snapshots"
$script:StatePath = "$env:LOCALAPPDATA\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\state.json"

function Save-TerminalState {
    if (-not (Test-Path $script:SnapshotDir)) { New-Item $script:SnapshotDir -ItemType Directory -Force | Out-Null }
    if (-not (Test-Path $script:StatePath)) { return }
    $state = Get-Content $script:StatePath -Raw | ConvertFrom-Json
    if (-not $state.persistedWindowLayouts) { return }
    $totalTabs = 0; foreach ($w in $state.persistedWindowLayouts) { if ($w.tabLayout) { $totalTabs += $w.tabLayout.Count } }
    if ($totalTabs -eq 0) { return }
    $latestPath = Join-Path $script:SnapshotDir "latest.json"
    $previousTabs = 0; if (Test-Path $latestPath) { try { $previousTabs = (Get-Content $latestPath -Raw | ConvertFrom-Json).TotalTabs } catch {} }
    $copilotAll = @(); $sr = Join-Path $env:USERPROFILE ".copilot\session-state"
    if (Test-Path $sr) {
        Get-ChildItem $sr -Directory | ForEach-Object {
            $ws = Join-Path $_.FullName "workspace.yaml"
            if (Test-Path $ws) {
                $ct = Get-Content $ws -Raw
                $cwd = if ($ct -match 'cwd: (.+)') { $Matches[1].Trim() } else { $null }
                $sum = if ($ct -match 'summary: (.+)') { $Matches[1].Trim() } else { "" }
                $upd = if ($ct -match 'updated_at: (.+)') { $Matches[1].Trim() } else { "" }
                if ($cwd) { $copilotAll += @{ SessionId = $_.Name; Cwd = $cwd; Summary = $sum; Updated = $upd } }
            }
        }
    }
    $tabs = @(); $wi = 0
    foreach ($window in $state.persistedWindowLayouts) {
        $wi++
        if ($window.tabLayout) {
            foreach ($tab in $window.tabLayout) {
                $dir = $tab.startingDirectory
                if ($dir) {
                    $ci = $copilotAll | Where-Object { $_.Cwd -eq $dir -or $dir.StartsWith($_.Cwd + "\") -or $_.Cwd.StartsWith($dir + "\") } | Where-Object { $_.Summary -ne "" } | Sort-Object { $_.Updated } -Descending | Select-Object -First 1
                    if (-not $ci) { $ci = $copilotAll | Where-Object { $_.Cwd -eq $dir -or $dir.StartsWith($_.Cwd + "\") -or $_.Cwd.StartsWith($dir + "\") } | Sort-Object { $_.Updated } -Descending | Select-Object -First 1 }
                    $tabs += @{ Path = $dir; Window = $wi; HasCopilot = $null -ne $ci; CopilotId = if ($ci) { $ci.SessionId } else { $null }; Summary = if ($ci) { $ci.Summary } else { $null } }
                }
            }
        }
    }
    $snap = @{ Timestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss"); TotalTabs = $totalTabs; WindowCount = $state.persistedWindowLayouts.Count; CopilotCount = ($tabs | Where-Object { $_.HasCopilot }).Count; Tabs = $tabs }
    $snap | ConvertTo-Json -Depth 4 | Set-Content $latestPath -Encoding UTF8
    if ($totalTabs -ge $previousTabs) { $snap | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $script:SnapshotDir "$(Get-Date -Format 'yyyy-MM-dd_HH-mm-ss').json") -Encoding UTF8 }
    Get-ChildItem $script:SnapshotDir -Filter "202*.json" | Sort-Object Name -Descending | Select-Object -Skip 10 | Remove-Item -Force
}

$script:SnapshotDir = Join-Path $env:USERPROFILE ".copilotAccel\terminal-snapshots"
$script:StatePath = "$env:LOCALAPPDATA\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\state.json"

function Save-TerminalState {
    if (-not (Test-Path $script:SnapshotDir)) { New-Item $script:SnapshotDir -ItemType Directory -Force | Out-Null }
    if (-not (Test-Path $script:StatePath)) { return }
    $state = Get-Content $script:StatePath -Raw | ConvertFrom-Json
    if (-not $state.persistedWindowLayouts) { return }
    $totalTabs = 0
    foreach ($w in $state.persistedWindowLayouts) { if ($w.tabLayout) { $totalTabs += $w.tabLayout.Count } }
    if ($totalTabs -eq 0) { return }
    $latestPath = Join-Path $script:SnapshotDir "latest.json"
    $previousTabs = 0
    if (Test-Path $latestPath) { try { $previousTabs = (Get-Content $latestPath -Raw | ConvertFrom-Json).TotalTabs } catch {} }
    $copilotAll = @()
    $sr = Join-Path $env:USERPROFILE ".copilot\session-state"
    if (Test-Path $sr) {
        Get-ChildItem $sr -Directory | ForEach-Object {
            $ws = Join-Path $_.FullName "workspace.yaml"
            if (Test-Path $ws) {
                $ct = Get-Content $ws -Raw
                $cwd = if ($ct -match 'cwd: (.+)') { $Matches[1].Trim() } else { $null }
                $sum = if ($ct -match 'summary: (.+)') { $Matches[1].Trim() } else { "" }
                $upd = if ($ct -match 'updated_at: (.+)') { $Matches[1].Trim() } else { "" }
                if ($cwd) { $copilotAll += @{ SessionId = $_.Name; Cwd = $cwd; Summary = $sum; Updated = $upd } }
            }
        }
    }
    $tabs = @()
    $wi = 0
    foreach ($window in $state.persistedWindowLayouts) {
        $wi++
        if ($window.tabLayout) {
            foreach ($tab in $window.tabLayout) {
                $dir = $tab.startingDirectory
                if ($dir) {
                    $ci = $copilotAll | Where-Object { $_.Cwd -eq $dir -or $dir.StartsWith($_.Cwd + "\") -or $_.Cwd.StartsWith($dir + "\") } | Where-Object { $_.Summary -ne "" } | Sort-Object { $_.Updated } -Descending | Select-Object -First 1
                    if (-not $ci) { $ci = $copilotAll | Where-Object { $_.Cwd -eq $dir -or $dir.StartsWith($_.Cwd + "\") -or $_.Cwd.StartsWith($dir + "\") } | Sort-Object { $_.Updated } -Descending | Select-Object -First 1 }
                    $tabs += @{ Path = $dir; Window = $wi; HasCopilot = $null -ne $ci; CopilotId = if ($ci) { $ci.SessionId } else { $null }; Summary = if ($ci) { $ci.Summary } else { $null } }
                }
            }
        }
    }
    $snap = @{ Timestamp = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss"); TotalTabs = $totalTabs; WindowCount = $state.persistedWindowLayouts.Count; CopilotCount = ($tabs | Where-Object { $_.HasCopilot }).Count; Tabs = $tabs }
    $snap | ConvertTo-Json -Depth 4 | Set-Content $latestPath -Encoding UTF8
    if ($totalTabs -ge $previousTabs) { $snap | ConvertTo-Json -Depth 4 | Set-Content (Join-Path $script:SnapshotDir "$(Get-Date -Format 'yyyy-MM-dd_HH-mm-ss').json") -Encoding UTF8 }
    Get-ChildItem $script:SnapshotDir -Filter "202*.json" | Sort-Object Name -Descending | Select-Object -Skip 10 | Remove-Item -Force
}

function Start-TerminalWatcher {
    if (-not (Test-Path $script:StatePath)) { Write-Host "  ❌ state.json not found" -ForegroundColor Red; return }
    Write-Host "  🛡️ Terminal State Guard active" -ForegroundColor Green
    Write-Host "  📁 $script:SnapshotDir" -ForegroundColor DarkGray
    Write-Host "  Ctrl+C to stop" -ForegroundColor Yellow
    Save-TerminalState
    $lastWrite = (Get-Item $script:StatePath).LastWriteTime
    while ($true) {
        Start-Sleep 3
        try {
            $cw = (Get-Item $script:StatePath).LastWriteTime
            if ($cw -ne $lastWrite) {
                $lastWrite = $cw
                Save-TerminalState
                $s = Get-Content (Join-Path $script:SnapshotDir "latest.json") -Raw | ConvertFrom-Json
                Write-Host "  💾 $(Get-Date -Format 'HH:mm:ss') | $($s.WindowCount) win | $($s.TotalTabs) tabs | $($s.CopilotCount) copilot" -ForegroundColor Cyan
            }
        } catch {}
    }
}
