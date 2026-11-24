using RpfPreview.Cli;

const string usage = """
    rpf9-cli: search and export rpf9-explorer assets without the desktop app.

    Usage:
      rpf9-cli inspect <source> [options]
      rpf9-cli search <source>... [options]
      rpf9-cli export <source>... --out <folder> [options]
      rpf9-cli kits <source>... [options]
      rpf9-cli scan <source>... [options]
      rpf9-cli help <command>

    Sources include .rpf archives, .ttpf texture pools, ZIP files, decoded 0DRP metadata and folders.
    Recovery scans saved by the desktop app are reused automatically.

    Exit codes: 0 success, 1 failure, 2 invalid usage, 3 nothing matched, 130 cancelled.
    """;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(usage);
    return args.Length == 0 ? ExitCodes.Usage : ExitCodes.Success;
}

string command = args[0];
var rest = args.Skip(1).ToArray();
if (rest.Any(a => a is "-h" or "--help"))
{
    rest = [command];
    command = "help";
}

try
{
    return command switch
    {
        "inspect" => InspectCommand.Run(rest, cancellation.Token),
        "search" => SearchCommand.Run(rest, cancellation.Token),
        "export" => await ExportCommand.RunAsync(rest, cancellation.Token),
        "kits" => KitsCommand.Run(rest, cancellation.Token),
        "scan" => ScanCommand.Run(rest, cancellation.Token),
        "help" => Help(rest),
        "--version" or "version" => Version(),
        _ => throw new UsageException($"Unknown command '{command}'.")
    };
}
catch (UsageException ex)
{
    Console.Error.WriteLine("error: " + ex.Message);
    Console.Error.WriteLine("Run 'rpf9-cli --help' for usage.");
    return ExitCodes.Usage;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine();
    Console.Error.WriteLine("Cancelled.");
    return ExitCodes.Cancelled;
}
catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
{
    Console.Error.WriteLine("error: " + ex.Message);
    return ExitCodes.Failure;
}

int Help(IReadOnlyList<string> topic)
{
    Console.WriteLine(topic.FirstOrDefault() switch
    {
        "inspect" => InspectCommand.Usage,
        "search" => SearchCommand.Usage,
        "export" => ExportCommand.Usage,
        "kits" => KitsCommand.Usage,
        "scan" => ScanCommand.Usage,
        _ => usage
    });
    return ExitCodes.Success;
}

int Version()
{
    Console.WriteLine("rpf9-cli " + typeof(SourceWorkspace).Assembly.GetName().Version?.ToString(3));
    return ExitCodes.Success;
}
