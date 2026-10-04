using System.Text.RegularExpressions;

namespace PackageDownloader.Services;

/// <summary>
/// Версия пакета Python по PEP 440: <c>[N!]N(.N)*[{a|b|rc}N][.postN][.devN][+local]</c>.
/// Нужна для выбора версии зависимости по спецификатору («&gt;=1.2,&lt;2.0», «~=1.4.2»).
/// </summary>
public sealed partial class PyVersion : IComparable<PyVersion>
{
    [GeneratedRegex(
        @"^\s*v?(?:(?<epoch>\d+)!)?(?<rel>\d+(?:\.\d+)*)" +
        @"(?:[-_.]?(?<preL>alpha|beta|preview|pre|a|b|c|rc)[-_.]?(?<preN>\d+)?)?" +
        @"(?:-(?<postN1>\d+)|[-_.]?(?<postL>post|rev|r)[-_.]?(?<postN2>\d+)?)?" +
        @"(?<dev>[-_.]?dev[-_.]?(?<devN>\d+)?)?" +
        @"(?:\+(?<local>[a-z0-9]+(?:[-_.][a-z0-9]+)*))?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.ExplicitCapture)]
    private static partial Regex Pattern();

    public int     Epoch   { get; }
    public int[]   Release { get; }
    public string? PreL    { get; }   // нормализованная буква пред-релиза: a | b | rc
    public int     PreN    { get; }
    public int?    Post    { get; }
    public int?    Dev     { get; }
    public string  Local   { get; }
    public string  Text    { get; }

    /// <summary>Пред-релиз или dev-сборка — такие версии не берём, пока есть стабильные.</summary>
    public bool IsPreRelease => PreL is not null || Dev is not null;

    private PyVersion(int epoch, int[] release, string? preL, int preN,
                      int? post, int? dev, string local, string text)
    {
        Epoch = epoch; Release = release; PreL = preL; PreN = preN;
        Post = post; Dev = dev; Local = local; Text = text;
    }

    public static PyVersion? Parse(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var m = Pattern().Match(s);
        if (!m.Success) return null;
        try { return Build(m, s); }
        catch { return null; }   // запредельные числа в сегментах версии
    }

    private static PyVersion Build(Match m, string s)
    {
        int epoch = m.Groups["epoch"].Success ? int.Parse(m.Groups["epoch"].Value) : 0;
        var release = m.Groups["rel"].Value.Split('.').Select(int.Parse).ToArray();

        string? preL = null;
        int     preN = 0;
        if (m.Groups["preL"].Success)
        {
            preL = m.Groups["preL"].Value.ToLowerInvariant() switch
            {
                "alpha"                  => "a",
                "beta"                   => "b",
                "c" or "pre" or "preview" => "rc",
                var other                => other
            };
            preN = m.Groups["preN"].Success ? int.Parse(m.Groups["preN"].Value) : 0;
        }

        int? post = null;
        if (m.Groups["postN1"].Success) post = int.Parse(m.Groups["postN1"].Value);
        else if (m.Groups["postL"].Success)
            post = m.Groups["postN2"].Success ? int.Parse(m.Groups["postN2"].Value) : 0;

        int? dev = null;
        if (m.Groups["dev"].Success && m.Groups["dev"].Length > 0)
            dev = m.Groups["devN"].Success ? int.Parse(m.Groups["devN"].Value) : 0;

        var local = m.Groups["local"].Success ? m.Groups["local"].Value.ToLowerInvariant() : "";
        return new PyVersion(epoch, release, preL, preN, post, dev, local, s.Trim());
    }

    public int CompareTo(PyVersion? other)
    {
        if (other is null) return 1;
        if (Epoch != other.Epoch) return Epoch.CompareTo(other.Epoch);

        int c = CompareRelease(Release, other.Release);
        if (c != 0) return c;

        c = PreKey().CompareTo(other.PreKey());
        if (c != 0) return c;

        c = (Post ?? -1).CompareTo(other.Post ?? -1);
        if (c != 0) return c;

        return (Dev ?? int.MaxValue).CompareTo(other.Dev ?? int.MaxValue);
    }

    /// <summary>Сравнение без local-сегмента (по PEP 440 он игнорируется в спецификаторах).</summary>
    public int ComparePublic(PyVersion other) => CompareTo(other);

    // Ключ пред-релиза: dev-сборка без pre/post — раньше всех, финальный релиз — позже пред-релизов.
    private long PreKey()
    {
        if (PreL is null) return (Post is null && Dev is not null) ? long.MinValue : long.MaxValue;
        int letter = PreL switch { "a" => 0, "b" => 1, _ => 2 };
        return letter * 1_000_000L + PreN;
    }

    private static int CompareRelease(int[] a, int[] b)
    {
        int n = Math.Max(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            int x = i < a.Length ? a[i] : 0;
            int y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    public override string ToString() => Text;
}

/// <summary>Спецификаторы версий PEP 440: разбор строки «&gt;=1.2,&lt;2.0» и выбор лучшей версии.</summary>
public static partial class Pep440
{
    [GeneratedRegex(@"^\s*(?<op>===|==|!=|<=|>=|~=|<|>)\s*(?<ver>[^\s,]+)\s*$")]
    private static partial Regex Clause();

    /// <summary>Удовлетворяет ли версия набору спецификаторов (пустой набор — да).</summary>
    public static bool Satisfies(PyVersion v, string? specifierSet)
    {
        if (string.IsNullOrWhiteSpace(specifierSet)) return true;

        foreach (var raw in specifierSet.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Clause().Match(raw);
            if (!m.Success) continue;               // непонятный кусок — не ужесточаем условие
            if (!MatchClause(v, m.Groups["op"].Value, m.Groups["ver"].Value)) return false;
        }
        return true;
    }

    /// <summary>Упоминает ли спецификатор пред-релиз (тогда их можно брать в кандидаты).</summary>
    public static bool MentionsPreRelease(string? specifierSet)
    {
        if (string.IsNullOrWhiteSpace(specifierSet)) return false;
        foreach (var raw in specifierSet.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Clause().Match(raw);
            if (m.Success && PyVersion.Parse(m.Groups["ver"].Value.TrimEnd('*', '.')) is { IsPreRelease: true })
                return true;
        }
        return false;
    }

    /// <summary>
    /// Лучшая (максимальная) версия из списка, подходящая под спецификатор.
    /// Пред-релизы берутся, только если стабильных подходящих нет.
    /// </summary>
    public static string? FindBest(IEnumerable<string> candidates, string? specifierSet)
    {
        var parsed = candidates
            .Select(c => (text: c, ver: PyVersion.Parse(c)))
            .Where(p => p.ver is not null)
            .Select(p => (p.text, ver: p.ver!))
            .Where(p => Satisfies(p.ver, specifierSet))
            .ToList();

        if (parsed.Count == 0) return null;

        var stable = parsed.Where(p => !p.ver.IsPreRelease).ToList();
        var pool   = stable.Count > 0 && !MentionsPreRelease(specifierSet) ? stable : parsed;
        return pool.OrderByDescending(p => p.ver).First().text;
    }

    private static bool MatchClause(PyVersion v, string op, string spec)
    {
        // Префиксное сравнение: «==1.4.*» / «!=1.4.*».
        if (spec.EndsWith(".*") && (op == "==" || op == "!="))
        {
            var prefix = PyVersion.Parse(spec[..^2]);
            if (prefix is null) return true;
            bool pref = v.Epoch == prefix.Epoch && StartsWith(v.Release, prefix.Release);
            return op == "==" ? pref : !pref;
        }

        if (op == "===") return string.Equals(v.Text, spec.Trim(), StringComparison.OrdinalIgnoreCase);

        var s = PyVersion.Parse(spec);
        if (s is null) return true;                 // неразбираемая версия — не отсекаем кандидата

        int cmp = v.ComparePublic(s);
        return op switch
        {
            "==" => cmp == 0,
            "!=" => cmp != 0,
            ">=" => cmp >= 0,
            "<=" => cmp <= 0,
            // Строгие сравнения не должны цеплять post-релизы той же версии.
            ">"  => cmp > 0 && !(SameRelease(v, s) && s.Post is null && v.Post is not null),
            "<"  => cmp < 0 && !(SameRelease(v, s) && !s.IsPreRelease && v.IsPreRelease),
            "~=" => CompatibleRelease(v, s),
            _    => true
        };
    }

    // «~=1.4.2» ≡ «>=1.4.2, ==1.4.*»
    private static bool CompatibleRelease(PyVersion v, PyVersion s)
    {
        if (s.Release.Length < 2) return v.ComparePublic(s) >= 0;
        var prefix = s.Release[..^1];
        return v.ComparePublic(s) >= 0 && v.Epoch == s.Epoch && StartsWith(v.Release, prefix);
    }

    private static bool SameRelease(PyVersion a, PyVersion b)
    {
        int n = Math.Max(a.Release.Length, b.Release.Length);
        for (int i = 0; i < n; i++)
            if ((i < a.Release.Length ? a.Release[i] : 0) != (i < b.Release.Length ? b.Release[i] : 0))
                return false;
        return true;
    }

    private static bool StartsWith(int[] release, int[] prefix)
    {
        for (int i = 0; i < prefix.Length; i++)
            if ((i < release.Length ? release[i] : 0) != prefix[i]) return false;
        return true;
    }
}
