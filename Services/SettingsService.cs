using System.IO;
using System.Text.Json;

namespace PackageDownloader.Services;

public static class SettingsService
{
    static readonly string _dir  = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PackageDownloader");
    static readonly string _path = System.IO.Path.Combine(_dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path))
                       ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public static void Save(AppSettings s)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_path,
            JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
    }
}
