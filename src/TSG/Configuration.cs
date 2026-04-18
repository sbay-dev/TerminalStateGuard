using System.Text.Json;
using TSG.Platform;

namespace TSG;

/// <summary>
/// Manages TSG configuration (max snapshots, etc.) stored in ~/.tsg/tsg-config.json.
/// </summary>
public static class Configuration
{
    const int DefaultMaxSnapshots = 50;
    const int MinSnapshots = 5;
    const int MaxSnapshotsLimit = 1000;

    static readonly JsonSerializerOptions s_jsonOptions = new() { WriteIndented = true };

    sealed class ConfigData
    {
        public int MaxSnapshots { get; set; } = DefaultMaxSnapshots;
    }

    static string GetConfigPath(IPlatformHost host) =>
        Path.Combine(host.TsgDir, "tsg-config.json");

    public static async Task<int> RunAsync(IPlatformHost host, string[] args)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0)
        {
            await ShowConfigAsync(host);
            return 0;
        }

        if (args.Length >= 2 && args[0].Equals("max-snapshots", StringComparison.OrdinalIgnoreCase))
        {
            if (!int.TryParse(args[1], out var value) || value < MinSnapshots || value > MaxSnapshotsLimit)
            {
                Console.WriteLine($"  ❌ Invalid value. Must be {MinSnapshots}–{MaxSnapshotsLimit}");
                return 1;
            }
            await SetMaxSnapshotsAsync(host, value);
            return 0;
        }

        PrintConfigHelp();
        return 0;
    }

    static async Task ShowConfigAsync(IPlatformHost host)
    {
        var config = await LoadAsync(host);
        Console.WriteLine("\n  ⚙️  TSG Configuration\n");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"  max-snapshots = {config.MaxSnapshots}");
        Console.ResetColor();
        Console.WriteLine($"  📁 {GetConfigPath(host)}");

        var envVal = Environment.GetEnvironmentVariable("TSG_MAX_SNAPSHOTS");
        if (envVal is not null)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"  ⚠️  Overridden by TSG_MAX_SNAPSHOTS={envVal}");
            Console.ResetColor();
        }
        Console.WriteLine();
    }

    static async Task SetMaxSnapshotsAsync(IPlatformHost host, int value)
    {
        var config = await LoadAsync(host);
        config.MaxSnapshots = value;
        await SaveAsync(host, config);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"  ✅ max-snapshots → {value}");
        Console.ResetColor();
    }

    static async Task<ConfigData> LoadAsync(IPlatformHost host)
    {
        var path = GetConfigPath(host);
        if (!File.Exists(path))
            return new ConfigData();

        try
        {
            var json = await File.ReadAllTextAsync(path);
            return JsonSerializer.Deserialize<ConfigData>(json) ?? new ConfigData();
        }
        catch (JsonException)
        {
            return new ConfigData();
        }
    }

    static async Task SaveAsync(IPlatformHost host, ConfigData config)
    {
        Directory.CreateDirectory(host.TsgDir);
        var json = JsonSerializer.Serialize(config, s_jsonOptions);
        await File.WriteAllTextAsync(GetConfigPath(host), json);
    }

    static void PrintConfigHelp()
    {
        Console.WriteLine("""

          ⚙️  TSG Config

          USAGE:
            tsg config                    Show current configuration
            tsg config max-snapshots <N>  Set max saved snapshots (5–1000)

          ENVIRONMENT:
            TSG_MAX_SNAPSHOTS=<N>         Override max snapshots (takes priority)

        """);
    }
}
