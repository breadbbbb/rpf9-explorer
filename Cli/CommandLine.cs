namespace RpfPreview.Cli;

public sealed class UsageException(string message) : Exception(message);

public sealed record OptionSpec(string Name, string? Alias = null, bool TakesValue = true, bool Repeatable = false);

public sealed class CommandLine
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);

    private CommandLine(List<string> positionals) => Positionals = positionals;

    public IReadOnlyList<string> Positionals { get; }

    public static CommandLine Parse(IReadOnlyList<string> args, IReadOnlyList<OptionSpec> specs)
    {
        var byName = new Dictionary<string, OptionSpec>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            byName["--" + spec.Name] = spec;
            if (spec.Alias is not null) byName["-" + spec.Alias] = spec;
        }
        var positionals = new List<string>();
        var result = new CommandLine(positionals);
        bool literal = false;
        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (literal || arg.Length < 2 || arg[0] != '-')
            {
                positionals.Add(arg);
                continue;
            }
            if (arg == "--")
            {
                literal = true;
                continue;
            }
            string key = arg, inline = "";
            bool hasInline = false;
            int equals = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 2)
            {
                key = arg[..equals];
                inline = arg[(equals + 1)..];
                hasInline = true;
            }
            if (!byName.TryGetValue(key, out var option)) throw new UsageException($"Unknown option '{key}'.");
            if (!result._values.TryGetValue(option.Name, out var values)) result._values[option.Name] = values = [];
            else if (!option.Repeatable) throw new UsageException($"Option '--{option.Name}' was given more than once.");
            if (!option.TakesValue)
            {
                if (hasInline) throw new UsageException($"Option '--{option.Name}' does not take a value.");
                values.Add("true");
                continue;
            }
            if (hasInline) values.Add(inline);
            else if (i + 1 < args.Count) values.Add(args[++i]);
            else throw new UsageException($"Option '--{option.Name}' needs a value.");
        }
        return result;
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string? Value(string name) => _values.TryGetValue(name, out var values) ? values[^1] : null;

    public IReadOnlyList<string> Values(string name) => _values.TryGetValue(name, out var values) ? values : [];

    public int Integer(string name, int fallback, int minimum, int maximum)
    {
        string? text = Value(name);
        if (text is null) return fallback;
        if (!int.TryParse(text, out int value) || value < minimum || value > maximum)
            throw new UsageException($"Option '--{name}' must be a whole number from {minimum} to {maximum}.");
        return value;
    }
}
