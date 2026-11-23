using RpfPreview.Core;

namespace RpfPreview.Cli;

public sealed record MaterialRecord(int Shader, string? ColorMap, string? NormalMap, string? BaseColor, string Detail);

public sealed record ResolvedMaterials(IReadOnlyDictionary<int, ObjMaterial> Materials, IReadOnlyList<MaterialRecord> Records);

public sealed class ModelMaterials(ModelTextureResolver resolver, bool normals, Action<string, byte[]> write, CancellationToken token)
{
    private readonly Dictionary<ulong, ModelTextureLookup> _lookups = [];
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public ResolvedMaterials Resolve(ModelTextureSource source, WardrobeAppearance? appearance, uint? slodColor)
    {
        var model = source.Model;
        var textured = ModelTextureResolver.TexturedShaderIndices(model);
        var used = (model.Materials ?? []).Where(m => textured.Contains(m.ShaderIndex)).ToArray();
        var colorShaders = used.Where(m => ModelTextureBindings.ColorTextures(m).Count > 0).Select(m => m.ShaderIndex).ToArray();
        if (colorShaders.Length == 0 && used.Length == 1) colorShaders = [used[0].ShaderIndex];
        int? wardrobeShader = appearance is not null && colorShaders.Length == 1 ? colorShaders[0] : null;
        var materials = new Dictionary<int, ObjMaterial>();
        var records = new List<MaterialRecord>();
        foreach (var material in used)
        {
            bool authored = wardrobeShader == material.ShaderIndex;
            var notes = new List<string>();
            var color = FindColor(source, material, authored ? appearance!.AlbedoKey : 0, notes);
            string? normal = normals ? FindNormal(source, material, authored ? appearance!.NormalKey : 0) : null;
            uint? baseColor = color is null && authored ? slodColor : null;
            if (baseColor is not null) notes.Add("wardrobe SLOD color; uniform far-distance approximation");
            materials[material.ShaderIndex] = new(color?.File, normal, baseColor);
            records.Add(new(material.ShaderIndex, color?.File, normal, baseColor is uint rgba ? $"#{rgba & 0xFF:X2}{(rgba >> 8) & 0xFF:X2}{(rgba >> 16) & 0xFF:X2}" : null,
                color?.Detail ?? (notes.Count > 0 ? string.Join("; ", notes.Distinct().Take(4)) : "No recognized color sampler.")));
        }
        return new(materials, records);
    }

    private (string File, string Detail)? FindColor(ModelTextureSource source, RageMaterial material, ulong albedo, List<string> notes)
    {
        var candidates = ModelTextureBindings.ColorTextures(material).Select(c => (c.Reference, c.ParameterName)).ToList();
        if (albedo != 0)
        {
            var reference = candidates.Count > 0 ? candidates[0].Reference with { OpaqueId64 = albedo, OpaqueId32 = 0 } : new RageTextureReference(0, -1, -1, -1, albedo, 0, 0);
            candidates.Insert(0, (reference, "wardrobe albedo"));
        }
        bool opaque = ModelTextureResolver.IsOpaque(material);
        foreach (var (reference, parameter) in candidates)
        {
            var lookup = Lookup(source, reference);
            if (!lookup.Found) { notes.Add(parameter + ": " + lookup.Detail); continue; }
            string file = Store($"textures/{reference.OpaqueId64:X16}{(opaque ? "_opaque" : "")}.png", () => opaque ? Opaque(lookup.Pixels!) : lookup.Pixels!);
            return (file, (parameter == "wardrobe albedo" ? "wardrobe albedo, " : "") + lookup.Detail);
        }
        return null;
    }

    private string? FindNormal(ModelTextureSource source, RageMaterial material, ulong wardrobeNormal)
    {
        var candidates = ModelTextureBindings.NormalTextures(material).ToList();
        if (wardrobeNormal != 0)
            candidates.Insert(0, candidates.Count > 0 ? candidates[0] with { OpaqueId64 = wardrobeNormal, OpaqueId32 = 0 } : new RageTextureReference(0, -1, -1, -1, wardrobeNormal, 0, 0));
        foreach (var reference in candidates)
        {
            var lookup = Lookup(source, reference);
            if (lookup.Found) return Store($"textures/{reference.OpaqueId64:X16}_normal.png", () => OpenGlNormal(lookup.Pixels!));
        }
        return null;
    }

    private ModelTextureLookup Lookup(ModelTextureSource source, RageTextureReference reference)
    {
        if (!_lookups.TryGetValue(reference.OpaqueId64, out var lookup))
            _lookups[reference.OpaqueId64] = lookup = resolver.Find(source, reference, token);
        return lookup;
    }

    private string Store(string file, Func<RageTexturePixels> pixels)
    {
        if (_files.TryGetValue(file, out var existing)) return existing;
        write(file, PngWriter.Encode(pixels()));
        _files[file] = file;
        return file;
    }

    private static RageTexturePixels Opaque(RageTexturePixels pixels)
    {
        byte[] copy = (byte[])pixels.Bgra32.Clone();
        for (int i = 3; i < copy.Length; i += 4) copy[i] = 255;
        return pixels with { Bgra32 = copy };
    }

    private static RageTexturePixels OpenGlNormal(RageTexturePixels pixels)
    {
        byte[] copy = (byte[])pixels.Bgra32.Clone();
        bool twoChannel = true;
        for (int i = 0; i < copy.Length && twoChannel; i += 4) twoChannel = copy[i] == 0;
        for (int i = 0; i < copy.Length; i += 4)
        {
            if (twoChannel)
            {
                float x = copy[i + 2] / 127.5f - 1, y = copy[i + 1] / 127.5f - 1;
                copy[i] = (byte)Math.Round((MathF.Sqrt(Math.Max(0, 1 - x * x - y * y)) * .5f + .5f) * 255);
            }
            copy[i + 1] = (byte)(255 - copy[i + 1]);
            copy[i + 3] = 255;
        }
        return pixels with { Bgra32 = copy };
    }
}
