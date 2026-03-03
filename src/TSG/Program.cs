using System.Text;
using TSG;
using TSG.Platform;

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

var host = PlatformHost.Detect();
var commands = CommandRegistry.Build(host);

var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
var cmdArgs = args.Length > 1 ? args[1..] : [];

return commands.TryGetValue(cmd, out var handler)
    ? await handler(cmdArgs)
    : await commands["help"]([]);
