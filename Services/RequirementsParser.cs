using System.Text;
using System.Text.RegularExpressions;

namespace PackageDownloader.Services;

/// <summary>
/// Разбор файлов зависимостей Python в список «пакет → спецификатор версий»:
/// <c>requirements.txt</c>, <c>pyproject.toml</c> (PEP 621 и Poetry) и <c>Pipfile</c>.
/// Маркеры окружения вычисляются сразу — строки для другой платформы или
/// версии Python отбрасываются.
/// </summary>
public static partial class RequirementsParser
{
    [GeneratedRegex(@"""([^""]*)""|'([^']*)'")] private static partial Regex QuotedString();
    [GeneratedRegex(@"^\s*\[([^\]]+)\]\s*$")]   private static partial Regex TomlSection();
    [GeneratedRegex(@"^\s*([A-Za-z0-9._-]+)\s*=\s*(.+?)\s*$")] private static partial Regex TomlKeyValue();

    /// <summary>Пакеты из содержимого файла зависимостей. Формат определяется автоматически.</summary>
    public static List<(string Id, string Specifier)> Parse(string text, string pythonVersion = "3.12")
    {
        var env    = PyMarker.Environment(pythonVersion);
        var result = new List<(string, string)>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (LooksLikeToml(text)) ParseToml(text, env, result, seen);
        else                     ParseRequirements(text, env, result, seen);

        return result;
    }

    private static bool LooksLikeToml(string text) =>
        text.Contains("[project]")            || text.Contains("[tool.poetry") ||
        text.Contains("[packages]")           || text.Contains("[dev-packages]") ||
        text.Contains("[build-system]");

    // ── requirements.txt ──────────────────────────────────────────────────

    private static void ParseRequirements(
        string text, Dictionary<string, string> env,
        List<(string, string)> result, HashSet<string> seen)
    {
        foreach (var line in JoinContinuations(text))
        {
            var s = StripComment(line).Trim();
            if (s.Length == 0) continue;

            // Опции файла (-r other.txt, -e ., --index-url …) пропускаем.
            if (s.StartsWith('-')) continue;

            // Хэши и прочие суффиксы после требования.
            int hash = s.IndexOf("--hash", StringComparison.Ordinal);
            if (hash >= 0) s = s[..hash].TrimEnd();
            s = s.TrimEnd('\\', ' ');

            Add(s, env, result, seen);
        }
    }

    private static IEnumerable<string> JoinContinuations(string text)
    {
        var sb = new StringBuilder();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.TrimEnd().EndsWith('\\'))
            {
                sb.Append(line.TrimEnd().TrimEnd('\\')).Append(' ');
                continue;
            }
            sb.Append(line);
            yield return sb.ToString();
            sb.Clear();
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    private static string StripComment(string line)
    {
        // Решётка в начале слова — комментарий (в требованиях «#» иначе не встречается).
        if (line.TrimStart().StartsWith('#')) return "";
        int i = line.IndexOf(" #", StringComparison.Ordinal);
        return i >= 0 ? line[..i] : line;
    }

    // ── pyproject.toml / Pipfile ──────────────────────────────────────────

    private static void ParseToml(
        string text, Dictionary<string, string> env,
        List<(string, string)> result, HashSet<string> seen)
    {
        string section  = "";
        bool   inArray  = false;
        var    arrayBuf = new StringBuilder();

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            // Продолжение многострочного массива dependencies = [ … ]
            if (inArray)
            {
                arrayBuf.Append(line).Append('\n');
                if (line.Contains(']'))
                {
                    inArray = false;
                    foreach (var dep in QuotedValues(arrayBuf.ToString()))
                        Add(dep, env, result, seen);
                    arrayBuf.Clear();
                }
                continue;
            }

            var sec = TomlSection().Match(line);
            if (sec.Success) { section = sec.Groups[1].Value.Trim(); continue; }

            var trimmed = StripTomlComment(line).Trim();
            if (trimmed.Length == 0) continue;

            var kv = TomlKeyValue().Match(trimmed);
            if (!kv.Success) continue;
            var key   = kv.Groups[1].Value;
            var value = kv.Groups[2].Value;

            // PEP 621: [project] dependencies = ["flask>=3", …]
            if (section == "project" && key is "dependencies")
            {
                if (value.Contains(']'))
                {
                    foreach (var dep in QuotedValues(value)) Add(dep, env, result, seen);
                }
                else if (value.Contains('['))
                {
                    inArray = true;
                    arrayBuf.Append(value).Append('\n');
                }
                continue;
            }

            // Poetry и Pipfile: имя пакета = версия (или таблица со «version»).
            if (IsDependencyTable(section))
            {
                if (key.Equals("python", StringComparison.OrdinalIgnoreCase)) continue;
                var spec = ValueToSpecifier(value);
                if (spec is null) continue;
                Add(key + spec, env, result, seen);
            }
        }
    }

    private static bool IsDependencyTable(string section) =>
        section is "packages" or "dev-packages" ||                       // Pipfile
        section == "tool.poetry.dependencies"   ||
        section == "tool.poetry.dev-dependencies" ||
        (section.StartsWith("tool.poetry.group.") && section.EndsWith(".dependencies"));

    private static string StripTomlComment(string line)
    {
        if (line.TrimStart().StartsWith('#')) return "";
        int i = line.IndexOf(" #", StringComparison.Ordinal);
        return i >= 0 ? line[..i] : line;
    }

    private static IEnumerable<string> QuotedValues(string text) =>
        QuotedString().Matches(text)
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
            .Where(v => v.Length > 0);

    /// <summary>Значение записи Poetry/Pipfile → спецификатор PEP 440 («^1.2» → «&gt;=1.2,&lt;2»).</summary>
    private static string? ValueToSpecifier(string value)
    {
        var v = value.Trim();

        // Таблица: { version = "^1.2", optional = true } — берём только version.
        if (v.StartsWith('{'))
        {
            var m = Regex.Match(v, @"version\s*=\s*""([^""]*)""|version\s*=\s*'([^']*)'");
            if (!m.Success) return null;                    // git/path-зависимость — не из индекса
            v = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
        }
        else
        {
            var q = QuotedValues(v).FirstOrDefault();
            if (q is null) return null;
            v = q;
        }

        v = v.Trim();
        if (v.Length == 0 || v == "*") return "";
        return FromPoetryCaret(v);
    }

    /// <summary>Конструкции Poetry «^1.2.3» и «~1.2.3» → спецификаторы PEP 440.</summary>
    private static string FromPoetryCaret(string spec)
    {
        var s = spec.Trim();
        if (s.StartsWith('^'))
        {
            var parts = s[1..].Split('.');
            if (!int.TryParse(parts[0], out var maj)) return "";
            int min = parts.Length > 1 && int.TryParse(parts[1], out var mi) ? mi : 0;
            int pat = parts.Length > 2 && int.TryParse(parts[2], out var pa) ? pa : 0;

            // ^1.2.3 → <2.0.0 ; ^0.2.3 → <0.3.0 ; ^0.0.3 → <0.0.4
            var upper = maj > 0            ? $"{maj + 1}.0.0"
                      : min > 0 || parts.Length < 3 ? $"0.{min + 1}.0"
                      :                        $"0.0.{pat + 1}";
            return $">={s[1..]},<{upper}";
        }

        if (s.StartsWith('~') && !s.StartsWith("~="))
        {
            var parts = s[1..].Split('.');
            if (!int.TryParse(parts[0], out var maj)) return "";
            if (parts.Length == 1) return $">={s[1..]},<{maj + 1}.0.0";
            int min = int.TryParse(parts[1], out var mi) ? mi : 0;
            return $">={s[1..]},<{maj}.{min + 1}.0";
        }

        // Голая версия в Poetry («1.2.3») означает точное совпадение.
        return char.IsDigit(s[0]) ? "==" + s : s;
    }

    // ── Общее ─────────────────────────────────────────────────────────────

    private static void Add(
        string line, Dictionary<string, string> env,
        List<(string, string)> result, HashSet<string> seen)
    {
        var req = PyRequirement.Parse(line);
        if (req is null || req.IsUrl) return;
        if (!PyMarker.Evaluate(req.Marker, env)) return;      // другая ОС/версия Python
        if (!seen.Add(req.Name)) return;
        result.Add((req.Name, req.Specifier));
    }
}
