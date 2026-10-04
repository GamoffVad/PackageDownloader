using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace PackageDownloader;

public enum DialogKind { Info, Success, Warning, Error, Question }

/// <summary>
/// Своё стилизованное окно сообщения/предупреждения вместо стандартного MessageBox.
/// </summary>
public partial class MessageDialog : Window
{
    public MessageDialog(DialogKind kind, string title, string message, string okText, string? cancelText)
    {
        InitializeComponent();

        TitleText.Text   = title;
        MessageText.Text  = message;
        OkButton.Content = okText;

        if (cancelText is not null)
        {
            CancelButton.Content    = cancelText;
            CancelButton.Visibility = Visibility.Visible;
        }

        ApplyKind(kind);
        Loaded += (_, _) => OkButton.Focus();
    }

    private void ApplyKind(DialogKind kind)
    {
        var (glyph, color) = kind switch
        {
            DialogKind.Success  => ("", Color.FromRgb(0x4A, 0xD8, 0x95)),
            DialogKind.Warning  => ("", Color.FromRgb(0xF0, 0xB4, 0x5A)),
            DialogKind.Error    => ("", Color.FromRgb(0xFF, 0x6B, 0x6B)),
            DialogKind.Question => ("", Color.FromRgb(0x8B, 0x4F, 0xF5)),
            _                   => ("", Color.FromRgb(0x7C, 0x9C, 0xFF)), // Info
        };
        IconGlyph.Text       = glyph;
        IconGlyph.Foreground = new SolidColorBrush(color);
        IconBadge.Background  = new SolidColorBrush(Color.FromArgb(0x26, color.R, color.G, color.B));
    }

    private void Ok_Click(object sender, RoutedEventArgs e)     => DialogResult = true;
    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void Header_Drag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) DialogResult = false;
    }

    // ── Статический API ───────────────────────────────────────────────────

    public static void Info(Window? owner, string title, string message)    => ShowOk(owner, DialogKind.Info, title, message);
    public static void Success(Window? owner, string title, string message) => ShowOk(owner, DialogKind.Success, title, message);
    public static void Warn(Window? owner, string title, string message)    => ShowOk(owner, DialogKind.Warning, title, message);
    public static void Error(Window? owner, string title, string message)   => ShowOk(owner, DialogKind.Error, title, message);

    /// <summary>Диалог подтверждения. Возвращает true, если нажата основная кнопка.</summary>
    public static bool Confirm(Window? owner, string title, string message,
        string okText = "Да", string cancelText = "Отмена", DialogKind kind = DialogKind.Warning)
    {
        var d = Create(owner, kind, title, message, okText, cancelText);
        return d.ShowDialog() == true;
    }

    private static void ShowOk(Window? owner, DialogKind kind, string title, string message)
        => Create(owner, kind, title, message, "OK", null).ShowDialog();

    private static MessageDialog Create(Window? owner, DialogKind kind, string title, string message,
        string okText, string? cancelText)
    {
        var win = owner ?? Application.Current?.MainWindow;
        return new MessageDialog(kind, title, message, okText, cancelText)
        {
            Owner = win != null && win.IsLoaded ? win : null,
        };
    }
}
