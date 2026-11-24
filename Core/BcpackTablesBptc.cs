using System.Collections.Frozen;

namespace RpfPreview.Core;

internal readonly record struct Bc7ConfigEntry(byte Mode, byte Partition, byte Rotation, byte IndexSelection);

internal readonly record struct Bc6hPlanItem(sbyte Channel, sbyte Slot, byte BitIndex, byte StreamPos);

internal readonly record struct Bc6hModeInfo(
    byte Code,
    bool Transformed,
    byte BaseBits,
    byte DeltaR,
    byte DeltaG,
    byte DeltaB,
    byte Regions,
    byte HeaderBits,
    Bc6hPlanItem[] Plan);

internal sealed class Bc6hHistoryTable
{
    public static readonly Bc6hHistoryTable Empty = new();

    public int[] EndpointHistory { get; }
    public int[] IndexHistory { get; }
    public int[] EndpointClass { get; }
    public int[] IndexClass { get; }
    public byte[] EndpointConfirmedBits { get; }
    public byte[] IndexConfirmedBits { get; }

    public Bc6hHistoryTable()
        : this(new int[256 * 12], new int[256 * 16], new int[256], new int[256], new byte[256 * 12], new byte[256 * 16])
    {
    }

    public Bc6hHistoryTable(
        int[] endpointHistory,
        int[] indexHistory,
        int[] endpointClass,
        int[] indexClass,
        byte[] endpointConfirmedBits,
        byte[] indexConfirmedBits)
    {
        EndpointHistory = endpointHistory;
        IndexHistory = indexHistory;
        EndpointClass = endpointClass;
        IndexClass = indexClass;
        EndpointConfirmedBits = endpointConfirmedBits;
        IndexConfirmedBits = indexConfirmedBits;
    }
}

internal sealed class Bc7HistoryTable
{
    public static readonly Bc7HistoryTable Empty = new();

    public int[] HistoryEndpoints { get; }
    public int[] HistoryEndpointBits { get; }
    public int[] HistoryEndpointConfirmedBits { get; }
    public int[] HistoryIndices { get; }
    public int[] HistoryIndexBits { get; }
    public int[] HistoryIndexConfirmedBits { get; }
    public FrozenSet<(int Entry, int Slot)> LastResort { get; }

    public Bc7HistoryTable()
        : this(
            new int[256 * 24],
            new int[256 * 24],
            new int[256 * 24],
            new int[256 * 32],
            new int[256 * 32],
            new int[256 * 32],
            FrozenSet<(int Entry, int Slot)>.Empty)
    {
    }

    public Bc7HistoryTable(
        int[] historyEndpoints,
        int[] historyEndpointBits,
        int[] historyEndpointConfirmedBits,
        int[] historyIndices,
        int[] historyIndexBits,
        int[] historyIndexConfirmedBits,
        FrozenSet<(int Entry, int Slot)> lastResort)
    {
        HistoryEndpoints = historyEndpoints;
        HistoryEndpointBits = historyEndpointBits;
        HistoryEndpointConfirmedBits = historyEndpointConfirmedBits;
        HistoryIndices = historyIndices;
        HistoryIndexBits = historyIndexBits;
        HistoryIndexConfirmedBits = historyIndexConfirmedBits;
        LastResort = lastResort;
    }
}

internal static class BcpackTablesBptc
{
    private static readonly ushort[] Bc7Part2Packed =
    [
        0xcccc, 0x8888, 0xeeee, 0xecc8, 0xc880, 0xfeec, 0xfec8, 0xec80,
        0xc800, 0xffec, 0xfe80, 0xe800, 0xffe8, 0xff00, 0xfff0, 0xf000,
        0xf710, 0x008e, 0x7100, 0x08ce, 0x008c, 0x7310, 0x3100, 0x8cce,
        0x088c, 0x3110, 0x6666, 0x366c, 0x17e8, 0x0ff0, 0x718e, 0x399c,
        0xaaaa, 0xf0f0, 0x5a5a, 0x33cc, 0x3c3c, 0x55aa, 0x9696, 0xa55a,
        0x73ce, 0x13c8, 0x324c, 0x3bdc, 0x6996, 0xc33c, 0x9966, 0x0660,
        0x0272, 0x04e4, 0x4e40, 0x2720, 0xc936, 0x936c, 0x39c6, 0x639c,
        0x9336, 0x9cc6, 0x817e, 0xe718, 0xccf0, 0x0fcc, 0x7744, 0xee22
    ];

    private static readonly uint[] Bc7Part3Packed =
    [
        0xaa685050, 0x6a5a5040, 0x5a5a4200, 0x5450a0a8, 0xa5a50000, 0xa0a05050,
        0x5555a0a0, 0x5a5a5050, 0xaa550000, 0xaa555500, 0xaaaa5500, 0x90909090,
        0x94949494, 0xa4a4a4a4, 0xa9a59450, 0x2a0a4250, 0xa5945040, 0x0a425054,
        0xa5a5a500, 0x55a0a0a0, 0xa8a85454, 0x6a6a4040, 0xa4a45000, 0x1a1a0500,
        0x0050a4a4, 0xaaa59090, 0x14696914, 0x69691400, 0xa08585a0, 0xaa821414,
        0x50a4a450, 0x6a5a0200, 0xa9a58000, 0x5090a0a8, 0xa8a09050, 0x24242424,
        0x00aa5500, 0x24924924, 0x24499224, 0x50a50a50, 0x500aa550, 0xaaaa4444,
        0x66660000, 0xa5a0a5a0, 0x50a050a0, 0x69286928, 0x44aaaa44, 0x66666600,
        0xaa444444, 0x54a854a8, 0x95809580, 0x96969600, 0xa85454a8, 0x80959580,
        0xaa141414, 0x96960000, 0xaaaa1414, 0xa05050a0, 0xa0a5a5a0, 0x96000000,
        0x40804080, 0xa9a8a9a8, 0xaaaaaa44, 0x2a4a5254
    ];

    public static readonly byte[] Bc7Anchor2 =
    [
        15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15,
        15, 2, 8, 2, 2, 8, 8, 15, 2, 8, 2, 2, 8, 8, 2, 2,
        15, 15, 6, 8, 2, 8, 15, 15, 2, 8, 2, 2, 2, 15, 15, 6,
        6, 2, 6, 8, 15, 15, 2, 2, 15, 15, 15, 15, 15, 2, 2, 15
    ];

    public static readonly byte[] Bc7Anchor3A =
    [
        3, 3, 15, 15, 8, 3, 15, 15, 8, 8, 6, 6, 6, 5, 3, 3,
        3, 3, 8, 15, 3, 3, 6, 10, 5, 8, 8, 6, 8, 5, 15, 15,
        8, 15, 3, 5, 6, 10, 8, 15, 15, 3, 15, 5, 15, 15, 15, 15,
        3, 15, 5, 5, 5, 8, 5, 10, 5, 10, 8, 13, 15, 12, 3, 3
    ];

    public static readonly byte[] Bc7Anchor3B =
    [
        15, 8, 8, 3, 15, 15, 3, 8, 15, 15, 15, 15, 15, 15, 15, 8,
        15, 8, 15, 3, 15, 8, 15, 8, 3, 15, 6, 10, 15, 15, 10, 8,
        15, 3, 15, 10, 10, 8, 9, 10, 6, 15, 8, 15, 3, 6, 6, 8,
        15, 3, 15, 15, 15, 15, 15, 15, 15, 15, 15, 15, 3, 15, 15, 8
    ];

    private static readonly byte[] Bc7Mode2Head =
    [
        63, 59, 58, 57, 54, 53, 52, 47, 43, 42, 40,
        38, 37, 35, 34, 32, 23, 21, 18, 17, 16, 11
    ];

    public static readonly byte[] Deint0 = BuildDeint(0);
    public static readonly byte[] Deint1 = BuildDeint(1);
    public static readonly ushort[] Spread8 = BuildSpread8();

    public static readonly byte[][] Bc7Partition2 = BuildPartition2();
    public static readonly byte[][] Bc7Partition3 = BuildPartition3();
    public static readonly Bc7ConfigEntry[] Bc7Config = BuildBc7Config();
    public static readonly short[] Bc7RankOf = BuildBc7RankOf();
    public static readonly bool[] Bc7ConfigInferred = BuildBc7Inferred();

    public static readonly Bc6hModeInfo[] Bc6hModes = BuildBc6hModes();

    private static byte[] BuildDeint(int shift)
    {
        byte[] t = new byte[256];
        for (int s = 0; s < 256; s++)
        {
            t[s] = (byte)(
                ((s >> (0 + shift)) & 1)
                | (((s >> (2 + shift)) & 1) << 1)
                | (((s >> (4 + shift)) & 1) << 2)
                | (((s >> (6 + shift)) & 1) << 3));
        }
        return t;
    }

    private static ushort[] BuildSpread8()
    {
        ushort[] t = new ushort[256];
        for (int v = 0; v < 256; v++)
        {
            int acc = 0;
            for (int k = 0; k < 8; k++)
                acc |= ((v >> k) & 1) << (2 * k);
            t[v] = (ushort)acc;
        }
        return t;
    }

    private static byte[][] BuildPartition2()
    {
        var table = new byte[64][];
        for (int i = 0; i < 64; i++)
        {
            ushort mask = Bc7Part2Packed[i];
            byte[] row = new byte[16];
            for (int p = 0; p < 16; p++) row[p] = (byte)((mask >> p) & 1);
            table[i] = row;
        }
        return table;
    }

    private static byte[][] BuildPartition3()
    {
        var table = new byte[64][];
        for (int i = 0; i < 64; i++)
        {
            uint mask = Bc7Part3Packed[i];
            byte[] row = new byte[16];
            for (int p = 0; p < 16; p++) row[p] = (byte)((mask >> (2 * p)) & 3);
            table[i] = row;
        }
        return table;
    }

    private static Bc7ConfigEntry[] BuildBc7Config()
    {
        var headSet = new HashSet<byte>(Bc7Mode2Head);
        var list = new List<Bc7ConfigEntry>(286);
        for (int r = 0; r < 64; r++) list.Add(new(7, (byte)(63 - r), 0, 0));
        list.Add(new(6, 0, 0, 0));
        for (int r = 0; r < 4; r++) list.Add(new(5, 0, (byte)(3 - r), 0));
        for (int r = 0; r < 8; r++) list.Add(new(4, 0, (byte)((7 - r) >> 1), (byte)((7 - r) & 1)));
        for (int r = 0; r < 64; r++) list.Add(new(3, (byte)(63 - r), 0, 0));
        foreach (byte p in Bc7Mode2Head) list.Add(new(2, p, 0, 0));
        for (int r = 0; r < 64; r++) list.Add(new(1, (byte)(63 - r), 0, 0));
        for (int p = 15; p >= 8; p--) list.Add(new(0, (byte)p, 0, 0));
        for (int p = 5; p >= 0; p--) list.Add(new(0, (byte)p, 0, 0));
        for (int p = 63; p >= 0; p--)
        {
            if (!headSet.Contains((byte)p)) list.Add(new(2, (byte)p, 0, 0));
        }
        list.Add(new(0, 7, 0, 0));
        list.Add(new(0, 6, 0, 0));
        list.Add(new(255, 255, 0, 0));
        return list.ToArray();
    }

    private static short[] BuildBc7RankOf()
    {
        var cells = new (int A, int B)[512];
        int n = 0;
        for (int a = 0; a < 32; a++)
            for (int b = 0; b < 16; b++)
                cells[n++] = (a, b);
        Array.Sort(cells, static (x, y) =>
        {
            int px = (x.A + 1) * (x.B + 1);
            int py = (y.A + 1) * (y.B + 1);
            int cmp = px.CompareTo(py);
            return cmp != 0 ? cmp : x.A.CompareTo(y.A);
        });
        short[] rankOf = new short[512];
        Array.Fill(rankOf, (short)-1);
        for (int rank = 0; rank < Bc7Config.Length; rank++)
            rankOf[cells[rank].A * 16 + cells[rank].B] = (short)rank;
        return rankOf;
    }

    private static bool[] BuildBc7Inferred()
    {
        bool[] table = new bool[286];
        int[] ranks = [149, 151, 152, 153, 246, 248, 251, 252, 253, 254];
        foreach (int r in ranks) table[r] = true;
        return table;
    }

    private static Bc6hModeInfo[] BuildBc6hModes()
    {
        var defs = new (int Mode, byte Code, bool Trans, byte BaseBits, byte Dr, byte Dg, byte Db, byte Regions, string Layout)[]
        {
            (1,  0x00, true,  10, 5,  5,  5,  2, "gy4 by4 bz4 rw10 gw10 bw10 rx5 gz4 gy4_ gx5 bz0 gz4_ bx5 bz1 by4_ ry5 bz2 rz5 bz3 d"),
            (2,  0x01, true,  7,  6,  6,  6,  2, "gy5 gz4 gz5 rw7 bz0 bz1 by4 gw7 by5 bz2 gy4 bw7 bz3 bz5 bz4 rx6 gy4_ gx6 gz4_ bx6 by4_ ry6 rz6 d"),
            (3,  0x02, true,  11, 5,  4,  4,  2, "rw10 gw10 bw10 rx5 rw:10 gy4_ gx4 gw:10 bz0 gz4_ bx4 bw:10 bz1 by4_ ry5 bz2 rz5 bz3 d"),
            (4,  0x06, true,  11, 4,  5,  4,  2, "rw10 gw10 bw10 rx4 rw:10 gz4 gy4_ gx5 gw:10 gz4_ bx4 bw:10 bz1 by4_ ry4 bz0 bz2 rz4 gy4 bz3 d"),
            (5,  0x0A, true,  11, 4,  4,  5,  2, "rw10 gw10 bw10 rx4 rw:10 by4 gy4_ gx4 gw:10 bz0 gz4_ bx5 bw:10 by4_ ry4 bz1 bz2 rz4 bz4 bz3 d"),
            (6,  0x0E, true,  9,  5,  5,  5,  2, "rw9 by4 gw9 gy4 bw9 bz4 rx5 gz4 gy4_ gx5 bz0 gz4_ bx5 bz1 by4_ ry5 bz2 rz5 bz3 d"),
            (7,  0x12, true,  8,  6,  5,  5,  2, "rw8 gz4 by4 gw8 bz2 gy4 bw8 bz3 bz4 rx6 gy4_ gx5 bz0 gz4_ bx5 bz1 by4_ ry6 rz6 d"),
            (8,  0x16, true,  8,  5,  6,  5,  2, "rw8 bz0 by4 gw8 gy5 gy4 bw8 gz5 bz4 rx5 gz4 gy4_ gx6 gz4_ bx5 bz1 by4_ ry5 bz2 rz5 bz3 d"),
            (9,  0x1A, true,  8,  5,  5,  6,  2, "rw8 bz1 by4 gw8 by5 gy4 bw8 bz5 bz4 rx5 gz4 gy4_ gx5 bz0 gz4_ bx6 by4_ ry5 bz2 rz5 bz3 d"),
            (10, 0x1E, false, 6,  6,  6,  6,  2, "rw6 gz4 bz0 bz1 by4 gw6 gy5 by5 bz2 gy4 bw6 gz5 bz3 bz5 bz4 rx6 gy4_ gx6 gz4_ bx6 by4_ ry6 rz6 d"),
            (11, 0x03, false, 10, 10, 10, 10, 1, "rw10 gw10 bw10 rx10 gx10 bx10"),
            (12, 0x07, true,  11, 9,  9,  9,  1, "rw10 gw10 bw10 rx9 rw:10 gx9 gw:10 bx9 bw:10"),
            (13, 0x0B, true,  12, 8,  8,  8,  1, "rw10 gw10 bw10 rx8 rw:11-10 gx8 gw:11-10 bx8 bw:11-10"),
            (14, 0x0F, true,  16, 4,  4,  4,  1, "rw10 gw10 bw10 rx4 rw:15-10 gx4 gw:15-10 bx4 bw:15-10")
        };

        var modes = new Bc6hModeInfo[15];
        foreach (var d in defs)
        {
            (Bc6hPlanItem[] plan, byte headerBits) = ParseBc6hPlan(d.Code, d.Layout);
            modes[d.Mode] = new(d.Code, d.Trans, d.BaseBits, d.Dr, d.Dg, d.Db, d.Regions, headerBits, plan);
        }
        return modes;
    }

    private static (Bc6hPlanItem[] Plan, byte HeaderBits) ParseBc6hPlan(byte code, string layout)
    {
        int pos = code < 2 ? 2 : 5;
        var plan = new List<Bc6hPlanItem>(80);
        foreach (string token in layout.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token == "d")
            {
                for (int b = 0; b < 5; b++)
                    plan.Add(new(-1, -1, (byte)b, (byte)(pos + b)));
                pos += 5;
                continue;
            }

            int colon = token.IndexOf(':');
            if (colon >= 0)
            {
                (sbyte c, sbyte q) = FieldCoords(token.AsSpan(0, colon));
                ReadOnlySpan<char> span = token.AsSpan(colon + 1);
                int dash = span.IndexOf('-');
                int hi = dash >= 0 ? int.Parse(span[..dash]) : int.Parse(span);
                int lo = dash >= 0 ? int.Parse(span[(dash + 1)..]) : hi;
                for (int b = hi; b >= lo; b--)
                    plan.Add(new(c, q, (byte)b, (byte)pos++));
            }
            else if (token.EndsWith('_'))
            {
                (sbyte c, sbyte q) = FieldCoords(token.AsSpan(0, 2));
                int n = int.Parse(token.AsSpan(2, token.Length - 3));
                for (int b = 0; b < n; b++)
                    plan.Add(new(c, q, (byte)b, (byte)(pos + b)));
                pos += n;
            }
            else
            {
                ReadOnlySpan<char> name = token.AsSpan(0, 2);
                (sbyte c, sbyte q) = FieldCoords(name);
                int n = int.Parse(token.AsSpan(2));
                if (name[1] is 'w' or 'x' || name is "ry" or "rz")
                {
                    for (int b = 0; b < n; b++)
                        plan.Add(new(c, q, (byte)b, (byte)(pos + b)));
                    pos += n;
                }
                else
                {
                    plan.Add(new(c, q, (byte)n, (byte)pos++));
                }
            }
        }
        return (plan.ToArray(), (byte)pos);
    }

    private static (sbyte Channel, sbyte Slot) FieldCoords(ReadOnlySpan<char> name)
    {
        sbyte c = name[0] switch { 'r' => 0, 'g' => 1, 'b' => 2, _ => throw new InvalidDataException($"Unknown BC6H channel '{name}'.") };
        sbyte q = name[1] switch { 'w' => 0, 'x' => 1, 'y' => 2, 'z' => 3, _ => throw new InvalidDataException($"Unknown BC6H slot '{name}'.") };
        return (c, q);
    }
}
