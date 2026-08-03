# RecoverSessions - Window-grouped recovery with timestamps
param(
    [int]$Days = 7,
    [ValidateRange(1, 5000)][int]$Limit = 0,
    [switch]$All
)

$configuredLimit = if ($env:TSG_MAX_SNAPSHOTS) { [int]$env:TSG_MAX_SNAPSHOTS } else {
    $cfgPath = Join-Path $env:USERPROFILE ".tsg\tsg-config.json"
    if (Test-Path $cfgPath) { try { (Get-Content $cfgPath -Raw | ConvertFrom-Json).MaxSnapshots } catch { 50 } } else { 50 }
}
$script:MaxSessions = if ($All) { 5000 } elseif ($Limit -gt 0) { $Limit } else { $configuredLimit }
$script:CallerShell = if ($env:TSG_CALLER_SHELL -and (Test-Path $env:TSG_CALLER_SHELL)) {
    $env:TSG_CALLER_SHELL
} else {
    (Get-Process -Id $PID).Path
}
$script:PickerEntries = [Collections.Generic.List[object]]::new()

function Write-SessionLink {
    param(
        [string]$SessionId,
        [string]$Path
    )

    if ($env:WT_SESSION -and -not [Console]::IsOutputRedirected) {
        Write-Host "🖱️ $SessionId click" -ForegroundColor DarkYellow -NoNewline
        return
    }

    Write-Host $SessionId -ForegroundColor DarkGray -NoNewline
}

function Open-RecoveryTab {
    param(
        [string]$Path,
        [string]$SessionId
    )

    $startDir = if ($Path -and (Test-Path $Path -ErrorAction SilentlyContinue)) {
        $Path
    } else {
        $env:USERPROFILE
    }

    $arguments = @("-w", "0", "new-tab", "-d", $startDir, $script:CallerShell)
    if ($SessionId) {
        $sessionGuid = [guid]::Empty
        if (-not [guid]::TryParse($SessionId, [ref]$sessionGuid)) {
            Write-Warning "Invalid Copilot session ID: $SessionId"
            return
        }

        $normalizedId = $sessionGuid.ToString("D")
        $shellName = [IO.Path]::GetFileNameWithoutExtension($script:CallerShell).ToLowerInvariant()
        if ($shellName -eq "cmd") {
            $arguments += @("/K", "tsg resume-host $normalizedId")
        }
        else {
            $arguments += @("-NoExit", "-Command", "tsg resume-host $normalizedId")
        }
    }

    & wt @arguments
}

function Read-RecoveryChoice {
    if (-not $env:WT_SESSION -or [Console]::IsInputRedirected) {
        Write-Host "`n  Numbers (comma-sep), A=all tabs, W=restore windows, Q=quit:" -ForegroundColor Yellow
        return Read-Host "  Choice"
    }

    $esc = [char]27
    $typed = [Text.StringBuilder]::new()
    $script:PickerOffset = 0

    function Show-Picker {
        $pageSize = [Math]::Max(1, [Console]::WindowHeight - 5)
        $maxOffset = [Math]::Max(0, $script:PickerEntries.Count - $pageSize)
        $script:PickerOffset = [Math]::Min($maxOffset, [Math]::Max(0, $script:PickerOffset))
        $width = [Math]::Max(1, [Console]::WindowWidth - 2)

        function Write-PickerLine {
            param(
                [string]$Text,
                [ConsoleColor]$Color
            )

            $visibleText = if ($Text.Length -gt $width) { $Text.Substring(0, $width) } else { $Text }
            Write-Host $visibleText -ForegroundColor $Color
        }

        [Console]::Write("$esc[2J$esc[H")
        Write-PickerLine "  TSG Recover - click a session ID or use the mouse wheel" Cyan
        $lastVisible = [Math]::Min($script:PickerEntries.Count, $script:PickerOffset + $pageSize)
        Write-PickerLine "  Entries $($script:PickerOffset + 1)-$lastVisible of $($script:PickerEntries.Count)" DarkGray

        $visibleCount = [Math]::Min($pageSize, $script:PickerEntries.Count - $script:PickerOffset)
        for ($i = 0; $i -lt $visibleCount; $i++) {
            $entry = $script:PickerEntries[$script:PickerOffset + $i]
            $color = if ($entry.SessionId) { [ConsoleColor]::DarkYellow } else { [ConsoleColor]::White }
            Write-PickerLine $entry.Label $color
        }

        for ($i = $visibleCount; $i -lt $pageSize; $i++) {
            Write-Host ""
        }

        Write-PickerLine "  Type item numbers, A=all tabs, W=restore windows, Q=quit" Yellow
        $choiceLine = "  Choice: $($typed.ToString())"
        if ($choiceLine.Length -gt $width) { $choiceLine = $choiceLine.Substring($choiceLine.Length - $width) }
        Write-Host $choiceLine -ForegroundColor Yellow -NoNewline

        return @{
            PageSize = $pageSize
            VisibleCount = $visibleCount
            FirstMouseRow = 3
            MaxOffset = $maxOffset
        }
    }

    [Console]::Write("$esc[?1049h$esc[?1000h$esc[?1006h")
    $picker = Show-Picker

    try {
        while ($true) {
            $key = [Console]::ReadKey($true)

            if ($key.Key -eq [ConsoleKey]::Enter) {
                return $typed.ToString()
            }

            if ($key.Key -eq [ConsoleKey]::Backspace) {
                if ($typed.Length -gt 0) {
                    $typed.Length--
                    $picker = Show-Picker
                }
                continue
            }

            if ($key.Key -eq [ConsoleKey]::Home) {
                $script:PickerOffset = 0
                $picker = Show-Picker
                continue
            }

            if ($key.Key -eq [ConsoleKey]::End) {
                $script:PickerOffset = $picker.MaxOffset
                $picker = Show-Picker
                continue
            }

            if ($key.Key -eq [ConsoleKey]::PageUp -or $key.Key -eq [ConsoleKey]::UpArrow) {
                $step = if ($key.Key -eq [ConsoleKey]::PageUp) { $picker.PageSize } else { 1 }
                $script:PickerOffset -= $step
                $picker = Show-Picker
                continue
            }

            if ($key.Key -eq [ConsoleKey]::PageDown -or $key.Key -eq [ConsoleKey]::DownArrow) {
                $step = if ($key.Key -eq [ConsoleKey]::PageDown) { $picker.PageSize } else { 1 }
                $script:PickerOffset += $step
                $picker = Show-Picker
                continue
            }

            if ($key.Key -eq [ConsoleKey]::Escape) {
                $sequence = [Text.StringBuilder]::new()
                $deadline = [DateTime]::UtcNow.AddMilliseconds(150)
                while ([DateTime]::UtcNow -lt $deadline) {
                    if (-not [Console]::KeyAvailable) {
                        Start-Sleep -Milliseconds 5
                        continue
                    }
                    $part = [Console]::ReadKey($true).KeyChar
                    [void]$sequence.Append($part)
                    if ($part -eq 'M' -or $part -eq 'm') { break }
                }

                if ($sequence.ToString() -match '^\[<(\d+);(\d+);(\d+)([Mm])$') {
                    $button = [int]$Matches[1]
                    $mouseRow = [int]$Matches[3]
                    $eventType = $Matches[4]

                    if ($button -eq 64 -or $button -eq 65) {
                        $direction = if ($button -eq 64) { -1 } else { 1 }
                        $wheelStep = [Math]::Max(1, [int]($picker.PageSize / 5))
                        $script:PickerOffset += $direction * $wheelStep
                        $picker = Show-Picker
                        continue
                    }

                    if ($button -eq 0 -and $eventType -eq 'M') {
                        $visibleIndex = $mouseRow - $picker.FirstMouseRow
                        if ($visibleIndex -ge 0 -and $visibleIndex -lt $picker.VisibleCount) {
                            $entry = $script:PickerEntries[$script:PickerOffset + $visibleIndex]
                            if ($entry.SessionId) {
                                return [pscustomobject]@{
                                    Kind = "SessionClick"
                                    SessionId = $entry.SessionId
                                    Path = $entry.Path
                                }
                            }
                        }
                    }
                }
                elseif ($sequence.Length -eq 0) {
                    return "Q"
                }
                continue
            }

            if (-not [char]::IsControl($key.KeyChar)) {
                [void]$typed.Append($key.KeyChar)
                $picker = Show-Picker
            }
        }
    }
    finally {
        [Console]::Write("$esc[?1000l$esc[?1006l$esc[?1049l")
    }
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

$displayVersion = if ($env:TSG_VERSION) { $env:TSG_VERSION } else { "unknown" }
Write-Host "`n  🔄 TSG Session Recovery v$displayVersion  (max: $script:MaxSessions)" -ForegroundColor Cyan
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
        $script:PickerEntries.Add([pscustomobject]@{
            Label = "  [$n] Window [$wid] - $tabCount tabs, $copCount Copilot"
            SessionId = $null
            Path = $null
        })
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
                if ($t.CopilotId) {
                    $script:PickerEntries.Add([pscustomobject]@{
                        Label = "      $($t.CopilotId)  $folder"
                        SessionId = $t.CopilotId
                        Path = $t.Path
                    })
                    Write-Host "  " -NoNewline
                    Write-SessionLink -SessionId $t.CopilotId -Path $t.Path
                }
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
        $tabLabel = "  [$n] $($tab.Folder) (Win $($tab.Window))"
        if ($tab.CopilotId) { $tabLabel += "  $($tab.CopilotId)" }
        $script:PickerEntries.Add([pscustomobject]@{
            Label = $tabLabel
            SessionId = $tab.CopilotId
            Path = $tab.Path
        })
        Write-Host "  [$n] $ic $($tab.Folder) (Win $($tab.Window))" -ForegroundColor White
        if ($tab.Summary) { Write-Host "      💬 $($tab.Summary)" -ForegroundColor DarkCyan }
        if ($tab.CopilotId) {
            Write-Host "      🔑 " -ForegroundColor DarkGray -NoNewline
            Write-SessionLink -SessionId $tab.CopilotId -Path $tab.Path
            Write-Host ""
        }
    }
}

# Section 3: All Copilot sessions (not in open tabs)
if ($sess.AllSessions.Count -gt 0) {
    Write-Host "`n  ── Copilot Sessions ──" -ForegroundColor Yellow
    foreach ($cs in $sess.AllSessions) {
        $items += @{ Type = "session"; Data = $cs }
        $n = $items.Count; $folder = Split-Path $cs.Cwd -Leaf
        $script:PickerEntries.Add([pscustomobject]@{
            Label = "  [$n] $($cs.SessionId)  $folder"
            SessionId = $cs.SessionId
            Path = $cs.Cwd
        })
        Write-Host "  [$n] 🤖 $folder" -ForegroundColor White
        if ($cs.Summary) { Write-Host "      💬 $($cs.Summary)" -ForegroundColor DarkCyan }
        Write-Host "      🔑 " -ForegroundColor DarkGray -NoNewline
        Write-SessionLink -SessionId $cs.SessionId -Path $cs.Cwd
        Write-Host "  📅 $($cs.Updated)" -ForegroundColor DarkGray
    }
}

if ($items.Count -eq 0) { Write-Host "  No sessions found" -ForegroundColor Yellow; return }
$inp = Read-RecoveryChoice
if ($inp -and $inp.Kind -eq "SessionClick") {
    Open-RecoveryTab -Path $inp.Path -SessionId $inp.SessionId
    Write-Host "  🚀 Opened clicked session in the current Terminal window" -ForegroundColor Green
    return
}
if ($inp -eq "Q") { return }

if ($inp -eq "W") {
    # Restore all closed windows with their tabs
    foreach ($item in $items) {
        if ($item.Type -ne "window") { continue }
        $win = $item.Data
        if (-not $win.Tabs -or $win.Tabs.Count -eq 0) { continue }
        foreach ($t in $win.Tabs) {
            $sessionId = if ($t.HasCopilot) { $t.CopilotId } else { $null }
            Open-RecoveryTab -Path $t.Path -SessionId $sessionId
        }
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
        foreach ($t in $win.Tabs) {
            $sessionId = if ($t.HasCopilot) { $t.CopilotId } else { $null }
            Open-RecoveryTab -Path $t.Path -SessionId $sessionId
        }
        Write-Host "  🚀 Restored window with $($win.Tabs.Count) tabs" -ForegroundColor Green
    }
    elseif ($item.Type -eq "tab") {
        $t = $item.Data
        $sessionId = if ($t.HasCopilot) { $t.CopilotId } else { $null }
        Open-RecoveryTab -Path $t.Path -SessionId $sessionId
        if ($sessionId) { Write-Host "  🚀 $($t.Folder) + copilot" -ForegroundColor Green }
        else { Write-Host "  📂 $($t.Folder)" -ForegroundColor Cyan }
    }
    elseif ($item.Type -eq "session") {
        $cs = $item.Data
        Open-RecoveryTab -Path $cs.Cwd -SessionId $cs.SessionId
        Write-Host "  🚀 $(Split-Path $cs.Cwd -Leaf) + copilot" -ForegroundColor Green
    }
    Start-Sleep 1
}
Write-Host "`n  ✅ Done!" -ForegroundColor Green