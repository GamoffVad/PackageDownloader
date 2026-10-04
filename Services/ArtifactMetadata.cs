using System.IO;
using System.IO.Compression;
using System.Text;

namespace PackageDownloader.Services;

/// <summary>
/// Извлекает список зависимостей прямо из скачанных файлов пакета Python.
/// Нужно для пакетов, у которых PyPI не отдаёт <c>requires_dist</c> (старые sdist
/// без PEP 621): тогда зависимости есть только внутри wheel или архива —
/// в <c>*.dist-info/METADATA</c>, <c>PKG-INFO</c> или <c>*.egg-info/requires.txt</c>.
/// </summary>
public static class ArtifactMetadata
{
    /// <summary>
    /// Требования PEP 508 из набора файлов одной версии пакета.
    /// Сначала wheel (метаданные точнее), затем sdist. Пустой список — зависимостей нет.
    /// </summary>
    public static List<string> ExtractRequires(IEnumerable<string> files)
    {
        var list = files.ToList();

        foreach (var f in list.Where(f => f.EndsWith(".whl", StringComparison.OrdinalIgnoreCase)))
            if (FromZip(f, wheel: true) is { Count: > 0 } fromWheel) return fromWheel;

        foreach (var f in list.Where(IsTarball))
            if (FromTarball(f) is { Count: > 0 } fromTar) return fromTar;

        foreach (var f in list.Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)))
            if (FromZip(f, wheel: false) is { Count: > 0 } fromZip) return fromZip;

        return new List<string>();
    }

    private static bool IsTarball(string f) =>
        f.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
        f.EndsWith(".tgz",    StringComparison.OrdinalIgnoreCase);

    // ── wheel и sdist в формате zip ───────────────────────────────────────
    private static List<string> FromZip(string path, bool wheel)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);

            if (wheel)
            {
                var meta = zip.Entries.FirstOrDefault(e =>
                    e.FullName.Trim().EndsWith(".dist-info/METADATA", StringComparison.OrdinalIgnoreCase));
                return meta is null ? new() : FromMetadataHeaders(ReadEntry(meta));
            }

            var requires = zip.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith(".egg-info/requires.txt", StringComparison.OrdinalIgnoreCase));
            var pkgInfo = zip.Entries.FirstOrDefault(e =>
                e.FullName.EndsWith("/PKG-INFO", StringComparison.OrdinalIgnoreCase) &&
                e.FullName.Count(c => c == '/') == 1);

            return Choose(pkgInfo is null ? null : ReadEntry(pkgInfo),
                          requires is null ? null : ReadEntry(requires));
        }
        catch { return new(); }
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var r = new StreamReader(s);
        return r.ReadToEnd();
    }

    // ── sdist в формате .tar.gz ───────────────────────────────────────────
    // Читаем архив блоками по 512 байт сами: System.Formats.Tar на некоторых
    // старых sdist (нестандартный тип записи, пробелы в имени) смещает данные.
    private static List<string> FromTarball(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);

            string? pkgInfo = null, requires = null;
            var header = new byte[512];

            while (ReadExactly(gzip, header, 512))
            {
                if (header.All(b => b == 0)) break;             // два нулевых блока — конец архива

                var name = Encoding.UTF8.GetString(header, 0, 100).TrimEnd('\0').Trim();
                var prefix = Encoding.UTF8.GetString(header, 345, 155).TrimEnd('\0').Trim();
                if (prefix.Length > 0) name = prefix + "/" + name;
                name = name.TrimStart('.', '/');

                long size    = ParseOctal(header, 124, 12);
                long padded  = (size + 511) / 512 * 512;

                bool isPkgInfo  = name.EndsWith("/PKG-INFO", StringComparison.OrdinalIgnoreCase) &&
                                  name.Count(c => c == '/') == 1;
                bool isRequires = name.EndsWith(".egg-info/requires.txt", StringComparison.OrdinalIgnoreCase);

                if ((isPkgInfo || isRequires) && size is > 0 and < 4_000_000)
                {
                    var data = new byte[size];
                    if (!ReadExactly(gzip, data, (int)size)) break;
                    var text = Encoding.UTF8.GetString(data);
                    if (isPkgInfo) pkgInfo ??= text; else requires ??= text;
                    Skip(gzip, padded - size);
                }
                else Skip(gzip, padded);

                if (pkgInfo is not null && requires is not null) break;
            }

            return Choose(pkgInfo, requires);
        }
        catch { return new(); }
    }

    private static bool ReadExactly(Stream s, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = s.Read(buffer, read, count - read);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static void Skip(Stream s, long count)
    {
        var buf = new byte[8192];
        while (count > 0)
        {
            int n = s.Read(buf, 0, (int)Math.Min(buf.Length, count));
            if (n <= 0) return;
            count -= n;
        }
    }

    /// <summary>Числовое поле заголовка tar — восьмеричная строка с пробелами/нулями.</summary>
    private static long ParseOctal(byte[] header, int offset, int length)
    {
        long value = 0;
        for (int i = offset; i < offset + length; i++)
        {
            byte b = header[i];
            if (b is 0 or (byte)' ') continue;
            if (b < (byte)'0' || b > (byte)'7') break;
            value = value * 8 + (b - (byte)'0');
        }
        return value;
    }

    // PKG-INFO с Requires-Dist точнее, чем requires.txt; иначе берём requires.txt.
    private static List<string> Choose(string? pkgInfo, string? requiresTxt)
    {
        if (pkgInfo is not null && FromMetadataHeaders(pkgInfo) is { Count: > 0 } fromHeaders)
            return fromHeaders;
        return requiresTxt is null ? new() : FromRequiresTxt(requiresTxt);
    }

    /// <summary>Заголовки <c>Requires-Dist:</c> из METADATA / PKG-INFO (до пустой строки).</summary>
    private static List<string> FromMetadataHeaders(string text)
    {
        var result = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0) break;                       // конец заголовков, дальше описание
            if (!line.StartsWith("Requires-Dist:", StringComparison.OrdinalIgnoreCase)) continue;
            var value = line["Requires-Dist:".Length..].Trim();
            if (value.Length > 0) result.Add(value);
        }
        return result;
    }

    /// <summary>
    /// Формат setuptools <c>requires.txt</c>: строки до первой секции — обязательные
    /// зависимости; <c>[:маркер]</c> — условные; <c>[extra]</c> и <c>[extra:маркер]</c> — extras (пропускаем).
    /// </summary>
    private static List<string> FromRequiresTxt(string text)
    {
        var result  = new List<string>();
        string? marker = "";                                   // "" — базовая секция, null — extras

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var section = line[1..^1].Trim();
                marker = section.StartsWith(':') ? section[1..].Trim() : null;
                continue;
            }

            if (marker is null) continue;                      // зависимость из extras
            result.Add(marker.Length > 0 ? $"{line} ; {marker}" : line);
        }
        return result;
    }
}
