using RpfPreview.Core;

namespace RpfPreview.Cli;

public sealed record AssetSelection(string Query, string Type, IReadOnlyList<string> Ids, bool AvailableOnly, bool BestVariant)
{
    public static readonly OptionSpec[] Options =
    [
        new("query", "q"),
        new("type", "t"),
        new("id", Repeatable: true),
        new("available", TakesValue: false),
        new("best", TakesValue: false)
    ];

    public static AssetSelection From(CommandLine line, string defaultType) => new(
        line.Value("query") ?? "",
        line.Value("type") ?? defaultType,
        line.Values("id").SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray(),
        line.Has("available"),
        line.Has("best"));

    public IReadOnlyList<WorkspaceAsset> Apply(SourceWorkspace workspace)
    {
        var query = AssetSearchQuery.Parse(Query);
        var candidates = Ids.Count > 0 ? workspace.Ids.Resolve(Ids) : workspace.Index.Assets;
        IEnumerable<WorkspaceAsset> matches = candidates.Where(a =>
            AssetSearchQuery.TypeMatches(a.Kind, Type)
            && (!AvailableOnly || a.Available)
            && query.Matches(a));
        if (BestVariant)
            matches = matches.GroupBy(a => a.Kind + "/" + AssetLabels.Name(a), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Where(a => a.Available).DefaultIfEmpty(g.First()).MaxBy(a => a.Size)!);
        return matches.ToArray();
    }
}
