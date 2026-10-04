using System.IO;
using System.Net.Http;
using System.Text.Json;
using PackageDownloader.Models;

namespace PackageDownloader.Services;

public class NpmService
{
    private readonly HttpClient _http;
    private readonly string     _registry;

    // Кэш метаданных пакетов (версии + dist-теги) на время жизни сервиса.
    private readonly Dictionary<string, (List<string> Versions, Dictionary<string, string> Tags)> _packuments
        = new(StringComparer.OrdinalIgnoreCase);

    public NpmService(string registryUrl = "https://registry.npmjs.org")
    {
        _registry = registryUrl.TrimEnd('/');
        _http     = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public async Task<List<PackageItem>> SearchAsync(string query, CancellationToken ct)
    {
        var url  = $"{_registry}/-/v1/search?text={Uri.EscapeDataString(query)}&size=30";
        var json = await _http.GetStringAsync(url, ct);
        var doc  = JsonDocument.Parse(json);

        return doc.RootElement.GetProperty("objects")
            .EnumerateArray()
            .Select(obj =>
            {
                var pkg   = obj.GetProperty("package");
                var score = obj.TryGetProperty("score", out var s) &&
                            s.TryGetProperty("final", out var f)
                    ? (long)(f.GetDouble() * 1_000_000) : 0L;
                return new PackageItem
                {
                    Id          = pkg.GetProperty("name").GetString()!,
                    Version     = pkg.GetProperty("version").GetString()!,
                    Description = pkg.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                    Authors     = "",
                    Downloads   = score
                };
            })
            .ToList();
    }

    /// <summary>Все опубликованные версии пакета (свежие первыми). Пустой список — если пакет не найден.</summary>
    public async Task<List<string>> GetVersionsAsync(string id, CancellationToken ct)
    {
        try
        {
            // Сокращённые метаданные (install-v1) меньше полного packument'а и содержат список версий.
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_registry}/{EscapePkg(id)}");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.npm.install-v1+json");
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            var doc  = JsonDocument.Parse(json).RootElement;
            if (!doc.TryGetProperty("versions", out var vers)) return new();
            var list = vers.EnumerateObject().Select(p => p.Name).ToList();
            return VersionUtil.SortDescending(list).ToList();
        }
        catch { return new(); }
    }

    public async Task DownloadWithDependenciesAsync(
        string            packageId,
        string            version,
        string            outputDir,
        IProgress<string> progress,
        CancellationToken ct)
        => await DownloadManyWithDependenciesAsync(
            [(packageId, version)], outputDir, progress, ct);

    /// <summary>
    /// Скачивает несколько пакетов (id + версия или диапазон вида <c>^1.2.3</c>)
    /// вместе с их зависимостями. Общий список уже скачанного — повторов не будет.
    /// </summary>
    public async Task DownloadManyWithDependenciesAsync(
        IEnumerable<(string Id, string Range)> packages,
        string            outputDir,
        IProgress<string> progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, range) in packages)
        {
            ct.ThrowIfCancellationRequested();
            await DownloadRecursiveAsync(id, range, outputDir, visited, progress, ct);
        }

        progress.Report("═══════════════════════════════════════");
        progress.Report($"✔ Готово: пакетов с зависимостями — {visited.Count} → {outputDir}");
    }

    private async Task DownloadRecursiveAsync(
        string            id,
        string            version,
        string            outputDir,
        HashSet<string>   visited,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var exactVersion = await ResolveExactVersionAsync(id, version, ct);
        if (exactVersion is null) { progress.Report($"  ⚠ Не найдена версия {id}@{version}"); return; }

        var key = $"{id.ToLowerInvariant()}@{exactVersion}";
        if (!visited.Add(key)) return;
        ct.ThrowIfCancellationRequested();
        progress.Report($"  ↓ {id}@{exactVersion}");

        try
        {
            var metaUrl  = $"{_registry}/{EscapePkg(id)}/{Uri.EscapeDataString(exactVersion)}";
            var metaJson = await _http.GetStringAsync(metaUrl, ct);
            var meta     = JsonDocument.Parse(metaJson).RootElement;

            var tarball  = meta.GetProperty("dist").GetProperty("tarball").GetString()!;
            var fileName = TgzFileName(id, exactVersion);

            // Структура: <папка>/<имя пакета>/<версия>/<файл + метаданные>
            var pkgDir   = System.IO.Path.Combine(outputDir, SafeFolder(id), exactVersion);
            Directory.CreateDirectory(pkgDir);
            var target   = System.IO.Path.Combine(pkgDir, fileName);

            if (File.Exists(target)) { progress.Report($"  [пропуск] {fileName} уже существует"); }
            else
            {
                var bytes = await _http.GetByteArrayAsync(tarball, ct);
                await File.WriteAllBytesAsync(target, bytes, ct);
                progress.Report($"  ✓ {fileName}");
            }

            // Метаданные версии рядом с пакетом.
            try { await File.WriteAllTextAsync(System.IO.Path.Combine(pkgDir, "metadata.json"), metaJson, ct); }
            catch { /* метаданные не критичны */ }

            if (meta.TryGetProperty("dependencies", out var deps))
            {
                foreach (var dep in deps.EnumerateObject())
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        await DownloadRecursiveAsync(dep.Name, dep.Value.GetString() ?? "latest",
                            outputDir, visited, progress, ct);
                    }
                    catch (Exception ex) { progress.Report($"  ⚠ зависимость {dep.Name}: {ex.Message}"); }
                }
            }
        }
        catch (Exception ex) { progress.Report($"  ✗ {id}: {ex.Message}"); }
    }

    /// <summary>
    /// Подбирает конкретную версию под диапазон npm (<c>^1.2.3</c>, <c>&gt;=1 &lt;2</c>, <c>1.2.x</c>),
    /// dist-тег (<c>latest</c>, <c>next</c>) или точную версию.
    /// </summary>
    private async Task<string?> ResolveExactVersionAsync(string id, string range, CancellationToken ct)
    {
        var spec = (range ?? "").Trim();
        if (SemverRange.IsExact(spec)) return spec.TrimStart('v', '=');

        // Ссылки (git/file/http/npm:alias/workspace) из реестра не скачать.
        if (spec.Contains(':') || spec.StartsWith("./") || spec.StartsWith("../"))
            return null;

        var (versions, tags) = await GetPackumentAsync(id, ct);
        if (versions.Count == 0) return null;

        if (SemverRange.IsRange(spec))
            return SemverRange.FindBest(versions, spec)
                   // Ничего не подошло (например, есть только пред-релизы) — берём latest.
                   ?? (tags.TryGetValue("latest", out var l) ? l : null);

        // Не диапазон — значит dist-тег.
        if (tags.TryGetValue(spec, out var tagged)) return tagged;
        return tags.TryGetValue("latest", out var def) ? def : null;
    }

    /// <summary>Версии и dist-теги пакета (с кэшем на время сессии сервиса).</summary>
    private async Task<(List<string> Versions, Dictionary<string, string> Tags)> GetPackumentAsync(
        string id, CancellationToken ct)
    {
        if (_packuments.TryGetValue(id, out var cached)) return cached;

        var versions = new List<string>();
        var tags     = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{_registry}/{EscapePkg(id)}");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.npm.install-v1+json");
            using var resp = await _http.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();

            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct)).RootElement;
            if (doc.TryGetProperty("versions", out var vers))
                versions = vers.EnumerateObject().Select(p => p.Name).ToList();
            if (doc.TryGetProperty("dist-tags", out var dt))
                foreach (var t in dt.EnumerateObject())
                    if (t.Value.ValueKind == JsonValueKind.String) tags[t.Name] = t.Value.GetString()!;
        }
        catch (OperationCanceledException) { throw; }
        catch { /* пакет недоступен — вернём пустые списки */ }

        var result = (versions, tags);
        _packuments[id] = result;
        return result;
    }

    private static string EscapePkg(string id) =>
        id.StartsWith('@')
            ? "%40" + Uri.EscapeDataString(id.TrimStart('@'))
            : Uri.EscapeDataString(id);

    private static string TgzFileName(string id, string version)
    {
        var name = id.StartsWith('@') ? id.TrimStart('@').Replace('/', '-') : id;
        return $"{name}-{version}.tgz";
    }

    // Имя папки для пакета: scope-пакеты (@scope/name) разворачиваем в «scope-name».
    private static string SafeFolder(string id)
    {
        var name = id.StartsWith('@') ? id.TrimStart('@').Replace('/', '-') : id;
        foreach (var c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
        return name;
    }
}
