using System.Buffers.Binary;

namespace RpfPreview.Core;

/// <summary>
/// BCPack codec 7 (BC7) block rebuilder: the 16 decoded lanes become standard 16-byte BC7 blocks.
/// Port of `_bc7_check`, `_bc7_rebuild` and `_bc7_blocks` in Leonida's `tools/bcpack.py`, run without a
/// BCPack initial-history table.
/// </summary>
/// <remarks>
/// A block whose endpoint or index delta points before the start of the chunk reads BCPack's built-in
/// 256-entry initial history. That table is unpublished and is not part of this decoder, so strict mode
/// refuses such a chunk, and provisional mode decodes it against an all-zero history while at most
/// `max(2, blocks / 1024)` blocks depend on it. Chunks whose delta chains stay inside the chunk decode exactly.
/// </remarks>
internal sealed class Bc7Rebuilder
{
    private const int Ring = 256;
    private const int EndpointValues = 24;
    private const int IndexValues = 32;
    private const int StatusAnchor = 8, StatusPBit = 16, StatusSignature = 32;

    private static readonly int[] Subsets = [3, 2, 3, 2, 1, 1, 1, 2];
    private static readonly int[] ColorBits = [4, 6, 5, 7, 5, 7, 7, 5];
    private static readonly int[] AlphaBits = [0, 0, 0, 0, 6, 8, 7, 5];
    /// <summary>0 = none, 1 = per-endpoint p-bit, 2 = one p-bit shared by a subset.</summary>
    private static readonly int[] PBitKinds = [1, 2, 0, 1, 0, 0, 1, 1];
    /// <summary>Bits of the primary index set (lanes 11..14).</summary>
    private static readonly int[] IndexBits = [3, 3, 2, 2, 3, 2, 4, 2];
    private static readonly int[] PartitionBits = [4, 6, 6, 6, 0, 0, 0, 6];
    /// <summary>Lane holding each channel (R, G, B, A) of each subset's endpoints; -1 when absent.</summary>
    private static readonly int[][] EndpointLanes = [[2, 3, 4, 5], [6, 7, 8, 9], [10, 5, 9, -1]];
    private static readonly int[] ZeroEndpoints = new int[EndpointValues];
    private static readonly int[] ZeroIndices = new int[IndexValues];

    private readonly ushort[][] _lanes;
    private readonly int _blockCount;
    private readonly HashSet<string>? _provisional;
    private readonly byte[] _output;
    private readonly int[][] _endpoints = NewRing(EndpointValues);
    private readonly int[][] _indices = NewRing(IndexValues);
    private readonly int[] _values = new int[3 * 2 * 4];
    private readonly int[] _pBits = new int[3 * 2];
    private readonly int[] _first = new int[16];
    private readonly int[] _second = new int[16];
    private int _badBlocks, _firstBad, _firstStatus;

    private Bc7Rebuilder(ushort[][] lanes, int blockCount, HashSet<string>? provisional)
    {
        _lanes = lanes;
        _blockCount = blockCount;
        _provisional = provisional;
        _output = new byte[checked(blockCount * 16)];
    }

    internal static byte[] Rebuild(ushort[][] lanes, int blockCount, HashSet<string>? provisional, CancellationToken ct)
    {
        if (lanes[0].Length != 2 * blockCount)
            throw new InvalidDataException("BC7 lane length does not match the block count.");
        var rebuilder = new Bc7Rebuilder(lanes, blockCount, provisional);
        rebuilder.CheckSignaturesAndHistory();
        for (int i = 0; i < blockCount; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            rebuilder.RebuildBlock(i);
        }
        rebuilder.CheckConsistency();
        return rebuilder._output;
    }

    private (int Endpoint, int Index) Deltas(int i)
    {
        int a = 2 * i, b = a + 1;
        int index = BcpackDecoder.Interleave(_lanes[1][a], _lanes[1][b], 4);
        int endpoint = (index + BcpackDecoder.DecodeZigZag(BcpackDecoder.Interleave(_lanes[0][a], _lanes[0][b], 4))) & 255;
        return (endpoint, index);
    }

    private int RankOf(int i)
    {
        int a = _lanes[15][2 * i], b = _lanes[15][2 * i + 1];
        return a < 32 && b < BcpackBptcTables.SignatureColumns ? BcpackBptcTables.Bc7RankOf[a * BcpackBptcTables.SignatureColumns + b] : -1;
    }

    /// <summary>
    /// Validates every lane-15 signature, then applies the built-in history rule: strict mode refuses a chunk that
    /// reads the initial history or an inferred signature; provisional mode tags them and bounds the affected blocks.
    /// </summary>
    private void CheckSignaturesAndHistory()
    {
        var config = BcpackBptcTables.Bc7Config;
        var seen = new bool[32 * 32];
        var ranks = new bool[config.Length];
        for (int i = 0; i < _blockCount; i++)
        {
            int a = _lanes[15][2 * i], b = _lanes[15][2 * i + 1];
            if (seen[a * 32 + b]) continue;
            seen[a * 32 + b] = true;
            int rank = RankOf(i);
            if (rank < 0 || config[rank].Mode > 7)
                throw new InvalidDataException($"BC7 signature ({a}, {b}) is outside the signature table.");
            ranks[rank] = true;
        }
        for (int rank = 0; rank < ranks.Length; rank++)
        {
            if (!ranks[rank] || !BcpackBptcTables.Bc7Inferred[rank]) continue;
            if (_provisional is null)
                throw new BcpackProvisionalException($"BC7 signature rank {rank} is inferred-only (unconfirmed).");
            _provisional.Add($"BC7 signature {rank}");
        }

        var needEndpoint = new bool[Ring];
        var needIndex = new bool[Ring];
        for (int i = 0; i < Math.Min(_blockCount, Ring); i++)
        {
            var (endpoint, index) = Deltas(i);
            if (endpoint > i) needEndpoint[256 + i - endpoint] = true;
            if (index > i) needIndex[256 + i - index] = true;
        }
        var unknown = new List<(bool IsIndex, int Entry)>();
        for (int k = 0; k < Ring; k++) if (needEndpoint[k]) unknown.Add((false, k));
        for (int k = 0; k < Ring; k++) if (needIndex[k]) unknown.Add((true, k));
        if (unknown.Count == 0) return;
        if (_provisional is null)
            throw new BcpackProvisionalException(
                $"BC7 chunk references unknown built-in {(unknown[0].IsIndex ? "index" : "endpoint")} history entry {unknown[0].Entry}.");
        LimitUnknownBlocks(unknown);
    }

    /// <summary>Provisional mode: counts the blocks that depend on an unknown history entry and bounds them.</summary>
    private void LimitUnknownBlocks(List<(bool IsIndex, int Entry)> unknown)
    {
        var endpointRoot = new int[Ring];
        var indexRoot = new int[Ring];
        var endpointCount = new int[Ring];
        var indexCount = new int[Ring];
        int total = 0;
        for (int i = 0; i < _blockCount; i++)
        {
            var (endpoint, index) = Deltas(i);
            int slot = i & (Ring - 1);
            endpointRoot[slot] = endpoint == 0 ? -1 : endpoint > i ? 256 + i - endpoint : endpointRoot[(i - endpoint) & (Ring - 1)];
            indexRoot[slot] = index == 0 ? -1 : index > i ? 256 + i - index : indexRoot[(i - index) & (Ring - 1)];
            if (endpointRoot[slot] >= 0) endpointCount[endpointRoot[slot]]++;
            if (indexRoot[slot] >= 0) indexCount[indexRoot[slot]]++;
            if (endpointRoot[slot] >= 0 || indexRoot[slot] >= 0) total++;
        }
        if (total > BcpackDecoder.LooseUnknownBlockLimit(_blockCount))
        {
            string names = string.Join(", ", unknown.Take(6).Select(u => $"{(u.IsIndex ? "index" : "endpoint")} {u.Entry}"));
            throw new BcpackProvisionalException(
                $"BC7 chunk has {total}/{_blockCount} blocks from unknown built-in history entries ({names}{(unknown.Count > 6 ? ", ..." : "")}).");
        }
        foreach (var (isIndex, entry) in unknown)
            _provisional!.Add($"BC7 unknown {(isIndex ? "index" : "endpoint")} history {entry} ({(isIndex ? indexCount : endpointCount)[entry]} blocks)");
    }

    private void RebuildBlock(int i)
    {
        int a = 2 * i, b = a + 1, slot = i & (Ring - 1);
        int[] endpoints = _endpoints[slot], indices = _indices[slot];
        Array.Clear(endpoints);
        Array.Clear(indices);
        int rank = RankOf(i);
        if (rank < 0 || BcpackBptcTables.Bc7Config[rank].Mode > 7)
        {
            Flag(i, StatusSignature);
            return;
        }
        var signature = BcpackBptcTables.Bc7Config[rank];
        var (endpointDelta, indexDelta) = Deltas(i);
        int[] endpointRef = endpointDelta == 0 || i < endpointDelta ? ZeroEndpoints : _endpoints[(i - endpointDelta) & (Ring - 1)];
        int[] indexRef = indexDelta == 0 || i < indexDelta ? ZeroIndices : _indices[(i - indexDelta) & (Ring - 1)];

        int status = ReadEndpoints(signature, a, b, endpointRef, endpoints);
        ReadIndices(signature.Mode, a, b, indexRef, indices);
        if (signature.Mode == 0 && signature.Partition == 0 && !endpoints.Any(v => v != 0) && !_first.Any(v => v != 0))
        {
            Flag(i, status);
            return;
        }
        status |= WriteBlock(i, signature);
        Flag(i, status);
    }

    private void Flag(int i, int status)
    {
        if (status == 0) return;
        if (_badBlocks++ == 0)
        {
            _firstBad = i;
            _firstStatus = status;
        }
    }

    /// <summary>Fills `_values` and `_pBits`, stores the 8-bit scaled endpoints in `state`; returns the p-bit status.</summary>
    private int ReadEndpoints(BcpackBptcTables.Bc7Signature signature, int a, int b, int[] reference, int[] state)
    {
        int mode = signature.Mode, ns = Subsets[mode], pBitKind = PBitKinds[mode], status = 0;
        Span<int> width = [ColorBits[mode], ColorBits[mode], ColorBits[mode], AlphaBits[mode]];
        if (signature.Rotation != 0)
        {
            width[signature.Rotation - 1] = AlphaBits[mode];
            width[3] = ColorBits[mode];
        }
        Array.Clear(_values);
        Array.Clear(_pBits);
        for (int sub = 0; sub < ns; sub++)
        {
            int at = sub * 8;
            for (int c = 0; c < 4; c++)
            {
                int lane = EndpointLanes[sub][c];
                if (width[c] == 0 || lane < 0) continue;
                int bits = width[c] + (pBitKind != 0 && c == 0 ? 1 : 0), shift = 8 - bits, mask = (1 << bits) - 1;
                int v0 = ((reference[at + c] >> shift) + BcpackDecoder.DecodeZigZag(_lanes[lane][a])) & mask;
                int v1 = ((reference[at + 4 + c] >> shift) + BcpackDecoder.DecodeZigZag(_lanes[lane][b])) & mask;
                _values[sub * 8 + c] = v0;
                _values[sub * 8 + 4 + c] = v1;
                state[at + c] = v0 << shift;
                state[at + 4 + c] = v1 << shift;
            }
            if (pBitKind == 1)
            {
                _pBits[sub * 2] = _values[sub * 8] & 1;
                _pBits[sub * 2 + 1] = _values[sub * 8 + 4] & 1;
            }
            else if (pBitKind == 2)
            {
                int shared = _values[sub * 8] & 1;
                _pBits[sub * 2] = shared;
                _pBits[sub * 2 + 1] = shared;
                if ((_values[sub * 8 + 4] & 1) != shared) status |= StatusPBit;
            }
        }
        for (int sub = ns; sub < 3; sub++)
            state.AsSpan(0, 8).CopyTo(state.AsSpan(sub * 8, 8));
        return status;
    }

    /// <summary>Fills `_first`/`_second` (the primary/secondary index sets) and stores them, scaled to 4 bits, in `state`.</summary>
    private void ReadIndices(int mode, int a, int b, int[] reference, int[] state)
    {
        int bits = IndexBits[mode], shift = 4 - bits, mask = (1 << bits) - 1;
        bool two = mode is 4 or 5;
        int[] secondLanes = [6, 7, 8, 10];
        for (int y = 0; y < 4; y++)
        {
            ReadIndexRow(_lanes[11 + y], a, b, y, reference, 0, shift, mask, _first, state);
            if (two) ReadIndexRow(_lanes[secondLanes[y]], a, b, y, reference, 16, 2, 3, _second, state);
        }
    }

    private static void ReadIndexRow(ushort[] lane, int a, int b, int y, int[] reference, int stateOffset, int shift, int mask, int[] values, int[] state)
    {
        var (d0, d1, d2, d3) = BcpackDecoder.DeinterleaveRow(lane[a], lane[b], y);
        for (int off = 0; off < 4; off++)
        {
            int p = y * 4 + off;
            int delta = off switch { 0 => d0, 1 => d1, 2 => d2, _ => d3 };
            int index = ((reference[stateOffset + p] >> shift) + BcpackDecoder.DecodeZigZag(delta)) & mask;
            values[p] = index;
            state[stateOffset + p] = index << shift;
        }
    }

    /// <summary>Packs the block header, endpoints, p-bits and index bits; returns the anchor/length status flags.</summary>
    private int WriteBlock(int i, BcpackBptcTables.Bc7Signature signature)
    {
        int mode = signature.Mode, ns = Subsets[mode], colorBits = ColorBits[mode], alphaBits = AlphaBits[mode];
        int pBitKind = PBitKinds[mode], rotation = signature.Rotation, partition = signature.Partition, status = 0;
        UInt128 block = UInt128.One << mode;
        int pos = mode + 1;
        Put(ref block, ref pos, partition, PartitionBits[mode]);
        if (mode is 4 or 5) Put(ref block, ref pos, rotation, 2);
        if (mode == 4) Put(ref block, ref pos, signature.IndexSelection, 1);

        for (int c = 0; c < 3; c++)
            PutEndpoints(ref block, ref pos, rotation != 0 && c == rotation - 1 ? 3 : c, ns, colorBits, pBitKind);
        if (alphaBits != 0)
            PutEndpoints(ref block, ref pos, rotation != 0 ? rotation - 1 : 3, ns, alphaBits, pBitKind);
        for (int sub = 0; sub < ns; sub++)
        {
            if (pBitKind == 1) { Put(ref block, ref pos, _pBits[sub * 2], 1); Put(ref block, ref pos, _pBits[sub * 2 + 1], 1); }
            else if (pBitKind == 2) Put(ref block, ref pos, _pBits[sub * 2], 1);
        }

        byte[]? subsetMap = ns == 2 ? BcpackBptcTables.Partition2[partition] : ns == 3 ? BcpackBptcTables.Partition3[partition] : null;
        int[] anchors = ns == 1 ? [0] : ns == 2 ? [0, BcpackBptcTables.Anchor2[partition]]
            : [0, BcpackBptcTables.Anchor3A[partition], BcpackBptcTables.Anchor3B[partition]];
        int[] primary = mode == 4 ? _second : _first;
        int primaryBits = mode is 4 or 5 ? 2 : IndexBits[mode];
        for (int p = 0; p < 16; p++)
        {
            bool anchor = p == anchors[subsetMap is null ? 0 : subsetMap[p]];
            if (anchor && (primary[p] >> (primaryBits - 1)) != 0) status |= StatusAnchor;
            Put(ref block, ref pos, primary[p], anchor ? primaryBits - 1 : primaryBits);
        }
        if (mode is 4 or 5)
        {
            int[] secondary = mode == 4 ? _first : _second;
            int secondaryBits = mode == 4 ? 3 : 2;
            for (int p = 0; p < 16; p++)
            {
                if (p == 0 && (secondary[p] >> (secondaryBits - 1)) != 0) status |= StatusAnchor;
                Put(ref block, ref pos, secondary[p], p == 0 ? secondaryBits - 1 : secondaryBits);
            }
        }
        if (pos != 128) status |= StatusSignature;
        BinaryPrimitives.WriteUInt128LittleEndian(_output.AsSpan(16 * i, 16), block);
        return status;
    }

    /// <summary>Writes one channel's endpoint values for every subset; the shared low bit moves to the p-bit.</summary>
    private void PutEndpoints(ref UInt128 block, ref int pos, int channel, int ns, int bits, int pBitKind)
    {
        bool dropLowBit = pBitKind != 0 && channel == 0;
        for (int sub = 0; sub < ns; sub++)
            for (int e = 0; e < 2; e++)
            {
                int v = _values[sub * 8 + e * 4 + channel];
                Put(ref block, ref pos, dropLowBit ? v >> 1 : v, bits);
            }
    }

    private static void Put(ref UInt128 block, ref int pos, int value, int bits)
    {
        if (bits <= 0) return;
        block |= (UInt128)(uint)(value & ((1 << bits) - 1)) << pos;
        pos += bits;
    }

    private void CheckConsistency()
    {
        if (_badBlocks == 0) return;
        if ((_firstStatus & (StatusAnchor | StatusPBit)) == 0)
            throw new InvalidDataException($"BC7 chunk failed signature check ({_badBlocks}/{_blockCount} blocks; first: block {_firstBad}).");
        if (_provisional is null || _provisional.Count == 0)
            throw new InvalidDataException($"BC7 chunk failed anchor/p-bit consistency check ({_badBlocks}/{_blockCount} blocks; first: block {_firstBad}).");
        if (_badBlocks > BcpackDecoder.LooseUnknownBlockLimit(_blockCount))
            throw new BcpackProvisionalException($"BC7 chunk failed anchor/p-bit consistency check in {_badBlocks}/{_blockCount} blocks.");
        _provisional.Add($"BC7 consistency check failed in {_badBlocks} blocks");
    }

    private static int[][] NewRing(int length)
    {
        var ring = new int[Ring][];
        for (int i = 0; i < Ring; i++) ring[i] = new int[length];
        return ring;
    }
}
