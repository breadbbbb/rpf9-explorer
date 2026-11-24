using RpfPreview.Core;
using System.Numerics;

namespace RpfExplorer.Desktop;

internal sealed class ModelAssembly(AssetWorkspaceIndex index, Dictionary<ulong, string> edits, CancellationToken token)
{
    private readonly List<RageMesh> meshes = [];
    private readonly Dictionary<int, RageTexturePixels> images = [];
    private readonly HashSet<int> hidden = [];
    private readonly List<string> labels = [];
    private int vertices, indices;
    private Vector3 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
    public string Details => string.Join("\n", labels);
    public ModelView Finish(string name) => meshes.Count > 0 ? new ModelView(new(name, meshes, min, max), images) { Hidden = hidden } : throw new InvalidDataException("No readable components.");
    private void Add(ModelView part, string label, VehicleWheelMount? mount = null, float scale = 1)
    {
        token.ThrowIfCancellationRequested(); labels.Add(label + "   " + part.Model.Name);
        foreach (var mesh in part.Model.Meshes)
        {
            if (mesh.Positions.Count > 2_000_000 - vertices || mesh.Indices.Count > 6_000_000 - indices || meshes.Count >= 1024) throw new InvalidDataException("Assembly exceeds the preview geometry budget.");
            vertices += mesh.Positions.Count; indices += mesh.Indices.Count;
            var m = mount == null ? mesh : VehicleAssembly.TransformMesh(mesh, mount.Pivot, scale, mount.IsRight);
            int slot = meshes.Count;
            if (part.Hidden.Contains(mesh.ShaderIndex ?? -1)) hidden.Add(slot);
            if (part.Textures.TryGetValue(mesh.ShaderIndex ?? -1, out var image)) images[slot] = image;
            meshes.Add(m with { Name = label + " | " + mesh.Name, ShaderIndex = slot });
            foreach (var v in m.Positions) { min = Vector3.Min(min, v); max = Vector3.Max(max, v); }
        }
    }
    private ModelView Read(WorkspaceAsset a, WardrobeAppearance? appearance = null, uint? slod = null, ulong? expected = null, bool rim = false, uint? paint = null)
    {
        var d = ArchiveReader.ReadResourceData(a.Document, a.Entry, token); int b = a.Entry.ResourceSystemBytes ?? checked((int)a.Entry.Segments![0].Size);
        RageModel? model = null; int triangles = -1;
        for (int lod = 0; lod < (expected.HasValue ? 4 : 1); lod++)
        {
            token.ThrowIfCancellationRequested();
            var m = RageModelDecoder.Decode(d.AsSpan(0, b), d.AsSpan(b), a.Entry.ResourceDrawableOffset, lod);
            if (m == null || expected.HasValue && RageAssetKeys.DrawableKey(m.Name) != expected) continue;
            int count = m.Meshes.Sum(x => x.Indices.Count / 3);
            if (count > triangles) { triangles = count; model = m; }
        }
        return ModelView.Load(a.Document, d, b, model ?? throw new InvalidDataException("No matching drawable in " + a.Name), index, token, edits, appearance, slod, rim, paint);
    }
    public ModelView Manual(IEnumerable<WorkspaceAsset> assets)
    {
        foreach (var a in assets) Add(Read(a), a.Name);
        return Finish("Selected parts");
    }
    public ModelView Outfit(RageWardrobeCatalog catalog, uint id, WardrobeIndex wardrobe)
    {
        var resolved = catalog.Resolve(id);
        if (resolved.Warnings.Count > 0) throw new InvalidDataException(string.Join(" ", resolved.Warnings.Take(4)));
        var groups = resolved.Selections.Where(s => s.DrawableKey != 0).GroupBy(s => s.DrawableKey).ToArray();
        if (groups.Length is < 1 or > 96) throw new InvalidDataException("The kit must have 1 to 96 selected components.");
        var lookup = index.Assets.Where(a => a.Available && a.Kind == "model").ToLookup(a => WardrobeIndex.DrawableKey(a.Entry));
        foreach (var g in groups)
        {
            var states = g.Select(s => new WardrobeAppearance(s.AlbedoKey, s.NormalKey, s.PaletteHash, s.Tint0, s.Tint1, s.Tint2)).Distinct().ToArray();
            if (states.Length != 1) throw new InvalidDataException($"Conflicting appearances for {g.Key:X16}.");
            var candidates = lookup[g.Key].ToArray();
            if (candidates.Length > 128) throw new InvalidDataException("Too many variants for one component.");
            ModelView? best = null; int largest = -1;
            foreach (var a in candidates)
            {
                try { var v = Read(a, states[0], wardrobe.SlodColor(states[0]), g.Key); int n = v.Model.Meshes.Sum(m => m.Indices.Count); if (n > largest) { best = v; largest = n; } }
                catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException) { }
            }
            if (best == null) throw new InvalidDataException($"Kit {id:X8} needs drawable {g.Key:X16}. Load its model source.");
            Add(best, best.Model.Name);
        }
        return Finish($"wardrobe_{id:X8}");
    }
    internal static VehicleConfigPreset? Preset(WorkspaceAsset a, string name)
    {
        string dir = Path.GetDirectoryName(a.SourcePath)!;
        foreach (string path in new[] { AppContext.BaseDirectory, dir, Path.GetDirectoryName(dir) }.OfType<string>())
            if (VehicleConfigCatalog.TryLoad(Path.Combine(path, "vehicles.json")) is { } c && c.TryGet(name, out var preset)) return preset;
        return null;
    }
    internal static uint? Paint(string? hex)
    {
        if (hex == null || !uint.TryParse(hex.TrimStart('#'), System.Globalization.NumberStyles.HexNumber, null, out var rgb)) return null;
        return 0xff000000u | ((rgb & 255) << 16) | (rgb & 0xff00) | (rgb >> 16 & 255);
    }
    public ModelView Vehicle(ModelView body, IReadOnlyList<VehicleWheelMount> mounts, VehicleConfigPreset? preset)
    {
        if (mounts.Count == 0) throw new InvalidDataException("The drawable has no decoded wheel mounts.");
        Add(body, "Body");
        var assets = index.Assets.Where(a => a.Kind == "model" && a.Available).ToArray();
        string Name(WorkspaceAsset a) => VehicleAssembly.NormalizeVehicleBaseName(a.Entry.EmbeddedModelNames.FirstOrDefault() ?? a.Name);
        WorkspaceAsset? Find(string? name) => name == null ? null : assets.Where(a => Name(a).Equals(VehicleAssembly.NormalizeVehicleBaseName(name), StringComparison.OrdinalIgnoreCase)).OrderByDescending(a => a.Size).FirstOrDefault();
        WorkspaceAsset? Fallback(string prefix) => assets.Where(a => Name(a).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).OrderBy(a => Name(a), StringComparer.Ordinal).ThenByDescending(a => a.Size).FirstOrDefault();
        var (front, rear) = VehicleAssembly.ResolveWheelScales(preset);
        var pieces = new (string Label, WorkspaceAsset? Asset, bool Rim, float Scale, uint? Tint)[] {
            ("rim",Find(preset?.RimName)??Find("rim_uni_steel_002")??Find("rim_muscle_001")??Fallback("rim_"),true,1,0xffc8beb8),
            ("tyre",Find(preset?.TyreName)??Find("tyre_car_001")??Find("tyre_muscle_001")??Fallback("tyre_"),false,1,null),
            ("brake disc",Find(preset?.BrakeDiscName),true,VehicleAssembly.BrakeScaleRatio,null),
            ("caliper",Find(preset?.CaliperName),true,VehicleAssembly.BrakeScaleRatio,0xff201cb4)};
        foreach (var piece in pieces)
        {
            if (piece.Asset == null) { labels.Add("No matching " + piece.Label + " loaded."); continue; }
            var view = Read(piece.Asset, rim: piece.Rim, paint: piece.Tint);
            foreach (var mount in mounts) Add(view, mount.BoneName + " " + piece.Label, mount, (mount.IsRear ? rear : front) * piece.Scale);
        }
        foreach (string name in preset?.ExtraPartNames ?? []) { if (Find(name) is { } a) Add(Read(a, paint: Paint(preset?.PrimaryColorHex)), name); else labels.Add("Missing " + name); }
        return Finish(body.Model.Name + "_assembled");
    }
}
