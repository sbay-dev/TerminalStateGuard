<div align="center">

# ⚡ TSG — Terminal State Guard

### The Complete Terminal Intelligence Platform for Developers

[![NuGet](https://img.shields.io/nuget/v/TerminalStateGuard?style=for-the-badge&logo=nuget&color=004880)](https://www.nuget.org/packages/TerminalStateGuard)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg?style=for-the-badge)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux-blue?style=for-the-badge)]()

**Real-time window tracking · Live tab detection · Process management · Session recovery**

[Installation](#-installation) · [Commands](#-commands) · [Windows](#-window-tracking) · [Processes](#-process-manager) · [Monitor](#-safe-monitor) · [Recovery](#-session-recovery)

---

</div>

## 🚀 Installation

```bash
dotnet tool install -g TerminalStateGuard
tsg install
```

**Done.** Reopen your terminal — shortcuts, profiles, and integration are active.

### What `tsg install` does:

| Platform | Action |
|----------|--------|
| **Windows** | Installs [Windows Terminal Fragment](https://learn.microsoft.com/en-us/windows/terminal/json-fragment-extensions) with dedicated profiles & icons |
| **Windows** | Configures PSReadLine keyboard shortcuts in PowerShell profile |
| **Windows** | Deploys `FileSystemWatcher` for event-driven state tracking |
| **Linux** | Adds shell aliases and keybindings to `.bashrc`/`.zshrc` |
| **Both** | Deploys scripts to `~/.tsg/` and creates SQLite database |

## ⌨️ Commands

### Core

```bash
tsg install       # Setup scripts, shortcuts & terminal integration
tsg uninstall     # Remove configuration
tsg doctor        # 🩺 Diagnose environment issues
tsg config        # ⚙️ Show/set configuration (max-snapshots, etc.)
tsg version       # Show version
```

### Copilot Performance

```bash
tsg boost         # ⚡ Elevate Copilot process priority (Admin/sudo)
tsg monitor       # 📊 Safe live monitor with diagnostics
tsg status        # 📋 Quick health check
tsg restore       # 🔄 Revert all priority changes
tsg focus         # 🎯 Focus ALL resources on stuck process (Admin)
```

### Window & Session Management

```bash
tsg windows            # Show active & recently closed windows with tabs
tsg windows -i         # 🖥️ Interactive window dashboard
tsg windows --history  # Browse window history from database
tsg windows --restore  # Restore closed windows with all tabs
tsg recover            # 🔄 Recover terminal tabs + Copilot sessions
tsg snapshots          # 📸 List all saved terminal snapshots
tsg snapshots --all    # Show all with tab details
tsg capture            # Capture current terminal state to SQLite
```

### Process Manager

```bash
tsg processes          # Show dev processes with ports & resource usage
tsg ps                 # Alias for tsg processes
tsg ps -i              # 🔧 Interactive process manager with navigation
tsg ps --orphans       # ⚠️ Show only orphaned background processes
tsg ps --ports         # 🌐 Show only port-binding processes
tsg ps --kill <PID>    # 💀 Kill a process (with confirmation)
tsg ps --kill-tree <PID>  # 🌳 Kill process tree
```

### Database Query

```bash
tsg db "SELECT * FROM windows"              # Query terminal database
tsg db "SELECT * FROM events ORDER BY ts DESC LIMIT 10"
```

### Keyboard Shortcuts

| Shortcut | Action | Terminal Profile |
|----------|--------|-----------------|
| `Ctrl+Alt+B` | Boost | — |
| `Ctrl+Alt+M` | Monitor | — |
| `Ctrl+Alt+S` | Status | — |
| `Ctrl+Alt+F` | Recover | — |
| `Ctrl+Alt+R` | Restore | — |
| `Ctrl+Alt+W` | Window Dashboard | 🖼️ TSG Windows |
| `Ctrl+Alt+N` | Snapshots | 📸 TSG Snapshots |
| `Ctrl+Alt+P` | Process Manager | 🔧 TSG Processes |

## 🖥️ Window Tracking

TSG provides **real-time window and tab tracking** using COM IUIAutomation — no stale data.

```
  🪟 Terminal Windows — 2 active, 1 recently closed
  📅 2026-04-18 12:15:05  📑 8 tabs  🤖 5  [live]  👁️ UIA:2

  ── Active Windows ──
  🟢 Window 1  [a1b2c3d4e5f6]  📑 6 tabs  🤖 3
     📅 Opened: 2026-04-18 10:30:00  👁️ Last seen: 2026-04-18 12:15:05
     🤖 Build microservice API
     🤖 Fix auth middleware
     🤖 Debug test failures
     📂 Project Documentation
     📂 Terminal Configs
     📂 Source Repos

  🟢 Window 2  [f6e5d4c3b2a1]  📑 2 tabs  🤖 2
     🤖 Deploy staging
     🤖 Monitor logs

  ── Recently Closed ──
  🔴 Window 3  [x9y8z7w6v5u4]  📑 4 tabs
     📅 Opened: 2026-04-18 08:00:00  ❌ Closed: 2026-04-18 11:45:00
     🤖 Old debug session (restorable)
```

### Interactive Dashboard (`tsg windows -i`)

The interactive dashboard provides a menu-driven interface:

- **[R] Restore** — Restore closed windows with all their tabs
- **[S] Snapshot** — Take a snapshot of current terminal state
- **[H] History** — Browse window open/close timeline from database
- **[P] Processes** — Switch to process manager
- **[F] Refresh** — Refresh live window data
- **[Q] Quit**

### How Live Detection Works

TSG uses **COM IUIAutomation** (via P/Invoke with `CoCreateInstance`) to enumerate real terminal windows and tabs in real-time. This replaces the unreliable `state.json` which never removes closed tabs.

- **Primary source:** Live UIA tab enumeration (class `CASCADIA_HOSTING_WINDOW_CLASS`)
- **Fallback:** `state.json` replay actions (when UIA is unavailable)
- **Event tracking:** `FileSystemWatcher` on `state.json` triggers re-capture on any change
- **Storage:** All captures stored in SQLite at `~/.tsg/terminal.db`

## 🔧 Process Manager

A comprehensive dev process manager that identifies development servers, background tasks, and orphaned processes.

```
  🔧 Dev Processes — 45 found | 4200 MB | 3 ports | 2 orphans

  ── 🖥️ Terminal (PID 18672) ──  (12 processes, 1800 MB)
  ▶   20360  copilot     396.1 MB  ⏱️ 1.0h  🕐 9h
             📋 copilot-win32-x64.exe --stdio
      34388  pwsh        146.8 MB  ⏱️ 5s    🕐 16m
             📋 "C:\Program Files\PowerShell\7\pwsh.exe"
      48008  wsl          13.1 MB  ⏱️ 0s    🕐 1m   🌐 :8080
             📋 wsl.exe bash -lc "npm run dev"

  ── ⚠️ Unattributed ──  (33 processes, 2400 MB)
      21904  devenv     1329.2 MB  ⏱️ 10m   🕐 12h
      ...
```

### Interactive Mode (`tsg ps -i`)

Full keyboard-driven process management with viewport scrolling:

| Key | Action |
|-----|--------|
| `↑` `↓` | Navigate processes (viewport auto-scrolls) |
| `Enter` | Expand process details with impact analysis |
| `K` | Kill selected process (with confirmation) |
| `T` | Tree-kill process and all children |
| `O` | Filter to orphan processes only |
| `P` | Filter to port-binding processes only |
| `C` | Clean ALL orphan processes |
| `F` | Refresh (rescan all processes) |
| `Q` | Quit |

### Process Detail View

Pressing `Enter` on a process shows:

```
  ═══ Process Details ═══
  Name:       node
  PID:        48008
  Parent:     pwsh (34388)
  Memory:     256.4 MB
  CPU Time:   2m 15s
  Uptime:     45 minutes
  Terminal:   Window 1 (PID 18672)
  Ports:      :3000, :3001

  📋 Command Line:
  node /home/user/project/node_modules/.bin/next dev --port 3000

  👶 Child Processes (3):
    PID 48120  node     45.2 MB
    PID 48200  node     32.1 MB
    PID 48350  esbuild  12.0 MB

  ⚠️ Kill Impact Analysis:
    💾 Memory freed: 345.7 MB (4 processes)
    🌐 Ports released: :3000, :3001
    📂 Directories affected: /home/user/project

  [K] Kill  [T] Tree-kill  [←] Back
```

### What It Detects

| Category | Detection Method |
|----------|-----------------|
| **Terminal processes** | Parent chain tracing to `WindowsTerminal.exe` |
| **Dev servers** | Keywords: node, python, dotnet, cargo, go, java, ruby, etc. |
| **Listening ports** | P/Invoke `GetExtendedTcpTable` from `iphlpapi.dll` |
| **Orphan processes** | Parent PID points to dead/non-existent process |
| **Resource usage** | WMI `Win32_Process` bulk query + working set size |

## 📊 Safe Monitor

**100% read-only** — never modifies Copilot's internal files.

```
  ⚡ TSG MONITOR  06:30:00  [🟢 BOOSTED] [4/4]
  ══════════════════════════════════════════════════════
  PID 221120  | copilot    | 180 MB | H:    234 | Δ  0.3s | 🟢 IDLE
           📂 MyProject — Implement auth system
           📄 Events: 2.1MB | Last: assistant.turn_end | ok
  PID 52300   | copilot-ls |  16 MB | H:    120 | Δ  0.0s | 🟢 IDLE
           🟣 Visual Studio
  ══════════════════════════════════════════════════════
  TOTAL: 196 MB | CPU: 125.3s | Threads: 31
```

### Diagnostic Indicators

| State | Meaning | Action |
|-------|---------|--------|
| 🟢 IDLE | Healthy, idle | None |
| 🟡 ACTIVE | Processing | Wait |
| 🔴 HIGH CPU | Heavy computation | Wait or close → resume |
| 🔴 HANDLE LEAK | events.jsonl too large | Close tab → `copilot --resume` |
| ❄️ STUCK | Turn never completed | Close tab → resume (auto-recovers) |

## 🔄 Session Recovery

```bash
tsg recover
```

Scans Windows Terminal state + Copilot sessions and reopens tabs with `copilot --resume`:

```
  🔄 Session Recovery
  ✅ 8 tabs (5 copilot)

  [1] 🤖 MyProject (Win 1)
      💬 Implement auth system
  [2] 📂 Documents (Win 1)
  [3] 🤖 WebApp (Win 2)
      💬 Fix DllNotFoundException
```

### Window Restore (`tsg windows --restore`)

Restores closed windows with their last known tabs from the SQLite database:

```bash
tsg windows --restore    # Interactive selection from closed windows
```

## 📸 Snapshots

Snapshots capture the complete terminal state (windows, tabs, directories, Copilot sessions) at a point in time:

```bash
tsg snapshots       # List recent snapshots with timestamps
tsg snapshots --all # Show all snapshots with tab details
tsg capture         # Take a manual snapshot
```

```
  📸 Terminal Snapshots (15 total)
  [ 1] 📅 2026-04-18 12:15:05  ⏱️ 2m ago   📺 2 win  📑 8 tabs  🤖 5
  [ 2] 📅 2026-04-18 12:10:00  ⏱️ 7m ago   📺 2 win  📑 8 tabs  🤖 5
  [ 3] 📅 2026-04-18 11:45:00  ⏱️ 32m ago  📺 3 win  📑 12 tabs 🤖 8
```

Snapshots are configurable via `tsg config max-snapshots <N>` (default: 50, range: 5–1000).

## 🗄️ SQLite Database

All terminal state is tracked in a local SQLite database at `~/.tsg/terminal.db`:

| Table | Purpose |
|-------|---------|
| `captures` | Point-in-time state captures with quality flags |
| `capture_windows` | Windows in each capture |
| `capture_tabs` | Tabs with titles, directories, types |
| `windows` | Persistent window identity (first_seen, last_seen, closed_at) |
| `events` | State change timeline (window_opened, window_closed) |

Query directly with:

```bash
tsg db "SELECT * FROM windows WHERE closed_at IS NOT NULL"
tsg db "SELECT * FROM events ORDER BY ts DESC LIMIT 20"
```

### Data Quality Flags

| Flag | Source | Reliability |
|------|--------|-------------|
| `live` | COM IUIAutomation real-time | ✅ Highest |
| `verified` | UIA confirmed | ✅ High |
| `trimmed` | UIA-corrected count | ✅ High |
| `stale` | state.json age > threshold | ⚠️ Low |
| `no-uia` | UIA unavailable | ⚠️ Fallback |

## 🩺 Doctor

```bash
tsg doctor
```

```
  🩺 TSG Doctor — Environment Check

  ✅ .NET 10.0.0
  ✅ Shell: C:\Program Files\PowerShell\7\pwsh.exe
  ✅ TSG dir: C:\Users\you\.tsg
  ✅ Copilot sessions: 12
  ✅ Snapshots: 15/50 (max configurable)
  ⚠️ 1 session(s) > 20MB — may cause slowness
  ✅ Windows Terminal Fragment installed
  ✅ Terminal state: 2 windows, 8 tabs

  🎉 All checks passed!
```

## 🛡️ Safety

> **TSG never modifies Copilot's internal files.**

- ✅ All monitoring is **read-only** — only reads metadata
- ✅ Never deletes, trims, or edits `events.jsonl`
- ✅ Session recovery uses native `copilot --resume`
- ✅ Process kill requires explicit user confirmation with impact analysis
- ✅ All data stored locally in `~/.tsg/` — no network access

## ⚡ Architecture

```
tsg (dotnet tool)
 ├── Platform/
 │   ├── IPlatformHost.cs      — Cross-platform abstraction
 │   ├── WindowsHost.cs        — Windows Terminal Fragment + PSReadLine
 │   └── LinuxHost.cs          — bash/zsh aliases + keybindings
 ├── Scripts/
 │   ├── windows/*.ps1         — PowerShell monitoring & tracking scripts
 │   └── linux/*.sh            — Bash monitoring scripts
 ├── CommandRegistry.cs        — Lambda-based command routing (C# 14)
 ├── Configuration.cs          — Persistent settings (max-snapshots, etc.)
 ├── StateCapture.cs           — Live state capture engine (UIA + state.json)
 ├── UiaComHelper.cs           — COM IUIAutomation interop via P/Invoke
 ├── TerminalDatabase.cs       — SQLite temporal database layer
 ├── Windows.cs                — Window tracking & interactive dashboard
 ├── ProcessManager.cs         — Dev process manager with interactive UI
 ├── Snapshots.cs              — Snapshot listing and management
 ├── DbQuery.cs                — Direct SQL query interface
 ├── Installer.cs              — Script deployment + profile config
 ├── ScriptRunner.cs           — Cross-platform script execution
 └── Diagnostics.cs            — Environment health checks
```

**Built with:** .NET 10 · C# 14 · COM IUIAutomation · SQLite · Windows Terminal Fragments API

**Dependencies:** `Microsoft.Data.Sqlite` 10.0.6 (single external dependency)

## 📦 Requirements

| Platform | Requirements |
|----------|-------------|
| **Windows** | Windows Terminal, PowerShell 7+, .NET 10 Runtime |
| **Linux** | bash/zsh, .NET 10 Runtime |
| **Both** | GitHub Copilot CLI (`npm i -g @github/copilot`) |

## 🗑️ Uninstall

```bash
tsg uninstall
dotnet tool uninstall -g TerminalStateGuard
```

Removes all configuration including Fragment profiles, PSReadLine shortcuts, and `~/.tsg/` scripts. The SQLite database (`~/.tsg/terminal.db`) is preserved for reference.

## 📄 License

[MIT](LICENSE) — Made with ⚡ by [sbay-dev](https://github.com/sbay-dev)
