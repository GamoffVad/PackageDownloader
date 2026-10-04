using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using PackageDownloader.Models;

namespace PackageDownloader.Services;

/// <summary>
/// Поиск и скачивание моделей нейросетей с Hugging Face Hub.
/// Поиск идёт через /api/models, скачивание — потоковая загрузка всех файлов
/// репозитория модели (/resolve/main/&lt;файл&gt;) в локальную папку, что даёт
/// полностью автономный набор для оффлайн-использования.
/// </summary>
public class HuggingFaceService
{
    private readonly HttpClient _http;
    private readonly string     _endpoint;

    public HuggingFaceService(string endpoint = "https://huggingface.co")
    {
        _endpoint = endpoint.TrimEnd('/');
        _http     = new HttpClient { Timeout = TimeSpan.FromMinutes(60) };
        _http.DefaultRequestHeaders.Add("Accept", "application/json");
        _http.DefaultRequestHeaders.Add("User-Agent", "PackageDownloader");
    }

    public async Task<List<LlmModelItem>> SearchAsync(string query, CancellationToken ct)
    {
        var url = $"{_endpoint}/api/models?limit=50&sort=downloads&direction=-1&full=false";
        if (!string.IsNullOrWhiteSpace(query))
            url += $"&search={Uri.EscapeDataString(query)}";

        var json = await _http.GetStringAsync(url, ct);
        var doc  = JsonDocument.Parse(json);

        var list = new List<LlmModelItem>();
        foreach (var m in doc.RootElement.EnumerateArray())
        {
            var id = Str(m, "id");
            if (string.IsNullOrEmpty(id)) continue;

            list.Add(new LlmModelItem
            {
                Id          = id,
                Authors     = Str(m, "author"),
                PipelineTag = Str(m, "pipeline_tag"),
                Description = Tags(m),
                Downloads   = Num(m, "downloads"),
                Likes       = Num(m, "likes"),
                Gated       = Gated(m)
            });
        }
        return list;
    }

    /// <summary>Скачивает все файлы репозитория модели в outputDir/&lt;author&gt;/&lt;name&gt;.</summary>
    public async Task DownloadModelAsync(
        LlmModelItem      model,
        string            outputDir,
        IProgress<string> progress,
        CancellationToken ct)
    {
        if (model.IsGated)
        {
            progress.Report($"  ⚠ {model.Id}: модель закрытая ({model.Gated}) — требуется принятие лицензии/токен. Пропуск.");
            return;
        }

        var files = await GetFilesAsync(model.Id, ct);
        if (files.Count == 0) { progress.Report($"  ⚠ {model.Id}: файлы не найдены"); return; }

        var destRoot = Path.Combine(outputDir, SafePath(model.Id));
        Directory.CreateDirectory(destRoot);
        progress.Report($"  📁 {files.Count} файл(ов) → {destRoot}");

        foreach (var rel in files)
        {
            ct.ThrowIfCancellationRequested();
            try { await DownloadFileAsync(model.Id, rel, destRoot, progress, ct); }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                progress.Report($"  ✗ {rel}: доступ запрещён (закрытая модель/нужен токен)");
            }
            catch (Exception ex) { progress.Report($"  ✗ {rel}: {ex.Message}"); }
        }
        progress.Report($"  ✔ {model.Id} готово");
    }

    private async Task<List<string>> GetFilesAsync(string id, CancellationToken ct)
    {
        var json = await _http.GetStringAsync($"{_endpoint}/api/models/{id}", ct);
        var root = JsonDocument.Parse(json).RootElement;
        var files = new List<string>();
        if (root.TryGetProperty("siblings", out var sib) && sib.ValueKind == JsonValueKind.Array)
            foreach (var s in sib.EnumerateArray())
            {
                var f = Str(s, "rfilename");
                if (!string.IsNullOrEmpty(f)) files.Add(f);
            }
        return files;
    }

    private async Task DownloadFileAsync(
        string id, string rel, string destRoot, IProgress<string> progress, CancellationToken ct)
    {
        var target = Path.Combine(destRoot, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);

        var url = $"{_endpoint}/{id}/resolve/main/{EscapePath(rel)}?download=true";

        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        var total = resp.Content.Headers.ContentLength;

        if (File.Exists(target) && total is { } t && new FileInfo(target).Length == t)
        {
            progress.Report($"  [пропуск] {rel} ({Fmt(t)})");
            return;
        }

        progress.Report($"  ↓ {rel}{(total is { } s ? $" ({Fmt(s)})" : "")}");

        var tmp = target + ".part";
        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(tmp))
        {
            var buf = new byte[81920];
            long read = 0, lastReport = 0;
            int n;
            while ((n = await src.ReadAsync(buf, ct)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n), ct);
                read += n;
                if (read - lastReport >= 16 * 1024 * 1024)   // отчёт каждые ~16 МБ
                {
                    progress.Report($"      {Fmt(read)}{(total is { } tt ? $" / {Fmt(tt)}" : "")}");
                    lastReport = read;
                }
            }
        }
        if (File.Exists(target)) File.Delete(target);
        File.Move(tmp, target);
        progress.Report($"  ✓ {rel}");
    }

    // ── Утилиты разбора ───────────────────────────────────────────────────────

    private static string Str(JsonElement e, string p) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? "" : "";

    private static long Num(JsonElement e, string p) =>
        e.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;

    private static string Gated(JsonElement e)
    {
        if (!e.TryGetProperty("gated", out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.True   => "true",
            _                    => ""
        };
    }

    private static string Tags(JsonElement e)
    {
        if (!e.TryGetProperty("tags", out var t) || t.ValueKind != JsonValueKind.Array) return "";
        var meaningful = t.EnumerateArray()
            .Select(x => x.GetString() ?? "")
            .Where(s => s.Length > 0 && !s.Contains(':') && s != "region")
            .Take(8);
        return string.Join(", ", meaningful);
    }

    private static string SafePath(string id)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            if (c != '/' && c != '\\') id = id.Replace(c, '_');
        return id.Replace('/', Path.DirectorySeparatorChar);
    }

    private static string EscapePath(string path) =>
        string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static string Fmt(long bytes) =>
        bytes >= 1_073_741_824 ? $"{bytes / 1_073_741_824.0:F2} ГБ"
      : bytes >= 1_048_576     ? $"{bytes / 1_048_576.0:F1} МБ"
      : bytes >= 1_024         ? $"{bytes / 1_024.0:F0} КБ"
      : $"{bytes} Б";
}
