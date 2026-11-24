using System.Text;

namespace RpfPreview.Core;

internal static class AudioMounts
{
    internal static void Identify(ArchiveDocument source, ArchiveEntry[] entries, CancellationToken token)
    {
        string metadataHash = RageAssetKeys.PathHash("core.metadata");
        var metadata = entries.Where(e => e.IsNestedArchive && string.Equals(e.NameHash64, metadataHash, StringComparison.OrdinalIgnoreCase)).Take(2).ToArray();
        if (metadata.Length != 1) return;
        var matches = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var members = entries.Where(e => e.IsNestedArchive && e.NameHash64 != null).Select(e => e.NameHash64!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        try
        {
            var doc = ArchiveReader.OpenNestedRpf9(source, metadata[0], token);
            long budget = 1024 * 1024;
            foreach (var e in doc.Entries.Where(e => e.CanExtract && e.Size is >= 8 and <= 65536).OrderBy(e => e.Size))
            {
                token.ThrowIfCancellationRequested();
                if (e.Size > budget) break;
                budget -= e.Size;
                var bytes = doc.ReadPreview(e, (int)e.Size, token);
                if (!bytes.Contains((byte)'\n') || bytes.Any(b => b != '\n' && b != '\r' && (b < 32 || b > 126))) continue;
                var names = Encoding.ASCII.GetString(bytes).Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
                if (names.Length < 2 || names.Any(n => n.Length > 180 || !n.Contains('.') || !n.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))) continue;
                foreach (string name in names)
                {
                    string path = "sfx/" + name;
                    string hash = RageAssetKeys.PathHash(path);
                    if (members.Contains(hash) && !matches.TryAdd(hash, path + ".rpf") &&
                        !string.Equals(matches[hash], path + ".rpf", StringComparison.OrdinalIgnoreCase)) matches[hash] = null;
                }
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or UnauthorizedAccessException) { return; }
        if (matches.Count == 0) return;
        foreach (var e in entries)
        {
            if (e.IsNameCandidate || e.NameHash64 == null || !e.IsNestedArchive) continue;
            string? hint = string.Equals(e.NameHash64, metadataHash, StringComparison.OrdinalIgnoreCase) ? "core.metadata.rpf" : matches.GetValueOrDefault(e.NameHash64);
            if (hint != null) entries[e.Index] = e with { ContentPathHint = hint };
        }
    }
}
