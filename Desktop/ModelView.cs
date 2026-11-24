using System.Numerics;
using RpfPreview.Core;

namespace RpfExplorer.Desktop;

internal sealed record ModelView(RageModel Model, IReadOnlyDictionary<int, RageTexturePixels> Textures)
{
    internal IReadOnlySet<int> Hidden { get; init; } = new HashSet<int>();
    internal static ModelView Load(ArchiveDocument document, byte[] resource, int split, RageModel model,
        AssetWorkspaceIndex index, CancellationToken token, IReadOnlyDictionary<ulong, string>? edits = null, WardrobeAppearance? appearance = null, uint? slod = null, bool rim = false, uint? paint = null)
    {
        var maps = new Dictionary<int, RageTexturePixels>();
        var shaders = ModelTextureResolver.TexturedShaderIndices(model);
        if (model.Materials is { } materials)
        {
            var resolver = new ModelTextureResolver(index, 512);
            var source = new ModelTextureSource(document, model, resource, split);
            var colorShaders = materials.Where(m => shaders.Contains(m.ShaderIndex) && ModelTextureBindings.ColorTextures(m).Count > 0).Select(m => m.ShaderIndex).ToArray();
            if (colorShaders.Length == 0 && shaders.Count == 1) colorShaders = shaders.ToArray();
            foreach (int shader in model.Meshes.Select(m => m.ShaderIndex ?? -1).Distinct())
            {
                if (shader < 0 || shader >= materials.Count) continue;
                var refs = ModelTextureBindings.ColorTextures(materials[shader]).Select(b => b.Reference).ToList();
                bool authored = appearance != null && colorShaders.Length == 1 && colorShaders[0] == shader;
                if (authored && appearance!.AlbedoKey != 0) refs.Insert(0, new(0, -1, -1, -1, appearance.AlbedoKey, 0, 0));
                foreach (var reference in refs)
                {
                    token.ThrowIfCancellationRequested();
                    RageTexturePixels? pixels = null;
                    if (edits != null && edits.TryGetValue(reference.OpaqueId64, out var path)) pixels = ReadImage(path);
                    pixels ??= resolver.Find(source, reference, token).Pixels;
                    if (pixels == null) continue;
                    maps[shader] = pixels;
                    break;
                }
                uint? color = VehicleAssembly.ClassifyMaterial(materials[shader], rim) switch
                {
                    VehicleMaterialKind.Paint => paint ?? 0xff534936,
                    VehicleMaterialKind.SecondaryPaint => 0xffc8beb8,
                    VehicleMaterialKind.Tyre => 0xff282828,
                    VehicleMaterialKind.LensAlpha => 0xffc5c8cf,
                    VehicleMaterialKind.Emissive => 0xffcaecff,
                    VehicleMaterialKind.Glass => 0xff493e36,
                    _ => null
                };
                if (!maps.ContainsKey(shader) && ((authored ? slod : null) ?? color) is uint c)
                    maps[shader] = new(1, 1, [(byte)(c >> 16), (byte)(c >> 8), (byte)c, 255]);
            }
        }
        return new(model, maps) { Hidden = (model.Materials ?? []).Where(m => VehicleAssembly.ClassifyMaterial(m, rim) == VehicleMaterialKind.Hidden).Select(m => m.ShaderIndex).ToHashSet() };
    }

    internal static RageTexturePixels ReadImage(string path)
    {
        if (Path.GetExtension(path).Equals(".dds", StringComparison.OrdinalIgnoreCase)) return DdsTextureReader.DecodeTexturePixels(File.ReadAllBytes(path));
        using var stream = File.OpenRead(path);
        using var image = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 1024);
        using var converted = new Avalonia.Media.Imaging.RenderTargetBitmap(image.PixelSize);
        using (var context = converted.CreateDrawingContext()) context.DrawImage(image, new Avalonia.Rect(image.Size), new Avalonia.Rect(0, 0, image.PixelSize.Width, image.PixelSize.Height));
        var bytes = new byte[checked(image.PixelSize.Width * image.PixelSize.Height * 4)];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(bytes, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { converted.CopyPixels(new Avalonia.PixelRect(image.PixelSize), handle.AddrOfPinnedObject(), bytes.Length, image.PixelSize.Width * 4); } finally { handle.Free(); }
        return new(image.PixelSize.Width, image.PixelSize.Height, bytes);
    }

    public RageTexturePixels Render(float yaw, float pitch, float zoom, CancellationToken token, float panX = 0, float panY = 0)
    {
        const int width = 800, height = 600;
        var bgra = new byte[width * height * 4];
        var depths = new float[width * height];
        Array.Fill(depths, float.NegativeInfinity);
        for (int p = 0; p < bgra.Length; p += 4)
        {
            bgra[p] = 34; bgra[p + 1] = 29; bgra[p + 2] = 25; bgra[p + 3] = 255;
        }
        var center = (Model.BoundsMin + Model.BoundsMax) * 0.5f;
        float scale = height * 0.88f * zoom / Math.Max(0.0001f, Vector3.Distance(Model.BoundsMin, Model.BoundsMax));
        var rotation = Matrix4x4.CreateRotationZ(yaw) * Matrix4x4.CreateRotationX(pitch);
        foreach (var mesh in Model.Meshes)
        {
            token.ThrowIfCancellationRequested();
            if (Hidden.Contains(mesh.ShaderIndex ?? -1)) continue;
            var points = new Vector3[mesh.Positions.Count];
            for (int i = 0; i < points.Length; i++)
            {
                var v = Vector3.Transform(mesh.Positions[i] - center, rotation) * scale;
                points[i] = new(width * 0.5f + v.X + panX, height * 0.5f - v.Z + panY, -v.Y);
            }
            Textures.TryGetValue(mesh.ShaderIndex ?? -1, out var texture);
            var uv = mesh.TexCoords;
            for (int i = 0; i + 2 < mesh.Indices.Count; i += 3)
            {
                if ((i & 255) == 0) token.ThrowIfCancellationRequested();
                int ia = mesh.Indices[i], ib = mesh.Indices[i + 1], ic = mesh.Indices[i + 2];
                var a = points[ia]; var b = points[ib]; var c = points[ic];
                var ab = b - a; var ac = c - a;
                var normal = Vector3.Cross(ab, ac);
                float area = normal.Z;
                if (!float.IsFinite(area) || Math.Abs(area) < 0.001f) continue;
                float shade = 0.35f + 0.65f * Math.Abs(Vector3.Dot(Vector3.Normalize(normal), new Vector3(-0.35f, -0.45f, 0.82f)));
                int left = Math.Max(0, (int)MathF.Floor(Math.Min(a.X, Math.Min(b.X, c.X))));
                int right = Math.Min(width - 1, (int)MathF.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))));
                int top = Math.Max(0, (int)MathF.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))));
                int bottom = Math.Min(height - 1, (int)MathF.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))));
                for (int y = top; y <= bottom; y++)
                    for (int x = left; x <= right; x++)
                    {
                        float dx = x + 0.5f - a.X, dy = y + 0.5f - a.Y;
                        float u = (dx * ac.Y - dy * ac.X) / area, v = (ab.X * dy - ab.Y * dx) / area;
                        if (u < 0 || v < 0 || u + v > 1) continue;
                        int pixel = y * width + x;
                        float z = a.Z + u * ab.Z + v * ac.Z;
                        if (z <= depths[pixel]) continue;
                        depths[pixel] = z;
                        int blue = 205, green = 197, red = 185;
                        if (texture is { Width: 1, Height: 1 }) { blue = texture.Bgra32[0]; green = texture.Bgra32[1]; red = texture.Bgra32[2]; }
                        else if (texture == null && uv == null && mesh.VertexColors is { } colors && colors.Count == points.Length)
                        {
                            uint ca = colors[ia], cb = colors[ib], cc = colors[ic];
                            int Channel(int shift) => (int)(((ca >> shift) & 255) * (1 - u - v) + ((cb >> shift) & 255) * u + ((cc >> shift) & 255) * v);
                            red = Channel(0); green = Channel(8); blue = Channel(16);
                        }
                        else if (texture != null && uv != null)
                        {
                            var coord = uv[ia] * (1 - u - v) + uv[ib] * u + uv[ic] * v;
                            int tx = Math.Clamp((int)((coord.X - MathF.Floor(coord.X)) * texture.Width), 0, texture.Width - 1);
                            int ty = Math.Clamp((int)((coord.Y - MathF.Floor(coord.Y)) * texture.Height), 0, texture.Height - 1);
                            int off = (ty * texture.Width + tx) * 4;
                            blue = texture.Bgra32[off]; green = texture.Bgra32[off + 1]; red = texture.Bgra32[off + 2];
                        }
                        bgra[pixel * 4] = (byte)(blue * shade);
                        bgra[pixel * 4 + 1] = (byte)(green * shade);
                        bgra[pixel * 4 + 2] = (byte)(red * shade);
                    }
            }
        }
        return new(width, height, bgra);
    }
}
