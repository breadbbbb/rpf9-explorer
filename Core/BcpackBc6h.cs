using System.Buffers.Binary;

namespace RpfPreview.Core;

/// <summary>
/// BCPack codec 6 (BC6H) block rebuilder: the 16 decoded lanes become standard 16-byte BC6H blocks.
/// Port of `_bc6h_blocks` in Leonida's `tools/bcpack.py`, run without a BCPack initial-history table.
/// </summary>
/// <remarks>
/// A block whose endpoint or index delta points before the start of the chunk reads BCPack's built-in
/// 256-entry initial history. That table is unpublished and is not part of this decoder, so strict mode
/// refuses such a chunk, and provisional mode decodes it against an all-zero history while at most
/// `max(2, blocks / 1024)` blocks depend on it. Chunks whose delta chains stay inside the chunk decode exactly.
/// </remarks>
internal sealed class Bc6hRebuilder
{
    private const int Ring = 256;
    private const int Channels = 3;
    private const int EndpointValues = Channels * 4;

    private readonly record struct PlanBit(int Channel, int Slot, int Bit, int Position);

    private sealed record Mode(int Code, bool Transformed, int BaseBits, int[] Deltas, int Regions, PlanBit[] Plan, int HeaderBits);

    /// <summary>Mode index (lane 15 symbol + 1) to the D3D11 mode bits; entry 0 is unused.</summary>
    private static readonly Mode?[] Modes = BuildModes();

    private static readonly int[] NoEndpoints = BuildNoEndpoints();
    private static readonly int[] NoIndices = Enumerable.Repeat(8, 16).ToArray();
    private static readonly int[] EmptyEndpointHistory = new int[EndpointValues];
    private static readonly int[] EmptyIndexHistory = new int[16];

    private readonly ushort[][] _lanes;
    private readonly int _blockCount;
    private readonly HashSet<string>? _provisional;
    private readonly byte[] _output;
    private readonly int[][] _endpoints = NewRing(EndpointValues);
    private readonly int[][] _indices = NewRing(16);
    private readonly int[] _endpointRoot = new int[Ring];
    private readonly int[] _indexRoot = new int[Ring];
    private readonly bool[] _endpointUsed = new bool[Ring];
    private readonly bool[] _indexUsed = new bool[Ring];
    private readonly int[] _streamFields = new int[EndpointValues];
    private readonly int[] _pixelIndex = new int[16];
    private int _firstEndpoint = -1, _firstIndex = -1, _lostBlocks, _violations;

    private Bc6hRebuilder(ushort[][] lanes, int blockCount, HashSet<string>? provisional)
    {
        _lanes = lanes;
        _blockCount = blockCount;
        _provisional = provisional;
        _output = new byte[checked(blockCount * 16)];
    }

    internal static byte[] Rebuild(ushort[][] lanes, int blockCount, HashSet<string>? provisional, CancellationToken ct)
    {
        if (lanes[0].Length != 2 * blockCount)
            throw new InvalidDataException("BC6H lane length does not match the block count.");
        var rebuilder = new Bc6hRebuilder(lanes, blockCount, provisional);
        for (int i = 0; i < blockCount; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            rebuilder.RebuildBlock(i);
        }
        rebuilder.CheckHistory();
        return rebuilder._output;
    }

    private void RebuildBlock(int i)
    {
        int a = 2 * i, b = a + 1, slot = i & (Ring - 1);
        int modeSymbol = _lanes[15][a];
        if (modeSymbol > 13)
            throw new InvalidDataException($"BC6H block {i} mode symbol {modeSymbol} outside 0..13.");
        var mode = Modes[modeSymbol + 1]!;
        int partition = Math.Min((int)_lanes[15][b], 31);

        int indexDelta = BcpackDecoder.Interleave(_lanes[1][a], _lanes[1][b], 4);
        int endpointDelta = (indexDelta + BcpackDecoder.DecodeZigZag(BcpackDecoder.Interleave(_lanes[0][a], _lanes[0][b], 4))) & 255;
        int[] endpointRef = ReferenceEndpoints(i, slot, endpointDelta);
        int[] indexRef = ReferenceIndices(i, slot, indexDelta);
        if (_endpointRoot[slot] >= 0 || _indexRoot[slot] >= 0) _lostBlocks++;

        DecodeEndpoints(mode, a, b, endpointRef, _endpoints[slot], modeSymbol + 1);
        DecodeIndices(mode, a, b, indexRef, _indices[slot]);
        WriteBlock(i, mode, partition);
    }

    private int[] ReferenceEndpoints(int i, int slot, int distance)
    {
        int[] reference;
        if (distance == 0)
        {
            _endpointRoot[slot] = -1;
            return NoEndpoints;
        }
        if (i >= distance)
        {
            int previous = (i - distance) & (Ring - 1);
            _endpointRoot[slot] = _endpointRoot[previous];
            reference = _endpoints[previous];
        }
        else
        {
            _endpointRoot[slot] = 256 + i - distance;
            reference = EmptyEndpointHistory;
        }
        NoteUsed(_endpointRoot[slot], _endpointUsed, ref _firstEndpoint);
        return reference;
    }

    private int[] ReferenceIndices(int i, int slot, int distance)
    {
        int[] reference;
        if (distance == 0)
        {
            _indexRoot[slot] = -1;
            return NoIndices;
        }
        if (i >= distance)
        {
            int previous = (i - distance) & (Ring - 1);
            _indexRoot[slot] = _indexRoot[previous];
            reference = _indices[previous];
        }
        else
        {
            _indexRoot[slot] = 256 + i - distance;
            reference = EmptyIndexHistory;
        }
        NoteUsed(_indexRoot[slot], _indexUsed, ref _firstIndex);
        return reference;
    }

    private static void NoteUsed(int root, bool[] used, ref int first)
    {
        if (root < 0 || used[root]) return;
        used[root] = true;
        if (first < 0) first = root;
    }

    /// <summary>
    /// Rebuilds the stored endpoint state (`state`, 16-bit scaled for later blocks) and the block's
    /// header fields (`_streamFields`, as they appear in the D3D11 bit layout).
    /// </summary>
    private void DecodeEndpoints(Mode mode, int a, int b, int[] reference, int[] state, int modeIndex)
    {
        int wShift = 16 - mode.BaseBits, baseMask = (1 << mode.BaseBits) - 1;
        for (int c = 0; c < Channels; c++)
        {
            int dc = mode.Deltas[c], deltaMask = (1 << dc) - 1, shift = dc < 6 ? 6 - dc : 0;
            int dz0 = BcpackDecoder.DecodeZigZag(BcpackDecoder.Interleave(_lanes[2 + c][a], _lanes[2 + c][b], 8));
            int dz1 = BcpackDecoder.DecodeZigZag(BcpackDecoder.Interleave(_lanes[5 + c][a], _lanes[5 + c][b], 5));
            int dz2 = BcpackDecoder.DecodeZigZag(_lanes[8 + c][a]);
            int dz3 = BcpackDecoder.DecodeZigZag(_lanes[8 + c][b]);
            int at = c * 4;
            int w = ((reference[at] >> wShift) + dz0) & baseMask;
            int x = ((reference[at + 1] >> (10 - dc)) + dz1) & deltaMask;
            int y = ((reference[at + 2] >> shift) + dz2) & deltaMask;
            int z = ((reference[at + 3] >> shift) + dz3) & deltaMask;
            int x10 = x << (10 - dc);
            _streamFields[at] = StreamBase(modeIndex, w);
            _streamFields[at + 1] = x;
            if (mode.Regions == 2)
            {
                state[at] = w << wShift;
                state[at + 1] = x10;
                state[at + 2] = y << shift;
                state[at + 3] = z << shift;
                _streamFields[at + 2] = y;
                _streamFields[at + 3] = z;
            }
            else
            {
                int v = (w & deltaMask) ^ (mode.Transformed ? 1 << (dc - 1) : 0);
                state[at] = w << wShift;
                state[at + 1] = x10;
                state[at + 2] = dc >= 6 ? v >> (dc - 6) : v << shift;
                state[at + 3] = x10 >> 4;
                _streamFields[at + 2] = 0;
                _streamFields[at + 3] = 0;
            }
        }
    }

    /// <summary>Maps BCPack's base endpoint into the D3D11 BC6H bit order (modes 13 and 14 swap bits).</summary>
    private static int StreamBase(int modeIndex, int w)
    {
        if (modeIndex == 13)
            return (w & 0x3FF) | (((w >> 10) & 1) << 11) | (((w >> 11) & 1) << 10);
        if (modeIndex != 14)
            return w;
        int result = w & 0x3FF;
        for (int bit = 10; bit < 16; bit++) result |= ((w >> bit) & 1) << (25 - bit);
        return result;
    }

    private void DecodeIndices(Mode mode, int a, int b, int[] reference, int[] state)
    {
        int bits = mode.Regions == 2 ? 3 : 4, mask = (1 << bits) - 1, shift = 4 - bits;
        for (int y = 0; y < 4; y++)
        {
            int packedA = _lanes[11 + y][a], packedB = _lanes[11 + y][b];
            var (d0, d1, d2, d3) = BcpackDecoder.DeinterleaveRow(packedA, packedB, y);
            int row = y * 4;
            for (int off = 0; off < 4; off++)
            {
                int p = row + off;
                int delta = off switch { 0 => d0, 1 => d1, 2 => d2, _ => d3 };
                int index = ((reference[p] >> shift) + BcpackDecoder.DecodeZigZag(delta)) & mask;
                _pixelIndex[p] = index;
                state[p] = index << shift;
            }
        }
    }

    private void WriteBlock(int i, Mode mode, int partition)
    {
        int bits = mode.Regions == 2 ? 3 : 4;
        int second = mode.Regions == 2 ? BcpackBptcTables.Anchor2[partition] : -1;
        if ((_pixelIndex[0] & (1 << (bits - 1))) != 0) _violations++;
        if (second >= 0 && (_pixelIndex[second] & 4) != 0) _violations++;

        UInt128 block = (UInt128)(uint)mode.Code;
        foreach (var item in mode.Plan)
        {
            bool set = item.Channel < 0
                ? ((partition >> item.Bit) & 1) != 0
                : ((_streamFields[item.Channel * 4 + item.Slot] >> item.Bit) & 1) != 0;
            if (set) block |= UInt128.One << item.Position;
        }
        int at = mode.HeaderBits;
        for (int p = 0; p < 16; p++)
        {
            int width = p == 0 || p == second ? bits - 1 : bits;
            block |= (UInt128)(uint)(_pixelIndex[p] & ((1 << width) - 1)) << at;
            at += width;
        }
        BinaryPrimitives.WriteUInt128LittleEndian(_output.AsSpan(16 * i, 16), block);
    }

    private void CheckHistory()
    {
        if (_provisional is null)
        {
            if (_firstEndpoint >= 0 || _firstIndex >= 0)
            {
                string kind = _firstEndpoint >= 0 ? "endpoint" : "index";
                throw new BcpackProvisionalException(
                    $"BC6H chunk references unknown built-in {kind} history entry {(_firstEndpoint >= 0 ? _firstEndpoint : _firstIndex)}.");
            }
            if (_violations > 0)
                throw new InvalidDataException($"BC6H chunk has {_violations} anchor pixel(s) with an inconsistent index.");
            return;
        }

        var missing = new List<string>();
        for (int k = 0; k < Ring; k++)
        {
            if (_endpointUsed[k]) missing.Add($"endpoint {k}");
            if (_indexUsed[k]) missing.Add($"index {k}");
        }
        missing.Sort(StringComparer.Ordinal);
        int limit = BcpackDecoder.LooseUnknownBlockLimit(_blockCount);
        if (_lostBlocks > limit)
            throw new BcpackProvisionalException(
                $"BC6H chunk has {_lostBlocks}/{_blockCount} blocks from unknown built-in history entries ({string.Join(", ", missing.Take(6))}{(missing.Count > 6 ? ", ..." : "")}).");
        if (_violations > limit)
            throw new BcpackProvisionalException($"BC6H chunk has {_violations} anchor pixels with an inconsistent index.");
        if (missing.Count > 0)
            _provisional.Add($"BC6H unknown history {string.Join(", ", missing)} ({_lostBlocks} blocks)");
        if (_violations > 0)
            _provisional.Add($"BC6H anchor index inconsistent in {_violations} pixels");
    }

    private static int[][] NewRing(int length)
    {
        var ring = new int[Ring][];
        for (int i = 0; i < Ring; i++) ring[i] = new int[length];
        return ring;
    }

    private static int[] BuildNoEndpoints()
    {
        var none = new int[EndpointValues];
        for (int c = 0; c < Channels; c++)
        {
            none[c * 4] = 0x8000;
            none[c * 4 + 1] = 0x200;
            none[c * 4 + 2] = 0x20;
            none[c * 4 + 3] = 0x20;
        }
        return none;
    }

    // Standard D3D11/Khronos BC6H header bit layouts (LSB first after the 2- or 5-bit mode).
    // Tokens: `rw10` bits 0..9 of the base red endpoint; `gy4` the single bit 4; `gy4_` bits 0..3; `rw:10` one
    // bit by number; `rw:11-10` a descending bit range; `d` the 5-bit partition.
    private static Mode?[] BuildModes()
    {
        (int Code, bool Transformed, int BaseBits, int[] Deltas, int Regions, string Layout)[] table =
        [
            (0x00, true, 10, [5, 5, 5], 2, "gy4 by4 bz4 rw10 gw10 bw10 rx5 gz4 gy4_ gx5 bz0 gz4_ bx5 bz1 by4_ ry5 bz2 rz5 bz3 d"),
            (0x01, true, 7, [6, 6, 6], 2, "gy5 gz4 gz5 rw7 bz0 bz1 by4 gw7 by5 bz2 gy4 bw7 bz3 bz5 bz4 rx6 gy4_ gx6 gz4_ bx6 by4_ ry6 rz6 d"),
            (0x02, true, 11, [5, 4, 4], 2, "rw10 gw10 bw10 rx5 rw:10 gy4_ gx4 gw:10 bz0 gz4_ bx4 bw:10 bz1 by4_ ry5 bz2 rz5 bz3 d"),
            (0x06, true, 11, [4, 5, 4], 2, "rw10 gw10 bw10 rx4 rw:10 gz4 gy4_ gx5 gw:10 gz4_ bx4 bw:10 bz1 by4_ ry4 bz0 bz2 rz4 gy4 bz3 d"),
            (0x0A, true, 11, [4, 4, 5], 2, "rw10 gw10 bw10 rx4 rw:10 by4 gy4_ gx4 gw:10 bz0 gz4_ bx5 bw:10 by4_ ry4 bz1 bz2 rz4 bz4 bz3 d"),
            (0x0E, true, 9, [5, 5, 5], 2, "rw9 by4 gw9 gy4 bw9 bz4 rx5 gz4 gy4_ gx5 bz0 gz4_ bx5 bz1 by4_ ry5 bz2 rz5 bz3 d"),
            (0x12, true, 8, [6, 5, 5], 2, "rw8 gz4 by4 gw8 bz2 gy4 bw8 bz3 bz4 rx6 gy4_ gx5 bz0 gz4_ bx5 bz1 by4_ ry6 rz6 d"),
            (0x16, true, 8, [5, 6, 5], 2, "rw8 bz0 by4 gw8 gy5 gy4 bw8 gz5 bz4 rx5 gz4 gy4_ gx6 gz4_ bx5 bz1 by4_ ry5 bz2 rz5 bz3 d"),
            (0x1A, true, 8, [5, 5, 6], 2, "rw8 bz1 by4 gw8 by5 gy4 bw8 bz5 bz4 rx5 gz4 gy4_ gx5 bz0 gz4_ bx6 by4_ ry5 bz2 rz5 bz3 d"),
            (0x1E, false, 6, [6, 6, 6], 2, "rw6 gz4 bz0 bz1 by4 gw6 gy5 by5 bz2 gy4 bw6 gz5 bz3 bz5 bz4 rx6 gy4_ gx6 gz4_ bx6 by4_ ry6 rz6 d"),
            (0x03, false, 10, [10, 10, 10], 1, "rw10 gw10 bw10 rx10 gx10 bx10"),
            (0x07, true, 11, [9, 9, 9], 1, "rw10 gw10 bw10 rx9 rw:10 gx9 gw:10 bx9 bw:10"),
            (0x0B, true, 12, [8, 8, 8], 1, "rw10 gw10 bw10 rx8 rw:11-10 gx8 gw:11-10 bx8 bw:11-10"),
            (0x0F, true, 16, [4, 4, 4], 1, "rw10 gw10 bw10 rx4 rw:15-10 gx4 gw:15-10 bx4 bw:15-10"),
        ];
        var modes = new Mode?[table.Length + 1];
        for (int m = 0; m < table.Length; m++)
        {
            var (code, transformed, baseBits, deltas, regions, layout) = table[m];
            var (plan, header) = BuildPlan(code, layout);
            modes[m + 1] = new Mode(code, transformed, baseBits, deltas, regions, plan, header);
        }
        return modes;
    }

    private static (PlanBit[] Plan, int HeaderBits) BuildPlan(int code, string layout)
    {
        var plan = new List<PlanBit>();
        int pos = code < 2 ? 2 : 5;
        foreach (string token in layout.Split(' '))
        {
            if (token == "d")
            {
                for (int bit = 0; bit < 5; bit++) plan.Add(new(-1, -1, bit, pos + bit));
                pos += 5;
                continue;
            }
            (int channel, int slot) = FieldOf(token[..2]);
            if (token.Contains(':'))
            {
                string span = token[(token.IndexOf(':') + 1)..];
                int hi = int.Parse(span.Split('-')[0]), lo = int.Parse(span.Split('-')[^1]);
                for (int bit = hi; bit >= lo; bit--) plan.Add(new(channel, slot, bit, pos++));
            }
            else if (token.EndsWith('_'))
            {
                int count = int.Parse(token[2..^1]);
                for (int bit = 0; bit < count; bit++) plan.Add(new(channel, slot, bit, pos + bit));
                pos += count;
            }
            else
            {
                int number = int.Parse(token[2..]);
                if (token[1] is 'w' or 'x' || (token[0] == 'r' && token[1] is 'y' or 'z'))
                {
                    for (int bit = 0; bit < number; bit++) plan.Add(new(channel, slot, bit, pos + bit));
                    pos += number;
                }
                else plan.Add(new(channel, slot, number, pos++));
            }
        }
        return (plan.ToArray(), pos);
    }

    private static (int Channel, int Slot) FieldOf(string name) =>
        ("rgb".IndexOf(name[0]), "wxyz".IndexOf(name[1]));
}
