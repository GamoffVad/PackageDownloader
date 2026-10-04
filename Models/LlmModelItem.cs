namespace PackageDownloader.Models;

/// <summary>
/// Модель нейросети из Hugging Face Hub. Наследует PackageItem (IsSelected, Id,
/// Description, Downloads) ради совместимости с таблицей и сортировкой, добавляя
/// специфичные для моделей поля: задачу, лайки и признак ограниченного доступа.
/// </summary>
public class LlmModelItem : PackageItem
{
    /// <summary>Задача модели, напр. text-generation, sentence-similarity.</summary>
    public string PipelineTag { get; init; } = "";

    public long Likes { get; init; }

    /// <summary>"" — открытая; "manual"/"auto" — требует принятия лицензии/токена.</summary>
    public string Gated { get; init; } = "";

    public bool IsGated => !string.IsNullOrEmpty(Gated) &&
                           !Gated.Equals("false", StringComparison.OrdinalIgnoreCase);
}
