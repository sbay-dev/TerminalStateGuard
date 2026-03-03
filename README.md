<div align="center">

# ⚡ TSG — Terminal State Guard

### Cross-platform CLI for Copilot Performance Boosting, Safe Monitoring & Session Recovery

[![NuGet](https://img.shields.io/nuget/v/TerminalStateGuard?style=for-the-badge&logo=nuget&color=004880)](https://www.nuget.org/packages/TerminalStateGuard)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg?style=for-the-badge)](LICENSE)
[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?style=for-the-badge&logo=dotnet)](https://dotnet.microsoft.com)
[![Platform](https://img.shields.io/badge/Platform-Windows%20%7C%20Linux-blue?style=for-the-badge)]()

**One command to install. Keyboard shortcuts ready. Zero config.**

[Installation](#-installation) · [Commands](#-commands) · [Monitor](#-safe-monitor) · [Recovery](#-session-recovery) · [Doctor](#-doctor)

---

</div>

## 🚀 Installation

```bash
dotnet tool install -g TerminalStateGuard
tsg install
```

**Done.** Reopen your terminal — shortcuts and integration are active.

### What `tsg install` does:

| Platform | Action |
|----------|--------|
| **Windows** | Installs [Windows Terminal Fragment](https://learn.microsoft.com/en-us/windows/terminal/json-fragment-extensions) for native shortcut integration |
| **Windows** | Configures PSReadLine keyboard shortcuts in PowerShell profile |
| **Linux** | Adds shell aliases and keybindings to `.bashrc`/`.zshrc` |
| **Both** | Deploys monitoring scripts to `~/.tsg/` |

## ⌨️ Commands

```bash
tsg install       # Setup scripts, shortcuts & terminal integration
tsg boost         # ⚡ Elevate Copilot process priority (Admin/sudo)
tsg monitor       # 📊 Safe live monitor with diagnostics
tsg status        # 📋 Quick health check
tsg recover       # 🔄 Recover terminal tabs + Copilot sessions
tsg restore       # 🔄 Revert all priority changes
tsg doctor        # 🩺 Diagnose environment issues
tsg uninstall     # 🗑️ Remove configuration
```

### Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| `Ctrl+Alt+B` | Boost |
| `Ctrl+Alt+M` | Monitor |
| `Ctrl+Alt+S` | Status |
| `Ctrl+Alt+F` | Recover |
| `Ctrl+Alt+R` | Restore |

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

## 🩺 Doctor

```bash
tsg doctor
```

Checks environment, finds stuck sessions, validates setup:

```
  🩺 TSG Doctor — Environment Check

  ✅ .NET 10.0.0
  ✅ Shell: C:\Program Files\PowerShell\7\pwsh.exe
  ✅ TSG dir: C:\Users\you\.tsg
  ✅ Copilot sessions: 12
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
- ✅ Diagnostics show root cause + safe manual recommendations

## ⚡ Architecture

```
tsg (dotnet tool)
 ├── Platform/
 │   ├── IPlatformHost.cs      — Cross-platform abstraction
 │   ├── WindowsHost.cs        — Windows Terminal Fragment + PSReadLine
 │   └── LinuxHost.cs          — bash/zsh aliases + keybindings
 ├── Scripts/
 │   ├── windows/*.ps1         — PowerShell monitoring scripts
 │   └── linux/*.sh            — Bash monitoring scripts
 ├── CommandRegistry.cs        — Lambda-based command routing
 ├── Installer.cs              — Script deployment + profile config
 ├── ScriptRunner.cs           — Cross-platform script execution
 └── Diagnostics.cs            — Environment health checks
```

**Built with:** .NET 10 · C# 14 · Windows Terminal Fragments API

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

## 📄 License

[MIT](LICENSE) — Made with ⚡ by [sbay-dev](https://github.com/sbay-dev)
