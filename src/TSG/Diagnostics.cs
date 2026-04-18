using System.Text.Json;
using TSG.Platform;

namespace TSG;

/// <summary>
/// Diagnoses environment health — checks all prerequisites.
/// </summary>
public static class Diagnostics
{
    public static async Task RunDoctorAsync(IPlatformHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Console.WriteLine("\n  🩺 TSG Doctor — Environment Check\n");
        var issues = 0;

        // .NET version
        Check("✅", $".NET {Environment.Version}");

        // Shell
        var shell = host.FindShell();
        if (shell is not null)
            Check("✅", $"Shell: {shell}");
        else
        {
            Check("❌", $"{host.ShellName} not found");
            issues++;
        }

        // TSG installed
        if (Directory.Exists(host.TsgDir))
            Check("✅", $"TSG dir: {host.TsgDir}");
        else
        {
            Check("❌", "TSG not installed — run 'tsg install'");
            issues++;
        }

        // Copilot sessions
        if (Directory.Exists(host.CopilotSessionDir))
        {
            var sessions = Directory.GetDirectories(host.CopilotSessionDir);
            Check("✅", $"Copilot sessions: {sessions.Length}");

            // Check for stuck/large sessions (READ-ONLY)
            var stuck = 0; var large = 0;
            foreach (var dir in sessions)
            {
                var evPath = Path.Combine(dir, "events.jsonl");
                if (!File.Exists(evPath)) continue;

                var fi = new FileInfo(evPath);
                if (fi.Length > 20 * 1024 * 1024) large++;

                try
                {
                    var lastLine = (await File.ReadAllLinesAsync(evPath))[^1];
                    using var doc = JsonDocument.Parse(lastLine);
                    var type = doc.RootElement.GetProperty("type").GetString();
                    if (type is "assistant.turn_start" or "tool.execution_start") stuck++;
                }
                catch (Exception ex) when (ex is IOException or JsonException or IndexOutOfRangeException or KeyNotFoundException)
                {
                    // Silently skip unreadable/malformed session files (READ-ONLY scan)
                }
            }

            if (large > 0) Check("⚠️", $"{large} session(s) > 20MB — may cause slowness", "Yellow");
            if (stuck > 0) Check("⚠️", $"{stuck} session(s) stuck — close tab & resume", "Yellow");
        }
        else
            Check("ℹ️", "No Copilot sessions found", "DarkGray");

        // Windows Terminal Fragment (Windows only)
        if (OperatingSystem.IsWindows())
        {
            var fragDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "Windows Terminal", "Fragments", "TerminalStateGuard");
            if (Directory.Exists(fragDir))
                Check("✅", "Windows Terminal Fragment installed");
            else
                Check("ℹ️", "Fragment not installed — run 'tsg install'", "DarkGray");
        }

        // Terminal state
        var stateJson = host.GetTerminalStateJson();
        if (stateJson is not null)
        {
            var content = await File.ReadAllTextAsync(stateJson);
            using var doc2 = JsonDocument.Parse(content);
            var layouts = doc2.RootElement.GetProperty("persistedWindowLayouts");
            var tabs = 0;
            foreach (var window in layouts.EnumerateArray())
                if (window.TryGetProperty("tabLayout", out var tl))
                    tabs += tl.GetArrayLength();
            Check("✅", $"Terminal state: {layouts.GetArrayLength()} windows, {tabs} tabs");
        }

        // Snapshot retention
        var snapDir = Path.Combine(host.HomeDir, ".copilotAccel", "terminal-snapshots");
        if (Directory.Exists(snapDir))
        {
            var snapCount = Directory.GetFiles(snapDir, "202*.json").Length;
            var configPath = Path.Combine(host.TsgDir, "tsg-config.json");
            var maxSnap = 50;
            var source = "default";
            var envVal = Environment.GetEnvironmentVariable("TSG_MAX_SNAPSHOTS");
            if (envVal is not null && int.TryParse(envVal, out var envMax))
            {
                maxSnap = envMax;
                source = "env";
            }
            else if (File.Exists(configPath))
            {
                try
                {
                    using var cfgDoc = JsonDocument.Parse(await File.ReadAllTextAsync(configPath));
                    if (cfgDoc.RootElement.TryGetProperty("MaxSnapshots", out var ms))
                    {
                        maxSnap = ms.GetInt32();
                        source = "config";
                    }
                }
                catch (JsonException) { /* use default */ }
            }
            Check("✅", $"Snapshots: {snapCount}/{maxSnap} ({source})");
        }

        Console.WriteLine(issues == 0
            ? "\n  🎉 All checks passed!\n"
            : $"\n  ⚠️ {issues} issue(s) found\n");

        await Task.CompletedTask;
    }

    static void Check(string icon, string msg, string color = "Green")
    {
        Console.ForegroundColor = color switch
        {
            "Yellow" => ConsoleColor.Yellow,
            "Red" => ConsoleColor.Red,
            "DarkGray" => ConsoleColor.DarkGray,
            _ => ConsoleColor.Green
        };
        Console.WriteLine($"  {icon} {msg}");
        Console.ResetColor();
    }
}
