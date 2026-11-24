using System.Text.Json;
using RpfPreview.Core;

namespace RpfPreview.Cli;

public static class SearchCommand
{
    public const string Usage = """
        rpf9-cli search <source>... [options]

        Lists assets that match a query. Sources are archives, texture pools or folders.

          -q, --query <text>     Search text, same syntax as the app (type:, name:, size:>1mb, -word, "phrase")
          -t, --type <type>      all, models, textures, audio, shaders, animations, videos (default: all)
              --id <id>          Only the asset with this id; repeat or separate with commas
              --inside <slot>    Search an indexed child archive (repeat to descend further); --json includes slot numbers
              --available        Hide assets the current decoders cannot extract
              --best             Keep only the largest variant of each name
              --limit <n>        Show at most n results (default: 200, 0 for all)
              --json             Print results as JSON
              --decoder <file>   Local RPF9 decoder configuration (default: the app's configuration)
              --quiet            Hide progress messages
        """;

    private static readonly OptionSpec[] Options =
    [
        .. AssetSelection.Options, .. CommonOptions.All,
        new("limit"),
        new("json", TakesValue: false)
    ];

    private sealed record Row(string Id, string Kind, string Name, string Source, long Size, string Offset, bool Available, int? Slot, string Group, int NamedStreams);

    public static int Run(IReadOnlyList<string> args, CancellationToken token)
    {
        var line = CommandLine.Parse(args, Options);
        var reporter = CommonOptions.Reporter(line);
        var selection = AssetSelection.From(line, "all");
        int limit = line.Integer("limit", 200, 0, int.MaxValue);
        var workspace = SourceWorkspace.Open(line.Positionals, line.Value("decoder"), reporter, token, line.Values("inside"));
        var matches = selection.Apply(workspace);
        var rows = (limit == 0 ? matches : matches.Take(limit)).Select(a => ToRow(a, workspace.Ids)).ToArray();
        if (line.Has("json"))
            Console.WriteLine(JsonSerializer.Serialize(rows, CliJson.Options));
        else
            PrintTable(rows);
        reporter.Info(matches.Count > rows.Length
            ? $"{matches.Count:N0} assets match; showing {rows.Length:N0}. Use --limit 0 to show all."
            : $"{matches.Count:N0} assets match.");
        return matches.Count == 0 ? ExitCodes.NoMatches : ExitCodes.Success;
    }

    private static Row ToRow(WorkspaceAsset asset, AssetIds ids) => new(
        ids[asset], asset.Kind, AssetLabels.Name(asset), asset.SourceName, asset.Size,
        asset.Entry.Offset.ToString("X10"), asset.Available, asset.Document.HasDecodedRpf9Index ? asset.Entry.Index : null, asset.ContentGroup, asset.AudioCues.Count);

    private static void PrintTable(IReadOnlyList<Row> rows)
    {
        if (rows.Count == 0) return;
        int id = Math.Max(2, rows.Max(r => r.Id.Length));
        int kind = Math.Max(4, rows.Max(r => r.Kind.Length));
        int source = Math.Max(6, rows.Max(r => r.Source.Length));
        Console.WriteLine($"{"ID".PadRight(id)}  {"KIND".PadRight(kind)}  {"SIZE",10}  {"SOURCE".PadRight(source)}  NAME");
        foreach (var row in rows)
        {
            string name = row.Available ? row.Name : row.Name + " (unavailable)";
            Console.WriteLine($"{row.Id.PadRight(id)}  {row.Kind.PadRight(kind)}  {AssetLabels.Size(row.Size),10}  {row.Source.PadRight(source)}  {name}");
        }
    }
}
