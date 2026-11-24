using System.Globalization;
using System.Text;
using System.Text.Json;
using RpfPreview.Core;

namespace RpfPreview.Cli;

public static class ExportCommand
{
    public const string Usage = """
        rpf9-cli export <source>... --out <folder> [options]

        Exports matching assets. Models become OBJ + MTL with PNG color and normal maps, textures become PNG,
        and anything else is extracted as stored. A manifest.json describes what was written.

          -o, --out <folder>       Output folder (required)
          -q, --query <text>       Search text, same syntax as the app
          -t, --type <type>        Asset type to export (default: models)
              --id <id>            Only the asset with this id (from 'search'); repeat or separate with commas
              --kit <id>           Export the models of an authored wardrobe kit (from 'kits') with its appearance;
                                   --query filters the kit's models and --id adds other models
              --available          Skip assets the current decoders cannot extract
              --best               Keep only the largest variant of each name
              --merge <name>       Write all models into one <name>.obj, keeping each part as its own object
              --inside <slot>      Open an indexed child archive (repeat to descend further)
              --drawable <number>  Select a fragment drawable by index (0 selects the primary)
              --lod <0-3>          Level of detail, 0 is the most detailed (default: 0)
              --max-texture <px>   Largest texture side in pixels (default: 2048)
              --no-textures        Write geometry and UVs only
              --no-normals         Skip normal maps
              --no-wardrobe        Do not read wardrobe catalogues for authored albedo and SLOD colors
              --overwrite          Replace files that already exist
              --decoder <file>     Local RPF9 decoder configuration (default: the app's configuration)
              --quiet              Hide progress messages
        """;

    private static readonly OptionSpec[] Options =
    [
        .. AssetSelection.Options, .. CommonOptions.All,
        new("out", "o"),
        new("kit"),
        new("merge"),
        new("lod"),
        new("drawable"),
        new("max-texture"),
        new("no-textures", TakesValue: false),
        new("no-normals", TakesValue: false),
        new("no-wardrobe", TakesValue: false),
        new("overwrite", TakesValue: false)
    ];

    private sealed record Settings(string Output, string? Merge, int Lod, int MaxTexture, bool Textures, bool Normals, bool Wardrobe, bool Overwrite, int? Drawable);
    private sealed record PartRecord(string Id, string Name, string Source, string? Obj, int Vertices, int Triangles, IReadOnlyList<MaterialRecord> Materials, int? FragmentDrawableIndex);
    private sealed record FileRecord(string Id, string Name, string Source, string File);
    private sealed record Failure(string Id, string Name, string Reason);
    private sealed record Manifest(string Tool, int Lod, string? Kit, string? Merged, IReadOnlyList<PartRecord> Models, IReadOnlyList<FileRecord> Images, int Extracted, IReadOnlyList<Failure> Failures);

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        var line = CommandLine.Parse(args, Options);
        var settings = ReadSettings(line);
        uint? kit = line.Value("kit") is { } kitText ? KitsCommand.ParseKitId(kitText) : null;
        if (!settings.Overwrite && File.Exists(Path.Combine(settings.Output, "manifest.json")))
            throw new UsageException($"{settings.Output} already holds an export; choose a new folder or add --overwrite.");
        var reporter = CommonOptions.Reporter(line);
        var selection = AssetSelection.From(line, "models");
        var workspace = SourceWorkspace.Open(line.Positionals, line.Value("decoder"), reporter, token, line.Values("inside"));
        WardrobeIndex? wardrobe = null;
        if (kit is not null || settings.Wardrobe && settings.Textures)
        {
            reporter.Progress("Reading wardrobe catalogues");
            wardrobe = WardrobeIndex.Build(workspace.Documents, token);
            if (kit is not null && wardrobe.Catalogs.Count == 0) throw new UsageException("No wardrobe catalogue was found. Run 'rpf9-cli scan --kinds psin' on the sources first.");
        }
        var appearances = new Dictionary<WorkspaceAsset, WardrobeAppearance>(ReferenceEqualityComparer.Instance);
        var failures = new List<Failure>();
        IReadOnlyList<WorkspaceAsset> assets = kit is uint kitId
            ? [.. KitAssets(kitId, wardrobe!, workspace, selection, appearances, failures),
                .. selection.Ids.Count > 0 ? (selection with { Query = "" }).Apply(workspace) : []]
            : selection.Apply(workspace);
        if (assets.Count == 0)
        {
            reporter.Info("No assets match.");
            foreach (var failure in failures) reporter.Warn(failure.Reason);
            return ExitCodes.NoMatches;
        }
        Directory.CreateDirectory(LocalPath.Require(settings.Output));
        var exporter = new Exporter(workspace, wardrobe, appearances, settings, reporter, token);
        exporter.Failures.AddRange(failures);
        var models = assets.Where(a => a.Kind == "model").ToArray();
        var images = assets.Where(a => a.IsImage).ToArray();
        var others = assets.Except(models).Except(images).ToArray();
        var parts = exporter.ExportModels(models);
        var imageFiles = exporter.ExportImages(images);
        int extracted = await exporter.ExtractAsync(others);
        var manifest = new Manifest("rpf9-cli", settings.Lod, kit?.ToString("X8"), settings.Merge is null ? null : settings.Merge + ".obj", parts, imageFiles, extracted, exporter.Failures);
        exporter.WriteText("manifest.json", JsonSerializer.Serialize(manifest, CliJson.Options));
        reporter.ClearProgress();
        var materials = parts.SelectMany(p => p.Materials).ToArray();
        reporter.Info($"Exported {parts.Count:N0} models ({materials.Count(m => m.ColorMap is not null):N0} textured, {materials.Count(m => m.BaseColor is not null):N0} SLOD colored, " +
            $"{materials.Count(m => m.ColorMap is null && m.BaseColor is null):N0} untextured materials; {materials.Count(m => m.NormalMap is not null):N0} normal maps), " +
            $"{imageFiles.Count:N0} images and {extracted:N0} other files to {settings.Output}");
        foreach (var failure in exporter.Failures.Take(10)) reporter.Warn($"{failure.Name} [{failure.Id}]: {failure.Reason}");
        if (exporter.Failures.Count > 10) reporter.Warn($"{exporter.Failures.Count - 10:N0} more failures are listed in manifest.json.");
        return parts.Count + imageFiles.Count + extracted == 0 ? ExitCodes.Failure : ExitCodes.Success;
    }

    private static IReadOnlyList<WorkspaceAsset> KitAssets(uint kitId, WardrobeIndex wardrobe, SourceWorkspace workspace, AssetSelection selection,
        Dictionary<WorkspaceAsset, WardrobeAppearance> appearances, List<Failure> failures)
    {
        var resolution = wardrobe.ResolveKit(kitId) ?? throw new UsageException($"Wardrobe kit {kitId:X8} is not in the loaded catalogues.");
        foreach (var warning in resolution.Warnings) failures.Add(new(kitId.ToString("X8"), "kit", warning));
        var query = AssetSearchQuery.Parse(selection.Query);
        var byDrawable = workspace.Index.Assets.Where(a => a.Kind == "model" && a.Available)
            .Select(a => (Asset: a, Key: WardrobeIndex.DrawableKey(a.Entry))).Where(p => p.Key is not null)
            .GroupBy(p => p.Key!.Value).ToDictionary(g => g.Key, g => g.Select(p => p.Asset).ToArray());
        var result = new List<WorkspaceAsset>();
        foreach (var group in resolution.Selections.Where(s => s.DrawableKey != 0).GroupBy(s => s.DrawableKey))
        {
            var states = group.Select(s => new WardrobeAppearance(s.AlbedoKey, s.NormalKey, s.PaletteHash, s.Tint0, s.Tint1, s.Tint2)).Distinct().ToArray();
            if (!byDrawable.TryGetValue(group.Key, out var candidates))
            {
                failures.Add(new(group.Key.ToString("X16"), "kit component", $"Drawable {group.Key:X16} is not indexed in the loaded model sources."));
                continue;
            }
            var best = candidates.Where(query.Matches).MaxBy(a => a.Size);
            if (best is null) continue;
            result.Add(best);
            if (states.Length == 1) appearances[best] = states[0];
            else failures.Add(new(workspace.Ids[best], AssetLabels.Name(best), "The kit selects conflicting appearance states; authored albedo was not applied."));
        }
        return result;
    }

    private static Settings ReadSettings(CommandLine line)
    {
        string output = line.Value("out") ?? throw new UsageException("Choose an output folder with --out.");
        string? merge = line.Value("merge");
        if (merge is not null && (merge.Length == 0 || merge.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new UsageException("--merge needs a plain file name without folders.");
        return new(SourceWorkspace.RequireLocal(output), merge is null ? null : Path.GetFileNameWithoutExtension(merge),
            line.Integer("lod", 0, 0, RageModelDecoder.LodCount - 1),
            line.Integer("max-texture", 2048, 16, 16384),
            !line.Has("no-textures"), !line.Has("no-normals"), !line.Has("no-wardrobe"), line.Has("overwrite"), line.Has("drawable") ? line.Integer("drawable", 0, 0, 4095) : null);
    }

    private sealed class Exporter
    {
        private readonly AssetIds _ids;
        private readonly WardrobeIndex? _wardrobe;
        private readonly IReadOnlyDictionary<WorkspaceAsset, WardrobeAppearance> _appearances;
        private readonly Settings _settings;
        private readonly Reporter _reporter;
        private readonly CancellationToken _token;
        private readonly ModelMaterials _materials;
        private readonly HashSet<string> _usedNames = new(StringComparer.OrdinalIgnoreCase);
        private (string Key, byte[] Data)? _resource;

        public Exporter(SourceWorkspace workspace, WardrobeIndex? wardrobe, IReadOnlyDictionary<WorkspaceAsset, WardrobeAppearance> appearances,
            Settings settings, Reporter reporter, CancellationToken token)
        {
            _ids = workspace.Ids;
            _wardrobe = wardrobe;
            _appearances = appearances;
            _settings = settings;
            _reporter = reporter;
            _token = token;
            _materials = new(new ModelTextureResolver(workspace.Index, settings.MaxTexture), settings.Normals, WriteBytes, token);
        }

        public List<Failure> Failures { get; } = [];

        public IReadOnlyList<PartRecord> ExportModels(IReadOnlyList<WorkspaceAsset> assets)
        {
            var records = new List<PartRecord>();
            var merged = new List<ObjModelPart>();
            for (int i = 0; i < assets.Count; i++)
            {
                _token.ThrowIfCancellationRequested();
                var asset = assets[i];
                string name = AssetLabels.Name(asset), id = _ids[asset];
                _reporter.Progress($"Model {i + 1}/{assets.Count}: {name}");
                if (!asset.Available) { Failures.Add(new(id, name, asset.Entry.Limitation ?? "Not extractable with the current decoders.")); continue; }
                try
                {
                    byte[] resource = ReadResource(asset);
                    int split = SystemBytes(asset.Entry);
                    var model = _settings.Drawable is int child
                        ? RageModelDecoder.DecodeFragmentDrawable(resource.AsSpan(0, split), resource.AsSpan(split), child, _settings.Lod)
                        : RageModelDecoder.Decode(resource.AsSpan(0, split), resource.AsSpan(split), asset.Entry.ResourceDrawableOffset, _settings.Lod);
                    if (model is null) { Failures.Add(new(id, name, $"LOD {_settings.Lod} could not be decoded.")); continue; }
                    var resolved = _settings.Textures ? ResolveMaterials(asset, new(asset.Document, model, resource, split)) : new ResolvedMaterials(new Dictionary<int, ObjMaterial>(), []);
                    string partName = UniqueName(AssetLabels.SafeFileName(name) + (_settings.Drawable is int n ? $"_drawable_{n}" : ""), id);
                    var part = new ObjModelPart(partName, model, resolved.Materials);
                    string? obj = null;
                    if (_settings.Merge is null)
                    {
                        obj = partName + ".obj";
                        WriteObj(partName, [part]);
                    }
                    else merged.Add(part);
                    records.Add(new(id, name, asset.SourceName, obj, model.Meshes.Sum(m => m.Positions.Count), model.Meshes.Sum(m => m.Indices.Count / 3), resolved.Records, model.FragmentDrawableIndex));
                }
                catch (Exception ex) when (IsExportFailure(ex)) { Failures.Add(new(id, name, ex.Message)); }
            }
            if (_settings.Merge is not null && merged.Count > 0) WriteObj(_settings.Merge, merged);
            return records;
        }

        public IReadOnlyList<FileRecord> ExportImages(IReadOnlyList<WorkspaceAsset> assets)
        {
            var records = new List<FileRecord>();
            for (int i = 0; i < assets.Count; i++)
            {
                _token.ThrowIfCancellationRequested();
                var asset = assets[i];
                string name = AssetLabels.Name(asset), id = _ids[asset];
                _reporter.Progress($"Image {i + 1}/{assets.Count}: {name}");
                try
                {
                    var pixels = DecodeImage(asset);
                    if (pixels is null) { Failures.Add(new(id, name, "Not a decodable texture.")); continue; }
                    string file = Path.Combine("images", UniqueName(AssetLabels.SafeFileName(name), id) + ".png");
                    WriteBytes(file, PngWriter.Encode(pixels));
                    records.Add(new(id, name, asset.SourceName, file.Replace('\\', '/')));
                }
                catch (Exception ex) when (IsExportFailure(ex)) { Failures.Add(new(id, name, ex.Message)); }
            }
            return records;
        }

        public async Task<int> ExtractAsync(IReadOnlyList<WorkspaceAsset> assets)
        {
            var available = assets.Where(a => a.Available).ToArray();
            foreach (var asset in assets.Except(available))
                Failures.Add(new(_ids[asset], AssetLabels.Name(asset), asset.Entry.Limitation ?? "Not extractable with the current decoders."));
            if (available.Length == 0) return 0;
            var progress = new Progress<ExtractionProgress>(p => _reporter.Progress($"Extracting {p.CompletedFiles}/{p.TotalFiles}: {p.CurrentFile}"));
            try
            {
                await WorkspaceAssetExporter.ExtractAsync(available, Path.Combine(_settings.Output, "files"), progress, _token);
                return available.Length;
            }
            catch (Exception ex) when (IsExportFailure(ex))
            {
                Failures.Add(new("", "extraction", ex.Message));
                return 0;
            }
        }

        public void WriteText(string relative, string content) => WriteBytes(relative, new UTF8Encoding(false).GetBytes(content));

        private ResolvedMaterials ResolveMaterials(WorkspaceAsset asset, ModelTextureSource source)
        {
            WardrobeAppearance? appearance = _appearances.TryGetValue(asset, out var authored) ? authored
                : WardrobeIndex.DrawableKey(asset.Entry) is ulong key ? _wardrobe?.Unambiguous(key) : null;
            uint? slod = appearance is not null ? _wardrobe?.SlodColor(appearance) : null;
            return _materials.Resolve(source, appearance, slod);
        }

        private RageTexturePixels? DecodeImage(WorkspaceAsset asset)
        {
            if (asset.PoolTexture is { } pool) return TtpfReader.DecodeTexturePreview(asset.SourcePath, pool.Index, _settings.MaxTexture, _token);
            if (asset.Texture is not { } texture) return null;
            byte[] resource = ReadResource(asset);
            int split = SystemBytes(asset.Entry), mip = 0;
            while (mip + 1 < texture.MipCount && (Math.Max(1, texture.Width >> mip) > _settings.MaxTexture || Math.Max(1, texture.Height >> mip) > _settings.MaxTexture)) mip++;
            return RageTextureDecoder.DecodeTexturePixelsAtMip(resource.AsSpan(0, split), resource.AsSpan(split), texture.Index, mip);
        }

        private void WriteObj(string baseName, IReadOnlyList<ObjModelPart> parts)
        {
            var document = ModelObjWriter.Write(parts, baseName + ".mtl");
            WriteText(baseName + ".obj", document.Obj);
            WriteText(baseName + ".mtl", document.Mtl);
        }

        private void WriteBytes(string relative, byte[] content)
        {
            string path = ArchiveReader.SafeDestination(_settings.Output, relative.Replace('\\', '/'));
            Directory.CreateDirectory(LocalPath.Require(Path.GetDirectoryName(path)!));
            if (File.Exists(path) && !_settings.Overwrite) throw new IOException($"{relative} already exists; use --overwrite to replace it.");
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
            try
            {
                File.WriteAllBytes(temporary, content);
                File.Move(temporary, path, _settings.Overwrite);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private byte[] ReadResource(WorkspaceAsset asset)
        {
            string key = asset.SourcePath + ":" + asset.Entry.Offset.ToString(CultureInfo.InvariantCulture);
            if (_resource?.Key != key) _resource = (key, ArchiveReader.ReadResourceData(asset.Document, asset.Entry, _token));
            return _resource.Value.Data;
        }

        private string UniqueName(string name, string id)
        {
            if (_usedNames.Add(name)) return name;
            string suffix = id.Replace(':', '_');
            string candidate = name + "_" + suffix;
            for (int n = 2; !_usedNames.Add(candidate); n++) candidate = $"{name}_{suffix}_{n}";
            return candidate;
        }

        private static int SystemBytes(ArchiveEntry entry) => entry.ResourceSystemBytes ?? checked((int)entry.Segments![0].Size);

        private static bool IsExportFailure(Exception ex)
            => ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException or ArgumentException or OverflowException;
    }
}
