using System.Text.Json;
using RpfPreview.Core;

namespace RpfPreview.Cli;

internal static class InspectCommand
{
    public const string Usage = """
        rpf9-cli inspect <source> [--inside <slot>] [--slot <slot>] [--block <number>] [--record <number>] [--json]
        Inspect decoded 0DRP metadata from a loose resource or an indexed archive. Repeat --inside for nested archives.
        Archive slots start at zero. Blocks and records start at one. The default selection is the first root record.
        Use --decoder <path> to select a local archive decoder configuration.
        """;

    public static int Run(IReadOnlyList<string> args, CancellationToken token)
    {
        var line = CommandLine.Parse(args, [.. CommonOptions.All, new("slot"),
            new("block"), new("record"), new("json", TakesValue: false)]);
        if (line.Positionals.Count != 1) throw new UsageException("Choose one archive or decoded metadata file.");
        var workspace = SourceWorkspace.Open(line.Positionals, line.Value("decoder"), CommonOptions.Reporter(line), token, line.Values("inside"));
        if (workspace.Documents.Count != 1) throw new UsageException("Choose a single source.");
        var doc = workspace.Documents[0];
        int index = line.Integer("slot", doc.Entries.Count == 1 ? 0 : -1, 0, int.MaxValue);
        if (index < 0 || index >= doc.Entries.Count) throw new UsageException("Select a member with --slot.");
        var entry = doc.Entries[index];
        var bytes = RageResourceMetadata.ReadSystem(doc, entry, token);
        var directory = RageResourceMetadata.Inspect(bytes) ?? throw new InvalidDataException("This member has no supported metadata directory.");
        int block = line.Integer("block", directory.RootBlock, 1, directory.Blocks.Count);
        int number = line.Integer("record", 1, 1, int.MaxValue);
        var record = RageResourceMetadata.InspectRecord(bytes, block, number) ?? throw new UsageException("That record is unavailable in this block.");
        Console.WriteLine(line.Has("json")
            ? JsonSerializer.Serialize(new { directory.RootBlock, directory.StructureCount, directory.Blocks, Record = record }, CliJson.Options)
            : RageResourceMetadata.DescribeRecord(record));
        return ExitCodes.Success;
    }
}
