using System.Windows;

namespace PackageDownloader;

/// <summary>
/// Переключение светлой/тёмной темы. Цвета вынесены в словари Themes/Dark.xaml и
/// Themes/Light.xaml; элементы ссылаются на них через DynamicResource, поэтому
/// достаточно подменить объединённый словарь — интерфейс перекрашивается на лету.
/// </summary>
public static class Theme
{
    public static bool IsLight { get; private set; }

    public static void Apply(bool light)
    {
        IsLight = light;

        var dicts = Application.Current.Resources.MergedDictionaries;
        for (int i = dicts.Count - 1; i >= 0; i--)
            if (dicts[i].Source?.OriginalString.Contains("Themes") == true)
                dicts.RemoveAt(i);

        var name = light ? "Light" : "Dark";
        dicts.Insert(0, new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/Themes/{name}.xaml", UriKind.Absolute)
        });
    }

    public static void Toggle() => Apply(!IsLight);
}
