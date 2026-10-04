using System.Xml.Linq;

namespace PackageDownloader.Services;

/// <summary>
/// Разбор файлов проекта .NET в список «пакет → диапазон версий»:
/// <c>.csproj/.fsproj/.vbproj</c> (PackageReference), <c>Directory.Packages.props</c>
/// (PackageVersion) и <c>packages.config</c> (package).
/// </summary>
public static class ProjectFileParser
{
    /// <summary>
    /// Возвращает пакеты из содержимого файла проекта.
    /// Бросает <see cref="System.Xml.XmlException"/>, если это не XML.
    /// </summary>
    public static List<(string Id, string Range)> Parse(string xml)
    {
        var doc    = XDocument.Parse(xml);
        var result = new List<(string, string)>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var el in doc.Descendants())
        {
            var (id, version) = el.Name.LocalName switch
            {
                "PackageReference" or "PackageVersion" or "GlobalPackageReference" =>
                    (Attr(el, "Include") ?? Attr(el, "Update"),
                     Attr(el, "Version") ?? Attr(el, "VersionOverride") ?? Child(el, "Version")),

                // packages.config
                "package" => (Attr(el, "id"), Attr(el, "version")),

                _ => (null, null)
            };

            if (string.IsNullOrWhiteSpace(id)) continue;
            id = id.Trim();

            // Ссылки с MSBuild-подстановками в имени разрешить невозможно.
            if (id.Contains("$(")) continue;
            if (!seen.Add(id)) continue;

            // Версия-подстановка ($(Ver), пусто) — возьмём последнюю доступную.
            var range = version?.Trim() ?? "";
            if (range.Contains("$(")) range = "";

            result.Add((id, range));
        }

        return result;
    }

    private static string? Attr(XElement el, string name) =>
        el.Attributes().FirstOrDefault(a =>
            string.Equals(a.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string? Child(XElement el, string name) =>
        el.Elements().FirstOrDefault(c =>
            string.Equals(c.Name.LocalName, name, StringComparison.OrdinalIgnoreCase))?.Value;
}
