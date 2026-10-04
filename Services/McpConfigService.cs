using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using PackageDownloader.Models;

namespace PackageDownloader.Services;

/// <summary>
/// Генерирует фрагмент конфигурации подключения MCP-сервера в формате
/// claude_desktop_config.json / mcp.json (секция "mcpServers") и сохраняет
/// его рядом со скачанным пакетом. Значения секретов/обязательных переменных
/// оставляются пустыми — их нужно заполнить перед использованием.
/// </summary>
public static class McpConfigService
{
    public static string Write(McpServerItem item, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var key  = ShortKey(item.Id);
        var node = BuildConfig(item, key);

        var json = node.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder       = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

        var path = Path.Combine(outputDir, $"{key}.mcp.json");
        File.WriteAllText(path, json);
        return path;
    }

    private static JsonObject BuildConfig(McpServerItem item, string key)
    {
        var pkg = item.Package!;

        var command = !string.IsNullOrEmpty(pkg.RuntimeHint)
            ? pkg.RuntimeHint
            : pkg.RegistryType switch { "npm" => "npx", "pypi" => "uvx", "oci" => "docker", _ => "npx" };

        var args = new JsonArray();
        foreach (var a in pkg.RuntimeArguments) args.Add(a);

        // Спецификатор пакета: для npm — name@version, иначе просто идентификатор.
        args.Add(pkg.RegistryType == "npm" && !string.IsNullOrEmpty(pkg.Version)
            ? $"{pkg.Identifier}@{pkg.Version}"
            : pkg.Identifier);

        foreach (var a in pkg.PackageArguments) args.Add(a);

        var server = new JsonObject
        {
            ["command"] = command,
            ["args"]    = args
        };

        if (pkg.Env.Count > 0)
        {
            var env = new JsonObject();
            foreach (var e in pkg.Env) env[e.Name] = e.Default ?? "";
            server["env"] = env;
        }

        return new JsonObject
        {
            ["mcpServers"] = new JsonObject { [key] = server }
        };
    }

    /// <summary>Короткий ключ сервера: последний сегмент имени без префикса домена.</summary>
    private static string ShortKey(string name)
    {
        var tail = name;
        int slash = tail.LastIndexOf('/');
        if (slash >= 0 && slash < tail.Length - 1) tail = tail[(slash + 1)..];
        tail = tail.Replace('@', '-').Replace(' ', '-');
        return string.IsNullOrEmpty(tail) ? "server" : tail;
    }
}
