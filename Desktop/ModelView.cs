using System.Numerics;
using RpfPreview.Core;

namespace RpfExplorer.Desktop;

internal sealed record ModelView(RageModel Model, IReadOnlyDictionary<int, RageTexturePixels> Textures)
{
    internal static ModelView Load(ArchiveDocument document, byte[] resource, int split, RageModel model,
        AssetWorkspaceIndex index, CancellationToken token)
    {
        var maps = new Dictionary<int, RageTexturePixels>();
        var shaders = ModelTextureResolver.TexturedShaderIndices(model);
        if (model.Materials is { } materials && shaders.Count > 0)
        {
            var resolver = new ModelTextureResolver(index, 512);
            var source = new ModelTextureSource(document, model, resource, split);
            foreach (int shader in shaders.Order().Take(64))
            {
                if (shader < 0 || shader >= materials.Count) continue;
                foreach (var binding in ModelTextureBindings.ColorTextures(materials[shader]))
                {
                    token.ThrowIfCancellationRequested();
                    if (resolver.Find(source, binding.Reference, token).Pixels is not { } pixels) continue;
                    maps[shader] = pixels;
                    break;
                }
            }
        }
        return new(model, maps);
    }

    public RageTexturePixels Render(float yaw, float pitch, float zoom, CancellationToken token)
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
            var points = new Vector3[mesh.Positions.Count];
            for (int i = 0; i < points.Length; i++)
            {
                var v = Vector3.Transform(mesh.Positions[i] - center, rotation) * scale;
                points[i] = new(width * 0.5f + v.X, height * 0.5f - v.Z, -v.Y);
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
                        if (texture != null && uv != null)
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
