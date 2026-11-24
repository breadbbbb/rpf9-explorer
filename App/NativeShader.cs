using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace RpfPreview.App;

internal static class NativeShader
{
    private static uint U(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt32LittleEndian(s[at..]);
    private static ulong Q(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt64LittleEndian(s[at..]);
    private static readonly Lazy<bool> Initialized = new(() => { LLVMInitializeAMDGPUTargetInfo(); LLVMInitializeAMDGPUTarget(); LLVMInitializeAMDGPUTargetMC(); LLVMInitializeAMDGPUDisassembler(); return true; });
    [DllImport("libLLVM")] private static extern void LLVMInitializeAMDGPUTargetInfo();
    [DllImport("libLLVM")] private static extern void LLVMInitializeAMDGPUTarget();
    [DllImport("libLLVM")] private static extern void LLVMInitializeAMDGPUTargetMC();
    [DllImport("libLLVM")] private static extern void LLVMInitializeAMDGPUDisassembler();
    [DllImport("libLLVM")] private static extern IntPtr LLVMCreateDisasmCPU(string triple, string cpu, IntPtr info, int tag, IntPtr op, IntPtr sym);
    [DllImport("libLLVM")] private static extern nuint LLVMDisasmInstruction(IntPtr ctx, IntPtr data, ulong size, ulong pc, [Out] byte[] text, nuint length);
    [DllImport("libLLVM")] private static extern void LLVMDisasmDispose(IntPtr ctx);

    public static string? Describe(ReadOnlySpan<byte> dxil)
    {
        if (dxil.Length < 48 || !dxil.Slice(8, 4).SequenceEqual("DXIL"u8) || !dxil.Slice(24, 4).SequenceEqual("XDAT"u8)) return null;
        uint count = U(dxil, 28);
        if (count is 0 or > 64 || U(dxil, 32) != count * 12 || 36 + count * 12 > dxil.Length) return null;
        var output = new StringBuilder();
        for (int i = 0; i < count; i++)
        {
            int header = 36 + i * 12; uint at = U(dxil, header + 4), length = U(dxil, header + 8);
            if (U(dxil, header) != 13 || length < 24 || (ulong)at + length > (ulong)dxil.Length) continue;
            var block = dxil.Slice((int)at, (int)length);
            if (!block[..4].SequenceEqual("XBCH"u8) || U(block, 4) != 3 || Q(block, 8) != length - 24) continue;
            ulong plain = Q(block, 16); if (plain is < 192 or > 16777216) continue;
            byte[] bytes = new byte[(int)plain]; using var packed = new MemoryStream(block[24..].ToArray()); using var inflater = new ZLibStream(packed, CompressionMode.Decompress);
            inflater.ReadExactly(bytes); if (inflater.ReadByte() != -1 || !bytes.AsSpan(0, 4).SequenceEqual("FSSM"u8) || U(bytes, 4) != 0x101 || Q(bytes, 8) != plain - 16) continue;
            var visited = new HashSet<int>();
            for (int slot = 24; slot < 120; slot += 8)
            {
                ulong ptr = Q(bytes, slot); if (ptr > plain - 32 || !visited.Add((int)ptr)) continue;
                int descriptor = 16 + (int)ptr; ulong offset = Q(bytes, descriptor), size = Q(bytes, descriptor + 8);
                if (offset > plain - 16 || size < 32 || size > plain - 16 - offset || size % 4 != 0) continue;
                int start = 16 + (int)offset;
                if (U(bytes, start) >> 16 != 0xd580 || U(bytes, start + 12) >> 16 != 0xd580) continue;
                int end = start + (int)size;
                while (end > start + 24 && U(bytes, end - 4) == 0xbf9f0000) end -= 4;
                if (end <= start + 24 || bytes.AsSpan(start + 24, end - start - 24).IndexOf(new byte[] { 0, 0, 0x81, 0xbf }) < 0) continue;
                output.AppendLine(Disassemble(bytes, start + 24, end));
            }
        }
        return output.Length == 0 ? null : output.ToString();
    }
    private static string Disassemble(byte[] code, int start, int end)
    {
        _ = Initialized.Value;
        var ctx = LLVMCreateDisasmCPU("amdgcn-amd-amdhsa", "gfx1030", IntPtr.Zero, 0, IntPtr.Zero, IntPtr.Zero);
        if (ctx == IntPtr.Zero) throw new NotSupportedException("The packaged LLVM library has no RDNA 2 disassembler.");
        var pinned = GCHandle.Alloc(code, GCHandleType.Pinned);
        try
        {
            var asm = new StringBuilder(); var lifted = new StringBuilder(); var buffer = new byte[768]; int decoded = 0, translated = 0;
            for (int at = start; at < end;)
            {
                if (decoded >= 16384) { asm.AppendLine("Display limit reached."); break; }
                int n = (int)LLVMDisasmInstruction(ctx, pinned.AddrOfPinnedObject() + at, (ulong)(end - at), (ulong)(at - start), buffer, (nuint)buffer.Length);
                if (n <= 0 || n > end - at) { asm.AppendLine($"{at - start:X6}   Unrecognized encoding 0x{U(code, at):X8}. Decoding stopped here."); break; }
                string op = Encoding.ASCII.GetString(buffer, 0, Array.IndexOf(buffer, (byte)0)).Trim();
                asm.AppendLine($"{at - start:X6}   {op}"); string? statement = Lift(op, at - start, n);
                if (statement != null) translated++;
                lifted.AppendLine($"L{at - start:X6}:   " + (statement ?? $"isa({op})")); decoded++; at += n;
            }
            return $"RDNA 2 shader pseudocode\n{translated:N0} translated operations in {decoded:N0} decoded instructions. Hardware operations retain their ISA spelling.\n\n{lifted}\nGPU instructions\n{asm}";
        }
        finally { pinned.Free(); LLVMDisasmDispose(ctx); }
    }
    private static string? Lift(string line, int pc, int width)
    {
        var parts = line.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries); string op = parts[0].Replace("_e32", "").Replace("_e64", "");
        string tail = parts.Length > 1 ? parts[1] : ""; var a = tail.Split(',', StringSplitOptions.TrimEntries);
        if (op == "s_endpgm") return "return;";
        if (op == "s_branch" && short.TryParse(tail, out short rel)) return $"goto L{pc + width + 4 * rel:X6};";
        if (op == "s_waitcnt") return $"wait({tail});";
        if (op == "exp") return $"export_outputs({tail});";
        if (a.Length < 2 || !Regex.IsMatch(a[0], @"^(?:[vs]\d+|[vs]\[\d+:\d+\])$")) return null;
        if (a.Length == 2 && op is "s_mov_b32" or "s_mov_b64" or "v_mov_b32") return $"{a[0]} = {a[1]};";
        if (a.Length == 2 && op.StartsWith("v_cvt_", StringComparison.Ordinal)) return $"{a[0]} = {op[6..]}({a[1]});";
        if (a.Length == 4 && op == "v_fma_f32") return $"{a[0]} = fma_f32({a[1]}, {a[2]}, {a[3]});";
        if (a.Length != 3) return null;
        string? sign = op switch { "v_add_f32" => "+", "v_mul_f32" => "*", "v_sub_f32" => "-", "v_and_b32" => "&", "v_or_b32" => "|", "v_xor_b32" => "^", _ => null };
        if (sign != null) return $"{a[0]} = {a[1]} {sign} {a[2]};";
        if (op == "v_lshlrev_b32") return $"{a[0]} = {a[2]} << ({a[1]} & 31);";
        return null;
    }
}
