namespace PackageDownloader.Models;

/// <summary>
/// MCP-сервер из официального реестра registry.modelcontextprotocol.io.
/// Наследует PackageItem (IsSelected, Id, Version, Description) ради совместимости
/// с существующими привязками таблицы и сортировкой, добавляя MCP-метаданные:
/// тип пакета, транспорт и сведения для генерации конфигурации подключения.
/// </summary>
public class McpServerItem : PackageItem
{
    /// <summary>stdio | sse | streamable-http | (пусто).</summary>
    public string Transport    { get; init; } = "";

    /// <summary>npm | pypi | oci | remote.</summary>
    public string RegistryType { get; init; } = "";

    /// <summary>Сведения о пакете для скачивания и генерации конфига (null для remote-серверов).</summary>
    public McpPackageInfo? Package { get; init; }

    /// <summary>true — пакет можно скачать существующим npm-загрузчиком.</summary>
    public bool IsDownloadable => Package is { RegistryType: "npm" };
}

/// <summary>Пакет MCP-сервера (npm/pypi/oci) с параметрами запуска.</summary>
public class McpPackageInfo
{
    public string RegistryType    { get; init; } = "";
    public string RegistryBaseUrl { get; init; } = "https://registry.npmjs.org";
    public string Identifier      { get; init; } = "";
    public string Version         { get; init; } = "";
    public string RuntimeHint     { get; init; } = "";   // npx | uvx | docker | …
    public string Transport       { get; init; } = "";

    public List<string>   RuntimeArguments  { get; init; } = new();
    public List<string>   PackageArguments  { get; init; } = new();
    public List<McpEnvVar> Env              { get; init; } = new();
}

/// <summary>Переменная окружения, требуемая MCP-сервером.</summary>
public class McpEnvVar
{
    public string  Name       { get; init; } = "";
    public string? Default    { get; init; }
    public bool    IsRequired { get; init; }
    public bool    IsSecret   { get; init; }
}
