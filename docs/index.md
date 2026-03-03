---
layout: default
title: TSG — Terminal State Guard
description: Cross-platform CLI for Copilot Performance Boosting, Safe Monitoring & Session Recovery
---

<div align="center" style="padding: 2rem 0;">

<h1>⚡ TSG — Terminal State Guard</h1>
<p style="font-size: 1.2rem; color: #666;">
Cross-platform CLI for Copilot Performance Boosting, Safe Monitoring & Session Recovery
</p>

<a href="https://www.nuget.org/packages/TerminalStateGuard">
  <img src="https://img.shields.io/nuget/v/TerminalStateGuard?style=for-the-badge&logo=nuget&color=004880" alt="NuGet">
</a>
<a href="https://github.com/sbay-dev/TerminalStateGuard">
  <img src="https://img.shields.io/github/stars/sbay-dev/TerminalStateGuard?style=for-the-badge&logo=github" alt="Stars">
</a>
<a href="https://github.com/sbay-dev/TerminalStateGuard/blob/main/LICENSE">
  <img src="https://img.shields.io/badge/License-MIT-yellow.svg?style=for-the-badge" alt="License">
</a>

</div>

---

## Quick Start

```bash
dotnet tool install -g TerminalStateGuard
tsg install
```

**Done.** Reopen your terminal — shortcuts and native integration are active.

---

## Features

### 📊 Safe Monitor
Real-time process monitoring with **100% read-only** diagnostics. Never modifies Copilot files.

### ⚡ Performance Boost
Elevates Copilot process priority, optimizes memory, assigns all CPU cores.

### 🔄 Session Recovery
Recovers terminal tabs + Copilot sessions after crash/reboot using native `copilot --resume`.

### 🩺 Doctor
Diagnoses environment — finds stuck sessions, validates setup, checks prerequisites.

### 🖥️ Cross-Platform
Windows Terminal Fragment extension + Linux bash/zsh integration.

---

## Commands

| Command | Description |
|---------|-------------|
| `tsg install` | Setup scripts, shortcuts & terminal integration |
| `tsg boost` | Elevate Copilot priority |
| `tsg monitor` | Safe live monitor |
| `tsg status` | Quick health check |
| `tsg recover` | Recover sessions |
| `tsg doctor` | Environment diagnostics |
| `tsg restore` | Revert priorities |

## Keyboard Shortcuts

| Shortcut | Action |
|----------|--------|
| `Ctrl+Alt+B` | Boost |
| `Ctrl+Alt+M` | Monitor |
| `Ctrl+Alt+S` | Status |
| `Ctrl+Alt+F` | Recover |
| `Ctrl+Alt+R` | Restore |

---

<div align="center" style="padding: 2rem 0; color: #666;">
<p>Built with .NET 10 · C# 14 · Windows Terminal Fragments API</p>
<p>Made with ⚡ by <a href="https://github.com/sbay-dev">sbay-dev</a></p>
</div>
