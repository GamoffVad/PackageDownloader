using System.Text.RegularExpressions;

namespace PackageDownloader.Services;

/// <summary>
/// Требование к зависимости Python (PEP 508), например
/// <c>requests[socks] (&gt;=2.0,&lt;3) ; python_version &gt;= "3.8"</c>.
/// </summary>
public sealed partial class PyRequirement
{
    [GeneratedRegex(
        @"^\s*(?<name>[A-Za-z0-9][A-Za-z0-9._-]*)\s*" +
        @"(?:\[(?<extras>[^\]]*)\])?\s*" +
        @"(?:@\s*(?<url>[^;]+)|\((?<spec1>[^)]*)\)|(?<spec2>[^;]*))?" +
        @"\s*(?:;\s*(?<marker>.*))?$",
        RegexOptions.ExplicitCapture)]
    private static partial Regex Pattern();

    public string  Name      { get; private init; } = "";
    public string  Specifier { get; private init; } = "";
    public string  Marker    { get; private init; } = "";
    /// <summary>Зависимость задана прямой ссылкой (<c>pkg @ https://…</c>) — из индекса её не взять.</summary>
    public bool    IsUrl     { get; private init; }

    public static PyRequirement? Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        var m = Pattern().Match(line.Trim());
        if (!m.Success) return null;

        var spec = m.Groups["spec1"].Success ? m.Groups["spec1"].Value : m.Groups["spec2"].Value;
        return new PyRequirement
        {
            Name      = m.Groups["name"].Value,
            Specifier = (spec ?? "").Trim(),
            Marker    = m.Groups["marker"].Value.Trim(),
            IsUrl     = m.Groups["url"].Success
        };
    }
}

/// <summary>
/// Вычисление маркеров окружения PEP 508 (<c>python_version &lt; "3.11" and sys_platform == "win32"</c>).
/// Всё, что не удалось разобрать, считается истинным — лучше скачать лишнее, чем пропустить нужное.
/// </summary>
public static partial class PyMarker
{
    [GeneratedRegex(@"'[^']*'|""[^""]*""|\(|\)|===|==|!=|<=|>=|~=|<|>|\bnot\s+in\b|\bin\b|\band\b|\bor\b|[A-Za-z_][A-Za-z_0-9.]*")]
    private static partial Regex Tokens();

    /// <summary>Окружение по умолчанию: Windows + CPython указанной версии, без extras.</summary>
    public static Dictionary<string, string> Environment(string pythonVersion = "3.12")
    {
        var v    = string.IsNullOrWhiteSpace(pythonVersion) ? "3.12" : pythonVersion.Trim();
        var full = v.Count(c => c == '.') >= 2 ? v : v + ".0";
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["python_version"]                = string.Join('.', v.Split('.').Take(2)),
            ["python_full_version"]           = full,
            ["implementation_version"]        = full,
            ["os_name"]                       = "nt",
            ["sys_platform"]                  = "win32",
            ["platform_system"]               = "Windows",
            ["platform_machine"]              = "AMD64",
            ["platform_release"]              = "",
            ["platform_version"]              = "",
            ["implementation_name"]           = "cpython",
            ["platform_python_implementation"] = "CPython",
            ["python_implementation"]         = "CPython",
            ["extra"]                         = ""     // extras не ставим — как обычный pip install
        };
    }

    /// <summary>Истинен ли маркер в заданном окружении. Пустой маркер — истина.</summary>
    public static bool Evaluate(string? marker, IReadOnlyDictionary<string, string> env)
    {
        if (string.IsNullOrWhiteSpace(marker)) return true;
        try
        {
            var tokens = Tokens().Matches(marker).Select(m => m.Value).ToList();
            int pos = 0;
            bool result = ParseOr(tokens, ref pos, env);
            return pos >= tokens.Count ? result : true;   // остался «хвост» — не доверяем разбору
        }
        catch { return true; }
    }

    private static bool ParseOr(List<string> t, ref int pos, IReadOnlyDictionary<string, string> env)
    {
        bool value = ParseAnd(t, ref pos, env);
        while (pos < t.Count && t[pos].Equals("or", StringComparison.OrdinalIgnoreCase))
        {
            pos++;
            value = ParseAnd(t, ref pos, env) || value;
        }
        return value;
    }

    private static bool ParseAnd(List<string> t, ref int pos, IReadOnlyDictionary<string, string> env)
    {
        bool value = ParseFactor(t, ref pos, env);
        while (pos < t.Count && t[pos].Equals("and", StringComparison.OrdinalIgnoreCase))
        {
            pos++;
            value = ParseFactor(t, ref pos, env) && value;
        }
        return value;
    }

    private static bool ParseFactor(List<string> t, ref int pos, IReadOnlyDictionary<string, string> env)
    {
        if (pos < t.Count && t[pos] == "(")
        {
            pos++;
            bool inner = ParseOr(t, ref pos, env);
            if (pos < t.Count && t[pos] == ")") pos++;
            return inner;
        }

        // Сравнение — ровно три токена; если их меньше, маркер считаем истинным.
        if (pos + 3 > t.Count) { pos = t.Count; return true; }

        var left  = t[pos++];
        var op    = t[pos++];
        var right = t[pos++];
        return Compare(left, op, right, env);
    }

    private static bool Compare(string left, string op, string right, IReadOnlyDictionary<string, string> env)
    {
        // Незнакомая переменная окружения — маркер не отбрасывает зависимость.
        if (!IsLiteral(left)  && !env.ContainsKey(left))  return true;
        if (!IsLiteral(right) && !env.ContainsKey(right)) return true;

        bool leftIsVar  = IsVariable(left,  env);
        bool rightIsVar = IsVariable(right, env);
        var  l = Value(left,  env);
        var  r = Value(right, env);

        if (op is "in" or "not in")
        {
            bool contains = r.Contains(l, StringComparison.Ordinal);
            return op == "in" ? contains : !contains;
        }

        // Версии сравниваем по PEP 440, остальное — как строки.
        bool versionish = (leftIsVar && IsVersionVar(left)) || (rightIsVar && IsVersionVar(right));
        if (versionish && op is "<" or "<=" or ">" or ">=" or "==" or "!=" or "~=")
        {
            var lv = PyVersion.Parse(l);
            var rv = PyVersion.Parse(r);
            if (lv is not null && rv is not null)
            {
                int c = lv.CompareTo(rv);
                return op switch
                {
                    "<" => c < 0, "<=" => c <= 0, ">" => c > 0, ">=" => c >= 0,
                    "==" => c == 0, "!=" => c != 0,
                    _   => Pep440.Satisfies(lv, "~=" + r)
                };
            }
        }

        int s = string.CompareOrdinal(l, r);
        return op switch
        {
            "==" or "===" => s == 0,
            "!="          => s != 0,
            "<"           => s <  0,
            "<="          => s <= 0,
            ">"           => s >  0,
            ">="          => s >= 0,
            _             => true
        };
    }

    private static bool IsLiteral(string token) =>
        token.Length >= 2 && (token[0] == '\'' || token[0] == '"');

    private static bool IsVariable(string token, IReadOnlyDictionary<string, string> env) =>
        !IsLiteral(token) && env.ContainsKey(token);

    private static bool IsVersionVar(string name) =>
        name is "python_version" or "python_full_version" or "implementation_version" or "platform_version";

    private static string Value(string token, IReadOnlyDictionary<string, string> env)
    {
        if (token.Length >= 2 && (token[0] == '\'' || token[0] == '"')) return token[1..^1];
        return env.TryGetValue(token, out var v) ? v : token;
    }
}
