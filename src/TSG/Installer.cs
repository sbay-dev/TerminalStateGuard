using System.Reflection;
using TSG.Platform;

namespace TSG;

/// <summary>
/// Installs/uninstalls TSG scripts, profile config and terminal integration.
/// </summary>
public class Installer(IPlatformHost host)
{
    const string Marker = "# ══ TSG START ══";
    const string EndMarker = "# ══ TSG END ══";

    public async Task InstallAsync()
    {
        Console.WriteLine("\n  ⚡ TSG — Terminal State Guard Installer\n");

        // 1. Extract scripts
        Directory.CreateDirectory(host.TsgDir);
        ExtractScripts();
        Console.WriteLine($"  ✅ Scripts → {host.TsgDir}");

        // 2. Shell profile (PSReadLine shortcuts on Windows, aliases on Linux)
        if (OperatingSystem.IsWindows())
            ConfigurePwshProfile();
        Console.WriteLine($"  ✅ Shell profile configured");

        // 3. Terminal integration (Fragment on Windows, aliases on Linux)
        await host.InstallTerminalIntegrationAsync();

        // 4. Summary
        Console.WriteLine("""

          ═══ COMMANDS ═══
            tsg boost       ⚡ Elevate Copilot priority
            tsg monitor     📊 Safe live monitor
            tsg status      📋 Health check
            tsg recover     🔄 Recover sessions
            tsg restore     🔄 Restore priorities
            tsg doctor      🩺 Environment diagnostics

          ═══ SHORTCUTS ═══
            Ctrl+Alt+B → Boost     Ctrl+Alt+M → Monitor
            Ctrl+Alt+S → Status    Ctrl+Alt+F → Recover
            Ctrl+Alt+R → Restore

          🎉 Installation complete! Reopen terminal to activate.
        """);
    }

    public async Task UninstallAsync()
    {
        Console.WriteLine("\n  🗑️ Uninstalling TSG...\n");

        // Remove profile block
        if (File.Exists(host.ShellProfilePath))
        {
            var content = await File.ReadAllTextAsync(host.ShellProfilePath);
            var start = content.IndexOf(Marker, StringComparison.Ordinal);
            var end = content.IndexOf(EndMarker, StringComparison.Ordinal);
            if (start >= 0 && end >= 0)
            {
                content = content[..start] + content[(end + EndMarker.Length)..];
                await File.WriteAllTextAsync(host.ShellProfilePath, content.Trim() + "\n");
                Console.WriteLine("  ✅ Profile cleaned");
            }
        }

        await host.RemoveTerminalIntegrationAsync();

        Console.WriteLine($"  ℹ️  Scripts kept at: {host.TsgDir}");
        Console.WriteLine("  ℹ️  To fully remove: rm -r ~/.tsg");
        Console.WriteLine("\n  ✅ Uninstalled\n");
    }

    void ExtractScripts()
    {
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var folder = OperatingSystem.IsWindows() ? ".Scripts.windows." : ".Scripts.linux.";

        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.Contains(folder, StringComparison.Ordinal)))
        {
            var fileName = name[(name.LastIndexOf('.', name.LastIndexOf('.') - 1) + 1)..];
            var destPath = Path.Combine(host.TsgDir, fileName);

            using var stream = assembly.GetManifestResourceStream(name)!;
            using var file = File.Create(destPath);
            stream.CopyTo(file);
        }
    }

    void ConfigurePwshProfile()
    {
        var dir = Path.GetDirectoryName(host.ShellProfilePath)!;
        Directory.CreateDirectory(dir);

        var existing = File.Exists(host.ShellProfilePath) ? File.ReadAllText(host.ShellProfilePath) : "";

        // Remove old block
        var start = existing.IndexOf(Marker, StringComparison.Ordinal);
        var end = existing.IndexOf(EndMarker, StringComparison.Ordinal);
        if (start >= 0 && end >= 0)
            existing = existing[..start] + existing[(end + EndMarker.Length)..];

        // PSReadLine shortcuts — execute tsg command directly (no script paths)
        var block = """

        # ══ TSG START ══
        # Terminal State Guard — auto-save + shortcuts
        $script:_tsgLastSave = Get-Date; $script:_tsgGuardLoaded = $false
        function global:prompt {
            $now = Get-Date
            if (($now - $script:_tsgLastSave).TotalMinutes -ge 5) {
                if (-not $script:_tsgGuardLoaded) { $g = Join-Path $env:USERPROFILE ".tsg\TerminalStateGuard.ps1"; if (Test-Path $g) { . $g; $script:_tsgGuardLoaded = $true } }
                if ($script:_tsgGuardLoaded) { try { Save-TerminalState } catch {} }
                $script:_tsgLastSave = $now
            }
            "PS $($executionContext.SessionState.Path.CurrentLocation)$('>' * ($nestedPromptLevel + 1)) "
        }
        Register-EngineEvent PowerShell.Exiting -Action { $g = Join-Path $env:USERPROFILE ".tsg\TerminalStateGuard.ps1"; if (Test-Path $g) { . $g; try { Save-TerminalState } catch {} } } | Out-Null
        Set-PSReadLineKeyHandler -Chord "Ctrl+Alt+b" -ScriptBlock { [Microsoft.PowerShell.PSConsoleReadLine]::RevertLine(); [Microsoft.PowerShell.PSConsoleReadLine]::Insert("tsg boost"); [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine() }
        Set-PSReadLineKeyHandler -Chord "Ctrl+Alt+m" -ScriptBlock { [Microsoft.PowerShell.PSConsoleReadLine]::RevertLine(); [Microsoft.PowerShell.PSConsoleReadLine]::Insert("tsg monitor"); [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine() }
        Set-PSReadLineKeyHandler -Chord "Ctrl+Alt+s" -ScriptBlock { [Microsoft.PowerShell.PSConsoleReadLine]::RevertLine(); [Microsoft.PowerShell.PSConsoleReadLine]::Insert("tsg status"); [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine() }
        Set-PSReadLineKeyHandler -Chord "Ctrl+Alt+f" -ScriptBlock { [Microsoft.PowerShell.PSConsoleReadLine]::RevertLine(); [Microsoft.PowerShell.PSConsoleReadLine]::Insert("tsg recover"); [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine() }
        Set-PSReadLineKeyHandler -Chord "Ctrl+Alt+r" -ScriptBlock { [Microsoft.PowerShell.PSConsoleReadLine]::RevertLine(); [Microsoft.PowerShell.PSConsoleReadLine]::Insert("tsg restore"); [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine() }
        Set-PSReadLineKeyHandler -Chord "Ctrl+Alt+h" -ScriptBlock { [Microsoft.PowerShell.PSConsoleReadLine]::RevertLine(); [Microsoft.PowerShell.PSConsoleReadLine]::Insert("tsg help"); [Microsoft.PowerShell.PSConsoleReadLine]::AcceptLine() }
        # ══ TSG END ══
        """;

        File.WriteAllText(host.ShellProfilePath, existing.TrimEnd() + "\n" + block + "\n");
    }
}
