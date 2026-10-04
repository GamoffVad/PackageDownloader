namespace PackageDownloader;

public class AppSettings
{
    public string NuGetRegistryUrl { get; set; } = "https://api.nuget.org/v3/index.json";
    public string NpmRegistryUrl   { get; set; } = "https://registry.npmjs.org";
    public string McpRegistryUrl   { get; set; } = "https://registry.modelcontextprotocol.io";
    public string HfEndpoint       { get; set; } = "https://huggingface.co";
    public string PyPiIndexUrl     { get; set; } = "https://pypi.org/pypi";
    public string NuGetOutputDir   { get; set; } = "";
    public string NpmOutputDir     { get; set; } = "";
    public string McpOutputDir     { get; set; } = "";
    public string HfOutputDir      { get; set; } = "";
    public string PyPiOutputDir    { get; set; } = "";

    /// <summary>Версия Python для вычисления маркеров зависимостей (python_version в requires_dist).</summary>
    public string PyPiPythonVersion { get; set; } = "3.12";

    /// <summary>Порт локального NPM-реестра для корпоративной сети.</summary>
    public int    NpmServerPort    { get; set; } = 4873;

    /// <summary>Порт локального NuGet-реестра (V3 API) для корпоративной сети.</summary>
    public int    NugetServerPort  { get; set; } = 5000;

    /// <summary>Светлая тема интерфейса (по умолчанию тёмная).</summary>
    public bool   LightTheme       { get; set; }

    // ── Состояние интерфейса ──────────────────────────────────────────────
    public string DownloadSection { get; set; } = "NuGet"; // активный раздел вкладки «Скачать»
    public string LocalFilter  { get; set; } = "All";   // Все | NuGet | NPM | MCP
    public string NugetSortKey { get; set; } = "Id";
    public bool   NugetSortAsc { get; set; } = true;
    public string NpmSortKey   { get; set; } = "Id";
    public bool   NpmSortAsc   { get; set; } = true;
    public string McpSortKey   { get; set; } = "Id";
    public bool   McpSortAsc   { get; set; } = true;
    public string LlmSortKey   { get; set; } = "Downloads";
    public bool   LlmSortAsc   { get; set; } = false;
    public int    SelectedTab  { get; set; } = 0;

    // ── Размер и положение окна ───────────────────────────────────────────
    public double  WindowWidth     { get; set; } = 1060;
    public double  WindowHeight    { get; set; } = 900;
    public double? WindowLeft      { get; set; }
    public double? WindowTop       { get; set; }
    public bool    WindowMaximized { get; set; }
}
