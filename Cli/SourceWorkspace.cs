using RpfPreview.Core;

namespace RpfPreview.Cli;

public sealed class SourceWorkspace
{
    private const int MaxSources = 512;
    private static readonly string[] SupportedExtensions = [".rpf", ".ttpf", ".zip"];

    private SourceWorkspace(IReadOnlyList<ArchiveDocument> documents, AssetWorkspaceIndex index)
    {
        Documents = documents;
        Index = index;
        Ids = AssetIds.Build(index);
    }

    public IReadOnlyList<ArchiveDocument> Documents { get; }
    public AssetWorkspaceIndex Index { get; }
    public AssetIds Ids { get; }

    public static string DefaultDecoderConfiguration => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "rpf9-explorer", "rpf9-decoder.json");

    public static SourceWorkspace Open(IReadOnlyList<string> inputs, string? decoderConfiguration, Reporter reporter, CancellationToken token)
    {
        var paths = ExpandSources(inputs);
        var provider = LoadDecoder(decoderConfiguration, reporter);
        var documents = new List<ArchiveDocument>(paths.Count);
        for (int i = 0; i < paths.Count; i++)
        {
            token.ThrowIfCancellationRequested();
            reporter.Progress($"Opening {i + 1}/{paths.Count}: {Path.GetFileName(paths[i])}");
            var document = OpenDocument(paths[i], provider, reporter, token);
            if (document is not null) documents.Add(document);
        }
        if (documents.Count == 0) throw new UsageException("None of the sources could be opened.");
        reporter.Progress("Indexing assets");
        var index = AssetWorkspaceIndex.Build(documents, cancellationToken: token);
        foreach (var warning in index.Warnings.Take(20)) reporter.Warn(warning);
        if (index.Warnings.Count > 20) reporter.Warn($"{index.Warnings.Count - 20:N0} more indexing warnings were not shown.");
        reporter.ClearProgress();
        return new(documents, index);
    }

    public static List<string> ExpandSources(IReadOnlyList<string> inputs)
    {
        if (inputs.Count == 0) throw new UsageException("Give at least one archive, texture pool or folder.");
        var paths = new List<string>();
        foreach (string input in inputs)
        {
            string full = RequireLocal(input);
            if (Directory.Exists(full))
                paths.AddRange(Directory.EnumerateFiles(full, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = LocalPath.RemoteAttributes })
                    .Where(p => SupportedExtensions.Contains(Path.GetExtension(p).ToLowerInvariant()))
                    .Order(StringComparer.OrdinalIgnoreCase));
            else if (File.Exists(full)) paths.Add(full);
            else throw new UsageException($"Source not found: {input}");
        }
        var distinct = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (distinct.Count == 0) throw new UsageException("No .rpf, .ttpf or .zip files were found in the given sources.");
        if (distinct.Count > MaxSources) throw new UsageException($"A workspace can hold up to {MaxSources} sources; {distinct.Count:N0} were found.");
        return distinct;
    }

    public static string RequireLocal(string path)
    {
        try { return LocalPath.Require(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new UsageException($"{path}: {ex.Message}"); }
    }

    private static Rpf9IndexProvider? LoadDecoder(string? configuration, Reporter reporter)
    {
        bool explicitPath = configuration is not null;
        string path;
        try { path = LocalPath.Require(configuration ?? DefaultDecoderConfiguration); }
        catch (IOException ex) when (!explicitPath)
        {
            reporter.Warn("Local RPF9 decoder unavailable, using recovery scans: " + ex.Message);
            return null;
        }
        catch (IOException ex) { throw new UsageException("Local RPF9 decoder unavailable: " + ex.Message); }
        if (!File.Exists(path))
        {
            if (explicitPath) throw new UsageException($"Decoder configuration not found: {path}");
            return null;
        }
        try { return Rpf9IndexProvider.Load(path); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException)
        {
            if (explicitPath) throw new UsageException("Local RPF9 decoder unavailable: " + ex.Message);
            reporter.Warn("Local RPF9 decoder unavailable, using recovery scans: " + ex.Message);
            return null;
        }
    }

    private static ArchiveDocument? OpenDocument(string path, Rpf9IndexProvider? provider, Reporter reporter, CancellationToken token)
    {
        try { return ArchiveReader.Open(path, provider, token); }
        catch (Exception ex) when (provider is not null && IsOpenFailure(ex))
        {
            reporter.Warn($"{Path.GetFileName(path)}: indexed decoder failed ({ex.Message}); using recovery scan.");
        }
        catch (Exception ex) when (IsOpenFailure(ex))
        {
            reporter.Warn($"{Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
        try { return ArchiveReader.Open(path, token: token); }
        catch (Exception ex) when (IsOpenFailure(ex))
        {
            reporter.Warn($"{Path.GetFileName(path)}: {ex.Message}");
            return null;
        }
    }

    private static bool IsOpenFailure(Exception ex)
        => ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or ArgumentException or OverflowException;
}
