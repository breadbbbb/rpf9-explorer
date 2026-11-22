using System.Text;

namespace BcpackBptcCheck;

/// <summary>
/// Writes a minimal RDS5 texture pool (one 64x64 BC7 texture, 4 mips, 4 KB tiles) from synthetic bytes.
/// Virtual layout: metadata page, a raw 4 KB chunk with the packed mips 1..3, then one BCPack BC7 chunk with mip 0.
/// </summary>
internal static class SyntheticPool
{
    private const int Page = 4096;

    public static void Write(string path, byte[] bcpackMip0)
    {
        int packedPages = (bcpackMip0.Length + 255) / 256 * 256;
        long tailSource = 256 + Page, mipSource = tailSource + Page, fileSize = mipSource + packedPages;
        long virtualSize = 3L * Page;
        using var file = new MemoryStream();
        using var w = new BinaryWriter(file, Encoding.UTF8, true);
        w.Write("RDS5"u8); w.Write(4u); w.Write((ulong)virtualSize | (22UL << 56)); w.Write(1u); w.Write(0u);
        w.Write(Entry(tailSource, 0, Page, 0)); w.Write(Entry(mipSource, 7, 2 * Page, packedPages - bcpackMip0.Length)); w.Write(Entry(fileSize, 0, 3 * Page, 0));
        Pad(file, 256);
        w.Write("ETPF"u8); w.Write(634u); w.Write(1u); w.Write(24u);
        // width 64, height 64, 3 packed mips; 1 non-tail mip, DXGI 98 (BC7_UNORM), 2D, storage mode 2 (4 KB tiles).
        w.Write(63u | (63u << 14) | (3u << 28)); w.Write(1u | (98u << 3) | (1u << 21) | (2u << 23)); w.Write(1u); w.Write(0u);
        w.Write(0x1122334455667788UL);
        w.Write(new byte[32]);
        Pad(file, 256 + Page);
        for (int i = 0; i < Page; i++) w.Write((byte)((i * 31 + 7) >> 2));
        w.Write(bcpackMip0);
        Pad(file, fileSize);
        File.WriteAllBytes(path, file.ToArray());
    }

    private static ulong Entry(long source, int codec, long virtualOffset, int padding) =>
        (ulong)(source / 256) | ((ulong)codec << 26) | ((ulong)(virtualOffset / Page) << 30) | ((ulong)padding << 52);

    private static void Pad(MemoryStream stream, long length) => stream.Write(new byte[length - stream.Length]);
}
