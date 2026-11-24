using System.Buffers.Binary;
using System.Text;
using RpfPreview.Core;

namespace RpfPreview.App;

internal static class ShaderPreview
{
    public static string Describe(byte[] bytes)
    {
        if (!Rpf9ShaderRecovery.ValidateDxbc(bytes)) throw new System.IO.InvalidDataException("The recovered shader container bounds do not validate.");
        var text = new StringBuilder($"Native shader container\n\nDXBC · {bytes.Length:N0} bytes\n\nChunks:\n");
        int count = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(28)));
        for (int i = 0; i < count; i++)
        {
            int at = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(32 + i * 4)));
            string name = Encoding.ASCII.GetString(bytes, at, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(at + 4));
            if (name=="DXIL" && NativeShader.Describe(bytes.AsSpan(at+8,(int)size)) is {} program) text.AppendLine().AppendLine(program);
            text.AppendLine($"  {name}  ·  0x{at:X8}  ·  {size:N0} bytes");
        }
        text.AppendLine("\nExtract selected saves the compiled container. Native code ranges are displayed when their wrapper and program bounds validate.");
        return text.ToString();
    }
}
