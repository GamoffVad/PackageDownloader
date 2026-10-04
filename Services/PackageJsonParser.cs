using System.Text.Json;

namespace PackageDownloader.Services;

/// <summary>Какие секции зависимостей брать из package.json.</summary>
[Flags]
public enum DepSections
{
    Dependencies         = 1,
    DevDependencies      = 2,
    PeerDependencies     = 4,
    OptionalDependencies = 8
}

/// <summary>
/// Разбор вставленного package.json: имя пакета → диапазон версий.
/// Понимает и «голый» объект зависимостей (<c>{ "react": "^18.0.0" }</c>).
/// </summary>
public static class PackageJsonParser
{
    /// <summary>
    /// Возвращает список «пакет → диапазон» из выбранных секций.
    /// Бросает <see cref="JsonException"/>, если текст не является JSON.
    /// </summary>
    public static List<(string Id, string Range)> Parse(string json, DepSections sections)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Ожидается объект JSON (содержимое package.json).");

        var result = new List<(string, string)>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var names = new List<string>();
        if (sections.HasFlag(DepSections.Dependencies))         names.Add("dependencies");
        if (sections.HasFlag(DepSections.DevDependencies))      names.Add("devDependencies");
        if (sections.HasFlag(DepSections.PeerDependencies))     names.Add("peerDependencies");
        if (sections.HasFlag(DepSections.OptionalDependencies)) names.Add("optionalDependencies");

        bool hasAnySection = names.Any(n => root.TryGetProperty(n, out var s) &&
                                            s.ValueKind == JsonValueKind.Object);

        // Вставили только объект зависимостей — берём его целиком.
        if (!hasAnySection && !HasKnownSection(root))
        {
            AddFrom(root, result, seen);
            return result;
        }

        foreach (var name in names)
            if (root.TryGetProperty(name, out var section) && section.ValueKind == JsonValueKind.Object)
                AddFrom(section, result, seen);

        return result;
    }

    private static bool HasKnownSection(JsonElement root) =>
        root.TryGetProperty("dependencies", out _)     || root.TryGetProperty("devDependencies", out _) ||
        root.TryGetProperty("peerDependencies", out _) || root.TryGetProperty("optionalDependencies", out _);

    private static void AddFrom(JsonElement obj, List<(string, string)> result, HashSet<string> seen)
    {
        foreach (var p in obj.EnumerateObject())
        {
            if (p.Value.ValueKind != JsonValueKind.String) continue;
            if (!seen.Add(p.Name)) continue;
            result.Add((p.Name, p.Value.GetString() ?? "*"));
        }
    }
}
