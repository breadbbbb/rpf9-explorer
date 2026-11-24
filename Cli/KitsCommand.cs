using System.Globalization;
using System.Text.Json;
using RpfPreview.Core;

namespace RpfPreview.Cli;

public static class KitsCommand
{
    public const string Usage = """
        rpf9-cli kits <source>... [options]

        Lists authored wardrobe kits (outfits) from the wardrobe catalogues in the loaded PSIN metadata,
        with the indexed model names each kit selects. Export one with 'rpf9-cli export --kit <id>'.

          -q, --query <text>     Only kits whose id or model names contain every word
              --complete         Only kits whose models are all indexed
              --limit <n>        Show at most n kits (default: 100, 0 for all)
              --json             Print results as JSON
              --decoder <file>   Local RPF9 decoder configuration (default: the app's configuration)
              --quiet            Hide progress messages
        """;

    private static readonly OptionSpec[] Options =
    [
        .. CommonOptions.All,
        new("query", "q"),
        new("complete", TakesValue: false),
        new("limit"),
        new("json", TakesValue: false)
    ];

    private sealed record KitRow(string Id, int Components, int Indexed, int Warnings, IReadOnlyList<string> Models);

    public static int Run(IReadOnlyList<string> args, CancellationToken token)
    {
        var line = CommandLine.Parse(args, Options);
        var reporter = CommonOptions.Reporter(line);
        int limit = line.Integer("limit", 100, 0, int.MaxValue);
        var terms = (line.Value("query") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var workspace = SourceWorkspace.Open(line.Positionals, line.Value("decoder"), reporter, token, line.Values("inside"));
        reporter.Progress("Reading wardrobe catalogues");
        var wardrobe = WardrobeIndex.Build(workspace.Documents, token);
        reporter.ClearProgress();
        if (wardrobe.Catalogs.Count == 0) throw new UsageException("No wardrobe catalogue was found. Run 'rpf9-cli scan --kinds psin' on the sources first.");
        var names = workspace.Index.Assets.Where(a => a.Kind == "model")
            .Select(a => (Key: WardrobeIndex.DrawableKey(a.Entry), Name: AssetLabels.Name(a))).Where(p => p.Key is not null)
            .GroupBy(p => p.Key!.Value).ToDictionary(g => g.Key, g => g.First().Name);
        var rows = new List<KitRow>();
        foreach (var (kitId, catalog) in wardrobe.Kits)
        {
            token.ThrowIfCancellationRequested();
            RageWardrobeResolution resolution;
            try { resolution = catalog.Resolve(kitId); }
            catch (InvalidDataException) { continue; }
            var keys = resolution.Selections.Where(s => s.DrawableKey != 0).Select(s => s.DrawableKey).Distinct().ToArray();
            var models = keys.Select(k => names.TryGetValue(k, out var name) ? name : null).OfType<string>().ToArray();
            var row = new KitRow(kitId.ToString("X8", CultureInfo.InvariantCulture), keys.Length, models.Length, resolution.Warnings.Count, models);
            if (line.Has("complete") && (row.Indexed < row.Components || row.Components == 0)) continue;
            string haystack = row.Id + " " + string.Join(' ', models);
            if (terms.All(t => haystack.Contains(t, StringComparison.OrdinalIgnoreCase))) rows.Add(row);
        }
        var shown = (limit == 0 ? rows : rows.Take(limit)).ToArray();
        if (line.Has("json")) Console.WriteLine(JsonSerializer.Serialize(shown, CliJson.Options));
        else
            foreach (var row in shown)
                Console.WriteLine($"{row.Id}  {row.Indexed,3}/{row.Components,-3} {(row.Warnings > 0 ? $"{row.Warnings} warnings  " : "")}{string.Join(", ", row.Models.Take(6))}{(row.Models.Count > 6 ? ", ..." : "")}");
        reporter.Info(rows.Count > shown.Length
            ? $"{rows.Count:N0} kits match in {wardrobe.Catalogs.Count:N0} catalogues; showing {shown.Length:N0}. Use --limit 0 to show all."
            : $"{rows.Count:N0} kits match in {wardrobe.Catalogs.Count:N0} catalogues.");
        return rows.Count == 0 ? ExitCodes.NoMatches : ExitCodes.Success;
    }

    public static uint ParseKitId(string text)
    {
        string value = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
        if (!uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint id))
            throw new UsageException($"'{text}' is not a hexadecimal kit id.");
        return id;
    }
}
