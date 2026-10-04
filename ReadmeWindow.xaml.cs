using System.Windows;
using System.Windows.Input;
using PackageDownloader.Services;

namespace PackageDownloader;

/// <summary>Модальное окно для чтения README пакета (рендер Markdown).</summary>
public partial class ReadmeWindow : Window
{
    private readonly string _readme;

    public ReadmeWindow(string packageName, string readme)
    {
        InitializeComponent();
        PkgName.Text          = packageName;
        _readme               = readme;
        ReadmeViewer.Document = MarkdownRenderer.Render(readme);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_readme); } catch { /* буфер занят — игнорируем */ }
    }

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) Close();
    }

    public static void Show(Window owner, string packageName, string readme)
        => new ReadmeWindow(packageName, readme) { Owner = owner }.ShowDialog();
}
