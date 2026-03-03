# RecoverSessions v2.0 - READ-ONLY recovery
param([int]$Days = 7)
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
    $s = @{ Tabs = @(); FromSnapshot = $false }; $ca = Get-CopilotSessionMap
    $sp = "$env:LOCALAPPDATA\Packages\Microsoft.WindowsTerminal_8wekyb3d8bbwe\LocalState\state.json"
    $snp = Join-Path $env:USERPROFILE ".copilotAccel\terminal-snapshots\latest.json"
    if (Test-Path $sp) { $st = Get-Content $sp -Raw | ConvertFrom-Json
        if ($st.persistedWindowLayouts) { $wi = 0; foreach ($w in $st.persistedWindowLayouts) { $wi++; if ($w.tabLayout) { foreach ($t in $w.tabLayout) { $d = $t.startingDirectory
            if ($d -and (Test-Path $d -ErrorAction SilentlyContinue)) { $ci = Find-BestSession $d $ca; $s.Tabs += @{ Folder = Split-Path $d -Leaf; Path = $d; Window = $wi; HasCopilot = $null -ne $ci; CopilotId = if ($ci) { $ci.SessionId } else { $null }; Summary = if ($ci) { $ci.Summary } else { $null } } }
    } } } } }
    if ($s.Tabs.Count -eq 0 -and (Test-Path $snp)) { $s.FromSnapshot = $true; $sn = Get-Content $snp -Raw | ConvertFrom-Json
        foreach ($t in $sn.Tabs) { if ($t.Path -and (Test-Path $t.Path -ErrorAction SilentlyContinue)) { $s.Tabs += @{ Folder = Split-Path $t.Path -Leaf; Path = $t.Path; Window = $t.Window; HasCopilot = $t.HasCopilot; CopilotId = $t.CopilotId; Summary = $t.Summary } } }
    }; return $s
}
Write-Host "`n  🔄 Session Recovery v2.0" -ForegroundColor Cyan
$sess = Get-AllSessions -Days $Days; $cc = ($sess.Tabs | Where-Object { $_.HasCopilot }).Count
Write-Host "  ✅ $($sess.Tabs.Count) tabs ($cc copilot)`n" -ForegroundColor Green
if ($sess.Tabs.Count -eq 0) { Write-Host "  No tabs" -ForegroundColor Yellow; return }
$i = 0; $sess.Tabs | ForEach-Object { $i++; $ic = if ($_.HasCopilot) { "🤖" } else { "📂" }
    Write-Host "  [$i] $ic $($_.Folder) (Win $($_.Window))" -ForegroundColor White
    if ($_.Summary) { Write-Host "      💬 $($_.Summary)" -ForegroundColor DarkCyan }
    if ($_.CopilotId) { Write-Host "      🔑 $($_.CopilotId)" -ForegroundColor DarkGray }
}
Write-Host "`n  Numbers (comma-sep), A=all, Q=quit:" -ForegroundColor Yellow; $inp = Read-Host "  Choice"
if ($inp -eq "Q") { return }
$idx = if ($inp -eq "A") { 1..$sess.Tabs.Count } else { $inp -split "," | ForEach-Object { [int]$_.Trim() } }
$s = "C:\Program Files\PowerShell\7\pwsh.exe"
foreach ($x in $idx) { $t = $sess.Tabs[$x-1]; if (-not $t) { continue }
    if ($t.HasCopilot -and $t.CopilotId) { Start-Process wt -ArgumentList "-d `"$($t.Path)`" `"$s`" -NoExit -Command `"copilot --resume=$($t.CopilotId)`""; Write-Host "  🚀 $($t.Folder) + copilot" -ForegroundColor Green }
    else { Start-Process wt -ArgumentList "-d `"$($t.Path)`" `"$s`""; Write-Host "  📂 $($t.Folder)" -ForegroundColor Cyan }
    Start-Sleep 1
}
Write-Host "`n  ✅ Done!" -ForegroundColor Green