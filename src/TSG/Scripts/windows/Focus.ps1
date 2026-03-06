<#
.SYNOPSIS
    TSG Focus — Dedicate ALL machine resources to a single Copilot process
.DESCRIPTION
    Turbo-focuses CPU, GPU, I/O and Memory on one stuck process to break through hangs.
    Sets target to RealTime + all cores + High I/O + GPU priority.
    Throttles everything else to Idle.
.PARAMETER TargetPID
    The PID to focus on. If not provided, auto-detects the heaviest/stuck process.
.PARAMETER Undo
    Restores normal priorities for all processes.
#>
param(
    [int]$TargetPID = 0,
    [switch]$Undo
)

$ErrorActionPreference = 'SilentlyContinue'

# ═══════════════════════════════════════════
#  P/Invoke for I/O Priority & GPU
# ═══════════════════════════════════════════
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

public static class ResourceFocus {
    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);

    [DllImport("ntdll.dll")]
    public static extern int NtSetInformationProcess(IntPtr h, int infoClass, ref int val, int len);

    // Set I/O priority: 0=VeryLow, 1=Low, 2=Normal, 3=High
    public static bool SetIoPriority(int pid, int priority) {
        IntPtr h = OpenProcess(0x1000 | 0x0200, false, pid); // PROCESS_SET_INFORMATION | PROCESS_QUERY_LIMITED
        if (h == IntPtr.Zero) return false;
        int val = priority;
        int r = NtSetInformationProcess(h, 33, ref val, 4); // ProcessIoPriority = 33
        CloseHandle(h);
        return r == 0;
    }

    // Set Memory priority: 1=VeryLow .. 5=Normal
    public static bool SetMemoryPriority(int pid, int priority) {
        IntPtr h = OpenProcess(0x1000 | 0x0200, false, pid);
        if (h == IntPtr.Zero) return false;
        int val = priority;
        int r = NtSetInformationProcess(h, 39, ref val, 4); // ProcessPagePriority = 39
        CloseHandle(h);
        return r == 0;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool SetProcessWorkingSetSizeEx(IntPtr h, IntPtr min, IntPtr max, uint flags);

    // Lock process in physical RAM (prevent paging)
    public static bool LockInRam(int pid, long minMB, long maxMB) {
        IntPtr h = OpenProcess(0x1000 | 0x0100 | 0x0200, false, pid);
        if (h == IntPtr.Zero) return false;
        bool r = SetProcessWorkingSetSizeEx(h, (IntPtr)(minMB*1024*1024), (IntPtr)(maxMB*1024*1024), 0x08); // QUOTA_LIMITS_HARDWS_MAX_DISABLE
        CloseHandle(h);
        return r;
    }
}
"@ -ErrorAction SilentlyContinue

# ═══════════════════════════════════════════
#  Helpers
# ═══════════════════════════════════════════
function Write-Focus { param([string]$Icon, [string]$Msg, [ConsoleColor]$Color = 'White')
    Write-Host "  $Icon " -NoNewline; Write-Host $Msg -ForegroundColor $Color
}

function Get-CopilotProcesses {
    Get-Process | Where-Object {
        $_.ProcessName -match '^copilot' -and
        $_.ProcessName -notmatch 'language-server'
    } | Sort-Object WorkingSet64 -Descending
}

function Get-HeaviestProcess {
    $procs = Get-CopilotProcesses
    # Prefer stuck/heavy processes
    $stuck = $procs | Where-Object { $_.HandleCount -gt 5000 -or $_.WorkingSet64 -gt 500MB }
    if ($stuck) { return $stuck[0] }
    return $procs[0]
}

function Get-SystemInfo {
    $cpu = (Get-CimInstance Win32_Processor | Select-Object -First 1)
    $gpu = (Get-CimInstance Win32_VideoController | Select-Object -First 1)
    $ram = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
    return @{
        CpuName    = $cpu.Name
        Cores      = [Environment]::ProcessorCount
        GpuName    = $gpu.Name
        GpuRam     = [math]::Round($gpu.AdapterRAM / 1GB, 1)
        TotalRam   = [math]::Round($ram / 1GB, 1)
    }
}

# ═══════════════════════════════════════════
#  GPU Priority via Registry
# ═══════════════════════════════════════════
function Set-GpuHighPerformance {
    param([System.Diagnostics.Process]$Process)

    $exePath = $Process.MainModule.FileName
    if (-not $exePath) { return $false }

    # Method 1: DirectX UserGpuPreferences (Windows 10/11)
    $regPath = "HKCU:\Software\Microsoft\DirectX\UserGpuPreferences"
    if (-not (Test-Path $regPath)) { New-Item $regPath -Force | Out-Null }
    # GpuPreference=2 = High Performance GPU
    Set-ItemProperty -Path $regPath -Name $exePath -Value "GpuPreference=2;" -ErrorAction SilentlyContinue

    # Method 2: Graphics Settings (Windows 11)
    $gfxPath = "HKCU:\Software\Microsoft\Windows\CurrentVersion\GameDVR"
    if (Test-Path $gfxPath) {
        Set-ItemProperty -Path $gfxPath -Name "AppCaptureEnabled" -Value 0 -ErrorAction SilentlyContinue
    }

    # Method 3: Disable GPU power throttling for this process
    $throttlePath = "HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling"
    if (Test-Path $throttlePath) {
        Set-ItemProperty -Path $throttlePath -Name "PowerThrottlingOff" -Value 1 -ErrorAction SilentlyContinue
    }

    return $true
}

# ═══════════════════════════════════════════
#  Throttle other processes
# ═══════════════════════════════════════════
function Set-ThrottleOthers {
    param([int]$ProtectPID)

    $throttled = 0
    # Copilot processes not being focused
    Get-CopilotProcesses | Where-Object { $_.Id -ne $ProtectPID } | ForEach-Object {
        try {
            $_.PriorityClass = 'BelowNormal'
            $throttled++
        } catch {}
    }

    # Heavy background processes
    $bgProcs = @('Teams', 'Outlook', 'msedge', 'chrome', 'firefox', 'Spotify',
                 'OneDrive', 'SearchHost', 'PhoneExperienceHost', 'WidgetService',
                 'GameBar', 'YourPhone', 'Discord', 'Slack')
    Get-Process | Where-Object { $bgProcs -contains $_.ProcessName } | ForEach-Object {
        try {
            $_.PriorityClass = 'Idle'
            $throttled++
        } catch {}
    }
    return $throttled
}

function Restore-AllPriorities {
    $restored = 0
    Get-Process | Where-Object {
        $_.PriorityClass -eq 'Idle' -or $_.PriorityClass -eq 'BelowNormal'
    } | ForEach-Object {
        try {
            $_.PriorityClass = 'Normal'
            $restored++
        } catch {}
    }
    Get-CopilotProcesses | ForEach-Object {
        try { $_.PriorityClass = 'Normal' } catch {}
    }
    return $restored
}

# ═══════════════════════════════════════════
#  MAIN: FOCUS
# ═══════════════════════════════════════════
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

Write-Host ""
Write-Host "  ████████╗███████╗ ██████╗ " -ForegroundColor Cyan
Write-Host "  ╚══██╔══╝██╔════╝██╔════╝ " -ForegroundColor Cyan
Write-Host "     ██║   ███████╗██║  ███╗" -ForegroundColor Cyan
Write-Host "     ██║   ╚════██║██║   ██║" -ForegroundColor Cyan
Write-Host "     ██║   ███████║╚██████╔╝" -ForegroundColor Cyan
Write-Host "     ╚═╝   ╚══════╝ ╚═════╝ " -ForegroundColor Cyan
Write-Host "      🎯 FOCUS MODE" -ForegroundColor Yellow
Write-Host ""

if ($Undo) {
    Write-Host "  🔄 Restoring all priorities..." -ForegroundColor Yellow
    $count = Restore-AllPriorities
    Write-Focus "✅" "$count processes restored to Normal" Green
    Write-Host ""
    if ($isAdmin) { Write-Host "  Press any key..." -ForegroundColor DarkGray; $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown") }
    exit 0
}

if (-not $isAdmin) {
    Write-Focus "⚠️" "Admin required for full resource control. Elevating..." Yellow
    $args2 = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($TargetPID -gt 0) { $args2 += " -TargetPID $TargetPID" }
    Start-Process pwsh -Verb RunAs -ArgumentList $args2
    exit 0
}

# Auto-detect if no PID given
if ($TargetPID -eq 0) {
    $target = Get-HeaviestProcess
    if (-not $target) {
        Write-Focus "❌" "No Copilot processes found" Red
        exit 1
    }
    $TargetPID = $target.Id
    Write-Focus "🎯" "Auto-detected heaviest process: PID $TargetPID" Yellow
} else {
    $target = Get-Process -Id $TargetPID -ErrorAction SilentlyContinue
    if (-not $target) {
        Write-Focus "❌" "PID $TargetPID not found" Red
        exit 1
    }
}

# System info
$sys = Get-SystemInfo
Write-Host "  ═══ SYSTEM ═══" -ForegroundColor DarkCyan
Write-Focus "🖥️" "CPU: $($sys.CpuName) ($($sys.Cores) cores)" Cyan
Write-Focus "🎮" "GPU: $($sys.GpuName) ($($sys.GpuRam) GB)" Cyan
Write-Focus "💾" "RAM: $($sys.TotalRam) GB total" Cyan
Write-Host ""

Write-Host "  ═══ TARGET ═══" -ForegroundColor DarkYellow
$ramMB = [math]::Round($target.WorkingSet64 / 1MB, 1)
Write-Focus "🎯" "PID $TargetPID | $($target.ProcessName) | $ramMB MB | H:$($target.HandleCount)" Yellow
Write-Host ""

Write-Host "  ═══ FOCUSING ═══" -ForegroundColor DarkGreen

# 1. CPU Priority → RealTime
try {
    $target.PriorityClass = 'RealTime'
    Write-Focus "✅" "CPU Priority → RealTime" Green
} catch {
    try { $target.PriorityClass = 'High'; Write-Focus "⚠️" "CPU Priority → High (RealTime failed)" Yellow }
    catch { Write-Focus "❌" "CPU Priority failed" Red }
}

# 2. CPU Affinity → ALL cores
try {
    $allCores = [long]([math]::Pow(2, $sys.Cores) - 1)
    $target.ProcessorAffinity = [IntPtr]$allCores
    Write-Focus "✅" "CPU Affinity → All $($sys.Cores) cores dedicated" Green
} catch { Write-Focus "⚠️" "CPU Affinity unchanged" Yellow }

# 3. I/O Priority → High (via NtSetInformationProcess)
$ioResult = [ResourceFocus]::SetIoPriority($TargetPID, 3)
if ($ioResult) {
    Write-Focus "✅" "I/O Priority → High (disk reads accelerated)" Green
} else {
    Write-Focus "⚠️" "I/O Priority unchanged" Yellow
}

# 4. Memory Priority → Highest (keep in physical RAM)
$memResult = [ResourceFocus]::SetMemoryPriority($TargetPID, 5)
if ($memResult) {
    Write-Focus "✅" "Memory Priority → Highest (pinned in RAM)" Green
} else {
    Write-Focus "⚠️" "Memory Priority unchanged" Yellow
}

# 5. Lock Working Set (prevent paging to disk)
$lockResult = [ResourceFocus]::LockInRam($TargetPID, 256, 4096)
if ($lockResult) {
    Write-Focus "✅" "Working Set → Locked 256MB-4GB (no paging)" Green
} else {
    Write-Focus "⚠️" "Working Set lock skipped" Yellow
}

# 6. GPU → High Performance
$gpuResult = Set-GpuHighPerformance -Process $target
if ($gpuResult) {
    Write-Focus "✅" "GPU → High Performance mode" Green
} else {
    Write-Focus "⚠️" "GPU unchanged" Yellow
}

# 7. Disable Power Throttling (EcoQoS) for target
$ecoPath = "HKLM:\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling"
if (Test-Path $ecoPath) {
    Set-ItemProperty -Path $ecoPath -Name "PowerThrottlingOff" -Value 1 -ErrorAction SilentlyContinue
    Write-Focus "✅" "Power Throttling → Disabled (full speed)" Green
}

# 8. Set NODE_OPTIONS for larger heap
[System.Environment]::SetEnvironmentVariable("NODE_OPTIONS", "--max-old-space-size=16384", "User")
Write-Focus "✅" "NODE_OPTIONS → 16GB heap" Green

# 9. Throttle others
Write-Host ""
Write-Host "  ═══ THROTTLING OTHERS ═══" -ForegroundColor DarkRed
$throttled = Set-ThrottleOthers -ProtectPID $TargetPID
Write-Focus "✅" "$throttled processes throttled to Idle/BelowNormal" Green

# 10. Clear file system cache (helps with large events.jsonl)
Write-Host ""
[System.GC]::Collect()
[System.GC]::WaitForPendingFinalizers()

# Summary
Write-Host ""
Write-Host "  ╔══════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "  ║  🎯 ALL RESOURCES → PID $TargetPID" -ForegroundColor Green -NoNewline
Write-Host "              ║" -ForegroundColor Green
Write-Host "  ╚══════════════════════════════════════════╝" -ForegroundColor Green
Write-Host ""
Write-Host "  📊 Monitoring until process unsticks..." -ForegroundColor Cyan
Write-Host "  ⏱ Press Ctrl+C to stop, or 'U' to undo" -ForegroundColor DarkGray
Write-Host ""

# Live monitor loop
$prevCpu = $target.TotalProcessorTime.TotalSeconds
$stuckCount = 0
$startTime = Get-Date

while ($true) {
    Start-Sleep -Seconds 3
    $target.Refresh()

    if ($target.HasExited) {
        Write-Focus "⚠️" "Process exited" Yellow
        break
    }

    $nowCpu = $target.TotalProcessorTime.TotalSeconds
    $delta = [math]::Round($nowCpu - $prevCpu, 2)
    $prevCpu = $nowCpu
    $ramNow = [math]::Round($target.WorkingSet64 / 1MB, 0)
    $elapsed = [math]::Round(((Get-Date) - $startTime).TotalSeconds, 0)
    $handles = $target.HandleCount

    if ($delta -gt 0.1) {
        $stuckCount = 0
        $icon = "🟢"; $state = "ACTIVE"; $color = "Green"
    } elseif ($delta -gt 0) {
        $icon = "🟡"; $state = "SLOW"; $color = "Yellow"
    } else {
        $stuckCount++
        $icon = "🔴"; $state = "STUCK"; $color = "Red"
    }

    $bar = "█" * [math]::Min(20, [math]::Max(1, [int]($delta * 10))) + "░" * [math]::Max(0, 20 - [int]($delta * 10))
    Write-Host "`r  $icon [$bar] ${delta}s/3s | $ramNow MB | H:$handles | ${elapsed}s | $state   " -ForegroundColor $color -NoNewline

    # Auto-success detection
    if ($stuckCount -eq 0 -and $delta -gt 2 -and $elapsed -gt 10) {
        Write-Host ""
        Write-Host ""
        Write-Focus "🎉" "Process is actively computing! Focus working." Green
    }

    # Warn if stuck too long
    if ($stuckCount -ge 20) {
        Write-Host ""
        Write-Host ""
        Write-Focus "⚠️" "Process stuck for 60s+ even with full resources" Red
        Write-Focus "💡" "The process may need a restart: close tab → copilot --resume" Yellow
        break
    }

    # Check for key press
    if ([Console]::KeyAvailable) {
        $key = [Console]::ReadKey($true)
        if ($key.KeyChar -eq 'u' -or $key.KeyChar -eq 'U') {
            Write-Host ""
            $count = Restore-AllPriorities
            Write-Focus "🔄" "$count processes restored" Green
            break
        }
    }
}

Write-Host ""
Write-Host "  💡 Run 'tsg focus --undo' to restore all priorities" -ForegroundColor DarkGray
Write-Host ""
if ($isAdmin) { Write-Host "  Press any key..." -ForegroundColor DarkGray; $null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown") }
