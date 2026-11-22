using System.Text.Json;
using RpfPreview.Core;

namespace BcpackBptcCheck;

/// <summary>
/// Checks the BC6H/BC7 BCPack rebuilders against the reference decoder (tools/bcpack.py in Leonida) and the
/// strict-mode lower-mip fallback of TtpfReader, using only synthetic streams. Exits 0 when everything matches.
///     dotnet run -c Release --project Tools/BcpackBptcCheck [fixtures.json]
/// Regenerate fixtures.json with make_fixtures.py.
/// </summary>
internal static class Program
{
    private static int _failures, _checks;

    private static int Main(string[] args)
    {
        string path = args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "fixtures.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var chunks = document.RootElement.GetProperty("chunks");
        foreach (var chunk in chunks.EnumerateArray()) CheckChunk(chunk);
        CheckNonVacuous(chunks);
        try { CheckPool(document.RootElement.GetProperty("pool")); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException) { Expect(false, $"pool check threw {ex.GetType().Name}: {ex.Message}"); }
        Console.WriteLine($"{_checks} checks, {_failures} failed");
        return _failures == 0 ? 0 : 1;
    }

    private static void Expect(bool condition, string what)
    {
        _checks++;
        if (condition) return;
        _failures++;
        Console.WriteLine($"FAIL {what}");
    }

    private static void CheckChunk(JsonElement chunk)
    {
        string name = chunk.GetProperty("name").GetString()!;
        int codec = chunk.GetProperty("codec").GetInt32(), size = chunk.GetProperty("size").GetInt32();
        byte[] packed = Convert.FromBase64String(chunk.GetProperty("packed").GetString()!);
        foreach (var expected in chunk.GetProperty("cases").EnumerateArray())
        {
            bool allow = expected.GetProperty("allowProvisional").GetBoolean();
            string want = expected.GetProperty("result").GetString()!, label = $"{name} (allowProvisional={allow})";
            string got;
            BcpackDecodeResult? result = null;
            try { result = BcpackDecoder.DecodeWithInfo(packed, codec, size, allow); got = "ok"; }
            catch (BcpackProvisionalException) { got = "provisional"; }
            catch (InvalidDataException) { got = "invalid"; }
            catch (NotSupportedException) { got = "unsupported codec"; }
            Expect(got == want, $"{label}: expected {want}, got {got}");
            if (want != "ok" || result is null) continue;
            Expect(result.Data.AsSpan().SequenceEqual(Convert.FromBase64String(expected.GetProperty("data").GetString()!)), $"{label}: output bytes differ");
            var labels = expected.GetProperty("labels").EnumerateArray().Select(l => l.GetString()!).Order(StringComparer.Ordinal);
            Expect(labels.SequenceEqual(result.ProvisionalEntries.Order(StringComparer.Ordinal)), $"{label}: provisional labels differ");
        }
    }

    /// <summary>A check that never saw a successful decode of each codec would prove nothing.</summary>
    private static void CheckNonVacuous(JsonElement chunks)
    {
        foreach (int codec in new[] { 6, 7 })
            foreach (bool allow in new[] { false, true })
            {
                int ok = chunks.EnumerateArray().Where(c => c.GetProperty("codec").GetInt32() == codec)
                    .SelectMany(c => c.GetProperty("cases").EnumerateArray())
                    .Count(c => c.GetProperty("allowProvisional").GetBoolean() == allow && c.GetProperty("result").GetString() == "ok");
                Expect(ok > 0, $"fixtures contain successful codec {codec} decodes (allowProvisional={allow})");
            }
    }

    private static void CheckPool(JsonElement pool)
    {
        var clean = pool[0];
        var referencing = pool[1];
        byte[] cleanData = Convert.FromBase64String(clean.GetProperty("cases")[0].GetProperty("data").GetString()!);
        byte[] provisionalData = Convert.FromBase64String(referencing.GetProperty("cases")[1].GetProperty("data").GetString()!);
        Expect(referencing.GetProperty("cases")[0].GetProperty("result").GetString() == "provisional", "pool: the history-reading chunk is refused in strict mode");

        string dir = Directory.CreateTempSubdirectory("bcpack-bptc-check").FullName;
        try
        {
            string cleanPool = Path.Combine(dir, "clean.ttpf"), referencingPool = Path.Combine(dir, "referencing.ttpf");
            SyntheticPool.Write(cleanPool, Convert.FromBase64String(clean.GetProperty("packed").GetString()!));
            SyntheticPool.Write(referencingPool, Convert.FromBase64String(referencing.GetProperty("packed").GetString()!));

            var texture = TtpfReader.Inspect(cleanPool).Textures[0];
            Expect(texture.CanExtract && texture.Mips == 4 && texture.BaseMipLevel == 0, "pool: a BC7 BCPack top mip is exportable");

            byte[] full = TtpfReader.CreateDds(cleanPool, 0);
            CheckDds(full, "clean pool", width: 64, mips: 4, length: 148 + 4096 + 1024 + 256 + 64);
            Expect(SameBlocks(full.AsSpan(148, 4096), cleanData), "clean pool: mip 0 holds the reference BC7 blocks");
            byte[] lowerMips = full[(148 + 4096)..];

            byte[] fallback = TtpfReader.CreateDds(referencingPool, 0);
            CheckDds(fallback, "strict fallback", width: 32, mips: 3, length: 148 + 1024 + 256 + 64);
            Expect(fallback.AsSpan(148).SequenceEqual(lowerMips), "strict fallback: the lower mips are the zlib/raw mips of the pool");

            byte[] provisional = TtpfReader.CreateDds(referencingPool, 0, allowProvisional: true);
            CheckDds(provisional, "provisional export", width: 64, mips: 4, length: 148 + 4096 + 1024 + 256 + 64);
            Expect(SameBlocks(provisional.AsSpan(148, 4096), provisionalData), "provisional export: mip 0 holds the reference BC7 blocks");
            Expect(provisional.AsSpan(148 + 4096).SequenceEqual(lowerMips), "provisional export: lower mips unchanged");

            Expect(TtpfReader.DecodeTexturePreview(cleanPool, 0, 64).Width == 64, "preview: the full-size mip decodes");
            Expect(TtpfReader.DecodeTexturePreview(referencingPool, 0, 64).Width == 32, "preview: a refused mip falls back to the next smaller mip");
        }
        finally { Directory.Delete(dir, true); }
    }

    private static void CheckDds(byte[] dds, string label, int width, int mips, int length)
    {
        Expect(dds.Length == length, $"{label}: DDS length {dds.Length}, expected {length}");
        Expect(dds.Length > 31 && BitConverter.ToInt32(dds, 12) == width && BitConverter.ToInt32(dds, 16) == width && BitConverter.ToInt32(dds, 28) == mips,
            $"{label}: DDS header size/mip count");
    }

    /// <summary>The detiled mip holds the same 16-byte blocks as the decoded chunk, in tile order instead of linear order.</summary>
    private static bool SameBlocks(ReadOnlySpan<byte> exported, byte[] decoded)
    {
        static List<string> Blocks(ReadOnlySpan<byte> data)
        {
            var list = new List<string>();
            for (int at = 0; at < data.Length; at += 16) list.Add(Convert.ToHexString(data.Slice(at, 16)));
            list.Sort(StringComparer.Ordinal);
            return list;
        }
        return exported.Length == decoded.Length && Blocks(exported).SequenceEqual(Blocks(decoded));
    }
}
