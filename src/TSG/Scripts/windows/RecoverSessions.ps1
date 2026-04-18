# RecoverSessions v3.0 - Window-grouped recovery with timestamps
param([int]$Days = 7)

$script:MaxSessions = if ($env:TSG_MAX_SNAPSHOTS) { [int]$env:TSG_MAX_SNAPSHOTS } else {
    $cfgPath = Join-Path $env:USERPROFILE ".tsg\tsg-config.json"
    if (Test-Path $cfgPath) { try { (Get-Content $cfgPath -Raw | ConvertFrom-Json).MaxSnapshots } catch { 50 } } else { 50 }
}

function Get-CopilotSessionMap {
    $all = @(); $sr = Join-Path $env:USERPROFILE ".copilot\session-state"
    if (-not (Test-Path $sr)) { return $all }
    Get-ChildItem $sr -Directory | ForEach-Object {
        $ws = Join-Path $_.FullName "workspace.yaml"; if (Test-Path $ws) {
            $c = Get-Content $ws -Raw; $cwd = if ($c -match 'cwd: (.+)') { $Matches[1].Trim() } else { $null }
            $sum = if ($c -match 'summary: (.+)') { $Matches[1].Trim() } else { "" }
            $upd = if ($c -match 'updated_at: (.+)') { $Matches[1].Trim() } else { "" }
            if ($cwd) { $all += @{ SessionId = $_.Name; Cwd = $cwd; Summary = $sum; Updated = $upd } }
    } }; return $all
}
function Find-BestSession { param([string]$Dir, [array]$All)
    $m = $All | Where-Object { $_.Cwd -eq $Dir -or $Dir.StartsWith($_.Cwd + "\") -or $_.Cwd.StartsWith($Dir + "\") }
    if (-not $m) { return $null }
    $ws = $m | Where-Object { $_.Summary -ne "" } | Sort-Object { $_.Updated } -Descending
    if ($ws) { return ($ws | Select-Object -First 1) }
    return ($m | Sort-Object { $_.Updated } -Descending | Select-Object -First 1)
}
function Get-AllSessions { param([int]$Days = 7)
    $s = @{ Tabs = @(); FromSnapshot = $false; AllSessions = @(); TrackedWindows = @() }
    $ca = Get-CopilotSessionMap
    $sp = "$env:LOCALAPPDATA\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\state.json"
    $snp = Join-Path $env:USERPROFILE ".copilotAccel\terminal-snapshots\latest.json"
    $awp = Join-Path $env:USERPROFILE ".tsg\windows\active-windows.json"
    $matchedIds = @()

    # Read tracked windows (new v3.0)
    if (Test-Path $awp) {
        try {
            $awData = Get-Content $awp -Raw | ConvertFrom-Json
            if ($awData.Windows) { $s.TrackedWindows = @($awData.Windows) }
        } catch {}
    }

    if (Test-Path $sp) { $st = Get-Content $sp -Raw | ConvertFrom-Json
        if ($st.persistedWindowLayouts) { $wi = 0; foreach ($w in $st.persistedWindowLayouts) { $wi++; if ($w.tabLayout) { foreach ($t in $w.tabLayout) { $d = $t.startingDirectory
            if ($d -and (Test-Path $d -ErrorAction SilentlyContinue)) { $ci = Find-BestSession $d $ca; $s.Tabs += @{ Folder = Split-Path $d -Leaf; Path = $d; Window = $wi; HasCopilot = $null -ne $ci; CopilotId = if ($ci) { $ci.SessionId } else { $null }; Summary = if ($ci) { $ci.Summary } else { $null } }
                if ($ci) { $matchedIds += $ci.SessionId }
            }
    } } } } }
    if ($s.Tabs.Count -eq 0 -and (Test-Path $snp)) { $s.FromSnapshot = $true; $sn = Get-Content $snp -Raw | ConvertFrom-Json
        foreach ($t in $sn.Tabs) { if ($t.Path -and (Test-Path $t.Path -ErrorAction SilentlyContinue)) { $s.Tabs += @{ Folder = Split-Path $t.Path -Leaf; Path = $t.Path; Window = $t.Window; HasCopilot = $t.HasCopilot; CopilotId = $t.CopilotId; Summary = $t.Summary }
            if ($t.CopilotId) { $matchedIds += $t.CopilotId }
        } }
    }
    $s.AllSessions = $ca | Where-Object { $_.SessionId -notin $matchedIds } | Sort-Object { $_.Updated } -Descending | Select-Object -First $script:MaxSessions
    return $s
}

Write-Host "`n  🔄 Session Recovery v3.0  (max: $script:MaxSessions)" -ForegroundColor Cyan
$sess = Get-AllSessions -Days $Days; $cc = ($sess.Tabs | Where-Object { $_.HasCopilot }).Count
Write-Host "  ✅ $($sess.Tabs.Count) tabs ($cc copilot) + $($sess.AllSessions.Count) sessions`n" -ForegroundColor Green

$items = @()

# Section 1: Tracked Windows (grouped by window)
$closedWindows = @($sess.TrackedWindows | Where-Object { $_.ClosedAt })
if ($closedWindows.Count -gt 0) {
    Write-Host "  ── Last Windows ──" -ForegroundColor Magenta
    foreach ($win in $closedWindows) {
        $items += @{ Type = "window"; Data = $win }
        $n = $items.Count
        $tabCount = if ($win.Tabs) { $win.Tabs.Count } else { 0 }
        $copCount = if ($win.CopilotCount) { $win.CopilotCount } else { 0 }
        $wid = if ($win.Id.Length -gt 8) { $win.Id.Substring(0, 8) } else { $win.Id }
        Write-Host "  [$n] 🪟 Window [$wid]  📑 $tabCount tabs  🤖 $copCount" -ForegroundColor White
        Write-Host "      📅 $($win.OpenedAt) → ❌ $($win.ClosedAt)" -ForegroundColor DarkGray
        if ($win.Tabs) {
            foreach ($t in $win.Tabs) {
                $folder = if ($t.Path) { Split-Path $t.Path -Leaf } else { "(no dir)" }
                $tabType = if ($t.TabType) { $t.TabType } else { "shell" }
                $icon = if ($t.HasCopilot) { "🤖" } elseif ($tabType -eq "cmd") { "⬛" } else { "📂" }
                $label = if ($t.Title -and $t.Title -ne "PowerShell7" -and $t.Title -ne "Default") { " [$($t.Title)]" } elseif ($tabType -eq "cmd") { " [CMD]" } else { "" }
                Write-Host "      $icon $folder$label" -ForegroundColor DarkGray -NoNewline
                if ($t.Summary) { Write-Host "  💬 $($t.Summary)" -ForegroundColor DarkCyan -NoNewline }
                Write-Host ""
            }
        }
    }
    Write-Host ""
}

# Section 2: Open tabs
if ($sess.Tabs.Count -gt 0) {
    Write-Host "  ── Open Tabs ──" -ForegroundColor Yellow
    foreach ($tab in $sess.Tabs) {
        $items += @{ Type = "tab"; Data = $tab }
        $n = $items.Count; $ic = if ($tab.HasCopilot) { "🤖" } else { "📂" }
        Write-Host "  [$n] $ic $($tab.Folder) (Win $($tab.Window))" -ForegroundColor White
        if ($tab.Summary) { Write-Host "      💬 $($tab.Summary)" -ForegroundColor DarkCyan }
        if ($tab.CopilotId) { Write-Host "      🔑 $($tab.CopilotId)" -ForegroundColor DarkGray }
    }
}

# Section 3: All Copilot sessions (not in open tabs)
if ($sess.AllSessions.Count -gt 0) {
    Write-Host "`n  ── Copilot Sessions ──" -ForegroundColor Yellow
    foreach ($cs in $sess.AllSessions) {
        $items += @{ Type = "session"; Data = $cs }
        $n = $items.Count; $folder = Split-Path $cs.Cwd -Leaf
        Write-Host "  [$n] 🤖 $folder" -ForegroundColor White
        if ($cs.Summary) { Write-Host "      💬 $($cs.Summary)" -ForegroundColor DarkCyan }
        Write-Host "      🔑 $($cs.SessionId)  📅 $($cs.Updated)" -ForegroundColor DarkGray
    }
}

if ($items.Count -eq 0) { Write-Host "  No sessions found" -ForegroundColor Yellow; return }
Write-Host "`n  Numbers (comma-sep), A=all tabs, W=restore windows, Q=quit:" -ForegroundColor Yellow; $inp = Read-Host "  Choice"
if ($inp -eq "Q") { return }
$s = "C:\Program Files\PowerShell\7\pwsh.exe"

if ($inp -eq "W") {
    # Restore all closed windows with their tabs
    foreach ($item in $items) {
        if ($item.Type -ne "window") { continue }
        $win = $item.Data
        if (-not $win.Tabs -or $win.Tabs.Count -eq 0) { continue }
        $firstTab = $win.Tabs[0]
        $firstCmd = if ($firstTab.HasCopilot -and $firstTab.CopilotId) { "-d `"$($firstTab.Path)`" `"$s`" -NoExit -Command `"copilot --resume=$($firstTab.CopilotId)`"" } else { "-d `"$($firstTab.Path)`" `"$s`"" }
        $wtArgs = $firstCmd
        for ($i = 1; $i -lt $win.Tabs.Count; $i++) {
            $t = $win.Tabs[$i]
            $tabCmd = if ($t.HasCopilot -and $t.CopilotId) { "new-tab -d `"$($t.Path)`" `"$s`" -NoExit -Command `"copilot --resume=$($t.CopilotId)`"" } else { "new-tab -d `"$($t.Path)`" `"$s`"" }
            $wtArgs += " `; $tabCmd"
        }
        Start-Process wt -ArgumentList $wtArgs
        Write-Host "  🚀 Restored window with $($win.Tabs.Count) tabs" -ForegroundColor Green
        Start-Sleep 2
    }
    Write-Host "`n  ✅ Done!" -ForegroundColor Green
    return
}

$idx = if ($inp -eq "A") { 1..$items.Count } else { $inp -split "," | ForEach-Object { [int]$_.Trim() } }
foreach ($x in $idx) {
    if ($x -lt 1 -or $x -gt $items.Count) { continue }
    $item = $items[$x-1]
    if ($item.Type -eq "window") {
        # Restore entire window with composite wt command
        $win = $item.Data
        if (-not $win.Tabs -or $win.Tabs.Count -eq 0) { continue }
        $firstTab = $win.Tabs[0]
        $firstCmd = if ($firstTab.HasCopilot -and $firstTab.CopilotId) { "-d `"$($firstTab.Path)`" `"$s`" -NoExit -Command `"copilot --resume=$($firstTab.CopilotId)`"" } else { "-d `"$($firstTab.Path)`" `"$s`"" }
        $wtArgs = $firstCmd
        for ($i = 1; $i -lt $win.Tabs.Count; $i++) {
            $t = $win.Tabs[$i]
            $tabCmd = if ($t.HasCopilot -and $t.CopilotId) { "new-tab -d `"$($t.Path)`" `"$s`" -NoExit -Command `"copilot --resume=$($t.CopilotId)`"" } else { "new-tab -d `"$($t.Path)`" `"$s`"" }
            $wtArgs += " `; $tabCmd"
        }
        Start-Process wt -ArgumentList $wtArgs
        Write-Host "  🚀 Restored window with $($win.Tabs.Count) tabs" -ForegroundColor Green
    }
    elseif ($item.Type -eq "tab") {
        $t = $item.Data
        if ($t.HasCopilot -and $t.CopilotId) { Start-Process wt -ArgumentList "-d `"$($t.Path)`" `"$s`" -NoExit -Command `"copilot --resume=$($t.CopilotId)`""; Write-Host "  🚀 $($t.Folder) + copilot" -ForegroundColor Green }
        else { Start-Process wt -ArgumentList "-d `"$($t.Path)`" `"$s`""; Write-Host "  📂 $($t.Folder)" -ForegroundColor Cyan }
    }
    elseif ($item.Type -eq "session") {
        $cs = $item.Data
        Start-Process wt -ArgumentList "-d `"$($cs.Cwd)`" `"$s`" -NoExit -Command `"copilot --resume=$($cs.SessionId)`""; Write-Host "  🚀 $(Split-Path $cs.Cwd -Leaf) + copilot" -ForegroundColor Green
    }
    Start-Sleep 1
}
Write-Host "`n  ✅ Done!" -ForegroundColor Green