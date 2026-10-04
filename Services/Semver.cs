using System.Text.RegularExpressions;

namespace PackageDownloader.Services;

/// <summary>Версия npm-пакета (semver 2.0): <c>major.minor.patch[-prerelease][+build]</c>.</summary>
public sealed partial class SemVer : IComparable<SemVer>
{
    [GeneratedRegex(@"^\s*v?(?<maj>\d+)(?:\.(?<min>\d+))?(?:\.(?<pat>\d+))?" +
                    @"(?:-(?<pre>[0-9A-Za-z.-]+))?(?:\+(?<build>[0-9A-Za-z.-]+))?\s*$",
                    RegexOptions.ExplicitCapture)]
    private static partial Regex Pattern();

    public int      Major { get; private init; }
    public int      Minor { get; private init; }
    public int      Patch { get; private init; }
    public string[] Pre   { get; private init; } = [];
    public string   Text  { get; private init; } = "";

    public bool IsPreRelease => Pre.Length > 0;

    public static SemVer? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Pattern().Match(s);
        if (!m.Success) return null;
        try
        {
            return new SemVer
            {
                Major = int.Parse(m.Groups["maj"].Value),
                Minor = m.Groups["min"].Success ? int.Parse(m.Groups["min"].Value) : 0,
                Patch = m.Groups["pat"].Success ? int.Parse(m.Groups["pat"].Value) : 0,
                Pre   = m.Groups["pre"].Success ? m.Groups["pre"].Value.Split('.') : [],
                Text  = s.Trim()
            };
        }
        catch { return null; }
    }

    public int CompareTo(SemVer? other)
    {
        if (other is null) return 1;
        if (Major != other.Major) return Major.CompareTo(other.Major);
        if (Minor != other.Minor) return Minor.CompareTo(other.Minor);
        if (Patch != other.Patch) return Patch.CompareTo(other.Patch);

        // Пред-релиз всегда старше соответствующего релиза.
        if (Pre.Length == 0 && other.Pre.Length == 0) return 0;
        if (Pre.Length == 0) return 1;
        if (other.Pre.Length == 0) return -1;

        int n = Math.Max(Pre.Length, other.Pre.Length);
        for (int i = 0; i < n; i++)
        {
            if (i >= Pre.Length) return -1;
            if (i >= other.Pre.Length) return 1;
            bool na = int.TryParse(Pre[i], out var ia);
            bool nb = int.TryParse(other.Pre[i], out var ib);
            int c = (na, nb) switch
            {
                (true, true)   => ia.CompareTo(ib),
                (true, false)  => -1,               // числовые идентификаторы младше буквенных
                (false, true)  => 1,
                _              => string.CompareOrdinal(Pre[i], other.Pre[i])
            };
            if (c != 0) return c;
        }
        return 0;
    }

    /// <summary>Совпадает ли основная тройка (для правил pre-release в диапазонах).</summary>
    public bool SameCore(SemVer o) => Major == o.Major && Minor == o.Minor && Patch == o.Patch;

    public override string ToString() => Text;
}

/// <summary>
/// Диапазоны версий npm: <c>^1.2.3</c>, <c>~1.2</c>, <c>&gt;=1.0 &lt;2.0</c>,
/// <c>1.2.x</c>, <c>1.2.3 - 2.0.0</c>, <c>a || b</c>, <c>*</c>, <c>latest</c>.
/// </summary>
public static partial class SemverRange
{
    [GeneratedRegex(@"^(?<op>>=|<=|>|<|=|\^|~)?\s*(?<ver>[vV]?[0-9xX*][0-9A-Za-z.\-+*]*)$")]
    private static partial Regex Comparator();

    private sealed record Bound(SemVer Version, bool Inclusive);
    private sealed record Clause(Bound? Min, Bound? Max);

    /// <summary>Точная ли это версия (диапазон не нужен).</summary>
    public static bool IsExact(string? range) =>
        range is not null && SemVer.Parse(range.Trim()) is not null &&
        !range.Contains('^') && !range.Contains('~') && !range.Contains('*') &&
        !range.Contains('>') && !range.Contains('<') && !range.Contains('|');

    /// <summary>Разбирается ли строка как диапазон версий (а не как dist-tag, URL или git-ссылка).</summary>
    public static bool IsRange(string? range)
    {
        if (string.IsNullOrWhiteSpace(range)) return true;       // пустая строка ≡ «*»
        var r = range.Trim();
        if (r is "*" or "x" or "X" or "latest") return r != "latest";
        return ParseSets(r) is { Count: > 0 };
    }

    /// <summary>Удовлетворяет ли версия диапазону.</summary>
    public static bool Satisfies(SemVer v, string? range)
    {
        if (string.IsNullOrWhiteSpace(range) || range.Trim() is "*" or "x" or "X") return !v.IsPreRelease;
        var sets = ParseSets(range.Trim());
        return sets.Count > 0 && sets.Any(set => Matches(v, set));
    }

    /// <summary>
    /// Максимальная версия из списка, подходящая под диапазон.
    /// Пред-релизы берутся, только если сам диапазон их упоминает.
    /// </summary>
    public static string? FindBest(IEnumerable<string> versions, string? range)
    {
        var parsed = versions
            .Select(v => (text: v, ver: SemVer.Parse(v)))
            .Where(p => p.ver is not null)
            .Select(p => (p.text, ver: p.ver!))
            .Where(p => Satisfies(p.ver, range))
            .ToList();

        if (parsed.Count == 0) return null;
        return parsed.OrderByDescending(p => p.ver).First().text;
    }

    // ── Разбор ────────────────────────────────────────────────────────────

    private static List<List<Clause>> ParseSets(string range)
    {
        var sets = new List<List<Clause>>();
        foreach (var part in range.Split("||", StringSplitOptions.RemoveEmptyEntries))
        {
            var clauses = ParseClauses(part.Trim());
            if (clauses is null) return [];          // непонятный диапазон — считаем неразобранным
            sets.Add(clauses);
        }
        return sets;
    }

    private static List<Clause>? ParseClauses(string text)
    {
        var result = new List<Clause>();
        if (text.Length == 0 || text is "*" or "x" or "X")
        {
            result.Add(new Clause(null, null));
            return result;
        }

        // Дефисный диапазон: «1.2.3 - 2.0.0».
        var hyphen = Regex.Match(text, @"^(?<a>[^\s]+)\s+-\s+(?<b>[^\s]+)$");
        if (hyphen.Success)
        {
            var lo = Partial(hyphen.Groups["a"].Value);
            var hi = Partial(hyphen.Groups["b"].Value);
            if (lo is null || hi is null) return null;
            var min = new Bound(lo.Value.Low, true);
            // «- 2.3» без patch означает «включая всю ветку 2.3».
            var max = hi.Value.HasPatch
                ? new Bound(hi.Value.Low, true)
                : new Bound(hi.Value.Upper!, false);
            result.Add(new Clause(min, max));
            return result;
        }

        foreach (var token in text.Split([' ', ',', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            var clause = ParseComparator(token);
            if (clause is null) return null;
            result.Add(clause);
        }
        return result.Count > 0 ? result : null;
    }

    private static Clause? ParseComparator(string token)
    {
        var m = Comparator().Match(token);
        if (!m.Success) return null;

        var op  = m.Groups["op"].Success ? m.Groups["op"].Value : "=";
        var raw = m.Groups["ver"].Value;
        var p   = Partial(raw);
        if (p is null) return null;
        var (low, upper, hasMinor, hasPatch) = (p.Value.Low, p.Value.Upper, p.Value.HasMinor, p.Value.HasPatch);

        switch (op)
        {
            case ">":  return new Clause(new Bound(hasPatch ? low : upper!, !hasPatch), null);
            case ">=": return new Clause(new Bound(low, true), null);
            case "<":  return new Clause(null, new Bound(low, false));
            case "<=": return new Clause(null, new Bound(hasPatch ? low : upper!, hasPatch));

            case "^":
            {
                var max = low.Major > 0 || !hasMinor
                    ? Core(low.Major + 1, 0, 0)
                    : low.Minor > 0 || !hasPatch
                        ? Core(low.Major, low.Minor + 1, 0)
                        : Core(low.Major, low.Minor, low.Patch + 1);
                return new Clause(new Bound(low, true), new Bound(max, false));
            }
            case "~":
            {
                var max = hasMinor ? Core(low.Major, low.Minor + 1, 0) : Core(low.Major + 1, 0, 0);
                return new Clause(new Bound(low, true), new Bound(max, false));
            }
            default: // «=» либо голая версия: 1.2.3 — точная, 1.2 / 1.x — вся ветка
                return hasPatch
                    ? new Clause(new Bound(low, true), new Bound(low, true))
                    : new Clause(new Bound(low, true), new Bound(upper!, false));
        }
    }

    // Разбор «частичной» версии: 1 / 1.2 / 1.2.3 / 1.x / 1.2.* (+ пред-релиз).
    private static (SemVer Low, SemVer? Upper, bool HasMinor, bool HasPatch)? Partial(string raw)
    {
        var text  = raw.Trim();
        var plus  = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];

        string pre = "";
        int dash = text.IndexOf('-');
        if (dash > 0) { pre = text[(dash + 1)..]; text = text[..dash]; }

        var parts = text.TrimStart('v', 'V').Split('.');
        bool IsWild(string s) => s is "x" or "X" or "*" or "";

        if (parts.Length == 0 || IsWild(parts[0])) return (Core(0, 0, 0), null, false, false);
        if (!int.TryParse(parts[0], out var maj)) return null;

        bool hasMinor = parts.Length > 1 && !IsWild(parts[1]);
        bool hasPatch = hasMinor && parts.Length > 2 && !IsWild(parts[2]);

        int min = hasMinor && int.TryParse(parts[1], out var mi) ? mi : 0;
        int pat = hasPatch && int.TryParse(parts[2], out var pa) ? pa : 0;

        var low   = SemVer.Parse(pre.Length > 0 ? $"{maj}.{min}.{pat}-{pre}" : $"{maj}.{min}.{pat}");
        var upper = hasMinor ? Core(maj, min + 1, 0) : Core(maj + 1, 0, 0);
        return low is null ? null : (low, upper, hasMinor, hasPatch);
    }

    private static SemVer Core(int maj, int min, int pat) => SemVer.Parse($"{maj}.{min}.{pat}")!;

    private static bool Matches(SemVer v, List<Clause> set)
    {
        foreach (var c in set)
        {
            if (c.Min is { } lo)
            {
                int cmp = v.CompareTo(lo.Version);
                if (lo.Inclusive ? cmp < 0 : cmp <= 0) return false;
            }
            if (c.Max is { } hi)
            {
                int cmp = v.CompareTo(hi.Version);
                if (hi.Inclusive ? cmp > 0 : cmp >= 0) return false;
            }
        }

        // Пред-релиз годится, только если сам диапазон упоминает пред-релиз той же тройки.
        if (v.IsPreRelease)
            return set.Any(c =>
                (c.Min?.Version is { IsPreRelease: true } a && a.SameCore(v)) ||
                (c.Max?.Version is { IsPreRelease: true } b && b.SameCore(v)));

        return true;
    }
}
