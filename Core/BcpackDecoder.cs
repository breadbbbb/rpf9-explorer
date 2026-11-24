using System.Buffers.Binary;
using System.Collections.Frozen;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace RpfPreview.Core;

public sealed class BcpackProvisionalException : NotSupportedException
{
    public BcpackProvisionalException(string message) : base(message) { }
}

public sealed record BcpackDecodeResult(byte[] Data, IReadOnlySet<string> ProvisionalEntries);







public static class BcpackDecoder
{
    public const uint RansL = 1u << 24;
    public const int MaxWidth = 10;
    public const int PrefetchBudget = 8;
    public const int MaxVirtualZeros = 8;
    public const int ElementBytes = 8;
    public const int MaxOutputBytes = 256 * 1024 * 1024;

    private readonly record struct CodecSpec(int Id, string Name, int BlockBytes, int[] LaneWidths, bool SupportsRebuild);

    private static readonly CodecSpec[] CodecSpecs = BuildCodecSpecs();
    private static readonly RansModel[][] Models = BuildModels();
    private static readonly IReadOnlySet<string> EmptyProvisional = FrozenSet<string>.Empty;

    public static bool IsSupportedCodec(int codec) => TryGetCodecSpec(codec, out var spec) && spec.SupportsRebuild;

    public static bool IsEntropyCodec(int codec) => TryGetCodecSpec(codec, out _);

    public static string GetCodecName(int codec) => codec switch
    {
        2 => "BC2",
        _ => TryGetCodecSpec(codec, out var spec) ? spec.Name : $"codec {codec}"
    };

    public static int GetBlockBytes(int codec) =>
        TryGetCodecSpec(codec, out var spec)
            ? spec.BlockBytes
            : throw new NotSupportedException($"Xbox BCPack codec {codec} ({GetCodecName(codec)}) is not supported.");

    private static bool TryGetCodecSpec(int codec, out CodecSpec spec)
    {
        if ((uint)codec < (uint)CodecSpecs.Length && CodecSpecs[codec].BlockBytes != 0)
        {
            spec = CodecSpecs[codec];
            return true;
        }
        spec = default;
        return false;
    }

    private static CodecSpec[] BuildCodecSpecs()
    {
        var specs = new CodecSpec[8];
        specs[1] = new(1, "BC1", 8, [8, 8, 5, 6, 5, 5, 6, 5, 8, 8, 8, 8], true);
        specs[3] = new(3, "BC3", 16, [4, 4, 8, 6, 6, 6, 6, 5, 6, 5, 8, 8], true);
        specs[4] = new(4, "BC4", 8, [8, 8, 8, 8, 6, 6, 6, 6, 6, 6, 6, 6], true);
        specs[5] = new(5, "BC5", 16, [4, 4, 8, 8, 6, 6, 6, 6, 6, 6, 6, 6], true);
        specs[6] = new(6, "BC6H", 16, [4, 4, 8, 8, 8, 5, 5, 5, 6, 6, 6, 8, 8, 8, 8, 5], true);
        specs[7] = new(7, "BC7", 16, [4, 4, 8, 8, 8, 8, 8, 7, 7, 5, 6, 8, 8, 8, 8, 5], true);
        return specs;
    }





    public static byte[] Decode(
        ReadOnlySpan<byte> packed,
        int codec,
        int outputSize,
        bool allowProvisional = false,
        CancellationToken cancellationToken = default)
        => DecodeWithInfo(packed, codec, outputSize, allowProvisional, null, cancellationToken).Data;





    public static byte[] Decode(
        ReadOnlySpan<byte> packed,
        int codec,
        int outputSize,
        bool allowProvisional,
        BcpackHistory? history,
        CancellationToken cancellationToken = default)
        => DecodeWithInfo(packed, codec, outputSize, allowProvisional, history, cancellationToken).Data;





    public static BcpackDecodeResult DecodeWithInfo(
        ReadOnlySpan<byte> packed,
        int codec,
        int outputSize,
        bool allowProvisional = false,
        CancellationToken cancellationToken = default)
        => DecodeWithInfo(packed, codec, outputSize, allowProvisional, null, cancellationToken);





    public static BcpackDecodeResult DecodeWithInfo(
        ReadOnlySpan<byte> packed,
        int codec,
        int outputSize,
        bool allowProvisional,
        BcpackHistory? history,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetCodecSpec(codec, out var spec) || !spec.SupportsRebuild)
            throw new NotSupportedException($"Xbox BCPack codec {codec} ({GetCodecName(codec)}) is not supported.");

        ValidateOutputSize(spec, outputSize);
        var (lanes, _) = DecodeLanes(packed, spec, outputSize, cancellationToken);
        int blockCount = outputSize / spec.BlockBytes;
        HashSet<string>? provisional = allowProvisional ? new HashSet<string>(StringComparer.Ordinal) : null;

        byte[] data = spec.Id switch
        {
            1 => RebuildBc1(lanes, blockCount, provisional, history?.Bc1 ?? BcpackTables.Bc1History, cancellationToken),
            3 => RebuildBc3(lanes, blockCount, provisional, history?.Bc3 ?? BcpackTables.Bc3History, cancellationToken),
            4 => RebuildBc4(lanes, blockCount, provisional, history?.Bc4 ?? BcpackTables.Bc4History, cancellationToken),
            5 => RebuildBc5(lanes, blockCount, provisional, history?.Bc5 ?? BcpackTables.Bc5History, cancellationToken),
            6 => RebuildBc6h(lanes, blockCount, provisional, history?.Bc6h ?? Bc6hHistoryTable.Empty, history is not null, cancellationToken),
            7 => RebuildBc7(lanes, blockCount, provisional, history?.Bc7 ?? Bc7HistoryTable.Empty, history is not null, cancellationToken),
            _ => throw new NotSupportedException($"Xbox BCPack codec {codec} ({GetCodecName(codec)}) is not supported.")
        };

        return new BcpackDecodeResult(data, provisional is not null ? provisional : EmptyProvisional);
    }





    internal static (ushort[][] Lanes, int BytesUsed) DecodeLanes(
        ReadOnlySpan<byte> packed,
        int codec,
        int outputSize,
        CancellationToken cancellationToken = default)
    {
        if (!TryGetCodecSpec(codec, out var spec))
            throw new NotSupportedException($"Xbox BCPack codec {codec} ({GetCodecName(codec)}) is not supported.");

        ValidateOutputSize(spec, outputSize);
        return DecodeLanes(packed, spec, outputSize, cancellationToken);
    }

    private static void ValidateOutputSize(CodecSpec spec, int outputSize)
    {
        if (outputSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(outputSize), outputSize, "Decoded BCPack output size must be positive.");
        if (outputSize > MaxOutputBytes)
            throw new NotSupportedException($"Decoded BCPack size {outputSize} exceeds the {MaxOutputBytes / (1024 * 1024)} MiB output limit.");
        if (outputSize % spec.BlockBytes != 0)
            throw new InvalidDataException($"Decoded size {outputSize} is not a whole number of {spec.Name} blocks.");
    }

    private static (ushort[][] Lanes, int BytesUsed) DecodeLanes(
        ReadOnlySpan<byte> packed,
        CodecSpec spec,
        int outputSize,
        CancellationToken cancellationToken)
    {
        int symbolsPerLane = outputSize / ElementBytes;
        var (lanes, bytesUsed) = DecodeLanesRaw(packed, spec.LaneWidths, symbolsPerLane, cancellationToken);

        if (bytesUsed < packed.Length && packed[bytesUsed..].IndexOfAnyExcept((byte)0) >= 0)
            throw new InvalidDataException("BCPack stream has non-zero data left over after the last lane finished.");

        return (lanes, bytesUsed);
    }

    private static (ushort[][] Lanes, int BytesUsed) DecodeLanesRaw(
        ReadOnlySpan<byte> raw,
        ReadOnlySpan<int> defaultWidths,
        int symbolsPerLane,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        const byte HeaderByte0 = 0;
        const byte HeaderByte1 = 1;
        const byte HeaderByte2 = 2;
        const byte ProcessPayload = 3;

        int laneCount = defaultWidths.Length;
        Span<byte> controlState = stackalloc byte[laneCount];
        Span<int> symbolsRemaining = stackalloc int[laneCount];
        Span<int> symbolWidth = stackalloc int[laneCount];
        Span<int> modelQ = stackalloc int[laneCount];
        Span<byte> isThreeByteHeader = stackalloc byte[laneCount];
        Span<byte> isFlushFragment = stackalloc byte[laneCount];
        Span<bool> laneDone = stackalloc bool[laneCount];
        Span<bool> inputFull = stackalloc bool[laneCount];
        Span<bool> symbolHeld = stackalloc bool[laneCount];
        Span<byte> virtualZeroCount = stackalloc byte[laneCount];
        Span<byte> inputByte = stackalloc byte[laneCount];
        Span<int> decodedSymbol = stackalloc int[laneCount];
        Span<int> payloadBytesRemaining = stackalloc int[laneCount];
        Span<ulong> stateBeforeMerge = stackalloc ulong[laneCount];
        Span<ulong> stateAfterStep = stackalloc ulong[laneCount];
        Span<bool> outputSlotFull = stackalloc bool[laneCount];
        Span<ushort> outputSlotValue = stackalloc ushort[laneCount];
        Span<bool> laneStalled = stackalloc bool[laneCount];
        Span<int> outputCount = stackalloc int[laneCount];

        var models = new RansModel?[laneCount];
        var output = new ushort[laneCount][];
        for (int i = 0; i < laneCount; i++)
        {
            controlState[i] = HeaderByte0;
            symbolsRemaining[i] = symbolsPerLane;
            laneDone[i] = symbolsPerLane <= 0;
            output[i] = new ushort[symbolsPerLane];
        }

        int inputPos = 0;
        int inputLength = raw.Length;
        uint cycle = 0;

        static void Fail(string msg, int lane, int width, int qVal, int bytePos)
            => throw new InvalidDataException($"BCPack lane {lane} (width {width}, q {qVal}) at input byte {bytePos}: {msg}");

        while (true)
        {
            if ((cycle++ & 0xFFFFu) == 0)
                cancellationToken.ThrowIfCancellationRequested();

            bool allDone = true;
            for (int i = 0; i < laneCount; i++)
            {
                if (!laneDone[i] || outputSlotFull[i])
                {
                    allDone = false;
                    break;
                }
            }
            if (allDone)
                return (output, inputPos);



            int prefetchRemaining = PrefetchBudget;
            for (int i = 0; i < laneCount; i++)
            {
                if (laneDone[i] || (symbolHeld[i] && outputSlotFull[i]))
                {
                    laneStalled[i] = true;
                    continue;
                }
                laneStalled[i] = false;
                if (controlState[i] == ProcessPayload)
                {
                    stateBeforeMerge[i] = symbolHeld[i] ? models[i]!.Advance(decodedSymbol[i], stateAfterStep[i]) : stateAfterStep[i];
                }
                if (!inputFull[i] && (controlState[i] != ProcessPayload || payloadBytesRemaining[i] > 0))
                {
                    if (prefetchRemaining == 0)
                    {
                        laneStalled[i] = true;
                        continue;
                    }
                    if (inputPos >= inputLength)
                        Fail("input ran out", i, symbolWidth[i], modelQ[i], inputPos);
                    inputByte[i] = raw[inputPos++];
                    inputFull[i] = true;
                    prefetchRemaining--;
                }
                if (symbolHeld[i])
                {
                    outputSlotFull[i] = true;
                    outputSlotValue[i] = (ushort)decodedSymbol[i];
                    symbolHeld[i] = false;
                }
            }


            for (int i = 0; i < laneCount; i++)
            {
                if (laneDone[i] || laneStalled[i])
                    continue;

                byte ctrl = controlState[i];
                if (ctrl == HeaderByte0)
                {
                    if (!inputFull[i]) continue;
                    byte hdr0 = inputByte[i];
                    modelQ[i] = hdr0 & 15;
                    isFlushFragment[i] = (byte)((hdr0 >> 4) & 1);
                    isThreeByteHeader[i] = (byte)((hdr0 >> 5) & 1);
                    symbolWidth[i] = defaultWidths[i] - (hdr0 >> 6);
                    inputFull[i] = false;
                    controlState[i] = HeaderByte1;
                    virtualZeroCount[i] = 0;
                }
                else if (ctrl == HeaderByte1)
                {
                    if (!inputFull[i]) continue;
                    inputFull[i] = false;
                    int width = symbolWidth[i];
                    if (width < 1 || width > MaxWidth)
                        Fail("symbol width out of range", i, width, modelQ[i], inputPos);
                    models[i] = Models[width - 1][modelQ[i]];
                    if (isThreeByteHeader[i] != 0)
                    {
                        payloadBytesRemaining[i] = inputByte[i];
                        controlState[i] = HeaderByte2;
                    }
                    else
                    {
                        payloadBytesRemaining[i] = inputByte[i] + 1;
                        controlState[i] = ProcessPayload;
                    }
                }
                else if (ctrl == HeaderByte2)
                {
                    if (!inputFull[i]) continue;
                    payloadBytesRemaining[i] = ((inputByte[i] << 8) | (payloadBytesRemaining[i] & 0xFF)) + 257;
                    controlState[i] = ProcessPayload;
                    inputFull[i] = false;
                }
                else
                {
                    ulong state = stateBeforeMerge[i];
                    if (state < RansL)
                    {
                        if (payloadBytesRemaining[i] > 0)
                        {
                            if (inputFull[i])
                            {
                                state = (state << 8) | inputByte[i];
                                payloadBytesRemaining[i]--;
                                inputFull[i] = false;
                                if (payloadBytesRemaining[i] == 0 && isFlushFragment[i] != 0)
                                {
                                    while (state < RansL && state != 0 && virtualZeroCount[i] < MaxVirtualZeros)
                                    {
                                        state <<= 8;
                                        virtualZeroCount[i]++;
                                    }
                                }
                            }
                        }
                        else if (isFlushFragment[i] != 0)
                        {
                            if (state == 0 || virtualZeroCount[i] >= MaxVirtualZeros)
                                Fail("fragment did not end at its home state", i, symbolWidth[i], modelQ[i], inputPos);
                            while (state < RansL && state != 0 && virtualZeroCount[i] < MaxVirtualZeros)
                            {
                                state <<= 8;
                                virtualZeroCount[i]++;
                            }
                        }
                        else
                        {
                            stateAfterStep[i] = state;
                            if (symbolsRemaining[i] > 0)
                                controlState[i] = HeaderByte0;
                            else
                                laneDone[i] = true;
                            continue;
                        }
                    }

                    bool hasNextSymbol = state > RansL && symbolsRemaining[i] > 0;
                    if (hasNextSymbol)
                    {
                        int sym = models[i]!.Lookup((int)(state & 0xFFFFu));
                        decodedSymbol[i] = sym;
                        symbolsRemaining[i]--;
                        if ((sym >> symbolWidth[i]) != 0)
                            Fail("symbol outside its width", i, symbolWidth[i], modelQ[i], inputPos);
                    }
                    symbolHeld[i] = hasNextSymbol;
                    stateAfterStep[i] = state;

                    if (payloadBytesRemaining[i] == 0)
                    {
                        if (isFlushFragment[i] != 0)
                        {
                            if (state == RansL)
                            {
                                if (symbolsRemaining[i] > 0)
                                {
                                    controlState[i] = HeaderByte0;
                                    stateBeforeMerge[i] = 0;
                                    stateAfterStep[i] = 0;
                                }
                                else
                                {
                                    laneDone[i] = true;
                                }
                            }
                            else if (state > RansL && !hasNextSymbol)
                            {
                                Fail("fragment did not end at its home state", i, symbolWidth[i], modelQ[i], inputPos);
                            }
                        }
                        else if (symbolsRemaining[i] == 0 && !hasNextSymbol)
                        {
                            laneDone[i] = true;
                        }
                        else if (state == RansL)
                        {
                            Fail("fragment did not end at its home state", i, symbolWidth[i], modelQ[i], inputPos);
                        }
                    }
                    else if (state == RansL || (symbolsRemaining[i] == 0 && !hasNextSymbol && state > RansL))
                    {
                        Fail("fragment did not end at its home state", i, symbolWidth[i], modelQ[i], inputPos);
                    }
                }
            }


            bool ready = true;
            bool anyFull = false;
            for (int i = 0; i < laneCount; i++)
            {
                if (outputSlotFull[i])
                {
                    anyFull = true;
                }
                else if (!(laneDone[i] && !symbolHeld[i]))
                {
                    ready = false;
                    break;
                }
            }
            if (ready && anyFull)
            {
                for (int i = 0; i < laneCount; i++)
                {
                    if (outputSlotFull[i])
                    {
                        int idx = outputCount[i];
                        if ((uint)idx >= (uint)symbolsPerLane)
                            Fail("produced more symbols than expected", i, symbolWidth[i], modelQ[i], inputPos);
                        output[i][idx] = outputSlotValue[i];
                        outputCount[i] = idx + 1;
                        outputSlotFull[i] = false;
                    }
                }
            }
            for (int i = 0; i < laneCount; i++)
            {
                if (laneDone[i] && symbolHeld[i] && !outputSlotFull[i])
                {
                    outputSlotFull[i] = true;
                    outputSlotValue[i] = (ushort)decodedSymbol[i];
                    symbolHeld[i] = false;
                }
            }
        }
    }

    private static void EnsureZeroLane1(ushort[] lane1, string codecName)
    {
        if (lane1.AsSpan().IndexOfAnyExcept((ushort)0) >= 0)
            throw new InvalidDataException($"{codecName} lane 1 is non-zero (unexpected).");
    }

    private static bool ResolveBuiltinHistoryStatus(
        BcpackHistoryStatus status,
        string codecName,
        int blockIndex,
        int entryIndex,
        HashSet<string>? provisional,
        bool allowUnknownFallback,
        ref SortedSet<int>? unknownEntries)
    {
        if (status == BcpackHistoryStatus.Confirmed)
            return true;

        if (status == BcpackHistoryStatus.Provisional)
        {
            if (provisional is null)
                throw new BcpackProvisionalException($"{codecName} block {blockIndex} references unconfirmed built-in history entry {entryIndex}.");
            provisional.Add($"{codecName} history {entryIndex}");
            return true;
        }

        if (provisional is null || !allowUnknownFallback)
            throw new BcpackProvisionalException($"{codecName} block {blockIndex} references unknown built-in history entry {entryIndex}.");

        unknownEntries ??= new SortedSet<int>();
        unknownEntries.Add(entryIndex);
        return false;
    }

    private static void FinalizeUnknownHistory(
        SortedSet<int>? unknownEntries,
        int lostBlocks,
        int blockCount,
        string codecName,
        HashSet<string>? provisional)
    {
        if (unknownEntries is null)
            return;

        if (lostBlocks > LooseUnknownBlockLimit(blockCount))
            throw new BcpackProvisionalException($"{codecName} chunk has {lostBlocks}/{blockCount} blocks from unknown built-in history entries.");

        provisional!.Add($"{codecName} unknown history [{string.Join(", ", unknownEntries)}] ({lostBlocks} blocks)");
    }

    private static byte[] RebuildBc1(ushort[][] lanes, int blockCount, HashSet<string>? provisional, Bc1HistoryEntry[] builtin, CancellationToken ct)
    {
        EnsureZeroLane1(lanes[1], "BC1");

        byte[] quad = BcpackTables.Bc1Quadrant;
        byte[] output = new byte[checked(blockCount * 8)];
        Span<Bc1Endpoints> history = stackalloc Bc1Endpoints[256];
        SortedSet<int>? unusedUnknown = null;

        ushort[] refLane = lanes[0];
        ushort[] r0Lane = lanes[2], g0Lane = lanes[3], b0Lane = lanes[4];
        ushort[] r1Lane = lanes[5], g1Lane = lanes[6], b1Lane = lanes[7];
        ushort[] q0Lane = lanes[8], q1Lane = lanes[9], q2Lane = lanes[10], q3Lane = lanes[11];

        for (int i = 0; i < blockCount; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            int v = refLane[i];
            int dist = (v & 1) != 0 ? 256 - ((v + 1) >> 1) : (v >> 1);
            Bc1Endpoints pred;
            if (dist == 0)
            {
                pred = default;
            }
            else if (i >= dist)
            {
                pred = history[(i - dist) & 255];
            }
            else
            {
                int entry = 256 + i - dist;
                var h = builtin[entry];
                ResolveBuiltinHistoryStatus(h.Status, "BC1", i, entry, provisional, allowUnknownFallback: false, ref unusedUnknown);
                pred = new(h.R0, h.G0, h.B0, h.R1, h.G1, h.B1);
            }

            byte e0 = (byte)((pred.R0 + DecodeZigZag(r0Lane[i])) & 31);
            byte e1 = (byte)((pred.G0 + DecodeZigZag(g0Lane[i])) & 63);
            byte e2 = (byte)((pred.B0 + DecodeZigZag(b0Lane[i])) & 31);
            byte e3 = (byte)((pred.R1 + DecodeZigZag(r1Lane[i])) & 31);
            byte e4 = (byte)((pred.G1 + DecodeZigZag(g1Lane[i])) & 63);
            byte e5 = (byte)((pred.B1 + DecodeZigZag(b1Lane[i])) & 31);
            history[i & 255] = new(e0, e1, e2, e3, e4, e5);

            int at = i * 8;
            ushort col0 = (ushort)((e0 << 11) | (e1 << 5) | e2);
            ushort col1 = (ushort)((e3 << 11) | (e4 << 5) | e5);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(at, 2), col0);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(at + 2, 2), col1);

            byte qa = quad[q0Lane[i]], qb = quad[q1Lane[i]], qc = quad[q2Lane[i]], qd = quad[q3Lane[i]];
            output[at + 4] = (byte)((qa & 15) | ((qb & 15) << 4));
            output[at + 5] = (byte)((qa >> 4) | ((qb >> 4) << 4));
            output[at + 6] = (byte)((qc & 15) | ((qd & 15) << 4));
            output[at + 7] = (byte)((qc >> 4) | ((qd >> 4) << 4));
        }

        return output;
    }

    private static byte[] RebuildBc3(ushort[][] lanes, int blockCount, HashSet<string>? provisional, Bc3HistoryEntry[] builtin, CancellationToken ct)
    {
        EnsureZeroLane1(lanes[1], "BC3");

        byte[] pairs = BcpackTables.Bc3AlphaPair;
        byte[] quads = BcpackTables.Bc3ColourQuadrant;
        byte[] output = new byte[checked(blockCount * 16)];
        Span<Bc3Endpoints> history = stackalloc Bc3Endpoints[256];
        Span<bool> lostHistory = stackalloc bool[256];
        int lostTotal = 0;
        SortedSet<int>? unknownEntries = null;

        ushort[] refLane = lanes[0], alphaLane = lanes[2];
        ushort[] row0 = lanes[3], row1 = lanes[4], row2 = lanes[5], row3 = lanes[6];
        ushort[] rLane = lanes[7], gLane = lanes[8], bLane = lanes[9];
        ushort[] topQuad = lanes[10], bottomQuad = lanes[11];
        Bc3Endpoints defaultEndpoints = new(128, 128, 0, 0, 0, 0, 0, 0);

        for (int i = 0; i < blockCount; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            int a = 2 * i, bIdx = a + 1;
            int dist = DecodeZigZag(InterleaveNibbles(refLane[a], refLane[bIdx])) & 255;
            Bc3Endpoints pred;
            bool isLost = false;
            if (dist == 0)
            {
                pred = defaultEndpoints;
            }
            else if (i >= dist)
            {
                int prev = (i - dist) & 255;
                pred = history[prev];
                isLost = lostHistory[prev];
            }
            else
            {
                int entry = 256 + i - dist;
                var h = builtin[entry];
                if (ResolveBuiltinHistoryStatus(h.Status, "BC3", i, entry, provisional, allowUnknownFallback: true, ref unknownEntries))
                {
                    pred = new(h.A0, h.A1, h.R0, h.G0, h.B0, h.R1, h.G1, h.B1);
                }
                else
                {
                    pred = defaultEndpoints;
                    isLost = true;
                }
            }

            lostHistory[i & 255] = isLost;
            if (isLost) lostTotal++;

            byte ea0 = (byte)((pred.A0 + DecodeZigZag(alphaLane[a])) & 255);
            byte ea1 = (byte)((pred.A1 + DecodeZigZag(alphaLane[bIdx])) & 255);
            byte er0 = (byte)((pred.R0 + DecodeZigZag(rLane[a])) & 31);
            byte eg0 = (byte)((pred.G0 + DecodeZigZag(gLane[a])) & 63);
            byte eb0 = (byte)((pred.B0 + DecodeZigZag(bLane[a])) & 31);
            byte er1 = (byte)((pred.R1 + DecodeZigZag(rLane[bIdx])) & 31);
            byte eg1 = (byte)((pred.G1 + DecodeZigZag(gLane[bIdx])) & 63);
            byte eb1 = (byte)((pred.B1 + DecodeZigZag(bLane[bIdx])) & 31);
            history[i & 255] = new(ea0, ea1, er0, eg0, eb0, er1, eg1, eb1);

            ulong alphaBits =
                PackRow12(pairs[row0[a]], pairs[row0[bIdx]])
                | (PackRow12(pairs[row1[a]], pairs[row1[bIdx]]) << 12)
                | (PackRow12(pairs[row2[a]], pairs[row2[bIdx]]) << 24)
                | (PackRow12(pairs[row3[a]], pairs[row3[bIdx]]) << 36);

            int at = i * 16;
            output[at] = ea0;
            output[at + 1] = ea1;
            WriteUInt48LittleEndian(output.AsSpan(at + 2, 6), alphaBits);

            ushort col0 = (ushort)((er0 << 11) | (eg0 << 5) | eb0);
            ushort col1 = (ushort)((er1 << 11) | (eg1 << 5) | eb1);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(at + 8, 2), col0);
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(at + 10, 2), col1);

            byte qa = quads[topQuad[a]], qb = quads[topQuad[bIdx]];
            byte qc = quads[bottomQuad[a]], qd = quads[bottomQuad[bIdx]];
            output[at + 12] = (byte)((qa & 15) | ((qb & 15) << 4));
            output[at + 13] = (byte)((qa >> 4) | ((qb >> 4) << 4));
            output[at + 14] = (byte)((qc & 15) | ((qd & 15) << 4));
            output[at + 15] = (byte)((qc >> 4) | ((qd >> 4) << 4));
        }

        FinalizeUnknownHistory(unknownEntries, lostTotal, blockCount, "BC3", provisional);
        return output;
    }

    private static byte[] RebuildBc4(ushort[][] lanes, int blockCount, HashSet<string>? provisional, Bc4HistoryEntry[] builtin, CancellationToken ct)
    {
        EnsureZeroLane1(lanes[1], "BC4");

        byte[] pairs = BcpackTables.Bc4Pair;
        byte[] output = new byte[checked(blockCount * 8)];
        Span<Bc4Endpoints> history = stackalloc Bc4Endpoints[256];
        SortedSet<int>? unusedUnknown = null;

        ushort[] refLane = lanes[0], a0Lane = lanes[2], a1Lane = lanes[3];
        ushort[] r0L = lanes[4], r0R = lanes[5], r1L = lanes[6], r1R = lanes[7];
        ushort[] r2L = lanes[8], r2R = lanes[9], r3L = lanes[10], r3R = lanes[11];

        for (int i = 0; i < blockCount; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            int v = refLane[i];
            int dist = (v & 1) != 0 ? 256 - ((v + 1) >> 1) : (v >> 1);
            Bc4Endpoints pred;
            if (dist == 0)
            {
                pred = new(128, 128);
            }
            else if (i >= dist)
            {
                pred = history[(i - dist) & 255];
            }
            else
            {
                int entry = 256 + i - dist;
                var h = builtin[entry];
                ResolveBuiltinHistoryStatus(h.Status, "BC4", i, entry, provisional, allowUnknownFallback: false, ref unusedUnknown);
                pred = new(h.A0, h.A1);
            }

            byte a0 = (byte)((pred.A0 + DecodeZigZag(a0Lane[i])) & 255);
            byte a1 = (byte)((pred.A1 + DecodeZigZag(a1Lane[i])) & 255);
            history[i & 255] = new(a0, a1);

            ulong indexBits =
                PackRow12(pairs[r0L[i]], pairs[r0R[i]])
                | (PackRow12(pairs[r1L[i]], pairs[r1R[i]]) << 12)
                | (PackRow12(pairs[r2L[i]], pairs[r2R[i]]) << 24)
                | (PackRow12(pairs[r3L[i]], pairs[r3R[i]]) << 36);

            int at = i * 8;
            output[at] = a0;
            output[at + 1] = a1;
            WriteUInt48LittleEndian(output.AsSpan(at + 2, 6), indexBits);
        }

        return output;
    }

    private static byte[] RebuildBc5(ushort[][] lanes, int blockCount, HashSet<string>? provisional, Bc5HistoryEntry[] builtin, CancellationToken ct)
    {
        EnsureZeroLane1(lanes[1], "BC5");

        byte[] pairs = BcpackTables.Bc5Pair;
        byte[] output = new byte[checked(blockCount * 16)];
        Span<Bc5Endpoints> history = stackalloc Bc5Endpoints[256];
        Span<bool> lostHistory = stackalloc bool[256];
        int lostTotal = 0;
        SortedSet<int>? unknownEntries = null;

        ushort[] refLane = lanes[0], redLane = lanes[2], greenLane = lanes[3];
        ushort[] r0 = lanes[4], r1 = lanes[5], r2 = lanes[6], r3 = lanes[7];
        ushort[] g0 = lanes[8], g1 = lanes[9], g2 = lanes[10], g3 = lanes[11];
        Bc5Endpoints defaultEndpoints = new(128, 128, 128, 128);

        for (int i = 0; i < blockCount; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            int a = 2 * i, bIdx = a + 1;
            int dist = DecodeZigZag(InterleaveNibbles(refLane[a], refLane[bIdx])) & 255;
            Bc5Endpoints pred;
            bool isLost = false;
            if (dist == 0)
            {
                pred = defaultEndpoints;
            }
            else if (i >= dist)
            {
                int prev = (i - dist) & 255;
                pred = history[prev];
                isLost = lostHistory[prev];
            }
            else
            {
                int entry = 256 + i - dist;
                var h = builtin[entry];
                if (ResolveBuiltinHistoryStatus(h.Status, "BC5", i, entry, provisional, allowUnknownFallback: true, ref unknownEntries))
                {
                    pred = new(h.R0, h.R1, h.G0, h.G1);
                }
                else
                {
                    pred = defaultEndpoints;
                    isLost = true;
                }
            }

            lostHistory[i & 255] = isLost;
            if (isLost) lostTotal++;

            byte er0 = (byte)((pred.R0 + DecodeZigZag(redLane[a])) & 255);
            byte er1 = (byte)((pred.R1 + DecodeZigZag(redLane[bIdx])) & 255);
            byte eg0 = (byte)((pred.G0 + DecodeZigZag(greenLane[a])) & 255);
            byte eg1 = (byte)((pred.G1 + DecodeZigZag(greenLane[bIdx])) & 255);
            history[i & 255] = new(er0, er1, eg0, eg1);

            ulong redBits =
                PackRow12(pairs[r0[a]], pairs[r0[bIdx]])
                | (PackRow12(pairs[r1[a]], pairs[r1[bIdx]]) << 12)
                | (PackRow12(pairs[r2[a]], pairs[r2[bIdx]]) << 24)
                | (PackRow12(pairs[r3[a]], pairs[r3[bIdx]]) << 36);

            ulong greenBits =
                PackRow12(pairs[g0[a]], pairs[g0[bIdx]])
                | (PackRow12(pairs[g1[a]], pairs[g1[bIdx]]) << 12)
                | (PackRow12(pairs[g2[a]], pairs[g2[bIdx]]) << 24)
                | (PackRow12(pairs[g3[a]], pairs[g3[bIdx]]) << 36);

            int at = i * 16;
            output[at] = er0;
            output[at + 1] = er1;
            WriteUInt48LittleEndian(output.AsSpan(at + 2, 6), redBits);
            output[at + 8] = eg0;
            output[at + 9] = eg1;
            WriteUInt48LittleEndian(output.AsSpan(at + 10, 6), greenBits);
        }

        FinalizeUnknownHistory(unknownEntries, lostTotal, blockCount, "BC5", provisional);
        return output;
    }

    private static readonly int[] Bc6hNoneE =
    [
        0x8000, 0x200, 0x20, 0x20,
        0x8000, 0x200, 0x20, 0x20,
        0x8000, 0x200, 0x20, 0x20
    ];

    private static readonly int[] Bc6hNoneI = [8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8, 8];

    private static readonly byte[] Bc7Ns = [3, 2, 3, 2, 1, 1, 1, 2];
    private static readonly byte[] Bc7Cb = [4, 6, 5, 7, 5, 7, 7, 5];
    private static readonly byte[] Bc7Ab = [0, 0, 0, 0, 6, 8, 7, 5];
    private static readonly byte[] Bc7Pk = [1, 2, 0, 1, 0, 0, 1, 1];
    private static readonly byte[] Bc7Ib = [3, 3, 2, 2, 3, 2, 4, 2];
    private static readonly byte[] Bc7Pb = [4, 6, 6, 6, 0, 0, 0, 6];
    private static readonly sbyte[] Bc7LaneMap = [2, 3, 4, 5, 6, 7, 8, 9, 10, 5, 9, -1];
    private static readonly int[] ZeroBc7Ep = new int[24];
    private static readonly int[] ZeroBc7Ix = new int[32];

    private const int Bc7StatusAnchor = 8;
    private const int Bc7StatusPbit = 16;
    private const int Bc7StatusSignature = 32;
    private const int Bc7MinDepth = 4;

    private static byte[] RebuildBc6h(
        ushort[][] lanes,
        int blockCount,
        HashSet<string>? provisional,
        Bc6hHistoryTable history,
        bool hasHistory,
        CancellationToken ct)
    {
        if (lanes[0].Length != 2 * blockCount)
            throw new InvalidDataException("BC6H lane length does not match the block count.");

        int[] ehTable = history.EndpointHistory;
        int[] ihTable = history.IndexHistory;
        int[] eclass = history.EndpointClass;
        int[] iclass = history.IndexClass;
        byte[] epCb = history.EndpointConfirmedBits;
        byte[] ixCb = history.IndexConfirmedBits;
        byte[] anchor2 = BcpackTablesBptc.Bc7Anchor2;
        byte[] deint0 = BcpackTablesBptc.Deint0;
        byte[] deint1 = BcpackTablesBptc.Deint1;
        ushort[] spread8 = BcpackTablesBptc.Spread8;
        Bc6hModeInfo[] modes = BcpackTablesBptc.Bc6hModes;

        byte[] output = new byte[checked(blockCount * 16)];
        int[] stE = new int[256 * 12];
        int[] stI = new int[256 * 16];
        Span<int> eroot = stackalloc int[256];
        Span<int> iroot = stackalloc int[256];
        eroot.Fill(-1);
        iroot.Fill(-1);

        Span<bool> seenE = stackalloc bool[256];
        Span<bool> seenI = stackalloc bool[256];
        Span<bool> weakE = stackalloc bool[256];
        Span<bool> weakI = stackalloc bool[256];
        var endsOrder = new List<int>();
        var idxsOrder = new List<int>();
        int lost = 0;
        int violations = 0;

        ushort[] l0 = lanes[0], l1 = lanes[1], l15 = lanes[15];
        Span<int> fBlock = stackalloc int[12];
        Span<int> stBlock = stackalloc int[12];
        Span<int> ixBlock = stackalloc int[16];
        Span<int> siBlock = stackalloc int[16];

        for (int i = 0; i < blockCount; i++)
        {
            if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();

            int a = 2 * i, b = a + 1;
            int mSym = l15[a];
            if (mSym > 13)
                throw new InvalidDataException($"BC6H block {i} mode symbol {mSym} outside 0..13");
            int mode = mSym + 1;
            Bc6hModeInfo info = modes[mode];
            int baseBits = info.BaseBits;
            int reg = info.Regions;
            int part = l15[b] < 31 ? l15[b] : 31;

            int di = InterleaveNibbles(l1[a], l1[b]);
            int de = (di + DecodeZigZag(InterleaveNibbles(l0[a], l0[b]))) & 255;

            ReadOnlySpan<int> eref;
            int er;
            if (de == 0)
            {
                eref = Bc6hNoneE;
                er = -1;
            }
            else if (i >= de)
            {
                int prev = (i - de) & 255;
                eref = stE.AsSpan(prev * 12, 12);
                er = eroot[prev];
            }
            else
            {
                int entry = 256 + i - de;
                eref = ehTable.AsSpan(entry * 12, 12);
                er = entry;
                for (int c = 0; c < 3; c++)
                {
                    int dc = c == 0 ? info.DeltaR : (c == 1 ? info.DeltaG : info.DeltaB);
                    int cbAt = entry * 12 + c * 4;
                    if (epCb[cbAt] < baseBits || epCb[cbAt + 1] < dc
                        || (reg == 2 && (epCb[cbAt + 2] < dc || epCb[cbAt + 3] < dc)))
                    {
                        weakE[entry] = true;
                        break;
                    }
                }
            }
            eroot[i & 255] = er;
            if (er >= 0 && !seenE[er])
            {
                seenE[er] = true;
                endsOrder.Add(er);
            }

            ReadOnlySpan<int> iref;
            int ir;
            if (di == 0)
            {
                iref = Bc6hNoneI;
                ir = -1;
            }
            else if (i >= di)
            {
                int prev = (i - di) & 255;
                iref = stI.AsSpan(prev * 16, 16);
                ir = iroot[prev];
            }
            else
            {
                int entry = 256 + i - di;
                iref = ihTable.AsSpan(entry * 16, 16);
                ir = entry;
                int needI = reg == 2 ? 3 : 4;
                int cbAt = entry * 16;
                for (int p = 0; p < 16; p++)
                {
                    if (ixCb[cbAt + p] < needI)
                    {
                        weakI[entry] = true;
                        break;
                    }
                }
            }
            iroot[i & 255] = ir;
            if (ir >= 0 && !seenI[ir])
            {
                seenI[ir] = true;
                idxsOrder.Add(ir);
            }

            if ((er >= 0 && eclass[er] == 0) || (ir >= 0 && iclass[ir] == 0))
                lost++;

            int mw = (1 << baseBits) - 1;
            int wShift = 16 - baseBits;
            for (int c = 0; c < 3; c++)
            {
                int dc = c == 0 ? info.DeltaR : (c == 1 ? info.DeltaG : info.DeltaB);
                int mD = (1 << dc) - 1;
                int sh = dc < 6 ? 6 - dc : 0;
                ushort[] lW = lanes[2 + c], lX = lanes[5 + c], lYZ = lanes[8 + c];
                int dz0 = DecodeZigZag(spread8[lW[a] & 255] | (spread8[lW[b] & 255] << 1));
                int dz1 = DecodeZigZag(spread8[lX[a] & 31] | (spread8[lX[b] & 31] << 1));
                int dz2 = DecodeZigZag(lYZ[a]);
                int dz3 = DecodeZigZag(lYZ[b]);

                int off = c * 4;
                int w = ((eref[off] >> wShift) + dz0) & mw;
                int x = ((eref[off + 1] >> (10 - dc)) + dz1) & mD;
                int y = ((eref[off + 2] >> sh) + dz2) & mD;
                int z = ((eref[off + 3] >> sh) + dz3) & mD;
                int x10 = x << (10 - dc);
                int sw = Bc6hStreamW(mode, w);
                if (reg == 2)
                {
                    stBlock[off] = w << wShift;
                    stBlock[off + 1] = x10;
                    stBlock[off + 2] = y << sh;
                    stBlock[off + 3] = z << sh;
                    fBlock[off] = sw;
                    fBlock[off + 1] = x;
                    fBlock[off + 2] = y;
                    fBlock[off + 3] = z;
                }
                else
                {
                    int v = (w & mD) ^ (info.Transformed ? (1 << (dc - 1)) : 0);
                    int lowY = dc >= 6 ? (v >> (dc - 6)) : (v << sh);
                    stBlock[off] = w << wShift;
                    stBlock[off + 1] = x10;
                    stBlock[off + 2] = lowY;
                    stBlock[off + 3] = x10 >> 4;
                    fBlock[off] = sw;
                    fBlock[off + 1] = x;
                    fBlock[off + 2] = 0;
                    fBlock[off + 3] = 0;
                }
            }
            stBlock.CopyTo(stE.AsSpan((i & 255) * 12, 12));

            int ibits = reg == 2 ? 3 : 4;
            int imask = (1 << ibits) - 1;
            int ishift = 4 - ibits;
            for (int yy = 0; yy < 4; yy++)
            {
                int sa = lanes[11 + yy][a];
                int sb = lanes[11 + yy][b];
                int row = yy * 4;
                int d0, d1, d2, d3;
                if (yy < 3)
                {
                    d0 = deint1[sa]; d1 = deint0[sa];
                    d2 = deint1[sb]; d3 = deint0[sb];
                }
                else
                {
                    d0 = deint0[sa]; d1 = deint1[sa];
                    d2 = deint0[sb]; d3 = deint1[sb];
                }
                int ix0 = ((iref[row] >> ishift) + DecodeZigZag(d0)) & imask;
                int ix1 = ((iref[row + 1] >> ishift) + DecodeZigZag(d1)) & imask;
                int ix2 = ((iref[row + 2] >> ishift) + DecodeZigZag(d2)) & imask;
                int ix3 = ((iref[row + 3] >> ishift) + DecodeZigZag(d3)) & imask;
                ixBlock[row] = ix0; siBlock[row] = ix0 << ishift;
                ixBlock[row + 1] = ix1; siBlock[row + 1] = ix1 << ishift;
                ixBlock[row + 2] = ix2; siBlock[row + 2] = ix2 << ishift;
                ixBlock[row + 3] = ix3; siBlock[row + 3] = ix3 << ishift;
            }
            siBlock.CopyTo(stI.AsSpan((i & 255) * 16, 16));

            int second = reg == 2 ? anchor2[part] : -1;
            if ((ixBlock[0] & (reg == 2 ? 4 : 8)) != 0) violations++;
            if (reg == 2 && (ixBlock[second] & 4) != 0) violations++;

            UInt128 acc = info.Code;
            Bc6hPlanItem[] plan = info.Plan;
            for (int k = 0; k < plan.Length; k++)
            {
                Bc6hPlanItem item = plan[k];
                int bit = item.Channel < 0
                    ? (part >> item.BitIndex) & 1
                    : (fBlock[item.Channel * 4 + item.Slot] >> item.BitIndex) & 1;
                if (bit != 0) acc |= UInt128.One << item.StreamPos;
            }

            int at = info.HeaderBits;
            for (int p = 0; p < 16; p++)
            {
                int nb = (p == 0 || p == second) ? ibits - 1 : ibits;
                acc |= (UInt128)(uint)(ixBlock[p] & ((1 << nb) - 1)) << at;
                at += nb;
            }
            BinaryPrimitives.WriteUInt128LittleEndian(output.AsSpan(16 * i, 16), acc);
        }

        if (provisional is null)
        {
            int unconfE = -1;
            foreach (int k in endsOrder)
            {
                if (eclass[k] != 3 || weakE[k]) { unconfE = k; break; }
            }
            int unconfI = -1;
            if (unconfE < 0)
            {
                foreach (int k in idxsOrder)
                {
                    if (iclass[k] != 3 || weakI[k]) { unconfI = k; break; }
                }
            }
            if (unconfE >= 0 || unconfI >= 0)
            {
                bool isEp = unconfE >= 0;
                int k = isEp ? unconfE : unconfI;
                string kind = isEp ? "endpoint" : "index";
                int cls = isEp ? eclass[k] : iclass[k];
                string state = cls == 0 ? "unknown" : "unconfirmed";
                string hint = hasHistory ? "" : " (pass --history <path>)";
                throw new BcpackProvisionalException($"BC6H chunk references {state} built-in {kind} history entry {k}{hint}");
            }
            if (violations > 0)
                throw new InvalidDataException($"BC6H chunk has {violations} anchor pixel(s) with an inconsistent index");
            return output;
        }

        var missing = new List<string>();
        foreach (int k in endsOrder)
            if (eclass[k] == 0) missing.Add($"endpoint {k}");
        foreach (int k in idxsOrder)
            if (iclass[k] == 0) missing.Add($"index {k}");
        missing.Sort(StringComparer.Ordinal);

        int limit = LooseUnknownBlockLimit(blockCount);
        if (lost > limit)
        {
            string hint = hasHistory ? "" : " (pass --history <path>)";
            string preview = string.Join(", ", missing.Take(6)) + (missing.Count > 6 ? ", ..." : "");
            throw new BcpackProvisionalException(
                $"BC6H chunk has {lost}/{blockCount} blocks from unknown built-in history entries ({preview}){hint}");
        }
        if (violations > limit)
            throw new BcpackProvisionalException($"BC6H chunk has {violations} anchor pixels with an inconsistent index");

        if (missing.Count > 0)
            provisional.Add($"BC6H unknown history {string.Join(", ", missing)} ({lost} blocks)");
        if (violations > 0)
            provisional.Add($"BC6H anchor index inconsistent in {violations} pixels");

        for (int k = 0; k < 256; k++)
            if (weakE[k] && eclass[k] == 3) provisional.Add($"BC6H endpoint history {k} (unconfirmed bits)");
        for (int k = 0; k < 256; k++)
            if (weakI[k] && iclass[k] == 3) provisional.Add($"BC6H index history {k} (unconfirmed bits)");

        AddBc6hClassLabels("endpoint", endsOrder, eclass, provisional);
        AddBc6hClassLabels("index", idxsOrder, iclass, provisional);
        return output;
    }

    private static void AddBc6hClassLabels(string name, List<int> entries, int[] cls, HashSet<string> provisional)
    {
        var fitted = new List<int>();
        var rough = new List<int>();
        foreach (int k in entries)
        {
            if (cls[k] == 1) fitted.Add(k);
            else if (cls[k] == 2) rough.Add(k);
        }
        if (fitted.Count > 0)
        {
            fitted.Sort();
            provisional.Add($"BC6H {name} history {string.Join(", ", fitted)}");
        }
        if (rough.Count > 0)
        {
            rough.Sort();
            provisional.Add($"BC6H {name} history {string.Join(", ", rough)} (last-resort)");
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int Bc6hStreamW(int mode, int w)
    {
        if (mode == 13)
            return (w & 0x3FF) | (((w >> 10) & 1) << 11) | (((w >> 11) & 1) << 10);
        if (mode == 14)
        {
            return (w & 0x3FF)
                | (((w >> 10) & 1) << 15)
                | (((w >> 11) & 1) << 14)
                | (((w >> 12) & 1) << 13)
                | (((w >> 13) & 1) << 12)
                | (((w >> 14) & 1) << 11)
                | (((w >> 15) & 1) << 10);
        }
        return w;
    }

    private static byte[] RebuildBc7(
        ushort[][] lanes,
        int blockCount,
        HashSet<string>? provisional,
        Bc7HistoryTable history,
        bool hasHistory,
        CancellationToken ct)
    {
        if (lanes[0].Length != 2 * blockCount)
            throw new InvalidDataException("BC7 lane length does not match the block count.");

        List<(bool IsEndpoint, int Entry)> unknown = CheckBc7(lanes, blockCount, history, provisional, hasHistory, ct);
        if (unknown.Count > 0)
        {
            ushort[] l0 = lanes[0], l1 = lanes[1];
            Span<int> er = stackalloc int[256];
            Span<int> ir = stackalloc int[256];
            Span<bool> unkE = stackalloc bool[256];
            Span<bool> unkI = stackalloc bool[256];
            Span<int> countE = stackalloc int[256];
            Span<int> countI = stackalloc int[256];
            foreach (var (isEp, k) in unknown)
            {
                if (isEp) unkE[k] = true;
                else unkI[k] = true;
            }
            int total = 0;
            for (int i = 0; i < blockCount; i++)
            {
                if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                int a = 2 * i, b = a + 1;
                int di = InterleaveNibbles(l1[a], l1[b]);
                int de = (di + DecodeZigZag(InterleaveNibbles(l0[a], l0[b]))) & 255;
                int eRoot = de == 0 ? -1 : (de > i ? 256 + i - de : er[(i - de) & 255]);
                int iRoot = di == 0 ? -1 : (di > i ? 256 + i - di : ir[(i - di) & 255]);
                er[i & 255] = eRoot;
                ir[i & 255] = iRoot;
                bool hitE = eRoot >= 0 && unkE[eRoot];
                bool hitI = iRoot >= 0 && unkI[iRoot];
                if (hitE) countE[eRoot]++;
                if (hitI) countI[iRoot]++;
                if (hitE || hitI) total++;
            }
            if (total > LooseUnknownBlockLimit(blockCount))
            {
                string names = string.Join(", ", unknown.Take(6).Select(u => $"{(u.IsEndpoint ? "endpoint" : "index")} {u.Entry}"))
                    + (unknown.Count > 6 ? ", ..." : "");
                string hint = hasHistory ? "" : " (pass --history <path>)";
                throw new BcpackProvisionalException(
                    $"BC7 chunk has {total}/{blockCount} blocks from unknown built-in history entries ({names}){hint}");
            }
            foreach (var (isEp, k) in unknown)
            {
                int cnt = isEp ? countE[k] : countI[k];
                provisional!.Add($"BC7 unknown {(isEp ? "endpoint" : "index")} history {k} ({cnt} blocks)");
            }
        }

        (byte[] data, int badCount, int firstBadBlock, int firstBadStatus) = RebuildBc7Blocks(lanes, blockCount, history, ct);
        if (badCount > 0)
        {
            if ((firstBadStatus & (Bc7StatusAnchor | Bc7StatusPbit)) == 0)
                throw new InvalidDataException($"BC7 chunk failed signature check ({badCount}/{blockCount} blocks; first: block {firstBadBlock})");
            if (provisional is null || provisional.Count == 0)
                throw new InvalidDataException($"BC7 chunk failed anchor/p-bit consistency check ({badCount}/{blockCount} blocks; first: block {firstBadBlock})");
            if (badCount > LooseUnknownBlockLimit(blockCount))
                throw new BcpackProvisionalException($"BC7 chunk failed anchor/p-bit consistency check in {badCount}/{blockCount} blocks");
            provisional.Add($"BC7 consistency check failed in {badCount} blocks");
        }
        return data;
    }

    private static List<(bool IsEndpoint, int Entry)> CheckBc7(
        ushort[][] lanes,
        int blockCount,
        Bc7HistoryTable history,
        HashSet<string>? provisional,
        bool hasHistory,
        CancellationToken ct)
    {
        Bc7ConfigEntry[] cfg = BcpackTablesBptc.Bc7Config;
        short[] rankOf = BcpackTablesBptc.Bc7RankOf;
        bool[] inferred = BcpackTablesBptc.Bc7ConfigInferred;
        ushort[] l0 = lanes[0], l1 = lanes[1], l15 = lanes[15];

        Span<bool> seenPair = stackalloc bool[512];
        Span<bool> seenRank = stackalloc bool[286];
        for (int i = 0; i < blockCount; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            int a = l15[2 * i], b = l15[2 * i + 1];
            if (a < 32 && b < 16)
            {
                int cell = a * 16 + b;
                if (seenPair[cell]) continue;
                seenPair[cell] = true;
                int rank = rankOf[cell];
                if (rank < 0 || cfg[rank].Mode > 7 || cfg[rank].Partition == 255)
                    throw new InvalidDataException($"BC7 signature ({a}, {b}) is outside the signature table");
                seenRank[rank] = true;
            }
            else
            {
                throw new InvalidDataException($"BC7 signature ({a}, {b}) is outside the signature table");
            }
        }

        for (int rank = 0; rank < 286; rank++)
        {
            if (!seenRank[rank] || !inferred[rank]) continue;
            if (provisional is null)
                throw new BcpackProvisionalException($"BC7 signature rank {rank} is inferred-only (unconfirmed)");
            provisional.Add($"BC7 signature {rank}");
        }

        byte[] needE = new byte[256 * 12];
        byte[] needI = new byte[256 * 2];
        Span<bool> hasNeedE = stackalloc bool[256];
        Span<bool> hasNeedI = stackalloc bool[256];
        Span<int> fieldBits = stackalloc int[4];

        int scanLimit = Math.Min(blockCount, 256);
        for (int i = 0; i < scanLimit; i++)
        {
            int rank = rankOf[l15[2 * i] * 16 + l15[2 * i + 1]];
            Bc7ConfigEntry c = cfg[rank];
            int mode = c.Mode;
            int rot = c.Rotation;
            int di = InterleaveNibbles(l1[2 * i], l1[2 * i + 1]);
            int de = (di + DecodeZigZag(InterleaveNibbles(l0[2 * i], l0[2 * i + 1]))) & 255;

            if (de > i)
            {
                int k = 256 + i - de;
                hasNeedE[k] = true;
                Bc7FieldBits(mode, rot, fieldBits);
                int ns = Bc7Ns[mode];
                for (int sub = 0; sub < ns; sub++)
                {
                    int row = k * 12 + sub * 4;
                    for (int ch = 0; ch < 4; ch++)
                    {
                        if (fieldBits[ch] > needE[row + ch])
                            needE[row + ch] = (byte)fieldBits[ch];
                    }
                }
            }
            if (di > i)
            {
                int k = 256 + i - di;
                hasNeedI[k] = true;
                int row = k * 2;
                if (Bc7Ib[mode] > needI[row]) needI[row] = Bc7Ib[mode];
                if (mode is 4 or 5) needI[row + 1] = 2;
            }
        }

        var unknown = new List<(bool IsEndpoint, int Entry)>();
        int[] epBits = history.HistoryEndpointBits;
        int[] epConf = history.HistoryEndpointConfirmedBits;
        int[] ixBits = history.HistoryIndexBits;
        int[] ixConf = history.HistoryIndexConfirmedBits;
        var lastResort = history.LastResort;

        for (int k = 0; k < 256; k++)
        {
            if (!hasNeedE[k]) continue;
            bool weak = false;
            bool missing = false;
            bool hasLr = false;
            int shallow = 8;
            int baseEp = k * 24;
            int baseNeed = k * 12;
            for (int sub = 0; sub < 3; sub++)
            {
                for (int e = 0; e < 2; e++)
                {
                    for (int ch = 0; ch < 4; ch++)
                    {
                        int want = needE[baseNeed + sub * 4 + ch];
                        if (want == 0) continue;
                        int slot = sub * 8 + e * 4 + ch;
                        int bk = epBits[baseEp + slot];
                        int ck = epConf[baseEp + slot];
                        if (bk < want)
                        {
                            missing = true;
                            if (bk < shallow) shallow = bk;
                        }
                        if (ck < want) weak = true;
                        if (!hasLr && provisional is not null && lastResort.Contains((k, slot)))
                            hasLr = true;
                    }
                }
            }
            if (missing)
            {
                if (provisional is null)
                {
                    string hint = hasHistory ? "" : " (pass --history <path>)";
                    throw new BcpackProvisionalException($"BC7 chunk references unknown built-in endpoint history entry {k}{hint}");
                }
                if (shallow >= Bc7MinDepth)
                    provisional.Add($"BC7 endpoint history {k} (top {shallow} bits known)");
                else
                    unknown.Add((true, k));
            }
            else if (weak)
            {
                if (provisional is null)
                    throw new BcpackProvisionalException($"BC7 chunk references unconfirmed built-in endpoint history entry {k}");
                provisional.Add($"BC7 endpoint history {k}");
            }
            if (hasLr)
                provisional!.Add($"BC7 endpoint history {k} (last-resort)");
        }

        for (int k = 0; k < 256; k++)
        {
            if (!hasNeedI[k]) continue;
            bool weak = false;
            bool missing = false;
            int baseIx = k * 32;
            int baseNeed = k * 2;
            for (int st = 0; st < 2; st++)
            {
                int want = needI[baseNeed + st];
                if (want == 0) continue;
                for (int p = 0; p < 16; p++)
                {
                    int slot = st * 16 + p;
                    if (ixBits[baseIx + slot] < want) missing = true;
                    if (ixConf[baseIx + slot] < want) weak = true;
                }
            }
            if (missing)
            {
                if (provisional is null)
                {
                    string hint = hasHistory ? "" : " (pass --history <path>)";
                    throw new BcpackProvisionalException($"BC7 chunk references unknown built-in index history entry {k}{hint}");
                }
                unknown.Add((false, k));
            }
            else if (weak)
            {
                if (provisional is null)
                    throw new BcpackProvisionalException($"BC7 chunk references unconfirmed built-in index history entry {k}");
                provisional.Add($"BC7 index history {k}");
            }
        }

        return unknown;
    }

    private static void Bc7FieldBits(int mode, int rot, Span<int> result)
    {
        int cb = Bc7Cb[mode];
        int ab = Bc7Ab[mode];
        result[0] = cb;
        result[1] = cb;
        result[2] = cb;
        result[3] = ab;
        if (rot != 0)
        {
            result[rot - 1] = ab;
            result[3] = cb;
        }
        int pk = Bc7Pk[mode];
        if (pk != 0 && result[0] != 0) result[0]++;
    }

    private static (byte[] Data, int BadCount, int FirstBadBlock, int FirstBadStatus) RebuildBc7Blocks(
        ushort[][] lanes,
        int blockCount,
        Bc7HistoryTable history,
        CancellationToken ct)
    {
        Bc7ConfigEntry[] cfg = BcpackTablesBptc.Bc7Config;
        short[] rankOf = BcpackTablesBptc.Bc7RankOf;
        byte[][] p2 = BcpackTablesBptc.Bc7Partition2;
        byte[][] p3 = BcpackTablesBptc.Bc7Partition3;
        byte[] a2 = BcpackTablesBptc.Bc7Anchor2;
        byte[] a3a = BcpackTablesBptc.Bc7Anchor3A;
        byte[] a3b = BcpackTablesBptc.Bc7Anchor3B;
        byte[] deint0 = BcpackTablesBptc.Deint0;
        byte[] deint1 = BcpackTablesBptc.Deint1;
        int[] preE = history.HistoryEndpoints;
        int[] preI = history.HistoryIndices;

        byte[] output = new byte[checked(blockCount * 16)];
        int[] epsBuf = new int[256 * 24];
        int[] ixsBuf = new int[256 * 32];
        ushort[] l0 = lanes[0], l1 = lanes[1], l15 = lanes[15];
        ushort[][] rows1 = [lanes[11], lanes[12], lanes[13], lanes[14]];
        ushort[][] rows2 = [lanes[6], lanes[7], lanes[8], lanes[10]];

        Span<int> w = stackalloc int[4];
        Span<int> val = stackalloc int[24];
        Span<int> sep = stackalloc int[24];
        Span<int> pb = stackalloc int[6];
        Span<int> s1 = stackalloc int[16];
        Span<int> s2 = stackalloc int[16];
        Span<int> six = stackalloc int[32];

        int badCount = 0;
        int firstBadBlock = -1;
        int firstBadStatus = 0;

        for (int i = 0; i < blockCount; i++)
        {
            if ((i & 0x3FFF) == 0) ct.ThrowIfCancellationRequested();

            int a = 2 * i, b = a + 1;
            int s15a = l15[a], s15b = l15[b];
            int rank = (s15a < 32 && s15b < 16) ? rankOf[s15a * 16 + s15b] : -1;
            if (rank < 0 || cfg[rank].Mode > 7)
            {
                if (badCount == 0) { firstBadBlock = i; firstBadStatus = Bc7StatusSignature; }
                badCount++;
                epsBuf.AsSpan((i & 255) * 24, 24).Clear();
                ixsBuf.AsSpan((i & 255) * 32, 32).Clear();
                continue;
            }

            Bc7ConfigEntry entryCfg = cfg[rank];
            int mode = entryCfg.Mode;
            int part = entryCfg.Partition;
            int rot = entryCfg.Rotation;
            int isel = entryCfg.IndexSelection;
            int st = 0;

            int di = InterleaveNibbles(l1[a], l1[b]);
            int de = (di + DecodeZigZag(InterleaveNibbles(l0[a], l0[b]))) & 255;

            ReadOnlySpan<int> rep = de == 0
                ? ZeroBc7Ep
                : (i >= de ? epsBuf.AsSpan(((i - de) & 255) * 24, 24) : preE.AsSpan((256 + i - de) * 24, 24));
            ReadOnlySpan<int> rix = di == 0
                ? ZeroBc7Ix
                : (i >= di ? ixsBuf.AsSpan(((i - di) & 255) * 32, 32) : preI.AsSpan((256 + i - di) * 32, 32));

            int ns = Bc7Ns[mode];
            int cb = Bc7Cb[mode];
            int ab = Bc7Ab[mode];
            int pk = Bc7Pk[mode];
            w[0] = cb; w[1] = cb; w[2] = cb; w[3] = ab;
            if (rot != 0)
            {
                w[rot - 1] = ab;
                w[3] = cb;
            }

            val.Clear();
            sep.Clear();
            pb.Clear();
            for (int sub = 0; sub < ns; sub++)
            {
                int baseSub = sub * 8;
                int laneBase = sub * 4;
                for (int c = 0; c < 4; c++)
                {
                    int laneIdx = Bc7LaneMap[laneBase + c];
                    int wc = w[c];
                    if (wc == 0 || laneIdx < 0) continue;
                    int bits = wc + ((pk != 0 && c == 0) ? 1 : 0);
                    int shift = 8 - bits;
                    int mask = (1 << bits) - 1;
                    ushort[] ln = lanes[laneIdx];
                    int v0 = ((rep[baseSub + c] >> shift) + DecodeZigZag(ln[a])) & mask;
                    int v1 = ((rep[baseSub + 4 + c] >> shift) + DecodeZigZag(ln[b])) & mask;
                    val[baseSub + c] = v0;
                    val[baseSub + 4 + c] = v1;
                    sep[baseSub + c] = v0 << shift;
                    sep[baseSub + 4 + c] = v1 << shift;
                }
                if (pk == 1)
                {
                    pb[sub * 2] = val[baseSub] & 1;
                    pb[sub * 2 + 1] = val[baseSub + 4] & 1;
                }
                else if (pk == 2)
                {
                    int p0 = val[baseSub] & 1;
                    pb[sub * 2] = p0;
                    pb[sub * 2 + 1] = p0;
                    if ((val[baseSub + 4] & 1) != p0) st |= Bc7StatusPbit;
                }
            }
            for (int sub = ns; sub < 3; sub++)
                sep[..8].CopyTo(sep.Slice(sub * 8, 8));
            sep.CopyTo(epsBuf.AsSpan((i & 255) * 24, 24));

            int b1 = Bc7Ib[mode];
            int b1Shift = 4 - b1;
            int b1Mask = (1 << b1) - 1;
            bool two = mode is 4 or 5;
            six.Clear();
            for (int y = 0; y < 4; y++)
            {
                ushort[] r1 = rows1[y];
                int sa = r1[a], sb = r1[b];
                int row = y * 4;
                int d0, d1, d2, d3;
                if (y < 3)
                {
                    d0 = deint1[sa]; d1 = deint0[sa]; d2 = deint1[sb]; d3 = deint0[sb];
                }
                else
                {
                    d0 = deint0[sa]; d1 = deint1[sa]; d2 = deint0[sb]; d3 = deint1[sb];
                }
                int iv0 = ((rix[row] >> b1Shift) + DecodeZigZag(d0)) & b1Mask;
                int iv1 = ((rix[row + 1] >> b1Shift) + DecodeZigZag(d1)) & b1Mask;
                int iv2 = ((rix[row + 2] >> b1Shift) + DecodeZigZag(d2)) & b1Mask;
                int iv3 = ((rix[row + 3] >> b1Shift) + DecodeZigZag(d3)) & b1Mask;
                s1[row] = iv0; six[row] = iv0 << b1Shift;
                s1[row + 1] = iv1; six[row + 1] = iv1 << b1Shift;
                s1[row + 2] = iv2; six[row + 2] = iv2 << b1Shift;
                s1[row + 3] = iv3; six[row + 3] = iv3 << b1Shift;

                if (two)
                {
                    ushort[] r2 = rows2[y];
                    int s2a = r2[a], s2b = r2[b];
                    int e0, e1, e2, e3;
                    if (y < 3)
                    {
                        e0 = deint1[s2a]; e1 = deint0[s2a]; e2 = deint1[s2b]; e3 = deint0[s2b];
                    }
                    else
                    {
                        e0 = deint0[s2a]; e1 = deint1[s2a]; e2 = deint0[s2b]; e3 = deint1[s2b];
                    }
                    int j0 = ((rix[16 + row] >> 2) + DecodeZigZag(e0)) & 3;
                    int j1 = ((rix[16 + row + 1] >> 2) + DecodeZigZag(e1)) & 3;
                    int j2 = ((rix[16 + row + 2] >> 2) + DecodeZigZag(e2)) & 3;
                    int j3 = ((rix[16 + row + 3] >> 2) + DecodeZigZag(e3)) & 3;
                    s2[row] = j0; six[16 + row] = j0 << 2;
                    s2[row + 1] = j1; six[16 + row + 1] = j1 << 2;
                    s2[row + 2] = j2; six[16 + row + 2] = j2 << 2;
                    s2[row + 3] = j3; six[16 + row + 3] = j3 << 2;
                }
            }
            six.CopyTo(ixsBuf.AsSpan((i & 255) * 32, 32));

            if (mode == 0 && part == 0 && IsAllZero(sep) && IsAllZero(s1))
            {
                if (st != 0)
                {
                    if (badCount == 0) { firstBadBlock = i; firstBadStatus = st; }
                    badCount++;
                }
                continue;
            }

            byte[]? sm = ns == 2 ? p2[part] : (ns == 3 ? p3[part] : null);
            int anchor1 = ns == 2 ? a2[part] : (ns == 3 ? a3a[part] : 0);
            int anchor2 = ns == 3 ? a3b[part] : 0;

            ReadOnlySpan<int> pri = mode == 4 ? s2 : s1;
            ReadOnlySpan<int> sec = mode == 4 ? s1 : (mode == 5 ? s2 : ReadOnlySpan<int>.Empty);
            int ib = mode is 4 or 5 ? 2 : b1;
            int ib2 = mode == 4 ? 3 : (mode == 5 ? 2 : 0);

            UInt128 acc = UInt128.One << mode;
            int pos = mode + 1;
            int pbits = Bc7Pb[mode];
            if (pbits > 0)
            {
                acc |= (UInt128)(uint)(part & ((1 << pbits) - 1)) << pos;
                pos += pbits;
            }
            if (mode is 4 or 5)
            {
                acc |= (UInt128)(uint)(rot & 3) << pos;
                pos += 2;
            }
            if (mode == 4)
            {
                acc |= (UInt128)(uint)(isel & 1) << pos;
                pos += 1;
            }

            int cbMask = (1 << cb) - 1;
            for (int c = 0; c < 3; c++)
            {
                int tc = (rot != 0 && c == rot - 1) ? 3 : c;
                bool drop = pk != 0 && tc == 0;
                for (int sub = 0; sub < ns; sub++)
                {
                    int baseSub = sub * 8;
                    int v0 = val[baseSub + tc];
                    acc |= (UInt128)(uint)((drop ? (v0 >> 1) : v0) & cbMask) << pos;
                    pos += cb;
                    int v1 = val[baseSub + 4 + tc];
                    acc |= (UInt128)(uint)((drop ? (v1 >> 1) : v1) & cbMask) << pos;
                    pos += cb;
                }
            }
            if (ab > 0)
            {
                int abMask = (1 << ab) - 1;
                int tc = rot != 0 ? rot - 1 : 3;
                bool drop = pk != 0 && tc == 0;
                for (int sub = 0; sub < ns; sub++)
                {
                    int baseSub = sub * 8;
                    int v0 = val[baseSub + tc];
                    acc |= (UInt128)(uint)((drop ? (v0 >> 1) : v0) & abMask) << pos;
                    pos += ab;
                    int v1 = val[baseSub + 4 + tc];
                    acc |= (UInt128)(uint)((drop ? (v1 >> 1) : v1) & abMask) << pos;
                    pos += ab;
                }
            }
            if (pk == 1)
            {
                for (int sub = 0; sub < ns; sub++)
                {
                    acc |= (UInt128)(uint)(pb[sub * 2] & 1) << pos;
                    pos++;
                    acc |= (UInt128)(uint)(pb[sub * 2 + 1] & 1) << pos;
                    pos++;
                }
            }
            else if (pk == 2)
            {
                for (int sub = 0; sub < ns; sub++)
                {
                    acc |= (UInt128)(uint)(pb[sub * 2] & 1) << pos;
                    pos++;
                }
            }

            int ibMask = (1 << ib) - 1;
            int ibAMask = (1 << (ib - 1)) - 1;
            for (int p = 0; p < 16; p++)
            {
                int subset = sm is not null ? sm[p] : 0;
                int expectedAnchor = subset == 0 ? 0 : (subset == 1 ? anchor1 : anchor2);
                int pv = pri[p];
                if (p == expectedAnchor)
                {
                    if ((pv >> (ib - 1)) != 0) st |= Bc7StatusAnchor;
                    acc |= (UInt128)(uint)(pv & ibAMask) << pos;
                    pos += ib - 1;
                }
                else
                {
                    acc |= (UInt128)(uint)(pv & ibMask) << pos;
                    pos += ib;
                }
            }
            if (!sec.IsEmpty)
            {
                int ib2Mask = (1 << ib2) - 1;
                int ib2AMask = (1 << (ib2 - 1)) - 1;
                for (int p = 0; p < 16; p++)
                {
                    int sv = sec[p];
                    if (p == 0)
                    {
                        if ((sv >> (ib2 - 1)) != 0) st |= Bc7StatusAnchor;
                        acc |= (UInt128)(uint)(sv & ib2AMask) << pos;
                        pos += ib2 - 1;
                    }
                    else
                    {
                        acc |= (UInt128)(uint)(sv & ib2Mask) << pos;
                        pos += ib2;
                    }
                }
            }
            if (pos != 128) st |= Bc7StatusSignature;
            BinaryPrimitives.WriteUInt128LittleEndian(output.AsSpan(16 * i, 16), acc);
            if (st != 0)
            {
                if (badCount == 0) { firstBadBlock = i; firstBadStatus = st; }
                badCount++;
            }
        }

        return (output, badCount, firstBadBlock, firstBadStatus);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool IsAllZero(ReadOnlySpan<int> values)
    {
        for (int i = 0; i < values.Length; i++)
            if (values[i] != 0) return false;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int DecodeZigZag(int v) => (v & 1) != 0 ? -((v + 1) >> 1) : (v >> 1);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int InterleaveNibbles(int lowNibble, int highNibble)
        => (lowNibble & 1) | ((highNibble & 1) << 1)
         | ((lowNibble & 2) << 1) | ((highNibble & 2) << 2)
         | ((lowNibble & 4) << 2) | ((highNibble & 4) << 3)
         | ((lowNibble & 8) << 3) | ((highNibble & 8) << 4);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong PackRow12(byte left, byte right) => (uint)(left | (right << 6));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int LooseUnknownBlockLimit(int blockCount) => Math.Max(2, blockCount / 1024);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt48LittleEndian(Span<byte> dest, ulong value)
    {
        dest[0] = (byte)value;
        dest[1] = (byte)(value >> 8);
        dest[2] = (byte)(value >> 16);
        dest[3] = (byte)(value >> 24);
        dest[4] = (byte)(value >> 32);
        dest[5] = (byte)(value >> 40);
    }

    private static RansModel[][] BuildModels()
    {
        var table = BcpackTables.ProbTables;
        var models = new RansModel[MaxWidth][];
        for (int w = 1; w <= MaxWidth; w++)
        {
            var row = new RansModel[16];
            for (int q = 0; q < 16; q++)
                row[q] = new RansModel(w, table[w - 1][q]);
            models[w - 1] = row;
        }
        return models;
    }

    private readonly record struct Bc1Endpoints(byte R0, byte G0, byte B0, byte R1, byte G1, byte B1);
    private readonly record struct Bc3Endpoints(byte A0, byte A1, byte R0, byte G0, byte B0, byte R1, byte G1, byte B1);
    private readonly record struct Bc4Endpoints(byte A0, byte A1);
    private readonly record struct Bc5Endpoints(byte R0, byte R1, byte G0, byte G1);

    private sealed class RansModel
    {
        private readonly int _width;
        private readonly int[] _freq;
        private readonly int[] _base;
        private readonly ushort[] _symFreq;
        private readonly ushort[] _symBase;

        internal RansModel(int width, int[] freq)
        {
            _width = width;
            _freq = freq;
            _base = new int[freq.Length];
            int acc = 0;
            for (int p = 0; p < freq.Length; p++)
            {
                if (freq[p] <= 0)
                    throw new InvalidDataException($"Probability table ({width}) has non-positive band frequency.");
                _base[p] = acc;
                acc += freq[p] * (p == 0 ? 1 : 1 << (p - 1));
            }
            if (acc != 65536)
                throw new InvalidDataException($"Probability table ({width}) does not sum to 65536 (got {acc}).");

            int symCount = 1 << width;
            _symFreq = new ushort[symCount];
            _symBase = new ushort[symCount];
            for (int s = 0; s < symCount; s++)
            {
                int p = s == 0 ? 0 : BitOperations.Log2((uint)s) + 1;
                int f = freq[p];
                int c = _base[p] + f * (s - (p == 0 ? 0 : 1 << (p - 1)));
                if (c == 0)
                {
                    f -= 1;
                    c = 1;
                }
                _symFreq[s] = (ushort)f;
                _symBase[s] = (ushort)c;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int Lookup(int cf)
        {
            int[] b = _base;
            if (cf < b[1]) return 0;
            int p = 1;
            int w = _width;
            while (p < w && cf >= b[p + 1]) p++;
            return (1 << (p - 1)) + (int)((uint)(cf - b[p]) / (uint)_freq[p]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal ulong Advance(int s, ulong x)
            => (ulong)_symFreq[s] * (x >> 16) + (x & 0xFFFFu) - (ulong)_symBase[s];
    }
}
