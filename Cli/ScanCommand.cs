using System.Diagnostics;
using RpfPreview.Core;

namespace RpfPreview.Cli;

public static class ScanCommand
{
    public const string Usage = """
        rpf9-cli scan <source>... [options]

        Runs the full payload scan ("Scan entire archive") on every RPF9 source and saves the results
        to the index cache shared with the desktop app. Kinds that already have a full scan are skipped,
        and a failing kind does not stop the others.

              --kinds <list>     Comma-separated kinds to scan: models, textures, audio, shaders, psin, clips
                                 (default: all)
              --force            Scan again even when a full scan is already saved
              --dry-run          Show what would be scanned without scanning
              --quiet            Hide progress messages
        """;

    private static readonly OptionSpec[] Options =
    [
        new("kinds"),
        new("force", TakesValue: false),
        new("dry-run", TakesValue: false),
        new("quiet", TakesValue: false)
    ];

    public static int Run(IReadOnlyList<string> args, CancellationToken token)
    {
        var line = CommandLine.Parse(args, Options);
        var reporter = CommonOptions.Reporter(line);
        var kinds = ReadKinds(line.Value("kinds"));
        bool force = line.Has("force"), dryRun = line.Has("dry-run");
        var sources = SourceWorkspace.ExpandSources(line.Positionals).Where(PayloadIndexBuilder.IsRpf9)
            .OrderBy(p => new FileInfo(p).Length).ToArray();
        if (sources.Length == 0) throw new UsageException("None of the sources is an RPF9 archive.");
        int scanned = 0, skipped = 0, failed = 0;
        var total = Stopwatch.StartNew();
        foreach (string source in sources)
        {
            string name = Path.GetFileName(source);
            reporter.Info($"{name} ({AssetLabels.Size(new FileInfo(source).Length)})");
            foreach (string kind in kinds)
            {
                token.ThrowIfCancellationRequested();
                if (!force && PayloadIndexBuilder.HasFullScan(kind, source))
                {
                    reporter.Info($"  {kind,-9} already scanned");
                    skipped++;
                    continue;
                }
                if (dryRun)
                {
                    reporter.Info($"  {kind,-9} would scan");
                    continue;
                }
                var watch = Stopwatch.StartNew();
                var progress = new Progress<PayloadIndexProgress>(p => reporter.Progress($"  {kind,-9} {p.Fraction,4:P0} {p.Message}"));
                try
                {
                    int count = PayloadIndexBuilder.BuildKind(kind, source, progress, token);
                    reporter.Info($"  {kind,-9} {count,9:N0} found in {Elapsed(watch.Elapsed)}");
                    scanned++;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException)
                {
                    reporter.Warn($"{name} {kind}: {ex.Message}");
                    failed++;
                }
            }
        }
        reporter.Info($"Done in {Elapsed(total.Elapsed)}: {scanned:N0} scanned, {skipped:N0} already complete, {failed:N0} failed.");
        return failed > 0 && scanned == 0 ? ExitCodes.Failure : ExitCodes.Success;
    }

    private static IReadOnlyList<string> ReadKinds(string? text)
    {
        if (text is null) return PayloadIndexBuilder.Kinds;
        var kinds = text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(k => k.ToLowerInvariant()).Distinct().ToArray();
        var unknown = kinds.Except(PayloadIndexBuilder.Kinds).ToArray();
        if (unknown.Length > 0) throw new UsageException($"Unknown scan kind '{unknown[0]}'. Use: {string.Join(", ", PayloadIndexBuilder.Kinds)}.");
        if (kinds.Length == 0) throw new UsageException("--kinds needs at least one kind.");
        return PayloadIndexBuilder.Kinds.Where(kinds.Contains).ToArray();
    }

    private static string Elapsed(TimeSpan time) => time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes}m {time.Seconds:D2}s" : $"{time.TotalSeconds:0.0}s";
}
