# Security Policy

## Supported Versions

| Version | Supported |
|---------|-----------|
| 1.x     | ✅ Active |

## Security Design Principles

TSG is built with a **security-first** philosophy:

### 🛡️ Read-Only Monitoring
- The monitor **never modifies** Copilot session files (`events.jsonl`, `session.db`, `workspace.yaml`)
- All session diagnostics are performed by reading file metadata only
- No write operations are performed on any files outside `~/.tsg/`

### 🔒 Zero External Dependencies
- TSG has **no third-party NuGet dependencies** — only .NET 10 SDK libraries
- This eliminates supply-chain attack vectors entirely
- Verified via `dotnet list package --vulnerable --include-transitive`

### 🔍 Static Analysis
- Built with `AnalysisLevel=latest-all` (.NET Roslyn analyzers at maximum strictness)
- `NuGetAudit=true` with `NuGetAuditLevel=low` enabled in CI
- All CA1031 (broad exception), CA1062 (null validation) findings resolved
- CI runs `dotnet format --verify-no-changes` to enforce code style

### 📦 Package Integrity
- Published via GitHub Actions with `--skip-duplicate` to prevent version overwriting
- NuGet API key stored as GitHub encrypted secret (`NUGET_TSG_API_KEY`)
- Packages are signed by NuGet.org's repository signature
- Source link enabled for debuggable builds

### 🖥️ Permissions
- **No network access** — TSG never makes HTTP calls (except optional `Test-Connection` in monitor)
- **File access** limited to:
  - `~/.tsg/` — scripts and snapshots (read/write)
  - `~/.copilot/session-state/` — session metadata (read-only)
  - PowerShell profile — appends a marked block (write, with clean uninstall)
  - Windows Terminal Fragments dir — drops a JSON file (write, with clean uninstall)
- **Process operations**: reads process list, optionally sets priority (requires Admin/sudo)

### 🧪 Security Audit Results

```
Vulnerability Scan:     ✅ 0 vulnerable packages
Deprecated Packages:    ✅ 0 deprecated packages
Static Analysis (CA):   ✅ 0 security warnings
NuGet Audit:            ✅ Enabled (level: low, mode: all)
External Dependencies:  ✅ None (zero third-party packages)
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
