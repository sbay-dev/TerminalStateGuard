using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Runtime.InteropServices;
using TSG.Platform;

namespace TSG;

/// <summary>
/// Terminal process manager — enumerates dev processes, ports, terminal attribution.
/// Uses WMI bulk query + GetExtendedTcpTable P/Invoke for efficient collection.
/// </summary>
public static class ProcessManager
{
    // Known dev-server process patterns (name or cmdline keywords)
    static readonly HashSet<string> DevProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "npm", "npx", "yarn", "pnpm", "bun", "deno",
        "python", "python3", "pip", "uvicorn", "gunicorn", "flask",
        "dotnet", "kestrel",
        "go", "air",
        "java", "javaw", "gradle", "gradlew", "mvn",
        "ruby", "rails", "puma", "unicorn",
        "cargo", "rustc",
        "php", "artisan", "composer",
        "webpack", "vite", "esbuild", "tsc", "next", "nuxt", "remix",
        "docker", "docker-compose", "podman",
        "redis-server", "mongod", "postgres", "mysqld",
        "hugo", "jekyll", "eleventy",
        "copilot", "gh",
        "pwsh", "powershell", "cmd",
    };

    // System processes to always exclude
    static readonly HashSet<string> ExcludedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "msedge", "chrome", "firefox", "brave",
        "TextInputHost", "SearchHost", "ShellExperienceHost", "StartMenuExperienceHost",
        "Widgets", "WidgetService", "SystemSettings", "ApplicationFrameHost",
        "RuntimeBroker", "svchost", "csrss", "smss", "wininit", "winlogon",
        "explorer", "dwm", "taskhostw", "sihost", "fontdrvhost", "ctfmon",
        "SecurityHealthSystray", "SecurityHealthService", "SgrmBroker",
        "WhatsApp", "WhatsApp.Root", "Spotify", "Discord", "Slack", "Teams",
        "OneDrive", "GameBar", "GameBarFTServer",
        "conhost", "OpenConsole", "WindowsTerminal",
        "WmiPrvSE", "dllhost", "msiexec", "TiWorker",
        "spoolsv", "lsass", "services", "wuauserv",
        "CompPkgSrv", "msedgewebview2", "MicrosoftEdgeUpdate",
    };

    // Cmdline keywords that indicate dev activity
    static readonly string[] DevKeywords =
    [
        "serve", "dev", "start", "watch", "run", "build", "test",
        "server", "listen", "--port", "-p ", "localhost", "127.0.0.1",
        "hot-reload", "--watch", "nodemon", "ts-node", "tsx",
    ];

    public static async Task<int> RunAsync(IPlatformHost host, string[] args)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length > 0 && args[0].Equals("--kill", StringComparison.OrdinalIgnoreCase))
            return HandleKill(args);

        if (args.Length > 0 && args[0].Equals("--kill-tree", StringComparison.OrdinalIgnoreCase))
            return HandleKillTree(args);

        var interactive = args.Any(a => a.Equals("--interactive", StringComparison.OrdinalIgnoreCase)
                                     || a.Equals("-i", StringComparison.OrdinalIgnoreCase));
        var orphansOnly = args.Any(a => a.Equals("--orphans", StringComparison.OrdinalIgnoreCase));
        var portsOnly = args.Any(a => a.Equals("--ports", StringComparison.OrdinalIgnoreCase));

        if (interactive)
            return await InteractiveLoop(host);

        return await ShowProcesses(host, orphansOnly, portsOnly);
    }

    static async Task<List<DevProcess>> ScanProcesses()
    {
        var processesTask = Task.Run(GetDevProcesses);
        var portsTask = Task.Run(GetListeningPorts);
        var terminalsTask = Task.Run(GetTerminalProcessIds);

        await Task.WhenAll(processesTask, portsTask, terminalsTask);

        var processes = await processesTask;
        var portMap = await portsTask;
        var terminalPids = await terminalsTask;

        foreach (var proc in processes)
        {
            if (portMap.TryGetValue(proc.Pid, out var ports))
                proc.Ports.AddRange(ports);
            proc.TerminalGroup = TraceToTerminal(proc.Pid, proc.ParentPid, terminalPids, processes);
            proc.IsOrphan = proc.ParentPid > 0 && !IsProcessAlive(proc.ParentPid);
        }

        return processes;
    }

    /// <summary>
    /// Builds a flat ordered list of processes matching the current filter, grouped by terminal.
    /// </summary>
    static List<DevProcess> BuildDisplayList(List<DevProcess> processes, string? filterMode)
    {
        var filtered = filterMode switch
        {
            "orphans" => [.. processes.Where(p => p.IsOrphan)],
            "ports" => [.. processes.Where(p => p.Ports.Count > 0)],
            _ => processes
        };

        return [.. filtered
            .GroupBy(p => p.TerminalGroup ?? "⚠️ Unattributed")
            .OrderBy(g => g.Key == "⚠️ Unattributed" ? 1 : 0)
            .ThenBy(g => g.Key)
            .SelectMany(g => g.OrderByDescending(p => p.MemoryMb))];
    }

    static int DisplayProcesses(List<DevProcess> processes, string? filterMode = null, int selectedIndex = -1)
    {
        var filtered = BuildDisplayList(processes, filterMode);

        if (filtered.Count == 0)
        {
            Console.WriteLine($"  ℹ️  No {filterMode ?? "dev"} processes found.");
            return 0;
        }

        var totalMem = filtered.Sum(p => p.MemoryMb);
        var totalPorts = filtered.Sum(p => p.Ports.Count);
        var orphanCount = filtered.Count(p => p.IsOrphan);

        Console.WriteLine($"\n  🔧 Dev Processes — {filtered.Count} found | {totalMem:F0} MB | {totalPorts} ports | {orphanCount} orphans");
        if (selectedIndex >= 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  ↑↓ Navigate  ⏎ Expand  K Kill  T Tree-kill  Q Quit");
            Console.ResetColor();
        }
        Console.WriteLine();

        // Viewport: each process takes ~2 lines (main + cmdline), group header 1 line
        // Reserve ~15 lines for header + menu + footer
        int termHeight;
        try { termHeight = Console.WindowHeight; } catch { termHeight = 40; }
        var maxVisibleProcesses = Math.Max(5, (termHeight - 18) / 2);

        // Calculate viewport window around selectedIndex
        int viewStart = 0, viewEnd = filtered.Count;
        if (selectedIndex >= 0 && filtered.Count > maxVisibleProcesses)
        {
            // Center the selected item in the viewport
            viewStart = Math.Max(0, selectedIndex - maxVisibleProcesses / 2);
            viewEnd = Math.Min(filtered.Count, viewStart + maxVisibleProcesses);
            // Adjust if we hit the bottom
            if (viewEnd == filtered.Count)
                viewStart = Math.Max(0, viewEnd - maxVisibleProcesses);
        }

        // Scroll-up indicator
        if (viewStart > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  ▲ {viewStart} more above...");
            Console.ResetColor();
        }

        string? lastGroup = null;
        for (int i = viewStart; i < viewEnd; i++)
        {
            var proc = filtered[i];
            var group = proc.TerminalGroup ?? "⚠️ Unattributed";

            if (group != lastGroup)
            {
                lastGroup = group;
                var isUnattributed = group == "⚠️ Unattributed";
                Console.ForegroundColor = isUnattributed ? ConsoleColor.Yellow : ConsoleColor.Green;
                Console.Write($"  ── {group}");
                var groupItems = filtered.Where(p => (p.TerminalGroup ?? "⚠️ Unattributed") == group).ToList();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine($"  ({groupItems.Count} processes, {groupItems.Sum(p => p.MemoryMb):F0} MB) ──");
                Console.ResetColor();
            }

            PrintProcess(proc, isSelected: i == selectedIndex);
        }

        // Scroll-down indicator
        if (viewEnd < filtered.Count)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"  ▼ {filtered.Count - viewEnd} more below...");
            Console.ResetColor();
        }

        Console.WriteLine();
        return 0;
    }
    static async Task<int> ShowProcesses(IPlatformHost host, bool orphansOnly, bool portsOnly)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("\n  ⏳ Scanning processes");
        Console.ResetColor();

        var processes = await ScanProcesses();

        Console.Write("\r                        \r");

        var filter = orphansOnly ? "orphans" : portsOnly ? "ports" : null;
        DisplayProcesses(processes, filter);

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  💡 tsg processes -i                 Interactive dashboard");
        Console.WriteLine("  💡 tsg processes --kill <PID>       Kill a process");
        Console.WriteLine("  💡 tsg processes --kill-tree <PID>  Kill process tree");
        Console.WriteLine("  💡 tsg processes --orphans          Show only orphans");
        Console.WriteLine("  💡 tsg processes --ports             Show only port-binding processes");
        Console.ResetColor();
        Console.WriteLine();

        return 0;
    }

    /// <summary>Interactive process manager dashboard with arrow-key navigation.</summary>
    static async Task<int> InteractiveLoop(IPlatformHost host)
    {
        string? filterMode = null;
        List<DevProcess> processes = [];
        List<DevProcess> displayList = [];
        int selectedIndex = 0;
        bool needsScan = true;

        while (true)
        {
            // Full clear: screen + scrollback + cursor home
            Console.Write("\x1b[2J\x1b[3J\x1b[H");

            if (needsScan)
            {
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write("  ⏳ Scanning processes...");
                Console.ResetColor();

                processes = await ScanProcesses();
                Console.Write("\r                          \r");
                needsScan = false;
            }

            displayList = BuildDisplayList(processes, filterMode);
            if (displayList.Count == 0) selectedIndex = -1;
            else if (selectedIndex >= displayList.Count) selectedIndex = displayList.Count - 1;
            else if (selectedIndex < 0) selectedIndex = 0;

            DisplayProcesses(processes, filterMode, selectedIndex);

            // Menu
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("  📋 Actions:");
            WriteMenuOption("[↑↓]", "🔍 Navigate processes");
            WriteMenuOption("[⏎]", "📄 Expand process details & impact analysis");
            WriteMenuOption("[K]", "💀 Kill selected process");
            WriteMenuOption("[T]", "🌳 Tree-kill selected process & children");
            WriteMenuOption("[O]", filterMode == "orphans" ? "⚠️  Show ALL (currently: orphans)" : "⚠️  Show ORPHAN processes only");
            WriteMenuOption("[P]", filterMode == "ports" ? "🌐 Show ALL (currently: ports)" : "🌐 Show PORT-binding only");
            WriteMenuOption("[C]", "🧹 Clean ALL orphans");
            WriteMenuOption("[F]", "🔃 Refresh (rescan)");
            WriteMenuOption("[Q]", "❌ Quit");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");

            if (selectedIndex >= 0 && selectedIndex < displayList.Count)
            {
                var sel = displayList[selectedIndex];
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"  ▶ Selected: ");
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($"{sel.Name} (PID {sel.Pid})");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"  {sel.MemoryMb:F1} MB");
                if (sel.Ports.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.Write($"  🌐 :{string.Join(",", sel.Ports.Select(p => p.ToString(CultureInfo.InvariantCulture)))}");
                }
                Console.ResetColor();
                Console.WriteLine();
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write("\n  Select: ");
            Console.ResetColor();

            var key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.UpArrow:
                    if (selectedIndex > 0) selectedIndex--;
                    continue;

                case ConsoleKey.DownArrow:
                    if (selectedIndex < displayList.Count - 1) selectedIndex++;
                    continue;

                case ConsoleKey.Enter:
                    if (selectedIndex >= 0 && selectedIndex < displayList.Count)
                        await ShowProcessDetails(displayList[selectedIndex], processes);
                    continue;

                case ConsoleKey.Escape:
                    return 0;

                default:
                    break;
            }

            switch (char.ToUpperInvariant(key.KeyChar))
            {
                case 'K':
                    if (selectedIndex >= 0 && selectedIndex < displayList.Count)
                        await KillWithImpactAnalysis(displayList[selectedIndex], processes, tree: false);
                    else
                        await InteractiveKill(processes, tree: false);
                    needsScan = true;
                    break;

                case 'T':
                    if (selectedIndex >= 0 && selectedIndex < displayList.Count)
                        await KillWithImpactAnalysis(displayList[selectedIndex], processes, tree: true);
                    else
                        await InteractiveKill(processes, tree: true);
                    needsScan = true;
                    break;

                case 'O':
                    filterMode = filterMode == "orphans" ? null : "orphans";
                    selectedIndex = 0;
                    break;

                case 'P':
                    filterMode = filterMode == "ports" ? null : "ports";
                    selectedIndex = 0;
                    break;

                case 'C':
                    await CleanOrphans(processes);
                    needsScan = true;
                    break;

                case 'F':
                    needsScan = true;
                    break;

                case 'Q':
                    return 0;
            }
        }
    }

    static void WriteMenuOption(string key, string label)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"    {key,-5} ");
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(label);
    }

    /// <summary>Shows expanded details for a selected process including children, ports, and impact.</summary>
    static async Task ShowProcessDetails(DevProcess proc, List<DevProcess> allProcesses)
    {
        Console.Write("\x1b[2J\x1b[3J\x1b[H");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n  ╔══════════════════════════════════════════════════════════╗");
        Console.WriteLine($"  ║  📄 Process Details                                     ║");
        Console.WriteLine($"  ╚══════════════════════════════════════════════════════════╝");
        Console.ResetColor();

        // Basic info
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"\n  🏷️  Name:     ");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine(proc.Name);

        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"  🔢 PID:      ");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(proc.Pid);

        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"  👆 Parent:   ");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(proc.ParentPid);

        // Memory
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"  💾 Memory:   ");
        Console.ForegroundColor = proc.MemoryMb > 500 ? ConsoleColor.Red
            : proc.MemoryMb > 100 ? ConsoleColor.Yellow : ConsoleColor.Green;
        Console.WriteLine($"{proc.MemoryMb:F1} MB");

        // CPU
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"  ⏱️  CPU Time: ");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(FormatTimeSpan(proc.CpuTime));

        // Uptime
        if (proc.StartTime.HasValue)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"  🕐 Uptime:   ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            var uptime = DateTime.Now - proc.StartTime.Value;
            Console.WriteLine($"{FormatAge(uptime)} (started {proc.StartTime.Value:yyyy-MM-dd HH:mm:ss})");
        }

        // Terminal group
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"  🖥️  Terminal: ");
        Console.ForegroundColor = proc.TerminalGroup != null ? ConsoleColor.Green : ConsoleColor.Yellow;
        Console.WriteLine(proc.TerminalGroup ?? "⚠️ Unattributed");

        // Orphan status
        if (proc.IsOrphan)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  ⚠️  Status:  ORPHAN — parent process no longer exists");
        }

        // Ports
        if (proc.Ports.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"  🌐 Ports:    ");
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine(string.Join(", ", proc.Ports.Select(p => $":{p}")));
        }

        // Full command line
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"\n  📋 Command Line:");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        var cmdLine = proc.CommandLine ?? "(unavailable)";
        // Word-wrap at ~70 chars
        var wrapped = WrapText(cmdLine, 70);
        foreach (var line in wrapped)
            Console.WriteLine($"     {line}");

        // Working directory
        if (!string.IsNullOrEmpty(proc.WorkingDir))
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"\n  📂 Directory: ");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(proc.WorkingDir);
        }

        // === CHILD PROCESSES (Impact Analysis) ===
        var children = FindChildProcesses(proc.Pid, allProcesses);
        if (children.Count > 0)
        {
            var totalChildMem = children.Sum(c => c.MemoryMb);
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine($"\n  👶 Child Processes ({children.Count}, total {totalChildMem:F0} MB):");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  ─────────────────────────────────────────────");
            foreach (var child in children.OrderByDescending(c => c.MemoryMb))
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write($"     {child.Pid,7}  ");
                Console.ForegroundColor = ConsoleColor.Cyan;
                Console.Write($"{child.Name,-16}");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"  {child.MemoryMb:F1} MB");
                if (child.Ports.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.Write($"  🌐 :{string.Join(",", child.Ports.Select(p => p.ToString(CultureInfo.InvariantCulture)))}");
                }
                Console.ResetColor();
                Console.WriteLine();
            }
        }

        // === IMPACT SUMMARY ===
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"\n  ⚡ Kill Impact Analysis:");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ─────────────────────────────────────────────");

        var allAffected = new List<DevProcess> { proc };
        allAffected.AddRange(children);
        var affectedMem = allAffected.Sum(a => a.MemoryMb);
        var affectedPorts = allAffected.SelectMany(a => a.Ports).Distinct().ToList();
        var affectedDirs = allAffected.Where(a => !string.IsNullOrEmpty(a.WorkingDir))
                                       .Select(a => a.WorkingDir!).Distinct().ToList();

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"     💀 Kill:      PID {proc.Pid} ({proc.Name}) only");
        Console.WriteLine($"     🌳 Tree-kill: PID {proc.Pid} + {children.Count} children ({allAffected.Count} total)");
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"     💾 Memory freed: ~{affectedMem:F0} MB");
        if (affectedPorts.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"     🌐 Ports released: {string.Join(", ", affectedPorts.Select(p => $":{p}"))}");
        }
        if (affectedDirs.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"     📂 Affected directories:");
            foreach (var dir in affectedDirs)
                Console.WriteLine($"        {dir}");
        }

        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        WriteMenuOption("[K]", "💀 Kill this process");
        WriteMenuOption("[T]", "🌳 Tree-kill (process + all children)");
        WriteMenuOption("[←]", "🔙 Back to list");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("  ━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write("\n  Select: ");
        Console.ResetColor();

        var key = Console.ReadKey(intercept: true);
        Console.WriteLine();

        switch (char.ToUpperInvariant(key.KeyChar))
        {
            case 'K':
                await KillWithImpactAnalysis(proc, allAffected, tree: false);
                break;
            case 'T':
                await KillWithImpactAnalysis(proc, allAffected, tree: true);
                break;
        }
    }

    /// <summary>Kill a process with full impact analysis confirmation.</summary>
    static async Task KillWithImpactAnalysis(DevProcess target, List<DevProcess> allProcesses, bool tree)
    {
        var children = FindChildProcesses(target.Pid, allProcesses);
        var allAffected = tree ? new List<DevProcess> { target }.Concat(children).ToList() : [target];

        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n  ⚠️  {(tree ? "TREE-KILL" : "KILL")} Confirmation");
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("  ─────────────────────────────────────────────");

        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"  Target: ");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write($"{target.Name} ");
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"(PID {target.Pid})");

        if (tree && children.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n  🌳 Will also kill {children.Count} child process(es):");
            foreach (var child in children.OrderByDescending(c => c.MemoryMb))
            {
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"     └─ {child.Pid,7}  ");
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write($"{child.Name,-16}");
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.Write($"  {child.MemoryMb:F1} MB");
                if (child.Ports.Count > 0)
                {
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.Write($"  🌐 :{string.Join(",", child.Ports.Select(p => p.ToString(CultureInfo.InvariantCulture)))}");
                }
                Console.ResetColor();
                Console.WriteLine();
            }
        }

        // Impact summary
        var totalMem = allAffected.Sum(a => a.MemoryMb);
        var portsFreed = allAffected.SelectMany(a => a.Ports).Distinct().ToList();
        var dirsAffected = allAffected.Where(a => !string.IsNullOrEmpty(a.WorkingDir))
                                        .Select(a => a.WorkingDir!).Distinct().ToList();

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n  📊 Impact:");
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"     💾 Memory freed: ~{totalMem:F0} MB");
        Console.WriteLine($"     🔢 Processes terminated: {allAffected.Count}");
        if (portsFreed.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.WriteLine($"     🌐 Ports released: {string.Join(", ", portsFreed.Select(p => $":{p}"))}");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"     ⚠️  Services on these ports will become unreachable!");
        }
        if (dirsAffected.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"     📂 Directories affected:");
            foreach (var dir in dirsAffected)
                Console.WriteLine($"        └─ {dir}");
        }

        Console.ResetColor();
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Write($"\n  ❓ Proceed with {(tree ? "tree-kill" : "kill")}? (y/N): ");
        Console.ResetColor();

        var answer = Console.ReadLine()?.Trim();
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("  ❌ Cancelled.");
            Console.ResetColor();
            await Task.Delay(1000);
            return;
        }

        // Execute kill
        if (tree)
        {
            try
            {
                var psi = new ProcessStartInfo("taskkill", $"/F /T /PID {target.Pid}")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var p = Process.Start(psi);
                if (p != null) await p.WaitForExitAsync();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ✅ Tree-killed PID {target.Pid} ({target.Name}) + {children.Count} children. Freed ~{totalMem:F0} MB");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  ❌ Failed: {ex.Message}");
            }
        }
        else
        {
            try
            {
                using var p = Process.GetProcessById(target.Pid);
                p.Kill();
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  ✅ Killed PID {target.Pid} ({target.Name}). Freed ~{target.MemoryMb:F0} MB");
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  ❌ Failed: {ex.Message}");
            }
        }
        Console.ResetColor();
        await Task.Delay(2000);
    }

    /// <summary>Finds all child processes (recursive) of the given PID.</summary>
    static List<DevProcess> FindChildProcesses(int parentPid, List<DevProcess> allProcesses)
    {
        var children = new List<DevProcess>();
        var directChildren = allProcesses.Where(p => p.ParentPid == parentPid && p.Pid != parentPid).ToList();
        foreach (var child in directChildren)
        {
            children.Add(child);
            children.AddRange(FindChildProcesses(child.Pid, allProcesses));
        }
        return children;
    }

    /// <summary>Word-wrap text at the given width.</summary>
    static List<string> WrapText(string text, int maxWidth)
    {
        var lines = new List<string>();
        var remaining = text;
        while (remaining.Length > maxWidth)
        {
            // Try to break at a space
            var breakAt = remaining.LastIndexOf(' ', maxWidth);
            if (breakAt <= 0) breakAt = maxWidth;
            lines.Add(remaining[..breakAt]);
            remaining = remaining[breakAt..].TrimStart();
        }
        if (remaining.Length > 0)
            lines.Add(remaining);
        return lines;
    }

    static async Task InteractiveKill(List<DevProcess> processes, bool tree)
    {
        var label = tree ? "tree-kill" : "kill";
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"\n  Enter PID to {label}: ");
        Console.ResetColor();

        var input = Console.ReadLine()?.Trim();
        if (string.IsNullOrEmpty(input) || !int.TryParse(input, out var pid))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("  ❌ Invalid PID.");
            Console.ResetColor();
            await Task.Delay(1000);
            return;
        }

        var target = processes.Find(p => p.Pid == pid);
        if (target == null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($"  ⚠️  PID {pid} not in current list. Proceed anyway? (y/N): ");
            Console.ResetColor();
            var confirm = Console.ReadLine()?.Trim();
            if (!string.Equals(confirm, "y", StringComparison.OrdinalIgnoreCase))
                return;
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"  → {target.Name} (PID {target.Pid})");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"  {target.MemoryMb:F1} MB");
            if (target.Ports.Count > 0)
                Console.Write($"  🌐 {string.Join(",", target.Ports.Select(p => p.ToString(CultureInfo.InvariantCulture)))}");
            Console.ResetColor();
            Console.WriteLine();
        }

        KillProcess(pid, tree, skipConfirm: false);
        await Task.Delay(1500);
    }

    static async Task CleanOrphans(List<DevProcess> processes)
    {
        var orphans = processes.Where(p => p.IsOrphan).ToList();
        if (orphans.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("\n  ✅ No orphan processes found!");
            Console.ResetColor();
            await Task.Delay(1000);
            return;
        }

        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"\n  ⚠️  Found {orphans.Count} orphan processes:");
        Console.ResetColor();
        foreach (var o in orphans.OrderByDescending(p => p.MemoryMb))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"    {o.Pid,7}");
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write($"  {o.Name,-16}");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.Write($"  {o.MemoryMb:F1} MB");
            if (o.Ports.Count > 0)
            {
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.Write($"  🌐 {string.Join(",", o.Ports.Select(p => p.ToString(CultureInfo.InvariantCulture)))}");
            }
            Console.ResetColor();
            Console.WriteLine();
        }

        var totalMem = orphans.Sum(p => p.MemoryMb);
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.Write($"\n  Total: {totalMem:F0} MB. Kill ALL? (y/N): ");
        Console.ResetColor();

        var answer = Console.ReadLine()?.Trim();
        if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("  ❌ Cancelled.");
            await Task.Delay(1000);
            return;
        }

        var killed = 0;
        foreach (var o in orphans)
        {
            try
            {
                using var proc = Process.GetProcessById(o.Pid);
                proc.Kill();
                killed++;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { /* Already gone or access denied */ }
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  ✅ Killed {killed}/{orphans.Count} orphan processes. Freed ~{totalMem:F0} MB");
        Console.ResetColor();
        await Task.Delay(2000);
    }

    static void PrintProcess(DevProcess proc, bool isSelected = false)
    {

        // Selection indicator
        if (isSelected)
        {
            Console.BackgroundColor = ConsoleColor.DarkBlue;
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("  ▶ ");
        }
        else
        {
            Console.Write("    ");
        }
        // PID
        Console.ForegroundColor = ConsoleColor.White;
        Console.Write($"{proc.Pid,7}");

        // Name
        Console.ForegroundColor = proc.IsOrphan ? ConsoleColor.Red : ConsoleColor.Cyan;
        Console.Write($"  {proc.Name,-16}");

        // Memory
        Console.ForegroundColor = proc.MemoryMb > 500 ? ConsoleColor.Red
            : proc.MemoryMb > 100 ? ConsoleColor.Yellow : ConsoleColor.DarkGray;
        Console.Write($"  {proc.MemoryMb,7:F1} MB");

        // CPU time
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.Write($"  ⏱️ {FormatTimeSpan(proc.CpuTime)}");

        // Uptime
        if (proc.StartTime.HasValue)
        {
            var uptime = DateTime.Now - proc.StartTime.Value;
            Console.Write($"  🕐 {FormatAge(uptime)}");
        }

        // Ports
        if (proc.Ports.Count > 0)
        {
            Console.ForegroundColor = ConsoleColor.Magenta;
            Console.Write($"  🌐 {string.Join(",", proc.Ports.Select(p => p.ToString(CultureInfo.InvariantCulture)))}");
        }

        // Orphan marker
        if (proc.IsOrphan)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Write("  ⚠️ ORPHAN");
        }

        Console.ResetColor();
        Console.WriteLine();

        // Command line (truncated)
        if (!string.IsNullOrEmpty(proc.CommandLine))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            var cmdDisplay = proc.CommandLine.Length > 100
                ? string.Concat(proc.CommandLine.AsSpan(0, 97), "...")
                : proc.CommandLine;
            Console.WriteLine($"            📋 {cmdDisplay}");
            Console.ResetColor();
        }

        // Working directory
        if (!string.IsNullOrEmpty(proc.WorkingDir))
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine($"            📂 {proc.WorkingDir}");
            Console.ResetColor();
        }

        if (isSelected)
            Console.ResetColor();
    }

    // ── Process Enumeration ──

    static List<DevProcess> GetDevProcesses()
    {
        var results = new List<DevProcess>();

        try
        {
            // Bulk WMI query for all processes with parent PID and command line
            var psi = new ProcessStartInfo("powershell", "-NoProfile -Command \"Get-CimInstance Win32_Process | Select-Object ProcessId,Name,ParentProcessId,CommandLine,WorkingSetSize,CreationDate | ConvertTo-Json -Compress\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return results;

            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(10000);

            if (string.IsNullOrEmpty(output)) return results;

            using var doc = System.Text.Json.JsonDocument.Parse(output);
            var array = doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array
                ? doc.RootElement.EnumerateArray()
                : new[] { doc.RootElement }.AsEnumerable();

            foreach (var item in array)
            {
                var pid = item.TryGetProperty("ProcessId", out var pidProp) ? pidProp.GetInt32() : 0;
                var name = item.TryGetProperty("Name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                var parentPid = item.TryGetProperty("ParentProcessId", out var ppProp) ? ppProp.GetInt32() : 0;
                var cmdLine = item.TryGetProperty("CommandLine", out var cmdProp) && cmdProp.ValueKind == System.Text.Json.JsonValueKind.String ? cmdProp.GetString() : null;
                var wsSize = item.TryGetProperty("WorkingSetSize", out var wsProp) && wsProp.ValueKind == System.Text.Json.JsonValueKind.Number ? wsProp.GetInt64() : 0;
                var created = item.TryGetProperty("CreationDate", out var cdProp) && cdProp.ValueKind == System.Text.Json.JsonValueKind.String ? cdProp.GetString() : null;

                // Strip .exe from name for matching
                var baseName = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? name[..^4] : name;

                if (!IsDevProcess(baseName, cmdLine)) continue;

                DateTime? startTime = null;
                if (!string.IsNullOrEmpty(created))
                {
                    // WMI date format: /Date(1234567890000)/ or ISO string
                    if (created.Contains("/Date(", StringComparison.Ordinal))
                    {
                        var ms = long.Parse(created.Split('(')[1].Split(')')[0], CultureInfo.InvariantCulture);
                        startTime = DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime;
                    }
                    else if (DateTime.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    {
                        startTime = dt;
                    }
                }

                // Get CPU time from Process object
                TimeSpan cpuTime = TimeSpan.Zero;
                string? workingDir = null;
                try
                {
                    using var sysProc = Process.GetProcessById(pid);
                    cpuTime = sysProc.TotalProcessorTime;
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                { /* Process may have exited */ }

                results.Add(new DevProcess
                {
                    Pid = pid,
                    Name = baseName,
                    ParentPid = parentPid,
                    CommandLine = cmdLine,
                    WorkingDir = workingDir,
                    MemoryMb = wsSize / (1024.0 * 1024.0),
                    CpuTime = cpuTime,
                    StartTime = startTime,
                });
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException or FormatException)
        {
            // Fallback: use Process.GetProcesses() without WMI
            return GetDevProcessesFallback();
        }

        return results;
    }

    static List<DevProcess> GetDevProcessesFallback()
    {
        var results = new List<DevProcess>();
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                var name = proc.ProcessName;
                if (!DevProcessNames.Contains(name)) continue;

                results.Add(new DevProcess
                {
                    Pid = proc.Id,
                    Name = name,
                    MemoryMb = proc.WorkingSet64 / (1024.0 * 1024.0),
                    CpuTime = proc.TotalProcessorTime,
                    StartTime = proc.StartTime,
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            { /* Access denied or exited */ }
            finally { proc.Dispose(); }
        }
        return results;
    }

    static bool IsDevProcess(string name, string? cmdLine)
    {
        if (ExcludedProcesses.Contains(name)) return false;
        if (DevProcessNames.Contains(name)) return true;
        if (string.IsNullOrEmpty(cmdLine)) return false;

        // Check cmdline for dev keywords — but exclude system paths
        if (cmdLine.Contains(@"\SystemApps\", StringComparison.OrdinalIgnoreCase)
            || cmdLine.Contains(@"\WindowsApps\Microsoft", StringComparison.OrdinalIgnoreCase)
            || cmdLine.Contains("-ServerName:", StringComparison.OrdinalIgnoreCase))
            return false;

        foreach (var kw in DevKeywords)
        {
            if (cmdLine.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    // ── Port Detection (GetExtendedTcpTable P/Invoke) ──

    static Dictionary<int, List<int>> GetListeningPorts()
    {
        var result = new Dictionary<int, List<int>>();

        try
        {
            // IPv4
            AddTcpPorts(result, false);
            // IPv6
            AddTcpPorts(result, true);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or DllNotFoundException)
        { /* Silently fallback - no port info */ }

        return result;
    }

    static void AddTcpPorts(Dictionary<int, List<int>> result, bool ipv6)
    {
        var af = ipv6 ? 23 : 2; // AF_INET6 : AF_INET
        var tableClass = ipv6
            ? NativeTcp.TCP_TABLE_OWNER_PID_LISTENER_V6
            : NativeTcp.TCP_TABLE_OWNER_PID_LISTENER;

        var size = 0;
        NativeTcp.GetExtendedTcpTable(nint.Zero, ref size, true, af, tableClass, 0);
        if (size == 0) return;

        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (NativeTcp.GetExtendedTcpTable(buffer, ref size, true, af, tableClass, 0) != 0)
                return;

            var numEntries = Marshal.ReadInt32(buffer);
            var rowPtr = buffer + 4;
            var rowSize = ipv6 ? 24 : 24; // MIB_TCP6ROW_OWNER_PID / MIB_TCPROW_OWNER_PID

            for (var i = 0; i < numEntries; i++)
            {
                int state, localPort, ownerPid;
                if (ipv6)
                {
                    // MIB_TCP6ROW_OWNER_PID: [20 bytes localAddr][4 scopeId][4 localPort][20 bytes remoteAddr][4 scopeId][4 remotePort][4 state][4 ownerPid]
                    // Simplified: state at offset 48, localPort at offset 24, ownerPid at offset 52
                    // Actually the struct is different. Let's use the simpler IPv4 approach for safety.
                    // Skip IPv6 for now — most dev servers bind to IPv4
                    break;
                }
                else
                {
                    // MIB_TCPROW_OWNER_PID: [4 state][4 localAddr][4 localPort][4 remoteAddr][4 remotePort][4 ownerPid]
                    state = Marshal.ReadInt32(rowPtr, 0);
                    var portRaw = Marshal.ReadInt32(rowPtr, 8);
                    localPort = IPAddress.NetworkToHostOrder(portRaw) >> 16 & 0xFFFF;
                    ownerPid = Marshal.ReadInt32(rowPtr, 20);
                }

                // State 2 = LISTEN
                if (state == 2 && ownerPid > 0 && localPort > 0)
                {
                    if (!result.TryGetValue(ownerPid, out var ports))
                    {
                        ports = [];
                        result[ownerPid] = ports;
                    }
                    if (!ports.Contains(localPort))
                        ports.Add(localPort);
                }

                rowPtr += rowSize;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    // ── Terminal Attribution ──

    static HashSet<int> GetTerminalProcessIds()
    {
        var termPids = new HashSet<int>();
        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (proc.ProcessName.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase))
                    termPids.Add(proc.Id);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            { }
            finally { proc.Dispose(); }
        }
        return termPids;
    }

    static string? TraceToTerminal(int pid, int parentPid, HashSet<int> terminalPids, List<DevProcess> allProcesses)
    {
        // Build a PID→ParentPID map from our process list
        var parentMap = new Dictionary<int, int>();
        foreach (var p in allProcesses)
        {
            parentMap[p.Pid] = p.ParentPid;
        }

        // Walk up from parent, max 10 levels
        var current = parentPid;
        var visited = new HashSet<int>();
        for (var depth = 0; depth < 10 && current > 0; depth++)
        {
            if (!visited.Add(current)) break; // cycle
            if (terminalPids.Contains(current))
                return $"🖥️ Terminal (PID {current})";

            // Try system process lookup
            try
            {
                using var proc = Process.GetProcessById(current);
                var pName = proc.ProcessName;

                if (pName.Equals("WindowsTerminal", StringComparison.OrdinalIgnoreCase))
                    return $"🖥️ Terminal (PID {current})";

                if (pName.Equals("OpenConsole", StringComparison.OrdinalIgnoreCase)
                    || pName.Equals("conhost", StringComparison.OrdinalIgnoreCase))
                {
                    // Console host — likely terminal child
                    // Try one more level up
                }

                // Get parent of current
                if (parentMap.TryGetValue(current, out var nextParent))
                    current = nextParent;
                else
                {
                    // Use WMI for this specific process parent
                    current = GetParentPid(current);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                break; // Process gone
            }
        }

        return null;
    }

    static int GetParentPid(int pid)
    {
        try
        {
            // Use NtQueryInformationProcess for parent PID
            var handle = NativeProcess.OpenProcess(0x0400, false, pid); // PROCESS_QUERY_INFORMATION
            if (handle == nint.Zero)
            {
                handle = NativeProcess.OpenProcess(0x1000, false, pid); // PROCESS_QUERY_LIMITED_INFORMATION
                if (handle == nint.Zero) return 0;
            }

            try
            {
                var pbi = new NativeProcess.PROCESS_BASIC_INFORMATION();
                var status = NativeProcess.NtQueryInformationProcess(handle, 0, ref pbi, Marshal.SizeOf(pbi), out _);
                if (status == 0)
                    return (int)pbi.InheritedFromUniqueProcessId;
            }
            finally
            {
                NativeProcess.CloseHandle(handle);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        { }

        return 0;
    }

    static bool IsProcessAlive(int pid)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            return !proc.HasExited;
        }
        catch { return false; }
    }

    // ── Kill ──

    static int HandleKill(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out var pid))
        {
            Console.WriteLine("  ❌ Usage: tsg processes --kill <PID>");
            return 1;
        }

        return KillProcess(pid, tree: false, skipConfirm: args.Contains("--yes", StringComparer.OrdinalIgnoreCase));
    }

    static int HandleKillTree(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], out var pid))
        {
            Console.WriteLine("  ❌ Usage: tsg processes --kill-tree <PID>");
            return 1;
        }

        return KillProcess(pid, tree: true, skipConfirm: args.Contains("--yes", StringComparer.OrdinalIgnoreCase));
    }

    static int KillProcess(int pid, bool tree, bool skipConfirm)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.Write($"\n  ⚠️  Kill {(tree ? "process tree for" : "process")} ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write($"{proc.ProcessName} (PID {pid})");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            try
            {
                Console.Write($"  Memory: {proc.WorkingSet64 / (1024 * 1024):F1} MB");
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            Console.ResetColor();
            Console.WriteLine();

            if (!skipConfirm)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Write("  Confirm? (y/N): ");
                Console.ResetColor();
                var answer = Console.ReadLine()?.Trim();
                if (!string.Equals(answer, "y", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("  ❌ Cancelled.");
                    return 0;
                }
            }

            if (tree)
            {
                // Use taskkill /T for tree kill
                var psi = new ProcessStartInfo("taskkill", $"/PID {pid} /T /F")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                using var killProc = Process.Start(psi);
                killProc?.WaitForExit(5000);
            }
            else
            {
                proc.Kill();
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"  ✅ Process {pid} {(tree ? "tree " : "")}terminated.");
            Console.ResetColor();
            return 0;
        }
        catch (ArgumentException)
        {
            Console.WriteLine($"  ❌ Process {pid} not found.");
            return 1;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  ❌ Cannot kill process {pid}: {ex.Message}");
            Console.ResetColor();
            return 1;
        }
    }

    // ── Helpers ──

    static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{ts.TotalHours:F1}h";
        if (ts.TotalMinutes >= 1)
            return $"{ts.TotalMinutes:F0}m";
        return $"{ts.TotalSeconds:F0}s";
    }

    static string FormatAge(TimeSpan age)
    {
        if (age.TotalDays >= 1) return $"{age.TotalDays:F0}d";
        if (age.TotalHours >= 1) return $"{age.TotalHours:F0}h";
        if (age.TotalMinutes >= 1) return $"{age.TotalMinutes:F0}m";
        return $"{age.TotalSeconds:F0}s";
    }
}

// ── Data Models ──

sealed class DevProcess
{
    public int Pid { get; set; }
    public string Name { get; set; } = "";
    public int ParentPid { get; set; }
    public string? CommandLine { get; set; }
    public string? WorkingDir { get; set; }
    public double MemoryMb { get; set; }
    public TimeSpan CpuTime { get; set; }
    public DateTime? StartTime { get; set; }
    public List<int> Ports { get; } = [];
    public string? TerminalGroup { get; set; }
    public bool IsOrphan { get; set; }
}

// ── Native Interop ──

static partial class NativeTcp
{
    public const int TCP_TABLE_OWNER_PID_LISTENER = 3;
    public const int TCP_TABLE_OWNER_PID_LISTENER_V6 = 3;

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    public static partial int GetExtendedTcpTable(
        nint pTcpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf, int tableClass, int reserved);
}

static partial class NativeProcess
{
    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenProcess(int dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryInformationProcess(
        nint processHandle, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength,
        out int returnLength);

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_BASIC_INFORMATION
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }
}
