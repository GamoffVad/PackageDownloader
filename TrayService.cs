using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Drawing  = System.Drawing;
using WinForms = System.Windows.Forms;

namespace PackageDownloader;

/// <summary>
/// Значок в системном трее со стилизованным контекстным меню (в стиле приложения).
/// Top-right угол значка = NPM-реестр, bottom-right угол = NuGet-реестр.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly HwndSource          _helper;   // невидимое окно: даёт меню фокус, чтобы оно закрывалось по клику мимо
    private bool _disposed;                         // защита от повторного Dispose (вызывается из нескольких мест при выходе)

    // 4 варианта значка (угол NPM × угол NuGet, зелёный = запущен, красный = остановлен).
    private readonly Drawing.Icon _iconBothOff;
    private readonly Drawing.Icon _iconNpmOn;
    private readonly Drawing.Icon _iconNugetOn;
    private readonly Drawing.Icon _iconBothOn;
    private readonly IntPtr _hBothOff, _hNpmOn, _hNugetOn, _hBothOn;

    private static readonly Drawing.Color DotGreen = Drawing.Color.FromArgb(0x4A, 0xD8, 0x95);
    private static readonly Drawing.Color DotRed   = Drawing.Color.FromArgb(0xE5, 0x48, 0x4D);

    private readonly Func<bool>                          _isNpmRunning;
    private readonly Func<(bool running, string? error)> _toggleNpm;
    private readonly Func<bool>                          _isNugetRunning;
    private readonly Func<(bool running, string? error)> _toggleNuget;

    private readonly ContextMenu _menu;
    private readonly MenuItem    _npmItem;
    private readonly TextBlock   _npmHeader;
    private readonly Ellipse     _npmDot;
    private readonly MenuItem    _nugetItem;
    private readonly TextBlock   _nugetHeader;
    private readonly Ellipse     _nugetDot;
    private readonly List<TextBlock> _glyphs = new();   // значки пунктов — перекрашиваются под тему при каждом открытии

    private static readonly Brush GreenDot = new SolidColorBrush(Color.FromRgb(0x4A, 0xD8, 0x95));
    private static readonly Brush RedDot   = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D));

    public TrayService(
        Func<bool>                          isNpmRunning,
        Func<(bool running, string? error)> toggleNpm,
        Func<bool>                          isNugetRunning,
        Func<(bool running, string? error)> toggleNuget,
        Action                              showApp,
        Action                              exit)
    {
        _isNpmRunning   = isNpmRunning;
        _toggleNpm      = toggleNpm;
        _isNugetRunning = isNugetRunning;
        _toggleNuget    = toggleNuget;

        // Скрытое окно-помощник: WS_POPUP (0x80000000) невидимое, WS_EX_TOOLWINDOW (0x80) — без панели задач.
        _helper = new HwndSource(new HwndSourceParameters("_PackageDownloaderHelper")
        {
            WindowStyle         = unchecked((int)0x80000000), // WS_POPUP
            ExtendedWindowStyle = 0x80,                       // WS_EX_TOOLWINDOW
            Width  = 0,
            Height = 0,
            PositionX = -32000,
            PositionY = -32000,
        });

        using (var baseBmp = LoadBaseBitmap())
        {
            (_iconBothOff, _hBothOff) = ComposeStatusIcon(baseBmp, DotRed,   DotRed);
            (_iconNpmOn,   _hNpmOn)   = ComposeStatusIcon(baseBmp, DotGreen, DotRed);
            (_iconNugetOn, _hNugetOn) = ComposeStatusIcon(baseBmp, DotRed,   DotGreen);
            (_iconBothOn,  _hBothOn)  = ComposeStatusIcon(baseBmp, DotGreen, DotGreen);
        }

        _icon = new WinForms.NotifyIcon
        {
            Icon    = SelectIcon(isNpmRunning(), isNugetRunning()),
            Visible = true,
            Text    = "Package Downloader"
        };

        // ── Контекстное меню (в стиле приложения) ──
        _menu = new ContextMenu { Style = Res<Style>("TrayContextMenu") };
        var itemStyle = Res<Style>("TrayMenuItem");
        var sepStyle  = Res<Style>("TraySeparator");

        // Пункт: NPM-реестр
        _npmHeader = new TextBlock();
        _npmDot    = new Ellipse { Width = 9, Height = 9 };
        _npmItem   = new MenuItem
        {
            Style  = itemStyle,
            Header = _npmHeader,
            Tag    = _npmDot,
            Icon   = Glyph(0xE774)  // глобус — NPM-реестр
        };
        _npmItem.Click += (_, _) =>
        {
            var (_, error) = _toggleNpm();
            RefreshServerItem();
            if (!string.IsNullOrEmpty(error))
                _icon.ShowBalloonTip(5000, "NPM-реестр", error!, WinForms.ToolTipIcon.Warning);
        };

        // Пункт: NuGet-реестр
        _nugetHeader = new TextBlock();
        _nugetDot    = new Ellipse { Width = 9, Height = 9 };
        _nugetItem   = new MenuItem
        {
            Style  = itemStyle,
            Header = _nugetHeader,
            Tag    = _nugetDot,
            Icon   = Glyph(0xE7B8)  // пакет — NuGet-реестр
        };
        _nugetItem.Click += (_, _) =>
        {
            var (_, error) = _toggleNuget();
            RefreshServerItem();
            if (!string.IsNullOrEmpty(error))
                _icon.ShowBalloonTip(5000, "NuGet-реестр", error!, WinForms.ToolTipIcon.Warning);
        };

        var openItem = new MenuItem { Style = itemStyle, Header = "Запустить приложение", Icon = Glyph(0xE8A7) };
        openItem.Click += (_, _) => showApp();

        var exitItem = new MenuItem { Style = itemStyle, Header = "Выход", Icon = Glyph(0xE7E8) };
        exitItem.Click += (_, _) => exit();

        _menu.Items.Add(_npmItem);
        _menu.Items.Add(_nugetItem);
        _menu.Items.Add(openItem);
        _menu.Items.Add(new Separator { Style = sepStyle });
        _menu.Items.Add(exitItem);

        // Правый клик — меню; двойной левый — открыть приложение.
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Right) ShowMenu();
        };
        _icon.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) showApp();
        };
    }

    private void ShowMenu()
    {
        RefreshServerItem();
        ApplyMenuTheme();                      // цвета под текущую тему (светлая/тёмная)
        SetForegroundWindow(_helper.Handle);   // чтобы меню закрывалось по клику вне него
        _menu.IsOpen = true;
    }

    /// <summary>Обновляет текст, индикаторы пунктов и значок трея по текущему состоянию обоих серверов.</summary>
    public void RefreshServerItem()
    {
        // Может вызываться из фонового потока (событие Exited процесса) — маршалим в UI-поток.
        var disp = Application.Current?.Dispatcher;
        if (disp is null) return;
        if (!disp.CheckAccess()) { disp.BeginInvoke((Action)RefreshServerItem); return; }

        bool npm   = _isNpmRunning();
        bool nuget = _isNugetRunning();

        _npmHeader.Text   = npm ? "Остановить npm-реестр" : "Запустить npm-реестр";
        _npmDot.Fill      = npm ? GreenDot : RedDot;

        _nugetHeader.Text = nuget ? "Остановить NuGet-реестр" : "Запустить NuGet-реестр";
        _nugetDot.Fill    = nuget ? GreenDot : RedDot;

        _icon.Icon = SelectIcon(npm, nuget);
        _icon.Text = BuildTooltip(npm, nuget);
    }

    private Drawing.Icon SelectIcon(bool npm, bool nuget)
    {
        if (npm && nuget) return _iconBothOn;
        if (npm)          return _iconNpmOn;
        if (nuget)        return _iconNugetOn;
        return _iconBothOff;
    }

    private static string BuildTooltip(bool npm, bool nuget)
    {
        if (npm && nuget) return "Package Downloader — NPM и NuGet запущены";
        if (npm)          return "Package Downloader — NPM-реестр запущен";
        if (nuget)        return "Package Downloader — NuGet-реестр запущен";
        return "Package Downloader";
    }

    private TextBlock Glyph(int codepoint)
    {
        var tb = new TextBlock
        {
            Text       = char.ConvertFromUtf32(codepoint),
            FontFamily = Res<FontFamily>("IconFont"),   // вложенный шрифт — значки видны на любой ОС
            FontSize   = 15,
        };
        _glyphs.Add(tb);   // цвет проставим под текущую тему в ApplyMenuTheme()
        return tb;
    }

    /// <summary>
    /// Явно проставляет цвета меню из текущей темы. Вызывается при каждом открытии, поэтому
    /// меню всегда соответствует активной теме — без опоры на распространение DynamicResource
    /// в отсоединённый попап трея (там оно ненадёжно).
    /// </summary>
    private void ApplyMenuTheme()
    {
        _menu.Background  = Res<Brush>("CardBrush");
        _menu.BorderBrush = Res<Brush>("BorderBrush");

        var text  = Res<Brush>("TextBrush");
        var muted = Res<Brush>("MutedBrush");

        foreach (var item in _menu.Items)
            if (item is MenuItem mi) mi.Foreground = text;

        _npmHeader.Foreground   = text;
        _nugetHeader.Foreground = text;
        foreach (var g in _glyphs) g.Foreground = muted;
    }

    private static T Res<T>(string key) => (T)Application.Current.FindResource(key);

    private static Drawing.Bitmap LoadBaseBitmap()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("app.ico", UriKind.Relative));
            if (info?.Stream is { } s)
            {
                using var ico = new Drawing.Icon(s, 32, 32);
                return ico.ToBitmap();
            }
        }
        catch { /* запасной вариант ниже */ }
        return Drawing.SystemIcons.Application.ToBitmap();
    }

    /// <summary>
    /// Рисует значок: сначала логотип, затем поверх него цветные угловые треугольники
    /// (top-right = NPM, bottom-right = NuGet).
    /// </summary>
    private static (Drawing.Icon icon, IntPtr handle) ComposeStatusIcon(
        Drawing.Bitmap baseBmp, Drawing.Color npmColor, Drawing.Color nugetColor)
    {
        int size = Math.Max(baseBmp.Width, 16);
        float s  = size;

        using var bmp = new Drawing.Bitmap(size, size, Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(bmp))
        {
            g.SmoothingMode     = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

            // Логотип — основа
            g.DrawImage(baseBmp, 0, 0, size, size);

            // NPM — правый верхний треугольник поверх логотипа
            using (var br = new Drawing.SolidBrush(npmColor))
                g.FillPolygon(br, new Drawing.PointF[]
                {
                    new(s * 0.55f, 0),
                    new(s,         0),
                    new(s,         s * 0.45f),
                });

            // NuGet — правый нижний треугольник поверх логотипа
            using (var br = new Drawing.SolidBrush(nugetColor))
                g.FillPolygon(br, new Drawing.PointF[]
                {
                    new(s,         s * 0.55f),
                    new(s,         s),
                    new(s * 0.55f, s),
                });
        }

        var handle = bmp.GetHicon();
        return (Drawing.Icon.FromHandle(handle), handle);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _icon.Visible = false;   // убрать значок из трея ДО освобождения — иначе остаётся «призрак»
        _icon.Dispose();
        _iconBothOff.Dispose();
        _iconNpmOn.Dispose();
        _iconNugetOn.Dispose();
        _iconBothOn.Dispose();
        if (_hBothOff  != IntPtr.Zero) DestroyIcon(_hBothOff);
        if (_hNpmOn    != IntPtr.Zero) DestroyIcon(_hNpmOn);
        if (_hNugetOn  != IntPtr.Zero) DestroyIcon(_hNugetOn);
        if (_hBothOn   != IntPtr.Zero) DestroyIcon(_hBothOn);
        _helper.Dispose();
    }

    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr hIcon);
}
