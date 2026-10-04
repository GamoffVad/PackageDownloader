using System.Text.RegularExpressions;

namespace PackageDownloader.Services;

/// <summary>
/// Утилиты для сравнения версий пакетов (semver-подобно): сначала числовые
/// сегменты, релиз новее пред-релиза. Используется для сортировки версий
/// «свежие сверху» в NPM и PyPI.
/// </summary>
public static partial class VersionUtil
{
    [GeneratedRegex(@"\d+|[A-Za-z]+")] private static partial Regex Token();

    /// <summary>Сравнение версий. Большее значение — более новая версия.</summary>
    public static int Compare(string a, string b)
    {
        var (relA, preA) = Split(a);
        var (relB, preB) = Split(b);

        int cmp = CompareParts(relA, relB);
        if (cmp != 0) return cmp;

        // Пред-релиз (1.0.0-rc1) считается СТАРШЕ соответствующего релиза (1.0.0).
        bool hasPreA = preA.Length > 0, hasPreB = preB.Length > 0;
        if (hasPreA && !hasPreB) return -1;
        if (!hasPreA && hasPreB) return 1;
        if (!hasPreA && !hasPreB) return 0;
        return CompareParts(preA, preB);
    }

    /// <summary>Версии по убыванию (свежие первыми).</summary>
    public static IEnumerable<string> SortDescending(IEnumerable<string> versions) =>
        versions.OrderByDescending(v => v, Comparer<string>.Create(Compare));

    private static (string[] release, string[] pre) Split(string version)
    {
        var v = version.Trim().TrimStart('v', 'V');
        int dash = v.IndexOf('-');
        var release = dash >= 0 ? v[..dash] : v;
        var pre     = dash >= 0 ? v[(dash + 1)..] : "";
        return (release.Split('.'), pre.Length == 0 ? Array.Empty<string>() : pre.Split('.'));
    }

    private static int CompareParts(string[] a, string[] b)
    {
        int n = Math.Max(a.Length, b.Length);
        for (int i = 0; i < n; i++)
        {
            string sa = i < a.Length ? a[i] : "";
            string sb = i < b.Length ? b[i] : "";
            int cmp = CompareToken(sa, sb);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    private static int CompareToken(string a, string b)
    {
        bool na = long.TryParse(a, out var ia);
        bool nb = long.TryParse(b, out var ib);
        if (na && nb) return ia.CompareTo(ib);
        if (na) return 1;   // числовой сегмент новее буквенного отсутствующего
        if (nb) return -1;
        return string.CompareOrdinal(a, b);
    }
}
