using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using PackageDownloader.Models;

namespace PackageDownloader.Services;

public static partial class LocalScanService
{
    // {Id}.{Version}.nupkg  →  Newtonsoft.Json.13.0.3.nupkg
    [GeneratedRegex(@"^(?<name>.+?)\.(?<ver>\d+(?:\.\d+)+(?:-[0-9A-Za-z.\-]+)?)\.nupkg$",
        RegexOptions.IgnoreCase)]
    private static partial Regex NupkgRegex();

    // {name}-{version}.tgz  →  react-18.2.0.tgz
    [GeneratedRegex(@"^(?<name>.+?)-(?<ver>\d+(?:\.\d+)+(?:-[0-9A-Za-z.\-]+)?)\.tgz$",
        RegexOptions.IgnoreCase)]
    private static partial Regex TgzRegex();

    public static List<LocalPackage> Scan(string nugetDir, string npmDir, string mcpDir, string llmDir, string pyDir)
    {
        var list = new List<LocalPackage>();
        ScanDir(list, nugetDir, "*.nupkg", "NuGet", NupkgRegex());
        ScanDir(list, npmDir,   "*.tgz",   "NPM",   TgzRegex());
        // MCP-сервер — это отдельная папка (сервер + зависимости), а не россыпь .tgz.
        ScanMcp(list, mcpDir);
        // LLM-модели — это папки (author/name), а не отдельные файлы.
        ScanModels(list, llmDir);
        // Python-пакеты раскладываются как <имя>/<версия>/<файлы>.
        ScanPython(list, pyDir);
        return list.OrderByDescending(x => x.Modified).ToList();
    }

    /// <summary>
    /// Сканирует скачанные Python-пакеты: структура «имя/версия/файлы».
    /// Каждая версия — отдельная запись (папка с дистрибутивами + metadata.json).
    /// </summary>
    private static void ScanPython(List<LocalPackage> list, string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

        foreach (var packageDir in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(packageDir);
            foreach (var versionDir in Directory.EnumerateDirectories(packageDir))
            {
                var dists = Directory.EnumerateFiles(versionDir)
                    .Where(f => f.EndsWith(".whl",   StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".zip",    StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".egg",    StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (dists.Count == 0) continue;

                long size = 0; DateTime modified = DateTime.MinValue;
                foreach (var f in dists)
                {
                    var fi = new FileInfo(f);
                    size += fi.Length;
                    if (fi.LastWriteTime > modified) modified = fi.LastWriteTime;
                }

                list.Add(new LocalPackage
                {
                    Kind     = "PyPI",
                    Name     = name,
                    Version  = Path.GetFileName(versionDir),
                    FileName = Path.GetFileName(versionDir),
                    FullPath = versionDir,
                    Size     = size,
                    Modified = modified
                });
            }
        }
    }

    /// <summary>
    /// Сканирует скачанные модели: каждая модель — папка вида author/name
    /// (как их раскладывает загрузчик Hugging Face). Размер — сумма всех файлов.
    /// </summary>
    private static void ScanModels(List<LocalPackage> list, string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

        foreach (var authorDir in Directory.EnumerateDirectories(dir))
        {
            var nameDirs = Directory.EnumerateDirectories(authorDir).ToList();
            if (nameDirs.Count > 0)
                foreach (var nameDir in nameDirs)
                    AddModel(list, nameDir, $"{Path.GetFileName(authorDir)}/{Path.GetFileName(nameDir)}");
            else
                AddModel(list, authorDir, Path.GetFileName(authorDir));
        }
    }

    private static void AddModel(List<LocalPackage> list, string folder, string name)
    {
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).ToList();
        if (files.Count == 0) return;

        long   size     = 0;
        DateTime modified = DateTime.MinValue;
        foreach (var f in files)
        {
            var fi = new FileInfo(f);
            size += fi.Length;
            if (fi.LastWriteTime > modified) modified = fi.LastWriteTime;
        }

        list.Add(new LocalPackage
        {
            Kind     = "LLM",
            Name     = name,
            Version  = "",
            FileName = Path.GetFileName(folder),
            FullPath = folder,
            Size     = size,
            Modified = modified
        });
    }

    /// <summary>
    /// Сканирует скачанные MCP-серверы: каждая папka — один сервер (пакет + зависимости).
    /// Имя и краткое описание берутся из .mcp-meta.json, README — из основного tarball'а.
    /// </summary>
    private static void ScanMcp(List<LocalPackage> list, string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

        foreach (var folder in Directory.EnumerateDirectories(dir))
        {
            var tarballs = Directory.EnumerateFiles(folder, "*.tgz", SearchOption.AllDirectories).ToList();
            if (tarballs.Count == 0) continue;

            string name = Path.GetFileName(folder), desc = "", ver = "", pkgFile = "";
            var metaPath = Path.Combine(folder, McpService.MetaFileName);
            if (File.Exists(metaPath))
                try
                {
                    var root = JsonDocument.Parse(File.ReadAllText(metaPath)).RootElement;
                    name    = MetaStr(root, "name",        name);
                    desc    = MetaStr(root, "description",  "");
                    ver     = MetaStr(root, "version",      "");
                    pkgFile = MetaStr(root, "packageFile",  "");
                }
                catch { /* битые метаданные — используем имя папки */ }

            long size = 0; DateTime modified = DateTime.MinValue;
            foreach (var t in tarballs)
            {
                var fi = new FileInfo(t);
                size += fi.Length;
                if (fi.LastWriteTime > modified) modified = fi.LastWriteTime;
            }

            // Основной tarball сервера (для README); иначе — самый крупный.
            string main = "";
            if (!string.IsNullOrEmpty(pkgFile) && File.Exists(Path.Combine(folder, pkgFile)))
                main = Path.Combine(folder, pkgFile);
            if (main.Length == 0)
                main = tarballs.OrderByDescending(t => new FileInfo(t).Length).First();

            list.Add(new LocalPackage
            {
                Kind     = "MCP",
                Name     = name,
                Summary  = desc,
                Version  = ver,
                FileName = Path.GetFileName(folder),
                FullPath = folder,
                MainFile = main,
                Size     = size,
                Modified = modified
            });
        }
    }

    private static string MetaStr(JsonElement e, string p, string fallback) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    private static void ScanDir(List<LocalPackage> list, string dir, string pattern, string kind, Regex rx)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

        // AllDirectories: пакеты могут лежать как «плоско», так и в иерархии
        // локального фида / кэша NuGet (id\version\id.version.nupkg).
        foreach (var path in Directory.EnumerateFiles(dir, pattern, SearchOption.AllDirectories))
        {
            var fi   = new FileInfo(path);
            var m    = rx.Match(fi.Name);
            var name = m.Success ? m.Groups["name"].Value : Path.GetFileNameWithoutExtension(fi.Name);
            var ver  = m.Success ? m.Groups["ver"].Value  : "";

            list.Add(new LocalPackage
            {
                Kind     = kind,
                Name     = name,
                Version  = ver,
                FileName = fi.Name,
                FullPath = fi.FullName,
                Size     = fi.Length,
                Modified = fi.LastWriteTime
            });
        }
    }

    /// <summary>Читает README верхнего уровня из npm-пакета (.tgz). null — если файла нет.</summary>
    public static string? ReadNpmReadme(string tgzPath)
    {
        try
        {
            using var fs  = File.OpenRead(tgzPath);
            using var gz  = new GZipStream(fs, CompressionMode.Decompress);
            using var tar = new TarReader(gz);

            while (tar.GetNextEntry() is { } entry)
            {
                // npm-пакеты упакованы в каталог package/, README лежит как package/README.md
                var entryName = entry.Name.Replace('\\', '/');
                if (entryName.StartsWith("package/readme", StringComparison.OrdinalIgnoreCase) &&
                    entry.DataStream is not null)
                {
                    using var reader = new StreamReader(entry.DataStream);
                    return reader.ReadToEnd();
                }
            }
        }
        catch { /* битый или нечитаемый архив */ }
        return null;
    }
}
