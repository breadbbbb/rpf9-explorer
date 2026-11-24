using System.Globalization;
using RpfPreview.Core;

namespace RpfPreview.Cli;

public static class AssetLabels
{
    public static string Name(WorkspaceAsset asset)
    {
        var entry = asset.Entry;
        if (asset.Texture is not null) return asset.Texture.Name;
        if (asset.ContentName != null) return asset.ContentName;
        if (asset.Document.HasDecodedRpf9Index && !entry.IsNameCandidate && entry.EmbeddedModelNames.Count > 0) return entry.EmbeddedModelNames[0];
        if (entry.Compression == "Recovered model") return entry.Name.Split("_at_", StringSplitOptions.None)[0];
        return entry.Name;
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => (bytes / (double)(1L << 30)).ToString("0.##", CultureInfo.InvariantCulture) + " GiB",
        >= 1L << 20 => (bytes / (double)(1L << 20)).ToString("0.##", CultureInfo.InvariantCulture) + " MiB",
        >= 1L << 10 => (bytes / (double)(1L << 10)).ToString("0.##", CultureInfo.InvariantCulture) + " KiB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B"
    };

    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => invalid.Contains(c) || char.IsControl(c) || char.IsWhiteSpace(c) || c == '#' ? '_' : c).ToArray();
        string value = new string(chars).Trim('.', '_');
        return value.Length == 0 ? "asset" : value[..Math.Min(120, value.Length)];
    }
}
