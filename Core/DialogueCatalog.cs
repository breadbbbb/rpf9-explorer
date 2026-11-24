using System.Buffers.Binary;
using System.Text;

namespace RpfPreview.Core;

internal sealed class DialogueCatalog
{
    private readonly Dictionary<string, HashSet<string>> cues = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string?> firstTakes = [];
    private const int MaxSamples = 100_000;

    public static DialogueCatalog Read(ArchiveDocument bank, CancellationToken token)
    {
        var result = new DialogueCatalog();
        ArchiveDocument parent = bank.ParentArchive ?? bank;
        var sources = parent.Entries.Where(e => e.IsNestedArchive && IsConversation(e.Path)).Take(16);
        long remaining = 256L * 1024 * 1024;
        foreach (var archive in sources)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var catalog = ArchiveReader.OpenNestedRpf9(parent, archive, token);
                foreach (var entry in catalog.Entries.Where(e => e.CanExtract && !e.IsNestedArchive && e.Size is >= 128 and <= 16 * 1024 * 1024))
                {
                    token.ThrowIfCancellationRequested();
                    if (entry.Size > remaining || result.cues.Count >= MaxSamples) break;
                    remaining -= entry.Size;
                    result.Add(catalog.ReadPreview(entry, checked((int)entry.Size), token));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or UnauthorizedAccessException) { }
        }
        return result;
    }

    private static bool IsConversation(string path)
    {
        string stem = Path.GetFileNameWithoutExtension(path);
        return stem.Equals("conversations", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("conversations_", StringComparison.OrdinalIgnoreCase);
    }

    internal void Add(byte[] archive)
    {
        if (PsinMetadata.Inspect(archive) is not { InternalChecksumVerified: true } parsed) return;
        int end = parsed.Sections[0].Length;
        var data = archive.AsSpan(0, end);
        var directory = new List<(uint Type, int Size)>();
        int pos = 8;
        while (pos <= end - 8 && data.Slice(pos, 8).IndexOfAnyExcept((byte)0x70) >= 0)
        {
            if (pos > end - 16 || directory.Count >= 128 || data.Slice(pos + 8, 8).IndexOfAnyExcept((byte)0xaa) >= 0) return;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(data[(pos + 4)..]);
            if (size > end) return;
            directory.Add((BinaryPrimitives.ReadUInt32LittleEndian(data[pos..]), (int)size));
            pos += 16;
        }
        pos += 8;
        int rows = -1, sizeRows = 0, strings = -1, sizeStrings = 0, stringBlock = -1;
        for (int i = 0; i < directory.Count; i++)
        {
            var (type, length) = directory[i];
            if (pos > end - length) return;
            if (type == 0x7622c7f9) { if (rows >= 0) return; rows = pos; sizeRows = length; }
            if (type == 0x10) { if (strings >= 0) return; strings = pos; sizeStrings = length; stringBlock = i + 1; }
            pos = (pos + length + 15) & ~15;
        }
        if (pos != end || rows < 0 || strings < 0 || sizeRows % 40 != 0) return;
        for (int at = rows; at < rows + sizeRows && cues.Count < MaxSamples; at += 40)
        {
            string? sample = Symbol(data, at, stringBlock, strings, sizeStrings);
            string? cue = Symbol(data, at + 8, stringBlock, strings, sizeStrings);
            if (sample == null || cue == null) continue;
            if (!cues.TryGetValue(sample, out var labels))
            {
                cues.Add(sample, labels = new(StringComparer.Ordinal));
                uint id = Hash(sample + "_01");
                if (!firstTakes.TryAdd(id, sample) && firstTakes[id] != sample) firstTakes[id] = null;
            }
            labels.Add(cue);
        }
    }

    private static string? Symbol(ReadOnlySpan<byte> data, int at, int block, int start, int length)
    {
        ulong pointer = BinaryPrimitives.ReadUInt64LittleEndian(data[at..]);
        ulong offset = pointer >> 20;
        if ((int)(pointer & 0xfffff) != block || offset >= (ulong)length) return null;
        var bytes = data.Slice(start + (int)offset, Math.Min(160, length - (int)offset));
        int n = bytes.IndexOf((byte)0);
        if (n < 3 || bytes[..n].IndexOf((byte)'_') < 0) return null;
        foreach (byte c in bytes[..n]) if (c != '_' && c is not (>= (byte)'A' and <= (byte)'Z') && c is not (>= (byte)'0' and <= (byte)'9')) return null;
        return Encoding.ASCII.GetString(bytes[..n]);
    }

    internal IReadOnlyDictionary<uint, string> Match(IReadOnlyList<uint> streams)
    {
        if (streams.Count == 0 || firstTakes.Count == 0) return Empty;
        var ids = streams.ToHashSet();
        var matches = new Dictionary<uint, string?>();
        foreach (uint primary in ids)
        {
            if (!firstTakes.TryGetValue(primary, out string? stem) || stem == null) continue;
            string contexts = string.Join(", ", cues[stem].Order(StringComparer.Ordinal));
            for (int take = 1; take <= 99; take++)
            {
                string name = $"{stem}_{take:D2}";
                uint id = Hash(name);
                if (!ids.Contains(id)) continue;
                if (!matches.TryAdd(id, name + " · " + contexts)) matches[id] = null;
            }
        }
        return matches.Where(p => p.Value != null).ToDictionary(p => p.Key, p => p.Value!);
    }

    internal static readonly IReadOnlyDictionary<uint, string> Empty = new Dictionary<uint, string>();

    private static uint Hash(string name)
    {
        unchecked
        {
            uint h = 0;
            foreach (char c in name) { h += (uint)char.ToLowerInvariant(c); h += h << 10; h ^= h >> 6; }
            h += h << 3; h ^= h >> 11; h += h << 15;
            return h & 0x1fff_ffff;
        }
    }
}
