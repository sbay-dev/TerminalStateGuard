<# Copilot Performance Booster v2.0 - ALL DIAGNOSTICS READ-ONLY #>
param([ValidateSet("Boost","Monitor","Status","Restore","Help")][string]$Mode = "Help", [int]$MonitorInterval = 5)
$script:AdminModes = @("Boost","Restore"); $script:PwshExe = "C:\Program Files\PowerShell\7\pwsh.exe"
function Write-Banner { Write-Host "`n ██████╗ ██████╗ ██████╗ ██╗██╗      ██████╗ ████████╗" -ForegroundColor Cyan; Write-Host "██╔════╝██╔═══██╗██╔══██╗██║██║     ██╔═══██╗╚══██╔══╝" -ForegroundColor Cyan; Write-Host "██║     ██║   ██║██████╔╝██║██║     ██║   ██║   ██║   " -ForegroundColor Cyan; Write-Host "██║     ██║   ██║██╔═══╝ ██║██║     ██║   ██║   ██║   " -ForegroundColor Cyan; Write-Host "╚██████╗╚██████╔╝██║     ██║███████╗╚██████╔╝   ██║   " -ForegroundColor Cyan; Write-Host " ╚═════╝ ╚═════╝ ╚═╝     ╚═╝╚══════╝ ╚═════╝    ╚═╝   " -ForegroundColor Cyan; Write-Host "      ⚡ v2.0 Safe Monitor ⚡`n" -ForegroundColor Yellow }
function Write-Section { param($T) Write-Host "`n  ═══ $T ═══" -ForegroundColor Cyan }
function Write-Step { param($I, $M, $C = "Green") Write-Host "  $I $M" -ForegroundColor $C }
function Test-Admin { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) }
function Get-CopilotProcesses { Get-Process -Name "copilot*" -ErrorAction SilentlyContinue }
function Get-AllCopilotCmdLines { $c = @{}; Get-CimInstance Win32_Process -Filter "Name LIKE 'copilot%'" -ErrorAction SilentlyContinue | ForEach-Object { $c[[int]$_.ProcessId] = $_.CommandLine }; return $c }

$script:SessionCache = @{}
function Get-CopilotSessionInfo {
    param($Proc, $CmdLineCache)
    $cmd = $CmdLineCache[$Proc.Id]
    if (-not $cmd) { return @{ Type = "unknown"; Label = "?" } }
    if ($cmd -match "WindowsApps") { return @{ Type = "system"; Label = "🪟 Microsoft Copilot" } }
    if ($cmd -match "visual studio|copilot-language-server") { return @{ Type = "vs"; Label = "🟣 Visual Studio" } }
    $sessionId = if ($cmd -match '--resume=([a-f0-9-]+)') { $Matches[1] } else { $null }
    if (-not $sessionId) { return @{ Type = "cli"; Label = "🔵 CLI (new session)" } }
    if ($script:SessionCache.ContainsKey($sessionId)) { return $script:SessionCache[$sessionId] }
    $ws = Join-Path $env:USERPROFILE ".copilot\session-state\$sessionId\workspace.yaml"
    if (Test-Path $ws) {
        $c = Get-Content $ws -Raw
        $cwd = if ($c -match 'cwd: (.+)') { $Matches[1].Trim() } else { "?" }
        $sum = if ($c -match 'summary: (.+)') { $Matches[1].Trim() } else { "" }
        $folder = Split-Path $cwd -Leaf; $label = "📂 $folder"; if ($sum) { $label += " — $sum" }
        $r = @{ Type = "cli"; Label = $label; Folder = $folder; Cwd = $cwd; Summary = $sum; SessionId = $sessionId }
        $script:SessionCache[$sessionId] = $r; return $r
    }
    return @{ Type = "cli"; Label = "🔵 $($sessionId.Substring(0,8))..." }
}

function Get-SessionDiagnostics {
    param([string]$SessionId)
    if (-not $SessionId) { return $null }
    $evPath = Join-Path $env:USERPROFILE ".copilot\session-state\$SessionId\events.jsonl"
    if (-not (Test-Path $evPath)) { return @{ Status = "no-events" } }
    $file = Get-Item $evPath; $sizeMB = [math]::Round($file.Length / 1MB, 1)
    $lastLine = Get-Content $evPath -Tail 1
    $lastEvent = $lastLine | ConvertFrom-Json -ErrorAction SilentlyContinue
    $lastType = if ($lastEvent) { $lastEvent.type } else { "parse-error" }
    $hasId = if ($lastEvent.id) { $true } else { $false }
    $stuck = $false; $stuckReason = ""
    if ($lastType -eq "assistant.turn_start") { $stuck = $true; $stuckReason = "Stuck: assistant started but never finished" }
    elseif ($lastType -eq "tool.execution_start") { $stuck = $true; $stuckReason = "Stuck: tool started but never completed" }
    elseif (-not $hasId -and $lastType -notin @("session.start","session.info")) { $stuck = $true; $stuckReason = "Corrupted: last event missing id (bad turn_end?)" }
    return @{ SizeMB = $sizeMB; LastType = $lastType; HasId = $hasId; Stuck = $stuck; StuckReason = $stuckReason; Status = if ($stuck) { "stuck" } elseif ($sizeMB -gt 20) { "large" } else { "ok" } }
}

if ($Mode -eq "Boost") { Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; public class MemTrim { [DllImport("psapi.dll")] public static extern bool EmptyWorkingSet(IntPtr hProcess); }' -ErrorAction SilentlyContinue }

function Get-BoostReport {
    $r = @{ Checks = @(); IsBoosted = $false; BoostedCount = 0; TotalChecks = 4 }
    $procs = Get-CopilotProcesses; if (-not $procs) { $r.Checks += @{ Name = "Processes"; Status = $false; Detail = "None" }; return $r }
    $hi = ($procs | Where-Object { $_.PriorityClass -match "High|RealTime|AboveNormal" }).Count
    $r.Checks += @{ Name = "Process Priority"; Status = $hi -gt 0; Detail = "$hi/$($procs.Count) at High+" }; if ($hi -gt 0) { $r.BoostedCount++ }
    $ac = ($procs | Where-Object { try { $_.ProcessorAffinity -eq [IntPtr]((1 -shl [Environment]::ProcessorCount) - 1) } catch { $true } }).Count
    $r.Checks += @{ Name = "CPU Affinity"; Status = $ac -gt 0; Detail = "$ac/$($procs.Count) all $([Environment]::ProcessorCount) cores" }; if ($ac -gt 0) { $r.BoostedCount++ }
    $no = [Environment]::GetEnvironmentVariable("NODE_OPTIONS", "User"); $hn = $no -match "max-old-space-size"
    $r.Checks += @{ Name = "NODE_OPTIONS"; Status = $hn; Detail = if ($hn) { $no } else { "Not set" } }; if ($hn) { $r.BoostedCount++ }
    $sp = Join-Path $env:USERPROFILE ".copilotAccel\boost-snapshot.json"; $hs = Test-Path $sp
    $r.Checks += @{ Name = "Snapshot"; Status = $hs; Detail = if ($hs) { "Saved" } else { "None" } }; if ($hs) { $r.BoostedCount++ }
    $r.IsBoosted = $r.BoostedCount -ge 3; return $r
}

function Invoke-Boost {
    if (-not (Test-Admin)) { Start-Process $script:PwshExe "-NoExit -Command `"& '$PSCommandPath' -Mode Boost`"" -Verb RunAs; return }
    Write-Section "BOOSTING"; $procs = Get-CopilotProcesses; if (-not $procs) { Write-Step "❌" "No processes" "Red"; return }
    $snap = @{ Timestamp = (Get-Date).ToString("o"); Processes = @(); OtherProcesses = @() }
    $procs | ForEach-Object { $snap.Processes += @{ Id = $_.Id; Name = $_.Name; Priority = $_.PriorityClass.ToString() } }
    Get-Process -Name "node","code","devenv","msedge" -ErrorAction SilentlyContinue | ForEach-Object { try { $snap.OtherProcesses += @{ Id = $_.Id; Name = $_.Name; Priority = $_.PriorityClass.ToString() } } catch {} }
    $b = 0; $procs | ForEach-Object { try { $_.PriorityClass = "RealTime"; $b++; [MemTrim]::EmptyWorkingSet($_.Handle) | Out-Null } catch { try { $_.PriorityClass = "High"; $b++ } catch {} } }
    Write-Step "⚡" "$b → RealTime/High"
    $lo = 0; Get-Process -Name "node","code","msedge","SearchHost","explorer" -ErrorAction SilentlyContinue | Where-Object { $_.Name -notmatch "copilot" } | ForEach-Object { try { $_.PriorityClass = "BelowNormal"; $lo++ } catch {} }
    Write-Step "⬇️" "$lo → BelowNormal"
    [Environment]::SetEnvironmentVariable("NODE_OPTIONS", "--max-old-space-size=8192", "User"); Write-Step "🧠" "NODE_OPTIONS=8GB"
    $snap | ConvertTo-Json -Depth 3 | Set-Content (Join-Path $env:USERPROFILE ".copilotAccel\boost-snapshot.json") -Encoding UTF8
    Write-Host "`n  ╔══════════════════════════════════════╗" -ForegroundColor Green; Write-Host "  ║   ⚡ BOOST COMPLETE                   ║" -ForegroundColor Green; Write-Host "  ╚══════════════════════════════════════╝" -ForegroundColor Green
}

function Invoke-Status {
    $rp = Get-BoostReport; $ic = if ($rp.IsBoosted) { "🟢 BOOSTED" } else { "⚪ NOT BOOSTED" }
    Write-Host "  ╔══════════════════════════════════════╗" -ForegroundColor $(if ($rp.IsBoosted) {"Green"} else {"Gray"})
    Write-Host "  ║  $ic           ║" -ForegroundColor $(if ($rp.IsBoosted) {"Green"} else {"Gray"})
    Write-Host "  ╚══════════════════════════════════════╝" -ForegroundColor $(if ($rp.IsBoosted) {"Green"} else {"Gray"})
    Write-Section "CHECKS"; $rp.Checks | ForEach-Object { Write-Step $(if ($_.Status) {"✅"} else {"❌"}) "$($_.Name.PadRight(22)) $($_.Detail)" }
    Write-Host "  📊 Score: $($rp.BoostedCount)/$($rp.TotalChecks)"
    Write-Section "PROCESSES"; $procs = Get-CopilotProcesses
    if ($procs) { $cc = Get-AllCopilotCmdLines; $procs | ForEach-Object {
        $ram = [math]::Round($_.WorkingSet64/1MB,1); $info = Get-CopilotSessionInfo $_ $cc; $diag = if ($info.SessionId) { Get-SessionDiagnostics $info.SessionId } else { $null }
        $si = if ($diag -and $diag.Stuck) { "❄️" } elseif ($diag -and $diag.Status -eq "large") { "⚠️" } else { "✅" }
        Write-Host "  $si PID $($_.Id.ToString().PadRight(8)) $($_.Name.PadRight(24)) $($ram.ToString().PadLeft(7)) MB  H:$($_.HandleCount)" -ForegroundColor White
        $ic2 = if ($info.Type -eq "vs") { "Magenta" } elseif ($info.Type -eq "system") { "DarkCyan" } else { "Cyan" }
        Write-Host "     $($info.Label)" -ForegroundColor $ic2
        if ($diag) { $dc = if ($diag.Stuck) { "Red" } elseif ($diag.Status -eq "large") { "Yellow" } else { "DarkGray" }; Write-Host "     📄 $($diag.SizeMB)MB | Last: $($diag.LastType) | $($diag.Status)" -ForegroundColor $dc; if ($diag.Stuck) { Write-Host "     ⚠️ $($diag.StuckReason)" -ForegroundColor Red } }
    } } else { Write-Step "❌" "No processes" "Red" }
    Write-Section "NETWORK"; try { $p = Test-Connection "api.github.com" -Count 3 -ErrorAction Stop; $a = [math]::Round(($p.Latency | Measure-Object -Average).Average,1); Write-Step "📡" "GitHub: ${a}ms" $(if ($a -lt 100){"Green"}elseif($a -lt 300){"Yellow"}else{"Red"}) } catch { Write-Step "📡" "Unreachable" "Red" }
}

function Invoke-Restore {
    if (-not (Test-Admin)) { Start-Process $script:PwshExe "-NoExit -Command `"& '$PSCommandPath' -Mode Restore`"" -Verb RunAs; return }
    Write-Section "RESTORING"; $sp = Join-Path $env:USERPROFILE ".copilotAccel\boost-snapshot.json"
    if (Test-Path $sp) { $sn = Get-Content $sp -Raw | ConvertFrom-Json; foreach ($s in $sn.Processes) { $p = Get-Process -Id $s.Id -ErrorAction SilentlyContinue; if ($p) { try { $p.PriorityClass = $s.Priority } catch {} } }; foreach ($s in $sn.OtherProcesses) { $p = Get-Process -Id $s.Id -ErrorAction SilentlyContinue; if ($p) { try { $p.PriorityClass = $s.Priority } catch {} } } }
    else { Get-CopilotProcesses | ForEach-Object { try { $_.PriorityClass = "Normal" } catch {} } }
    Write-Step "✅" "Restored"
}

function Invoke-Monitor {
    $script:SessionCache = @{}; $lastCpuMap = @{}; $histSize = 9001
    try { $wts = Get-Content "$env:LOCALAPPDATA\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\settings.json" -Raw | ConvertFrom-Json; if ($wts.profiles.defaults.historySize) { $histSize = $wts.profiles.defaults.historySize } } catch {}
    while ($true) {
        Clear-Host; $rp = Get-BoostReport; $si = if ($rp.IsBoosted) { "🟢 BOOSTED" } else { "⚪ NORMAL" }
        Write-Host "  ⚡ COPILOT MONITOR  $(Get-Date -Format 'HH:mm:ss')  [$si] [$($rp.BoostedCount)/$($rp.TotalChecks)]" -ForegroundColor Cyan
        Write-Host "  ══════════════════════════════════════════════════════════════════" -ForegroundColor DarkGray
        $procs = Get-CopilotProcesses
        if ($procs) {
            $cc = Get-AllCopilotCmdLines; $tRam = 0; $tCpu = 0; $tThr = 0; $issues = @()
            $procs | ForEach-Object {
                $ram = [math]::Round($_.WorkingSet64/1MB,2); $tRam += $ram; $tCpu += $_.CPU; $tThr += $_.Threads.Count
                $rc = if ($ram -gt 500) {"Red"} elseif ($ram -gt 200) {"Yellow"} else {"Green"}
                $info = Get-CopilotSessionInfo $_ $cc; $h = $_.HandleCount; $hc = if ($h -gt 10000) {"Red"} elseif ($h -gt 5000) {"Yellow"} else {"Green"}
                $cn = [math]::Round($_.CPU,2); $cp = if ($lastCpuMap.ContainsKey($_.Id)) { $lastCpuMap[$_.Id] } else { $cn }; $cd = [math]::Round($cn-$cp,2); $lastCpuMap[$_.Id] = $cn
                $diag = if ($info.SessionId) { Get-SessionDiagnostics $info.SessionId } else { $null }
                $act = "🟢 IDLE"; $ac = "Green"
                if ($h -gt 10000) { $act = "🔴 HANDLE LEAK"; $ac = "Red"; $ev = if ($diag) {"$($diag.SizeMB)MB"} else {"?"}; $issues += @{Pid=$_.Id;Type="leak";Detail="H:$h Events:$ev";Info=$info;Diag=$diag} }
                elseif ($diag -and $diag.Stuck) { $act = "❄️ STUCK"; $ac = "Red"; $issues += @{Pid=$_.Id;Type="stuck";Detail=$diag.StuckReason;Info=$info;Diag=$diag} }
                elseif ($cd -gt 5) { $act = "🔴 HIGH CPU"; $ac = "Red"; if ($info.Type -eq "cli") { $issues += @{Pid=$_.Id;Type="cpu";Detail="CPU Δ${cd}s";Info=$info;Diag=$diag} } }
                elseif ($cd -gt 0.5) { $act = "🟡 ACTIVE"; $ac = "Yellow" }
                Write-Host "  PID " -NoNewline; Write-Host "$($_.Id.ToString().PadRight(8))" -ForegroundColor White -NoNewline
                Write-Host "| $($_.Name.PadRight(24))" -NoNewline; Write-Host "| " -NoNewline
                Write-Host "$($ram.ToString().PadLeft(7)) MB" -ForegroundColor $rc -NoNewline
                Write-Host " | H:" -NoNewline; Write-Host "$($h.ToString().PadLeft(6))" -ForegroundColor $hc -NoNewline
                Write-Host " | Δ$($cd.ToString('0.0').PadLeft(5))s" -NoNewline -ForegroundColor Gray
                Write-Host " | $act" -ForegroundColor $ac
                $ic = if ($info.Type -eq "vs") {"Magenta"} elseif ($info.Type -eq "system") {"DarkCyan"} else {"Cyan"}
                Write-Host "           $($info.Label)" -ForegroundColor $ic
                if ($diag -and ($diag.Stuck -or $diag.Status -eq "large")) { $dc = if ($diag.Stuck) {"Red"} else {"Yellow"}; Write-Host "           📄 Events: $($diag.SizeMB)MB | Last: $($diag.LastType) | $($diag.Status)" -ForegroundColor $dc }
            }
            Write-Host "  ══════════════════════════════════════════════════════════════════" -ForegroundColor DarkGray
            Write-Host "  TOTAL: " -NoNewline; Write-Host "$([math]::Round($tRam,1)) MB" -ForegroundColor Magenta -NoNewline; Write-Host " | CPU: $([math]::Round($tCpu,1))s | Threads: $tThr" -ForegroundColor Gray
            if ($issues.Count -gt 0) {
                Write-Host "`n  ⚠️ DIAGNOSIS (read-only):" -ForegroundColor Red
                foreach ($is in $issues) {
                    Write-Host "    PID $($is.Pid): $($is.Detail)" -ForegroundColor Yellow
                    if ($is.Diag -and $is.Diag.Stuck) { Write-Host "      ❄️ $($is.Diag.StuckReason)" -ForegroundColor Red }
                    switch ($is.Type) {
                        "leak"  { Write-Host "      💡 events.jsonl too large — copilot re-reads in loop" -ForegroundColor DarkYellow; Write-Host "      💡 Close tab, reopen: copilot --resume=<id>" -ForegroundColor DarkYellow }
                        "stuck" { Write-Host "      💡 Session stuck on: $($is.Diag.LastType)" -ForegroundColor DarkYellow; Write-Host "      💡 Close tab and resume — copilot may auto-recover" -ForegroundColor DarkYellow }
                        "cpu"   { Write-Host "      💡 High CPU — wait or close tab and resume" -ForegroundColor DarkYellow }
                    }
                }
                Write-Host "`n  ⛔ NEVER delete/trim events.jsonl — destroys session!" -ForegroundColor Red
            }
        } else { Write-Host "  ❌ No Copilot processes" -ForegroundColor Red }
        Write-Host ""; $wtS = "$env:LOCALAPPDATA\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\state.json"
        if (Test-Path $wtS) { $st = Get-Content $wtS -Raw | ConvertFrom-Json; $wc = if ($st.persistedWindowLayouts) {$st.persistedWindowLayouts.Count} else {0}; $tc = 0; if ($st.persistedWindowLayouts) { foreach ($w in $st.persistedWindowLayouts) { if ($w.tabLayout) { $tc += $w.tabLayout.Count } } }; Write-Host "  📺 Terminal: $wc win | $tc tabs | History: $histSize lines" -ForegroundColor DarkGray }
        Write-Host "  ⏱ ${MonitorInterval}s | C=clear | Ctrl+C stop" -ForegroundColor DarkGray
        $wa = 0; while ($wa -lt ($MonitorInterval*1000)) { if ([Console]::KeyAvailable) { $k = [Console]::ReadKey($true); if ($k.Key -eq "C") { Clear-Host; Write-Host "  🧹 Cleared" -ForegroundColor Green; Start-Sleep 1 } }; Start-Sleep -Milliseconds 250; $wa += 250 }
    }
}

function Invoke-Help { Write-Section "COMMANDS"; Write-Host "  Boost   ⚡ Elevate priority (Admin)" -ForegroundColor Yellow; Write-Host "  Monitor 📊 Safe live monitor" -ForegroundColor Cyan; Write-Host "  Status  📋 Health check" -ForegroundColor Green; Write-Host "  Restore 🔄 Restore priorities (Admin)" -ForegroundColor Magenta; Write-Host ""; Write-Section "SAFETY"; Write-Host "  ⛔ Monitor is READ-ONLY" -ForegroundColor Red; Write-Host "  ⛔ NEVER delete events.jsonl" -ForegroundColor Red }

Write-Banner; $isAdmin = Test-Admin; Write-Host "  Running as: $(if ($isAdmin) {'✅ Admin'} else {'👤 User'})" -ForegroundColor $(if ($isAdmin) {"Green"} else {"Gray"})
switch ($Mode) { "Boost" { Invoke-Boost } "Monitor" { Invoke-Monitor } "Status" { Invoke-Status } "Restore" { Invoke-Restore } "Help" { Invoke-Help } }
if ($script:AdminModes -contains $Mode -and $isAdmin) { Write-Host "`n  Press any key..." -ForegroundColor DarkGray; $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown") }    Copilot Performance Booster Script
