using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using NuGet.Common;
using NuGet.Configuration;
using NuGet.Packaging.Core;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;
using NuGet.Versioning;
using PackageDownloader.Models;

namespace PackageDownloader.Services;

public class NuGetService
{
    private readonly SourceRepository   _repo;
    private readonly SourceCacheContext _cache;

    public NuGetService(string registryUrl = "https://api.nuget.org/v3/index.json")
    {
        var source = new PackageSource(registryUrl);
        _repo  = Repository.Factory.GetCoreV3(source);
        _cache = new SourceCacheContext { NoCache = false };
    }

    public async Task<List<PackageItem>> SearchAsync(string query, CancellationToken ct)
    {
        var resource = await _repo.GetResourceAsync<PackageSearchResource>(ct);
        var results  = await resource.SearchAsync(
            query, new SearchFilter(includePrerelease: false),
            skip: 0, take: 50,
            NullLogger.Instance, ct);

        return results.Select(r => new PackageItem
        {
            Id          = r.Identity.Id,
            Version     = r.Identity.Version.ToNormalizedString(),
            Description = r.Description ?? "",
            Authors     = r.Authors ?? "",
            Downloads   = r.DownloadCount ?? 0
        }).ToList();
    }

    /// <summary>Все версии пакета (свежие первыми). Пустой список — если пакет не найден.</summary>
    public async Task<List<string>> GetVersionsAsync(string id, CancellationToken ct)
    {
        try
        {
            var findRes  = await _repo.GetResourceAsync<FindPackageByIdResource>(ct);
            var versions = await findRes.GetAllVersionsAsync(id, _cache, NullLogger.Instance, ct);
            return versions.OrderByDescending(v => v).Select(v => v.ToNormalizedString()).ToList();
        }
        catch { return new(); }
    }

    public async Task DownloadWithDependenciesAsync(
        string            packageId,
        string            version,
        string            outputDir,
        IProgress<string> progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var findRes = await _repo.GetResourceAsync<FindPackageByIdResource>(ct);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await DownloadRecursiveAsync(findRes, packageId, NuGetVersion.Parse(version),
            outputDir, visited, progress, ct);
        progress.Report("═══════════════════════════════════════");
        progress.Report($"✔ Готово. Пакеты сохранены в: {outputDir}");
    }

    /// <summary>
    /// Скачивает несколько пакетов с зависимостями. Версия может быть точной,
    /// диапазоном NuGet (<c>[1.0,2.0)</c>, <c>1.2.3</c>) или пустой — тогда берётся последняя стабильная.
    /// </summary>
    public async Task DownloadManyWithDependenciesAsync(
        IEnumerable<(string Id, string Range)> packages,
        string            outputDir,
        IProgress<string> progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var findRes = await _repo.GetResourceAsync<FindPackageByIdResource>(ct);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, range) in packages)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var version = await ResolveVersionAsync(findRes, id, range, ct);
                if (version is null)
                {
                    progress.Report($"  ⚠ {id} {range}: подходящая версия не найдена");
                    continue;
                }
                await DownloadRecursiveAsync(findRes, id, version, outputDir, visited, progress, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { progress.Report($"  ✗ {id}: {ex.Message}"); }
        }

        progress.Report("═══════════════════════════════════════");
        progress.Report($"✔ Готово: пакетов с зависимостями — {visited.Count} → {outputDir}");
    }

    /// <summary>Подбирает версию под диапазон; пустой диапазон — последняя стабильная.</summary>
    private async Task<NuGetVersion?> ResolveVersionAsync(
        FindPackageByIdResource findRes, string id, string? range, CancellationToken ct)
    {
        var spec     = (range ?? "").Trim();
        var versions = (await findRes.GetAllVersionsAsync(id, _cache, NullLogger.Instance, ct)).ToList();
        if (versions.Count == 0) return null;

        // Голая версия в PackageReference формально означает «не ниже указанной»,
        // но при массовой загрузке берём ровно её, если она опубликована.
        if (spec.Length > 0 && NuGetVersion.TryParse(spec, out var exact) && versions.Contains(exact))
            return exact;

        if (spec.Length > 0 && VersionRange.TryParse(spec, out var vr))
            if (vr.FindBestMatch(versions) is { } best) return best;

        var stable = versions.Where(v => !v.IsPrerelease).ToList();
        return (stable.Count > 0 ? stable : versions).Max();
    }

    private async Task DownloadRecursiveAsync(
        FindPackageByIdResource findRes,
        string            id,
        NuGetVersion      version,
        string            outputDir,
        HashSet<string>   visited,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var key = $"{id.ToLowerInvariant()}@{version.ToNormalizedString()}";
        if (!visited.Add(key)) return;
        ct.ThrowIfCancellationRequested();

        var ver        = version.ToNormalizedString();
        var fileName   = $"{id}.{ver}.nupkg";
        // Структура: <папка>/<id>/<версия>/<файл + метаданные> (стандартная раскладка локального NuGet-фида).
        var pkgDir     = System.IO.Path.Combine(outputDir, id, ver);
        Directory.CreateDirectory(pkgDir);
        var targetPath = System.IO.Path.Combine(pkgDir, fileName);

        if (File.Exists(targetPath))
        {
            progress.Report($"  [пропуск] {fileName} уже существует");
        }
        else
        {
            progress.Report($"  ↓ {id} {version}");
            using var ms = new MemoryStream();
            bool ok = await findRes.CopyNupkgToStreamAsync(id, version, ms, _cache, NullLogger.Instance, ct);
            if (!ok) { progress.Report($"  ✗ Не удалось скачать {id} {version}"); return; }

            ms.Position = 0;
            await File.WriteAllBytesAsync(targetPath, ms.ToArray(), ct);
            progress.Report($"  ✓ {fileName}");

            // Метаданные (.nuspec) рядом с пакетом — извлекаем из nupkg.
            try
            {
                ms.Position = 0;
                using var zip = new ZipArchive(ms, ZipArchiveMode.Read, leaveOpen: true);
                var nuspec = zip.Entries.FirstOrDefault(e =>
                    e.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
                if (nuspec is not null)
                {
                    using var ns = nuspec.Open();
                    using var fs = File.Create(System.IO.Path.Combine(pkgDir, "metadata.nuspec"));
                    await ns.CopyToAsync(fs, ct);
                }
            }
            catch { /* метаданные не критичны */ }

            ms.Position = 0;
            var deps = ReadDependencies(ms);
            foreach (var (depId, versionRange) in deps)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var allVers = (await findRes.GetAllVersionsAsync(depId, _cache, NullLogger.Instance, ct)).ToList();
                    if (allVers.Count == 0) continue;
                    var range = VersionRange.Parse(versionRange);
                    var best  = range.FindBestMatch(allVers) ?? allVers.Max()!;
                    await DownloadRecursiveAsync(findRes, depId, best, outputDir, visited, progress, ct);
                }
                catch (Exception ex) { progress.Report($"  ⚠ зависимость {depId}: {ex.Message}"); }
            }
        }
    }

    private static List<(string Id, string Version)> ReadDependencies(Stream nupkgStream)
    {
        try
        {
            using var zip       = new ZipArchive(nupkgStream, ZipArchiveMode.Read, leaveOpen: true);
            var nuspecEntry     = zip.Entries.FirstOrDefault(e =>
                e.Name.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase));
            if (nuspecEntry is null) return [];

            using var ns = nuspecEntry.Open();
            var doc      = XDocument.Load(ns);
            var xmlns    = doc.Root!.Name.Namespace;
            var groups   = doc.Descendants(xmlns + "group").ToList();

            IEnumerable<XElement> depElements;
            if (groups.Count == 0)
            {
                depElements = doc.Descendants(xmlns + "dependency");
            }
            else
            {
                var best = groups.FirstOrDefault(g =>
                {
                    var tf = (g.Attribute("targetFramework")?.Value ?? "").ToLowerInvariant();
                    return tf.Contains("netstandard2") || tf.Contains("net6") ||
                           tf.Contains("net7")         || tf.Contains("net8") ||
                           string.IsNullOrEmpty(tf);
                }) ?? groups.First();
                depElements = best.Descendants(xmlns + "dependency");
            }

            return depElements
                .Select(d => (d.Attribute("id")?.Value ?? "", d.Attribute("version")?.Value ?? "*"))
                .Where(t => !string.IsNullOrWhiteSpace(t.Item1))
                .ToList();
        }
        catch { return []; }
    }
}
