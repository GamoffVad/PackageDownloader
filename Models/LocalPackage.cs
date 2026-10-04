namespace PackageDownloader.Models;

public class LocalPackage
{
    public string   Kind     { get; init; } = "";   // "NuGet" | "NPM" | "MCP" | "LLM"
    public string   Name     { get; init; } = "";
    public string   Version  { get; init; } = "";
    public string   FileName { get; init; } = "";
    public string   FullPath { get; init; } = "";
    public long     Size     { get; init; }
    public DateTime Modified { get; init; }

    /// <summary>Краткое описание (для MCP — из реестра). Пусто для обычных пакетов.</summary>
    public string   Summary  { get; init; } = "";

    /// <summary>Для MCP: путь к tarball основного пакета сервера (для README). Иначе пусто.</summary>
    public string   MainFile { get; init; } = "";

    public string SizeDisplay =>
        Size >= 1_073_741_824 ? $"{Size / 1_073_741_824.0:F1} ГБ"
      : Size >= 1_048_576     ? $"{Size / 1_048_576.0:F1} МБ"
      : Size >= 1_024         ? $"{Size / 1_024.0:F0} КБ"
      : $"{Size} Б";

    public string ModifiedDisplay => Modified.ToString("dd.MM.yyyy HH:mm");
}
