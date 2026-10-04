using System.IO;
using System.Net.Http;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PackageDownloader.Models;

namespace PackageDownloader.Services;

/// <summary>
/// Поиск и скачивание MCP-серверов через официальный реестр
/// registry.modelcontextprotocol.io — это кураторский список серверов с
/// метаданными о пакете (npm/pypi/oci) и транспорте (stdio/sse).
/// Скачивание выполняется для npm-пакетов тем же механизмом, что и обычные
/// npm-пакеты (tarball + рекурсивно зависимости), что даёт автономный набор
/// файлов; рядом сохраняется фрагмент конфигурации подключения.
/// </summary>
public class McpService
{
    private readonly HttpClient _http;
    private readonly string     _registry;

    public McpService(string registryUrl = "https://registry.modelcontextprotocol.io")
    {
        _registry = registryUrl.TrimEnd('/');
        _http     = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
    }

    public async Task<List<McpServerItem>> SearchAsync(string query, CancellationToken ct)
    {
        var url = $"{_registry}/v0/servers?limit=50";
        if (!string.IsNullOrWhiteSpace(query))
            url += $"&search={Uri.EscapeDataString(query)}";

        var json = await _http.GetStringAsync(url, ct);
        var doc  = JsonDocument.Parse(json);

        var list = new List<McpServerItem>();
        if (!doc.RootElement.TryGetProperty("servers", out var servers)) return list;

        foreach (var entry in servers.EnumerateArray())
        {
            if (!entry.TryGetProperty("server", out var s)) continue;

            var name = Str(s, "name");
            if (string.IsNullOrEmpty(name)) continue;

            var pkg     = ParsePackage(s);
            var remotes = s.TryGetProperty("remotes", out var r) && r.ValueKind == JsonValueKind.Array
                ? r : default;

            string regType, transport;
            if (pkg is not null)
            {
                regType   = pkg.RegistryType;
                transport = pkg.Transport;
            }
            else if (remotes.ValueKind == JsonValueKind.Array && remotes.GetArrayLength() > 0)
            {
                regType   = "remote";
                transport = Str(remotes[0], "type");
            }
            else { regType = ""; transport = ""; }

            list.Add(new McpServerItem
            {
                Id           = name,
                Version      = Str(s, "version"),
                Description  = Str(s, "description"),
                Transport    = transport,
                RegistryType = regType,
                Package      = pkg
            });
        }
        return list;
    }

    /// <summary>Имя файла метаданных внутри папки скачанного MCP-сервера.</summary>
    public const string MetaFileName = ".mcp-meta.json";

    /// <summary>
    /// Скачивает MCP-сервер как единое целое: создаёт отдельную папку
    /// outputDir/&lt;сервер&gt;, кладёт туда npm-пакет сервера со всеми зависимостями,
    /// метаданные (.mcp-meta.json) и фрагмент конфигурации подключения.
    /// </summary>
    public async Task DownloadServerAsync(
        McpServerItem     item,
        string            outputDir,
        IProgress<string> progress,
        CancellationToken ct)
    {
        if (item.Package is not { } pkg || pkg.RegistryType != "npm")
        {
            var kind = item.RegistryType is { Length: > 0 } k ? k : "remote";
            progress.Report($"  ⚠ {item.Id}: тип «{kind}» — оффлайн-скачивание поддерживается только для npm, пропуск");
            return;
        }

        var folder = Path.Combine(outputDir, FolderName(item.Id));
        Directory.CreateDirectory(folder);
        progress.Report($"  📁 {folder}");

        var npm = new NpmService(pkg.RegistryBaseUrl);
        await npm.DownloadWithDependenciesAsync(pkg.Identifier, pkg.Version, folder, progress, ct);

        try { WriteMeta(folder, item, pkg); }
        catch (Exception ex) { progress.Report($"  ⚠ метаданные не записаны: {ex.Message}"); }

        try
        {
            var cfg = McpConfigService.Write(item, folder);
            progress.Report($"  ⚙ конфиг: {Path.GetFileName(cfg)}");
        }
        catch (Exception ex) { progress.Report($"  ⚠ конфиг не создан: {ex.Message}"); }
    }

    private static void WriteMeta(string folder, McpServerItem item, McpPackageInfo pkg)
    {
        var meta = new JsonObject
        {
            ["name"]         = item.Id,
            ["description"]  = item.Description,
            ["package"]      = pkg.Identifier,
            ["version"]      = pkg.Version,
            ["packageFile"]  = TgzFileName(pkg.Identifier, pkg.Version),
            ["transport"]    = item.Transport,
            ["registryType"] = item.RegistryType
        };
        var json = meta.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder       = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        File.WriteAllText(Path.Combine(folder, MetaFileName), json);
    }

    private static string FolderName(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) id = id.Replace(c, '_');
        return id.Replace('/', '_').Replace('\\', '_');
    }

    /// <summary>Имя tarball'а как его сохраняет npm-загрузчик (совпадает с NpmService).</summary>
    private static string TgzFileName(string id, string version)
    {
        var name = id.StartsWith('@') ? id.TrimStart('@').Replace('/', '-') : id;
        return $"{name}-{version}.tgz";
    }

    // ── Разбор пакета ───────────────────────────────────────────────────────

    private static McpPackageInfo? ParsePackage(JsonElement server)
    {
        if (!server.TryGetProperty("packages", out var packages) ||
            packages.ValueKind != JsonValueKind.Array || packages.GetArrayLength() == 0)
            return null;

        // Предпочитаем npm-пакет (его умеем скачивать), иначе берём первый доступный.
        JsonElement chosen = default;
        bool found = false;
        foreach (var p in packages.EnumerateArray())
        {
            if (Str(p, "registryType") == "npm") { chosen = p; found = true; break; }
            if (!found) { chosen = p; found = true; }
        }
        if (!found) return null;

        var transport = chosen.TryGetProperty("transport", out var t) ? Str(t, "type") : "";

        return new McpPackageInfo
        {
            RegistryType    = Str(chosen, "registryType"),
            RegistryBaseUrl = Str(chosen, "registryBaseUrl") is { Length: > 0 } u ? u : "https://registry.npmjs.org",
            Identifier      = Str(chosen, "identifier"),
            Version         = Str(chosen, "version"),
            RuntimeHint     = Str(chosen, "runtimeHint"),
            Transport       = transport,
            RuntimeArguments = ParseArgs(chosen, "runtimeArguments"),
            PackageArguments = ParseArgs(chosen, "packageArguments"),
            Env              = ParseEnv(chosen)
        };
    }

    private static List<string> ParseArgs(JsonElement pkg, string prop)
    {
        var result = new List<string>();
        if (!pkg.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return result;
        foreach (var a in arr.EnumerateArray())
        {
            var v = Str(a, "value");
            if (!string.IsNullOrEmpty(v)) result.Add(v);
        }
        return result;
    }

    private static List<McpEnvVar> ParseEnv(JsonElement pkg)
    {
        var result = new List<McpEnvVar>();
        if (!pkg.TryGetProperty("environmentVariables", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var e in arr.EnumerateArray())
        {
            var n = Str(e, "name");
            if (string.IsNullOrEmpty(n)) continue;
            result.Add(new McpEnvVar
            {
                Name       = n,
                Default    = e.TryGetProperty("default", out var d) ? d.GetString() : null,
                IsRequired = e.TryGetProperty("isRequired", out var rq) && rq.ValueKind == JsonValueKind.True,
                IsSecret   = e.TryGetProperty("isSecret",   out var sc) && sc.ValueKind == JsonValueKind.True
            });
        }
        return result;
    }

    private static string Str(JsonElement e, string prop) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";
}
