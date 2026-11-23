using System.Text.Json;
using System.Text.Json.Serialization;

namespace RpfPreview.Cli;

public static class CommonOptions
{
    public static readonly OptionSpec[] All =
    [
        new("decoder"),
        new("quiet", TakesValue: false)
    ];

    public static Reporter Reporter(CommandLine line) => new(line.Has("quiet"));
}

public static class CliJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

public static class ExitCodes
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int Usage = 2;
    public const int NoMatches = 3;
    public const int Cancelled = 130;
}
