using System.Buffers.Binary;

namespace RpfPreview.Core;

public sealed record RageMetaBlock(uint Type, int Offset, int Length);
public sealed record RageMetaArray(int Count, int Capacity, int TargetBlock, int TargetOffset);
public sealed record RageMetaField(uint NameHash, int Offset, byte Type, RageMetaArray? Array);
public sealed record RageMetaRecord(int BlockNumber, int RecordNumber, int? RecordCount, int Offset,
    int Length, uint Type, IReadOnlyList<RageMetaField> Fields, string HexPrefix);

public sealed record RageResourceMetadata(int RootBlock, IReadOnlyList<RageMetaBlock> Blocks,
    int StructureCount, IReadOnlyList<RageMetaField> RootFields)
{
    public const string EntryKind = "0DRP metadata";

    public static byte[] ReadSystem(ArchiveDocument document, ArchiveEntry entry, CancellationToken token = default)
    {
        long length = entry.ResourceSystemBytes ?? entry.Size;
        if (length is < 0x50 or > 16 * 1024 * 1024 || length > entry.Size)
            throw new InvalidDataException("Metadata inspection requires a complete system section up to 16 MiB.");
        return document.ReadPreview(entry, (int)length, token);
    }

    public static string DescribeRecord(RageMetaRecord record)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"Block {record.BlockNumber}    Type 0x{record.Type:X8}");
        if (record.RecordCount.HasValue) text.AppendLine($"Record {record.RecordNumber} of {record.RecordCount:N0}");
        text.AppendLine($"System offset 0x{record.Offset:X8}    {record.Length:N0} bytes");
        foreach (var field in record.Fields)
        {
            text.Append($"\n+0x{field.Offset:X4}    0x{field.NameHash:X8}    type 0x{field.Type:X2}");
            if (field.Array is { } array)
            {
                text.Append($"    {array.Count:N0} items");
                if (array.TargetBlock > 0) text.Append($" in block {array.TargetBlock} at +0x{array.TargetOffset:X}");
            }
        }
        text.AppendLine().AppendLine().Append("First bytes    ").Append(record.HexPrefix);
        return text.ToString();
    }

    public static bool HasHeader(ReadOnlySpan<byte> prefix, int systemBytes)
    {
        if (prefix.Length < 0x50 || systemBytes < 0x50 || U64(prefix, 0) != 0 ||
            !prefix.Slice(16, 4).SequenceEqual("0DRP"u8) || U32(prefix, 20) != 122 ||
            U64(prefix, 24) != 0 || U32(prefix, 36) != 0) return false;

        int blocks = U16(prefix, 0x4c), structures = U16(prefix, 0x48);
        uint root = U32(prefix, 0x20);
        return root > 0 && root <= blocks &&
            SystemOffset(prefix, 0x38, blocks * 16, systemBytes) >= 0 &&
            (structures == 0 || SystemOffset(prefix, 0x28, structures * 32, systemBytes) >= 0);
    }

    public static RageResourceMetadata? Inspect(ReadOnlySpan<byte> system)
    {
        if (!HasHeader(system, system.Length)) return null;
        int rootNumber = (int)U32(system, 0x20);
        var blocks = new RageMetaBlock[U16(system, 0x4c)];
        int directory = SystemOffset(system, 0x38, blocks.Length * 16, system.Length);
        for (int i = 0; i < blocks.Length; i++)
        {
            int row = directory + i * 16;
            uint size = U32(system, row + 4);
            if (size > system.Length) return null;
            int offset = SystemOffset(system, row + 8, (int)size, system.Length);
            if (offset < 0) return null;
            blocks[i] = new(U32(system, row), offset, (int)size);
        }

        int structures = U16(system, 0x48);
        var root = ReadRecord(system, blocks, rootNumber, 1, structures);
        return root is null ? null : new(rootNumber, blocks, structures, root.Fields);
    }

    public static RageMetaRecord? InspectRecord(ReadOnlySpan<byte> system, int? blockNumber = null, int recordNumber = 1)
    {
        var meta = Inspect(system);
        if (meta is null) return null;
        return ReadRecord(system, meta.Blocks, blockNumber ?? meta.RootBlock, recordNumber, meta.StructureCount);
    }

    private static RageMetaRecord? ReadRecord(ReadOnlySpan<byte> system, IReadOnlyList<RageMetaBlock> blocks,
        int blockNumber, int recordNumber, int structures)
    {
        if (blockNumber < 1 || blockNumber > blocks.Count || recordNumber < 1) return null;
        var block = blocks[blockNumber - 1];
        int offsetInSystem = block.Offset, recordSize = block.Length;
        int? recordCount = null;
        int schemas = SystemOffset(system, 0x28, structures * 32, system.Length);
        var fields = new List<RageMetaField>();
        for (int i = 0; i < structures; i++)
        {
            int row = schemas + i * 32;
            if (U32(system, row) != block.Type) continue;
            uint stride = U32(system, row + 24);
            if (stride == 0 || stride > block.Length) return null;
            recordSize = (int)stride;
            recordCount = block.Length / recordSize;
            if (recordNumber > recordCount) return null;
            offsetInSystem += (recordNumber - 1) * recordSize;
            int count = U16(system, row + 30);
            int descriptors = SystemOffset(system, row + 16, count * 16, system.Length);
            if (count > 0 && descriptors < 0) return null;
            for (int j = 0; j < count; j++)
            {
                int field = descriptors + j * 16;
                uint hash = U32(system, field), offset = U32(system, field + 4);
                if (hash == 0x100) continue;
                if (offset >= stride) return null;
                byte type = system[field + 8];
                var array = type == 0x52 && stride - offset >= 16
                    ? ReadArray(system, offsetInSystem + (int)offset, blocks) : null;
                fields.Add(new(hash, (int)offset, type, array));
            }
            break;
        }
        if (recordCount is null && recordNumber != 1) return null;
        return new(blockNumber, recordNumber, recordCount, offsetInSystem, recordSize, block.Type, fields,
            Convert.ToHexString(system.Slice(offsetInSystem, Math.Min(recordSize, 64))));
    }

    private static RageMetaArray? ReadArray(ReadOnlySpan<byte> system, int at, IReadOnlyList<RageMetaBlock> blocks)
    {
        ulong pointer = U64(system, at);
        int count = U16(system, at + 8), capacity = U16(system, at + 10);
        if (count > capacity || U32(system, at + 12) != 0) return null;
        if (pointer == 0) return capacity == 0 ? new(0, 0, 0, 0) : null;
        int block = (int)(pointer & 0xfffff);
        ulong relative = pointer >> 20;
        if (block == 0 || block > blocks.Count || relative >= (ulong)blocks[block - 1].Length ||
            (ulong)count > (ulong)blocks[block - 1].Length - relative) return null;
        return new(count, capacity, block, (int)relative);
    }

    private static int SystemOffset(ReadOnlySpan<byte> data, int at, int length, int systemBytes)
    {
        ulong pointer = U64(data, at);
        if (pointer < 0x50000000 || length > systemBytes) return -1;
        ulong offset = pointer - 0x50000000;
        return offset <= (ulong)(systemBytes - length) ? (int)offset : -1;
    }

    private static ulong U64(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt64LittleEndian(data[at..]);
    private static uint U32(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt32LittleEndian(data[at..]);
    private static int U16(ReadOnlySpan<byte> data, int at) => BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
}
