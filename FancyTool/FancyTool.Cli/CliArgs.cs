using FancyToolAva.Cli;

namespace FancyToolAva.Cli;

public sealed class CliUsageException(string message) : Exception(message);

/// <summary>CLI 参数解析：选项别名归一化、重复值保留顺序、未知选项检测。</summary>
public sealed class CliArgs
{
    private readonly Dictionary<string, List<string?>> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _canonical = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _valueOptions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _allowed = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<KeyValuePair<string, string?>> _ordered = [];
    private readonly List<string> _positionals = [];

    public IReadOnlyList<string> Positionals => _positionals;

    public IReadOnlyList<KeyValuePair<string, string?>> Ordered => _ordered;

    public IReadOnlyCollection<string> UnknownOptions { get; private set; } = [];

    public static CliArgs Parse(IReadOnlyList<string> tokens, IReadOnlyList<CliOptionSpec> specs)
    {
        var args = new CliArgs();
        args.Register(specs);
        var unknown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            if (token == "--")
            {
                for (int j = i + 1; j < tokens.Count; j++) args._positionals.Add(tokens[j]);
                break;
            }
            if (!token.StartsWith('-') || token == "-")
            {
                args._positionals.Add(token);
                continue;
            }

            string rawName = token;
            string? inlineValue = null;
            int equals = token.IndexOf('=');
            if (equals > 0)
            {
                rawName = token[..equals];
                inlineValue = token[(equals + 1)..];
            }

            string canonical = args._canonical.TryGetValue(rawName, out string? mapped) ? mapped : rawName;
            if (!args._allowed.Contains(canonical)) unknown.Add(rawName);

            string? value = inlineValue;
            if (value is null && args._valueOptions.Contains(canonical))
            {
                if (i + 1 < tokens.Count && !tokens[i + 1].StartsWith('-'))
                {
                    value = tokens[++i];
                }
            }
            else if (value is null && !args._valueOptions.Contains(canonical)
                     && i + 1 < tokens.Count && !tokens[i + 1].StartsWith('-')
                     && unknown.Contains(rawName))
            {
                // 未知选项也消费一个值，避免把值误报成多余位置参数。
                value = tokens[++i];
            }

            args.Add(canonical, value);
        }

        args.UnknownOptions = unknown;
        return args;
    }

    private void Register(IReadOnlyList<CliOptionSpec> specs)
    {
        foreach (CliOptionSpec option in specs)
        {
            IReadOnlyList<string> tokens = option.Tokens.ToList();
            string canonical = tokens.FirstOrDefault(t => t.StartsWith("--", StringComparison.Ordinal))
                               ?? tokens[0];
            _allowed.Add(canonical);
            if (option.Value is not null) _valueOptions.Add(canonical);
            foreach (string token in tokens)
            {
                _canonical[token] = canonical;
                _allowed.Add(token);
                if (option.Value is not null) _valueOptions.Add(token);
            }
        }
        _canonical["-help"] = "--help";
        _allowed.Add("-help");
    }

    private void Add(string name, string? value)
    {
        if (!_values.TryGetValue(name, out List<string?>? list))
        {
            list = [];
            _values[name] = list;
        }
        list.Add(value);
        _ordered.Add(new(name, value));
    }

    private string Canonical(string name) => _canonical.TryGetValue(name, out string? mapped) ? mapped : name;

    public bool Has(string name) => _values.ContainsKey(Canonical(name));

    public string? Get(string name, string? fallback = null)
    {
        if (!_values.TryGetValue(Canonical(name), out List<string?>? list) || list.Count == 0) return fallback;
        return list[^1] ?? fallback;
    }

    public string Require(string name, string zhMessage)
    {
        string? value = Get(name);
        if (string.IsNullOrWhiteSpace(value)) throw new CliUsageException(zhMessage);
        return value;
    }

    public IReadOnlyList<string> GetAll(string name)
    {
        if (!_values.TryGetValue(Canonical(name), out List<string?>? list)) return [];
        return list.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList();
    }

    public int GetInt(string name, int fallback)
    {
        string? value = Get(name);
        if (value is null) return fallback;
        return int.TryParse(value, out int parsed) ? parsed : throw new CliUsageException($"Invalid integer for {name}: {value}");
    }

    public int? GetNullableInt(string name)
    {
        string? value = Get(name);
        if (value is null) return null;
        return int.TryParse(value, out int parsed) ? parsed : throw new CliUsageException($"Invalid integer for {name}: {value}");
    }

    public double GetDouble(string name, double fallback)
    {
        string? value = Get(name);
        if (value is null) return fallback;
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed)
            ? parsed : throw new CliUsageException($"Invalid number for {name}: {value}");
    }

    public double? GetNullableDouble(string name)
    {
        string? value = Get(name);
        if (value is null) return null;
        return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double parsed)
            ? parsed : throw new CliUsageException($"Invalid number for {name}: {value}");
    }

    public bool GetBool(string name)
    {
        string? value = Get(name);
        if (value is null) return Has(name);
        return value.ToLowerInvariant() switch
        {
            "false" or "0" or "no" or "off" => false,
            _ => true,
        };
    }

    public TEnum GetEnum<TEnum>(string name, TEnum fallback) where TEnum : struct, Enum
    {
        string? value = Get(name);
        if (value is null) return fallback;
        if (Enum.TryParse(value, ignoreCase: true, out TEnum parsed) && Enum.IsDefined(parsed)) return parsed;
        throw new CliUsageException($"Invalid value for {name}: {value}. Allowed: {string.Join(", ", Enum.GetNames<TEnum>())}");
    }

    public string? GetChoice(string name, string? fallback, params string[] allowed)
    {
        string? value = Get(name);
        if (value is null) return fallback;
        foreach (string candidate in allowed)
        {
            if (string.Equals(candidate, value, StringComparison.OrdinalIgnoreCase)) return candidate;
        }
        throw new CliUsageException($"Invalid value for {name}: {value}. Allowed: {string.Join(", ", allowed)}");
    }
}