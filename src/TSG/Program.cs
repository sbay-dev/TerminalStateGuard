using System.Reflection;
using TSG;
using TSG.Platform;

var host = PlatformHost.Detect();
var commands = CommandRegistry.Build(host);

var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var cmdArgs = args.Length > 1 ? args[1..] : [];

return commands.TryGetValue(cmd, out var handler)
    ? await handler(cmdArgs)
    : await commands["help"]([]);
