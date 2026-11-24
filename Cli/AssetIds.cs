using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RpfPreview.Core;

namespace RpfPreview.Cli;

public sealed class AssetIds
{
    private readonly Dictionary<WorkspaceAsset, string> _ids;
    private readonly Dictionary<string, WorkspaceAsset> _assets;

    private AssetIds(Dictionary<WorkspaceAsset, string> ids)
    {
        _ids = ids;
        _assets = ids.ToDictionary(p => p.Value, p => p.Key, StringComparer.OrdinalIgnoreCase);
    }

    public string this[WorkspaceAsset asset] => _ids[asset];

    public static AssetIds Build(AssetWorkspaceIndex index)
    {
        var tags = new Dictionary<string, string>(LocalPath.Comparer);
        string Tag(WorkspaceAsset asset)
        {
            if (!tags.TryGetValue(asset.SourcePath, out var tag))
                tags[asset.SourcePath] = tag = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(LocalPath.Identity(asset.SourcePath) + (asset.Document.NestedArchiveSlots.Count == 0 ? "" : "|" + string.Join("/", asset.Document.NestedArchiveSlots)))))[..8];
            return tag;
        }
        var byOffset = index.Assets.GroupBy(a => Tag(a) + ":" + OffsetForm(a), StringComparer.OrdinalIgnoreCase).ToArray();
        var ids = new Dictionary<WorkspaceAsset, string>(ReferenceEqualityComparer.Instance);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in byOffset)
        {
            bool unique = group.Count() == 1;
            foreach (var asset in group)
            {
                string id = unique ? group.Key : Tag(asset) + ":i" + asset.Entry.Index.ToString(CultureInfo.InvariantCulture) + Subresource(asset);
                if (!used.Add(id)) throw new InvalidDataException($"Asset id {id} is not unique in the loaded sources.");
                ids[asset] = id;
            }
        }
        return new(ids);
    }

    public IReadOnlyList<WorkspaceAsset> Resolve(IReadOnlyCollection<string> requested)
    {
        var unknown = requested.Where(id => !_assets.ContainsKey(id)).ToArray();
        if (unknown.Length > 0)
            throw new UsageException($"Unknown asset id{(unknown.Length > 1 ? "s" : "")} {string.Join(", ", unknown.Take(5))}. Run 'search' on the same sources to get current ids.");
        return requested.Distinct(StringComparer.OrdinalIgnoreCase).Select(id => _assets[id]).ToArray();
    }

    private static string OffsetForm(WorkspaceAsset asset)
    {
        string offset = asset.Entry.Offset.ToString("X10", CultureInfo.InvariantCulture);
        return asset.Entry.ResourceDrawableOffset is int drawable && asset.Texture is null ? $"{offset}:{drawable:X}" : offset + Subresource(asset);
    }

    private static string Subresource(WorkspaceAsset asset)
    {
        if (asset.Texture is not null) return ":t" + asset.Texture.Index.ToString(CultureInfo.InvariantCulture);
        if (asset.PoolTexture is not null) return ":p" + asset.PoolTexture.Index.ToString(CultureInfo.InvariantCulture);
        return asset.Entry.ResourceDrawableOffset is int drawable ? $":c{drawable:X}" : "";
    }
}
