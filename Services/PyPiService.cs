using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using PackageDownloader.Models;

namespace PackageDownloader.Services;

/// <summary>
/// Загрузчик пакетов Python из PyPI (Warehouse JSON API).
/// У PyPI нет публичного API поиска, поэтому «поиск по похожести» делается так:
/// один раз скачивается полный список имён пакетов (PEP 503 simple index),
/// кэшируется, и запрос сопоставляется с именами локально. Версии и файлы
/// берутся из JSON-API <c>{index}/{name}/json</c>.
/// </summary>
public class PyPiService
{
    private readonly HttpClient _http;
    private readonly string     _index;

    /// <summary>Окружение для вычисления маркеров зависимостей (версия Python, платформа).</summary>
    private readonly Dictionary<string, string> _env;

    // Кэш имён пакетов (нормализованных) — общий для всех экземпляров, привязан к адресу индекса.
    private static List<string>?       _names;
    private static string?             _namesOrigin;
    private static readonly SemaphoreSlim _namesLock = new(1, 1);

    // Кэш списка версий по имени пакета — на время одной загрузки дерева зависимостей.
    private readonly Dictionary<string, List<string>> _versionCache = new(StringComparer.OrdinalIgnoreCase);

    public PyPiService(string indexUrl = "https://pypi.org/pypi", string pythonVersion = "3.12")
    {
        _index = indexUrl.TrimEnd('/');
        _env   = PyMarker.Environment(pythonVersion);
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(120) };
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
        _http.DefaultRequestHeaders.Add("User-Agent", "PackageDownloader");
    }

    /// <summary>
    /// Поиск по похожести: возвращает имена пакетов, чьё имя совпадает/начинается/содержит запрос
    /// (точные — первыми, затем по длине имени). Пустой список — если ничего не найдено.
    /// </summary>
    public async Task<List<string>> SearchAsync(string query, CancellationToken ct)
    {
        var nq = Normalize(query);
        if (nq.Length == 0) return new();

        var all = await EnsureNamesAsync(ct);
        var ranked = new List<(string name, int rank)>();
        foreach (var name in all)
        {
            int rank;
            if (name.Length == nq.Length && name == nq)         rank = 0;
            else if (name.StartsWith(nq, StringComparison.Ordinal)) rank = 1;
            else if (name.Contains(nq, StringComparison.Ordinal))   rank = 2;
            else continue;
            ranked.Add((name, rank));
        }

        return ranked
            .OrderBy(r => r.rank)
            .ThenBy(r => r.name.Length)
            .ThenBy(r => r.name, StringComparer.Ordinal)
            .Take(40)
            .Select(r => r.name)
            .ToList();
    }

    /// <summary>Информация о пакете и все его версии (свежие первыми) — из одного запроса JSON-API.</summary>
    public async Task<(PackageItem info, List<string> versions)?> GetPackageAsync(string name, CancellationToken ct)
    {
        try
        {
            var json = await _http.GetStringAsync($"{_index}/{Uri.EscapeDataString(name)}/json", ct);
            var root = JsonDocument.Parse(json).RootElement;
            var info = root.GetProperty("info");

            var item = new PackageItem
            {
                Id          = Str(info, "name") is { Length: > 0 } nm ? nm : name,
                Version     = Str(info, "version"),
                Description = Str(info, "summary"),
                Authors     = Str(info, "author")
            };

            var versions = new List<string>();
            if (root.TryGetProperty("releases", out var releases))
                versions = VersionUtil.SortDescending(
                    releases.EnumerateObject()
                        .Where(p => p.Value.ValueKind == JsonValueKind.Array && p.Value.GetArrayLength() > 0)
                        .Select(p => p.Name)).ToList();

            return (item, versions);
        }
        catch { return null; }
    }

    /// <summary>
    /// Скачивает пакет вместе со всем деревом зависимостей (<c>requires_dist</c>).
    /// Необязательные зависимости (extras) не берутся; маркеры окружения
    /// вычисляются для версии Python из настроек и платформы Windows.
    /// </summary>
    public async Task DownloadWithDependenciesAsync(
        string            name,
        string            version,
        string            outputDir,
        IProgress<string> progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var downloaded = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        await DownloadRecursiveAsync(name, version, outputDir, downloaded, 0, progress, ct);

        int total = downloaded.Sum(p => p.Value.Count);
        progress.Report("═══════════════════════════════════════");
        progress.Report($"✔ Готово: пакетов с зависимостями — {total} → {outputDir}");
    }

    /// <summary>
    /// Скачивает несколько пакетов с зависимостями. Версия задаётся спецификатором
    /// PEP 440 (<c>==1.2.3</c>, <c>&gt;=1.0,&lt;2.0</c>) или пустой строкой — тогда берётся последняя.
    /// </summary>
    public async Task DownloadManyWithDependenciesAsync(
        IEnumerable<(string Id, string Specifier)> packages,
        string            outputDir,
        IProgress<string> progress,
        CancellationToken ct)
    {
        Directory.CreateDirectory(outputDir);
        var downloaded = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, spec) in packages)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var candidates = await GetVersionListAsync(id, ct);
                if (candidates.Count == 0)
                {
                    progress.Report($"  ⚠ {id}: не найден в индексе");
                    continue;
                }

                var best = Pep440.FindBest(candidates, spec);
                if (best is null)
                {
                    progress.Report($"  ⚠ {id} {spec}: нет подходящей версии");
                    continue;
                }

                await DownloadRecursiveAsync(id, best, outputDir, downloaded, 0, progress, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { progress.Report($"  ✗ {id}: {ex.Message}"); }
        }

        int total = downloaded.Sum(p => p.Value.Count);
        progress.Report("═══════════════════════════════════════");
        progress.Report($"✔ Готово: пакетов с зависимостями — {total} → {outputDir}");
    }

    private async Task DownloadRecursiveAsync(
        string                            name,
        string                            version,
        string                            outputDir,
        Dictionary<string, List<string>>  downloaded,
        int                               depth,
        IProgress<string>                 progress,
        CancellationToken                 ct)
    {
        ct.ThrowIfCancellationRequested();

        var key  = Normalize(name);
        var vers = downloaded.TryGetValue(key, out var v) ? v : downloaded[key] = new List<string>();
        if (vers.Contains(version, StringComparer.OrdinalIgnoreCase)) return;
        vers.Add(version);                                   // помечаем сразу — защита от циклов

        var pad = new string(' ', 2 + depth * 2);
        progress.Report($"{pad}↓ {name} {version}");

        string metaJson;
        try
        {
            var metaUrl = $"{_index}/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(version)}/json";
            metaJson = await _http.GetStringAsync(metaUrl, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            progress.Report($"{pad}✗ {name} {version}: {ex.Message}");
            return;
        }

        var root  = JsonDocument.Parse(metaJson).RootElement;
        var saved = await SaveFilesAsync(root, metaJson, name, version, outputDir, pad, progress, ct);

        // ── Зависимости ───────────────────────────────────────────────────
        var requirements = new List<string>();
        if (root.TryGetProperty("info", out var info) &&
            info.TryGetProperty("requires_dist", out var reqs) &&
            reqs.ValueKind == JsonValueKind.Array)
            requirements = reqs.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .ToList();

        // У старых пакетов PyPI не отдаёт requires_dist — читаем метаданные
        // (METADATA / PKG-INFO / requires.txt) прямо из скачанных файлов.
        if (requirements.Count == 0 && saved.Count > 0)
        {
            requirements = ArtifactMetadata.ExtractRequires(saved);
            if (requirements.Count > 0)
                progress.Report($"{pad}ℹ зависимости прочитаны из архива пакета");
        }

        foreach (var requirement in requirements)
        {
            ct.ThrowIfCancellationRequested();

            var req = PyRequirement.Parse(requirement);
            if (req is null) continue;
            if (!PyMarker.Evaluate(req.Marker, _env)) continue;      // другая платформа/версия или extra
            if (req.IsUrl)
            {
                progress.Report($"{pad}⚠ зависимость {req.Name}: задана прямой ссылкой, пропущена");
                continue;
            }

            var depKey = Normalize(req.Name);
            if (downloaded.TryGetValue(depKey, out var have) &&
                have.Any(h => PyVersion.Parse(h) is { } pv && Pep440.Satisfies(pv, req.Specifier)))
                continue;                                            // уже есть подходящая версия

            try
            {
                var candidates = await GetVersionListAsync(req.Name, ct);
                if (candidates.Count == 0)
                {
                    progress.Report($"{pad}⚠ зависимость {req.Name}: не найдена в индексе");
                    continue;
                }

                var best = Pep440.FindBest(candidates, req.Specifier);
                if (best is null)
                {
                    progress.Report($"{pad}⚠ зависимость {req.Name} {req.Specifier}: нет подходящей версии");
                    continue;
                }

                await DownloadRecursiveAsync(req.Name, best, outputDir, downloaded, depth + 1, progress, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { progress.Report($"{pad}⚠ зависимость {req.Name}: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Файлы одной версии пакета: <c>&lt;папка&gt;/&lt;имя&gt;/&lt;версия&gt;/…</c> + metadata.json.
    /// Возвращает пути к файлам пакета (скачанным и уже существовавшим).
    /// </summary>
    private async Task<List<string>> SaveFilesAsync(
        JsonElement       root,
        string            metaJson,
        string            name,
        string            version,
        string            outputDir,
        string            pad,
        IProgress<string> progress,
        CancellationToken ct)
    {
        var saved  = new List<string>();
        var pkgDir = Path.Combine(outputDir, SafeFolder(name), version);
        Directory.CreateDirectory(pkgDir);

        try { await File.WriteAllTextAsync(Path.Combine(pkgDir, "metadata.json"), metaJson, ct); }
        catch { /* метаданные не критичны */ }

        if (!root.TryGetProperty("urls", out var urls) || urls.GetArrayLength() == 0)
        {
            progress.Report($"{pad}⚠ Нет файлов для {name} {version}");
            return saved;
        }

        foreach (var file in urls.EnumerateArray())
        {
            ct.ThrowIfCancellationRequested();
            var fileName = Str(file, "filename");
            var url      = Str(file, "url");
            if (fileName.Length == 0 || url.Length == 0) continue;

            var target = Path.Combine(pkgDir, fileName);
            if (File.Exists(target))
            {
                progress.Report($"{pad}[пропуск] {fileName} уже существует");
                saved.Add(target);
                continue;
            }

            try
            {
                var bytes = await _http.GetByteArrayAsync(url, ct);
                await File.WriteAllBytesAsync(target, bytes, ct);
                saved.Add(target);
                progress.Report($"{pad}✓ {fileName}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { progress.Report($"{pad}✗ {fileName}: {ex.Message}"); }
        }

        return saved;
    }

    /// <summary>Список опубликованных версий пакета (с файлами), с кэшем на время сессии.</summary>
    private async Task<List<string>> GetVersionListAsync(string name, CancellationToken ct)
    {
        var key = Normalize(name);
        if (_versionCache.TryGetValue(key, out var cached)) return cached;

        var list = new List<string>();
        try
        {
            var json = await _http.GetStringAsync($"{_index}/{Uri.EscapeDataString(name)}/json", ct);
            var root = JsonDocument.Parse(json).RootElement;
            if (root.TryGetProperty("releases", out var releases))
                list = releases.EnumerateObject()
                    .Where(p => p.Value.ValueKind == JsonValueKind.Array && HasUsableFile(p.Value))
                    .Select(p => p.Name)
                    .ToList();
        }
        catch (OperationCanceledException) { throw; }
        catch { /* пакет недоступен — вернём пустой список */ }

        _versionCache[key] = list;
        return list;
    }

    /// <summary>
    /// Версия годится, если у неё есть хотя бы один не отозванный (yanked) файл,
    /// подходящий выбранной версии Python (<c>requires_python</c>).
    /// </summary>
    private bool HasUsableFile(JsonElement files)
    {
        var py = PyVersion.Parse(_env["python_full_version"]);
        foreach (var f in files.EnumerateArray())
        {
            if (f.TryGetProperty("yanked", out var y) && y.ValueKind == JsonValueKind.True) continue;

            var requires = Str(f, "requires_python");
            if (py is not null && requires.Length > 0 && !Pep440.Satisfies(py, requires)) continue;

            return true;
        }
        return false;
    }

    /// <summary>Загружает (один раз) и кэширует список имён пакетов из simple-индекса.</summary>
    private async Task<List<string>> EnsureNamesAsync(CancellationToken ct)
    {
        var origin = Origin();
        if (_names is { } cached && _namesOrigin == origin) return cached;

        await _namesLock.WaitAsync(ct);
        try
        {
            if (_names is { } c2 && _namesOrigin == origin) return c2;

            using var req = new HttpRequestMessage(HttpMethod.Get, $"{origin}/simple/");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.pypi.simple.v1+json");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            var bytes = await resp.Content.ReadAsByteArrayAsync(ct);

            // Достаём только имена (нормализованные), не строя весь JSON-документ.
            var set    = new HashSet<string>();
            var reader = new Utf8JsonReader(bytes);
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("name"))
                {
                    reader.Read();
                    var n = reader.GetString();
                    if (!string.IsNullOrEmpty(n)) set.Add(Normalize(n));
                }
            }

            _names       = set.ToList();
            _namesOrigin = origin;
            return _names;
        }
        catch
        {
            return _names ?? new List<string>();
        }
        finally { _namesLock.Release(); }
    }

    // Нормализация имени по PEP 503: нижний регистр, серии из -, _, . → один «-».
    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        bool prevSep = false;
        foreach (var ch in s.Trim())
        {
            char c = char.ToLowerInvariant(ch);
            if (c is '-' or '_' or '.')
            {
                if (!prevSep && sb.Length > 0) sb.Append('-');
                prevSep = true;
            }
            else { sb.Append(c); prevSep = false; }
        }
        return sb.ToString().TrimEnd('-');
    }

    private string Origin()
    {
        try { var u = new Uri(_index); return $"{u.Scheme}://{u.Authority}"; }
        catch { return "https://pypi.org"; }
    }

    private static string Str(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static string SafeFolder(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '-');
        return name;
    }
}
