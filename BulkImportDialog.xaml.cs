using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PackageDownloader.Services;

namespace PackageDownloader;

/// <summary>Что импортируем: манифест npm, файл проекта .NET или файл зависимостей Python.</summary>
public enum BulkImportMode { PackageJson, ProjectFile, PythonRequirements }

/// <summary>
/// Диалог массовой загрузки: вставленный package.json (npm), файл проекта
/// (.csproj / Directory.Packages.props / packages.config) или файл зависимостей
/// Python (requirements.txt / pyproject.toml / Pipfile) разбирается
/// в список «пакет → диапазон версий».
/// </summary>
public partial class BulkImportDialog : Window
{
    private readonly BulkImportMode _mode;
    private readonly string         _pythonVersion;

    /// <summary>Разобранные пакеты — заполняется при нажатии «Скачать».</summary>
    public List<(string Id, string Range)> Packages { get; private set; } = new();

    public BulkImportDialog(BulkImportMode mode, string pythonVersion = "3.12")
    {
        InitializeComponent();
        _mode          = mode;
        _pythonVersion = pythonVersion;

        switch (mode)
        {
            case BulkImportMode.PackageJson:
                TitleText.Text       = "Загрузка по package.json";
                HintText.Text        = "Вставьте содержимое package.json — все указанные пакеты будут скачаны " +
                                       "вместе с зависимостями.";
                PlaceholderText.Text = "{\n  \"dependencies\": {\n    \"react\": \"^18.2.0\"\n  }\n}";
                break;

            case BulkImportMode.ProjectFile:
                TitleText.Text           = "Загрузка по файлу проекта";
                HintText.Text            = "Вставьте содержимое .csproj, Directory.Packages.props или packages.config — " +
                                           "все пакеты будут скачаны вместе с зависимостями.";
                PlaceholderText.Text     = "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n" +
                                           "    <PackageReference Include=\"Newtonsoft.Json\" Version=\"13.0.3\" />\n" +
                                           "  </ItemGroup>\n</Project>";
                SectionsPanel.Visibility = Visibility.Collapsed;
                break;

            default:
                TitleText.Text           = "Загрузка по файлу зависимостей";
                HintText.Text            = $"Вставьте requirements.txt, pyproject.toml или Pipfile — пакеты будут " +
                                           $"скачаны вместе с зависимостями для Python {pythonVersion}.";
                PlaceholderText.Text     = "flask>=3.0\nrequests==2.31.0\nnumpy";
                SectionsPanel.Visibility = Visibility.Collapsed;
                break;
        }

        Loaded += (_, _) => ContentBox.Focus();
    }

    private DepSections SelectedSections()
    {
        DepSections s = 0;
        if (DepsChk.IsChecked     == true) s |= DepSections.Dependencies;
        if (DevDepsChk.IsChecked  == true) s |= DepSections.DevDependencies;
        if (PeerDepsChk.IsChecked == true) s |= DepSections.PeerDependencies;
        if (OptDepsChk.IsChecked  == true) s |= DepSections.OptionalDependencies;
        return s;
    }

    private void OpenFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = _mode switch
            {
                BulkImportMode.PackageJson => "Выберите package.json",
                BulkImportMode.ProjectFile => "Выберите файл проекта",
                _                          => "Выберите файл зависимостей"
            },
            Filter = _mode switch
            {
                BulkImportMode.PackageJson => "package.json|package.json|JSON (*.json)|*.json|Все файлы (*.*)|*.*",
                BulkImportMode.ProjectFile => "Файлы проекта|*.csproj;*.fsproj;*.vbproj;*.props;packages.config|Все файлы (*.*)|*.*",
                _                          => "Зависимости Python|requirements*.txt;pyproject.toml;Pipfile;*.txt|Все файлы (*.*)|*.*"
            }
        };
        if (dlg.ShowDialog(this) != true) return;

        try   { ContentBox.Text = File.ReadAllText(dlg.FileName); ShowError(null); }
        catch (Exception ex) { ShowError($"Не удалось прочитать файл: {ex.Message}"); }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var text = ContentBox.Text.Trim();
        if (text.Length == 0)
        {
            ShowError(_mode switch
            {
                BulkImportMode.PackageJson => "Вставьте содержимое package.json.",
                BulkImportMode.ProjectFile => "Вставьте содержимое файла проекта.",
                _                          => "Вставьте содержимое файла зависимостей."
            });
            return;
        }

        try
        {
            switch (_mode)
            {
                case BulkImportMode.PackageJson:
                    var sections = SelectedSections();
                    if (sections == 0) { ShowError("Выберите хотя бы одну секцию зависимостей."); return; }
                    Packages = PackageJsonParser.Parse(text, sections);
                    break;

                case BulkImportMode.ProjectFile:
                    Packages = ProjectFileParser.Parse(text);
                    break;

                default:
                    Packages = RequirementsParser.Parse(text, _pythonVersion);
                    break;
            }
        }
        catch (JsonException ex)               { ShowError($"Некорректный JSON: {ex.Message}"); return; }
        catch (System.Xml.XmlException ex)     { ShowError($"Некорректный XML: {ex.Message}"); return; }
        catch (Exception ex)                   { ShowError(ex.Message); return; }

        if (Packages.Count == 0)
        {
            ShowError(_mode switch
            {
                BulkImportMode.PackageJson => "В выбранных секциях нет зависимостей.",
                BulkImportMode.ProjectFile => "В файле не найдено ни одного PackageReference / package.",
                _                          => "В файле не найдено ни одной зависимости."
            });
            return;
        }

        DialogResult = true;
    }

    private void ShowError(string? message)
    {
        ErrorText.Text       = message ?? "";
        ErrorText.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }
}
