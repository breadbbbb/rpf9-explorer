using RpfPreview.Core;
using System.Text;

namespace RpfPreview.App;

internal static class RageResourcePreview
{
    public static (byte[] Bytes, string Details) Read(ArchiveDocument document, ArchiveEntry entry, CancellationToken token)
    {
        const int maxSystemPreview = 16 * 1024 * 1024;
        int systemBytes = entry.ResourceSystemBytes ?? checked((int)entry.Size);
        int limit = entry.Compression == RageResourceMetadata.EntryKind && systemBytes <= maxSystemPreview
            ? systemBytes : 64 * 1024;
        byte[] bytes = document.ReadPreview(entry, limit, token);
        var text = new StringBuilder();
        if (entry.ResourceSystemBytes.HasValue)
        {
            text.AppendLine($"System section      0x00000000   {systemBytes:N0} bytes");
            text.AppendLine($"Graphics section    0x{systemBytes:X8}   {entry.Size - systemBytes:N0} bytes");
            text.AppendLine($"Stored in archive   0x{entry.Offset:X}   {entry.StoredSize:N0} bytes");
        }
        else text.AppendLine($"Decoded resource    {entry.Size:N0} bytes");
        if (!RageResourceMetadata.HasHeader(bytes, systemBytes)) return (bytes, text.ToString());

        text.AppendLine().AppendLine("0DRP metadata, version 122");
        if (systemBytes > maxSystemPreview)
            return (bytes, text.AppendLine("The metadata exceeds the 16 MiB structure preview limit.").ToString());
        if (bytes.Length < systemBytes) bytes = document.ReadPreview(entry, systemBytes, token);
        if (RageResourceMetadata.Inspect(bytes.AsSpan(0, systemBytes)) is not { } meta)
            return (bytes, text.AppendLine("The metadata tables could not be validated.").ToString());

        var root = meta.Blocks[meta.RootBlock - 1];
        text.AppendLine($"Root block {meta.RootBlock}   Type 0x{root.Type:X8}");
        text.AppendLine($"{meta.Blocks.Count:N0} data blocks, {meta.StructureCount:N0} structure definitions");
        text.AppendLine().AppendLine("Block    Type          System offset     Bytes");
        for (int i = 0; i < Math.Min(256, meta.Blocks.Count); i++)
        {
            var block = meta.Blocks[i];
            text.AppendLine($"{i + 1,5}    0x{block.Type:X8}    0x{block.Offset:X8}    {block.Length,8:N0}{(i + 1 == meta.RootBlock ? "  root" : "")}");
        }
        if (meta.Blocks.Count > 256) text.AppendLine($"Showing the first 256 of {meta.Blocks.Count:N0} blocks.");
        if (meta.RootFields.Count > 0)
        {
            text.AppendLine().AppendLine("Root field    Offset     Type");
            foreach (var field in meta.RootFields.Take(256))
            {
                text.Append($"0x{field.NameHash:X8}    +0x{field.Offset:X4}    0x{field.Type:X2}");
                if (field.Array is { } array)
                {
                    text.Append($"    {array.Count:N0} items");
                    if (array.TargetBlock > 0) text.Append($" in block {array.TargetBlock} at +0x{array.TargetOffset:X}");
                }
                text.AppendLine();
            }
            if (meta.RootFields.Count > 256) text.AppendLine($"Showing the first 256 of {meta.RootFields.Count:N0} root fields.");
        }
        return (bytes, text.ToString());
    }
}
