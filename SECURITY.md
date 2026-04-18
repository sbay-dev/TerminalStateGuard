# Security Policy

## Supported Versions

| Version | Supported |
|---------|-----------|
| 2.x     | ✅ Active |
| 1.x     | ⚠️ Maintenance |

## Security Design Principles

TSG is built with a **security-first** philosophy:

### 🛡️ Read-Only Monitoring
- The monitor **never modifies** Copilot session files (`events.jsonl`, `session.db`, `workspace.yaml`)
- All session diagnostics are performed by reading file metadata only
- No write operations are performed on any files outside `~/.tsg/`

### 🔒 Minimal Dependencies
- TSG has **one external dependency**: `Microsoft.Data.Sqlite` 10.0.6 (Microsoft-maintained)
- All other functionality uses .NET 10 SDK built-in libraries and COM interop
- Supply-chain attack surface is minimal
- Verified via `dotnet list package --vulnerable --include-transitive`

### 🔍 Static Analysis
- Built with `AnalysisLevel=latest-all` (.NET Roslyn analyzers at maximum strictness)
- `NuGetAudit=true` with `NuGetAuditLevel=low` enabled in CI
- CI runs `dotnet format --verify-no-changes` to enforce code style

### 📦 Package Integrity
- Published via GitHub Actions with `--skip-duplicate` to prevent version overwriting
- NuGet API key stored as GitHub encrypted secret (`NUGET_TSG_API_KEY`)
- Packages are signed by NuGet.org's repository signature
- Source link enabled for debuggable builds

### 🖥️ Permissions
- **No network access** — TSG never makes HTTP calls (except optional `Test-Connection` in monitor)
- **File access** limited to:
  - `~/.tsg/` — scripts, config, and SQLite database (read/write)
  - `~/.copilot/session-state/` — session metadata (read-only)
  - `~/.copilotAccel/terminal-snapshots/` — snapshot files (read/write)
  - PowerShell profile — appends a marked block (write, with clean uninstall)
  - Windows Terminal Fragments dir — drops a JSON file (write, with clean uninstall)
- **Process operations**: reads process list via WMI, optionally kills processes (with user confirmation), optionally sets priority (requires Admin/sudo)
- **COM access**: Uses `IUIAutomation` COM interface for live window/tab enumeration (read-only)

### 🔐 Process Kill Safety
- Process kill operations always require explicit user confirmation
- Impact analysis is shown before kill: affected child processes, ports released, directories impacted
- Tree-kill uses `taskkill /T` for safe hierarchical termination
- System processes are excluded from the process list

### 🗄️ Database Security
- SQLite database is local-only at `~/.tsg/terminal.db`
- Named mutex (`Global\\TSG_DB_MUTEX`) prevents concurrent write corruption
- `tsg db` command is read-only (rejects INSERT/UPDATE/DELETE/DROP/ALTER/CREATE)
- No sensitive data stored — only window titles, tab names, and process metadata

### 🧪 Security Audit Results

```
Vulnerability Scan:     ✅ 0 vulnerable packages
Deprecated Packages:    ✅ 0 deprecated packages
Static Analysis (CA):   ✅ 0 security warnings
NuGet Audit:            ✅ Enabled (level: low, mode: all)
External Dependencies:  ✅ 1 (Microsoft.Data.Sqlite — Microsoft-maintained)
```

## Reporting a Vulnerability

If you discover a security vulnerability, please report it responsibly:

1. **Do NOT** open a public GitHub issue
2. Email: [Create a private security advisory](https://github.com/sbay-dev/TerminalStateGuard/security/advisories/new)
3. Include: description, reproduction steps, and impact assessment

We will respond within 48 hours and issue a patch release if confirmed.

## Security Scanning in CI

Every release is automatically scanned:

```yaml
# .github/workflows/release.yml
- dotnet list package --vulnerable --include-transitive
- dotnet build with AnalysisLevel=latest-all
- dotnet format --verify-no-changes
```
