using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using PackageDownloader.Models;
using PackageDownloader.Services;

namespace PackageDownloader;

public partial class MainWindow : Window
{
    private AppSettings       _settings;
    private NuGetService      _nuget;
    private NpmService        _npm;
    private McpService        _mcp;
    private HuggingFaceService _hf;
    private PyPiService       _pypi;

    private readonly ObservableCollection<PackageItem>  _nugetItems = new();
    private readonly ObservableCollection<PackageItem>  _npmItems   = new();
    private readonly ObservableCollection<PackageItem>  _mcpItems   = new();
    private readonly ObservableCollection<PackageItem>  _llmItems   = new();
    private readonly ObservableCollection<PackageItem>  _pypiItems  = new();
    private readonly ObservableCollection<LocalPackage> _localItems = new();
    private readonly List<LocalPackage>                 _localAll   = new();
    private string                                      _localFilter = "All";

    private string _mcpSortKey   = "Id";
    private bool   _mcpSortAsc   = true;
    private string _llmSortKey   = "Downloads";
    private bool   _llmSortAsc   = false;

    private CancellationTokenSource? _nugetCts;
    private CancellationTokenSource? _npmCts;
    private CancellationTokenSource? _mcpCts;
    private CancellationTokenSource? _llmCts;
    private CancellationTokenSource? _pypiCts;

    private readonly NpmRegistryServerService   _npmServer   = new();
    private readonly NugetRegistryServerService _nugetServer = new();
    private Progress<string>? _serverLog;
    private Progress<string>? _nugetServerLog;

    // Пока идёт восстановление настроек при старте — не пишем их обратно в файл.
    private bool _suppressSave = true;

    private TrayService? _tray;
    private bool         _exiting;   // true — реальный выход (а не сворачивание в трей)

    public MainWindow()
    {
        InitializeComponent();

        NugetList.ItemsSource = _nugetItems;
        NpmList.ItemsSource   = _npmItems;
        McpList.ItemsSource   = _mcpItems;
        LlmList.ItemsSource   = _llmItems;
        PyPiList.ItemsSource  = _pypiItems;
        LocalList.ItemsSource = _localItems;

        // NuGet, NPM и PyPI: результаты поиска группируем по имени пакета; внутри — версии
        // (вставляются «свежие сверху», поэтому отдельная сортировка версий не нужна).
        CollectionViewSource.GetDefaultView(_nugetItems)
            .GroupDescriptions.Add(new PropertyGroupDescription(nameof(PackageItem.Id)));
        CollectionViewSource.GetDefaultView(_npmItems)
            .GroupDescriptions.Add(new PropertyGroupDescription(nameof(PackageItem.Id)));
        CollectionViewSource.GetDefaultView(_pypiItems)
            .GroupDescriptions.Add(new PropertyGroupDescription(nameof(PackageItem.Id)));

        // «Скачанные»: группировка по имени библиотеки, внутри — версии (новые сверху).
        var localView = CollectionViewSource.GetDefaultView(_localItems);
        localView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(LocalPackage.Name)));
        localView.SortDescriptions.Add(new SortDescription(nameof(LocalPackage.Name),     ListSortDirection.Ascending));
        localView.SortDescriptions.Add(new SortDescription(nameof(LocalPackage.Modified), ListSortDirection.Descending));

        // Progress, созданный на UI-потоке, доставляет логи серверов обратно в UI-поток.
        _serverLog      = new Progress<string>(AppendServerLog);
        _nugetServerLog = new Progress<string>(AppendNugetServerLog);

        // Когда процесс сервера завершается сам по себе (краш, занятый порт и т.п.),
        // обновляем UI и значок трея немедленно — а не только при следующем открытии меню.
        // Событие приходит из фонового потока процесса, поэтому маршалим в UI-поток.
        _npmServer.Exited   += () => Dispatcher.BeginInvoke((Action)UpdateServerUi);
        _nugetServer.Exited += () => Dispatcher.BeginInvoke((Action)UpdateNugetServerUi);

        _settings = SettingsService.Load();
        _nuget    = new NuGetService(_settings.NuGetRegistryUrl);
        _npm      = new NpmService(_settings.NpmRegistryUrl);
        _mcp      = new McpService(_settings.McpRegistryUrl);
        _hf       = new HuggingFaceService(_settings.HfEndpoint);
        _pypi     = new PyPiService(_settings.PyPiIndexUrl, _settings.PyPiPythonVersion);

        // ── Восстановление сохранённого состояния ──
        Theme.Apply(_settings.LightTheme);
        UpdateThemeGlyph();
        RestoreWindowState();

        _mcpSortKey   = _settings.McpSortKey;   _mcpSortAsc   = _settings.McpSortAsc;
        _llmSortKey   = _settings.LlmSortKey;   _llmSortAsc   = _settings.LlmSortAsc;
        UpdateMcpSortIcons();
        UpdateLlmSortIcons();

        RefreshLocal();

        (_settings.LocalFilter switch
        {
            "NuGet" => LocalFilterNuGet,
            "NPM"   => LocalFilterNpm,
            "MCP"   => LocalFilterMcp,
            "LLM"   => LocalFilterLlm,
            "PyPI"  => LocalFilterPyPi,
            _       => LocalFilterAll,
        }).IsChecked = true;

        (_settings.DownloadSection switch
        {
            "NPM"    => DlSecNpm,
            "MCP"    => DlSecMcp,
            "LLM"    => DlSecLlm,
            "Python" => DlSecPython,
            _        => DlSecNuget,
        }).IsChecked = true;

        LoadAdminFields();
        UpdateServerUi();
        UpdateNugetServerUi();

        if (_settings.SelectedTab >= 0 && _settings.SelectedTab < MainTabs.Items.Count)
            MainTabs.SelectedIndex = _settings.SelectedTab;

        _suppressSave = false;   // дальше изменения сохраняем
    }

    private void RestoreWindowState()
    {
        if (_settings.WindowWidth  >= MinWidth)  Width  = _settings.WindowWidth;
        if (_settings.WindowHeight >= MinHeight) Height = _settings.WindowHeight;

        if (_settings.WindowLeft is double l && _settings.WindowTop is double t)
        {
            // Не восстанавливаем за пределами видимой области (например, отключённый монитор).
            double vx = SystemParameters.VirtualScreenLeft, vy = SystemParameters.VirtualScreenTop;
            double vw = SystemParameters.VirtualScreenWidth, vh = SystemParameters.VirtualScreenHeight;
            if (l >= vx - 50 && t >= vy - 50 && l < vx + vw - 100 && t < vy + vh - 100)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = l; Top = t;
            }
        }

        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    /// <summary>Сохраняет текущее состояние интерфейса и окна в settings.json.</summary>
    private void SaveSettings()
    {
        if (_suppressSave) return;

        _settings.LocalFilter  = _localFilter;
        _settings.McpSortKey   = _mcpSortKey;   _settings.McpSortAsc   = _mcpSortAsc;
        _settings.LlmSortKey   = _llmSortKey;   _settings.LlmSortAsc   = _llmSortAsc;
        _settings.SelectedTab  = MainTabs.SelectedIndex;

        if (IsLoaded)
        {
            _settings.WindowMaximized = WindowState == WindowState.Maximized;
            var b = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;
            if (!b.IsEmpty && b.Width > 0 && b.Height > 0)
            {
                _settings.WindowLeft   = b.Left;  _settings.WindowTop    = b.Top;
                _settings.WindowWidth  = b.Width; _settings.WindowHeight = b.Height;
            }
        }

        SettingsService.Save(_settings);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        SaveSettings();          // окно ещё живо — корректно снимаем размер/положение

        // Кнопка «Закрыть» сворачивает приложение в трей, а не завершает его.
        // Полное закрытие выполняется только через пункт «Выход» в меню трея.
        if (!_exiting)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _npmServer.Stop();
        _nugetServer.Stop();
        base.OnClosed(e);
    }

    // ── Интеграция с треем ────────────────────────────────────────────────

    public void SetTray(TrayService tray) => _tray = tray;

    /// <summary>Запущен ли локальный NPM-реестр.</summary>
    public bool IsServerRunning => _npmServer.IsRunning;

    /// <summary>Переключает состояние NPM-реестра из трея.</summary>
    public (bool running, string? error) TrayToggleServer()
    {
        if (_npmServer.IsRunning) { StopServerCore(); return (false, null); }
        bool ok = StartServerCore(out var error);
        return (_npmServer.IsRunning, ok ? null : error);
    }

    /// <summary>Запущен ли локальный NuGet-реестр.</summary>
    public bool IsNugetRunning => _nugetServer.IsRunning;

    /// <summary>Переключает состояние NuGet-реестра из трея.</summary>
    public (bool running, string? error) TrayToggleNuget()
    {
        if (_nugetServer.IsRunning)
        {
            _nugetServer.Stop();
            UpdateNugetServerUi();
            return (false, null);
        }
        bool ok = StartNugetServerCore(out var error);
        return (_nugetServer.IsRunning, ok ? null : error);
    }

    /// <summary>Готовит приложение к полному закрытию (вызывается из пункта «Выход»).</summary>
    public void PrepareExit()
    {
        _exiting = true;
        _npmServer.Stop();
        _nugetServer.Stop();
    }

    // ── Управление окном (свой заголовок) ─────────────────────────────────

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxRestore_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void CloseWin_Click(object sender, RoutedEventArgs e) => Close();

    // ── Тема ──────────────────────────────────────────────────────────────

    private void ThemeToggle_Click(object sender, RoutedEventArgs e)
    {
        Theme.Toggle();
        _settings.LightTheme = Theme.IsLight;
        UpdateThemeGlyph();
        SaveSettings();
    }

    // Тёмная тема → солнце (переключить на светлую); светлая → луна.
    private void UpdateThemeGlyph() =>
        ThemeGlyph.Text = char.ConvertFromUtf32(Theme.IsLight ? 0xE708 : 0xE706);

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (MaxRestoreGlyph is not null)
            MaxRestoreGlyph.Text = WindowState == WindowState.Maximized ? "" : "";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;

        // Скруглённые углы окна на Windows 11 (на более ранних версиях атрибут игнорируется).
        try { int round = 2; DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int)); } catch { /* ignore */ }

        // Без рамки окна нужно вручную ограничить разворот рабочей областью (не перекрывать панель задач).
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_GETMINMAXINFO = 0x0024;
        if (msg == WM_GETMINMAXINFO)
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            IntPtr monitor = MonitorFromWindow(hwnd, 2 /* MONITOR_DEFAULTTONEAREST */);
            if (monitor != IntPtr.Zero)
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                GetMonitorInfo(monitor, ref mi);
                mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
                mmi.ptMaxPosition.Y = mi.rcWork.Top  - mi.rcMonitor.Top;
                mmi.ptMaxSize.X     = mi.rcWork.Right  - mi.rcWork.Left;
                mmi.ptMaxSize.Y     = mi.rcWork.Bottom - mi.rcWork.Top;
                mmi.ptMinTrackSize.X = (int)MinWidth;
                mmi.ptMinTrackSize.Y = (int)MinHeight;
                Marshal.StructureToPtr(mmi, lParam, true);
            }
            handled = true;
        }
        return IntPtr.Zero;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO mi);

    [StructLayout(LayoutKind.Sequential)] private struct POINTSTRUCT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct RECTSTRUCT { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    { public POINTSTRUCT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize; }
    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    { public int cbSize; public RECTSTRUCT rcMonitor, rcWork; public int dwFlags; }

    // ── Вкладки ───────────────────────────────────────────────────────────

    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Игнорируем всплывающие SelectionChanged от внутренних ListView и вложенных вкладок.
        if (!ReferenceEquals(e.OriginalSource, MainTabs)) return;
        if (LocalTab.IsSelected) RefreshLocal();
        SaveSettings();
    }

    // ── Администрирование: пути ───────────────────────────────────────────

    private void LoadAdminFields()
    {
        NugetUrlBox.Text = _settings.NuGetRegistryUrl;
        NugetDirBox.Text = _settings.NuGetOutputDir;
        NpmUrlBox.Text   = _settings.NpmRegistryUrl;
        NpmDirBox.Text   = _settings.NpmOutputDir;
        McpUrlBox.Text   = _settings.McpRegistryUrl;
        McpDirBox.Text   = _settings.McpOutputDir;
        HfUrlBox.Text    = _settings.HfEndpoint;
        HfDirBox.Text    = _settings.HfOutputDir;
        PyPiUrlBox.Text  = _settings.PyPiIndexUrl;
        PyPiDirBox.Text  = _settings.PyPiOutputDir;
        PyPiPyVerBox.Text = _settings.PyPiPythonVersion;
        NpmPortBox.Text   = _settings.NpmServerPort.ToString();
        NugetPortBox.Text = _settings.NugetServerPort.ToString();
    }

    private void AdminSave_Click(object sender, RoutedEventArgs e)
    {
        // Меняем поля существующего объекта, чтобы не потерять остальные настройки.
        _settings.NuGetRegistryUrl = NugetUrlBox.Text.Trim();
        _settings.NuGetOutputDir   = NugetDirBox.Text.Trim();
        _settings.NpmRegistryUrl   = NpmUrlBox.Text.Trim();
        _settings.NpmOutputDir     = NpmDirBox.Text.Trim();
        _settings.McpRegistryUrl   = McpUrlBox.Text.Trim();
        _settings.McpOutputDir     = McpDirBox.Text.Trim();
        _settings.HfEndpoint       = HfUrlBox.Text.Trim();
        _settings.HfOutputDir      = HfDirBox.Text.Trim();
        _settings.PyPiIndexUrl     = PyPiUrlBox.Text.Trim();
        _settings.PyPiOutputDir    = PyPiDirBox.Text.Trim();
        _settings.PyPiPythonVersion = PyPiPyVerBox.Text.Trim() is { Length: > 0 } pyv ? pyv : "3.12";
        _settings.NpmServerPort   = ParsePort(NpmPortBox.Text);
        _settings.NugetServerPort = ParsePort(NugetPortBox.Text, 5000);
        SettingsService.Save(_settings);

        _nuget = new NuGetService(_settings.NuGetRegistryUrl);
        _npm   = new NpmService(_settings.NpmRegistryUrl);
        _mcp   = new McpService(_settings.McpRegistryUrl);
        _hf    = new HuggingFaceService(_settings.HfEndpoint);
        _pypi  = new PyPiService(_settings.PyPiIndexUrl, _settings.PyPiPythonVersion);
        RefreshLocal();

        MessageDialog.Success(this, "Администрирование", "Настройки сохранены.");
    }

    private void NugetBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFolder();
        if (path != null) NugetDirBox.Text = path;
    }

    private void NpmBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFolder();
        if (path != null) NpmDirBox.Text = path;
    }

    private void McpBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFolder();
        if (path != null) McpDirBox.Text = path;
    }

    private void HfBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFolder();
        if (path != null) HfDirBox.Text = path;
    }

    private void PyPiBrowse_Click(object sender, RoutedEventArgs e)
    {
        var path = PickFolder();
        if (path != null) PyPiDirBox.Text = path;
    }

    private string? PickFolder()
    {
        var dlg = new OpenFolderDialog { Title = "Выберите папку" };
        return dlg.ShowDialog(this) == true ? dlg.FolderName : null;
    }

    private static int ParsePort(string text, int @default = 4873) =>
        int.TryParse(text.Trim(), out var p) && p is > 0 and < 65536 ? p : @default;

    // ── Администрирование: локальный NPM-реестр ───────────────────────────

    private void StartServer_Click(object sender, RoutedEventArgs e)
    {
        if (!StartServerCore(out var error) && !string.IsNullOrEmpty(error))
            MessageDialog.Error(this, "NPM-реестр", error);
    }

    /// <summary>Запускает реестр, используя сохранённые настройки. Возвращает false и текст ошибки при неудаче.</summary>
    private bool StartServerCore(out string? error)
    {
        error = null;
        var port = ParsePort(NpmPortBox.Text);

        if (string.IsNullOrWhiteSpace(_settings.NpmOutputDir))
        {
            error = "Сначала укажите папку NPM и нажмите «Сохранить пути».";
            return false;
        }

        if (_settings.NpmServerPort != port)
        {
            _settings.NpmServerPort = port;
            SettingsService.Save(_settings);
        }

        NpmServerLog.Text = "";
        AppendServerLog($"$ node npm-registry.mjs  (порт {port}, папка {_settings.NpmOutputDir})");

        if (!_npmServer.Start(_settings.NpmOutputDir, port, _serverLog, out var err))
        {
            error = err ?? "Не удалось запустить реестр.";
            AppendServerLog($"✗ {error}");
            UpdateServerUi();
            return false;
        }

        UpdateServerUi();
        return true;
    }

    private void StopServer_Click(object sender, RoutedEventArgs e) => StopServerCore();

    private void StopServerCore()
    {
        _npmServer.Stop();
        UpdateServerUi();
    }

    private void AppendServerLog(string line)
    {
        NpmServerLog.Text += line + "\n";
        NpmServerLogScroll.ScrollToBottom();
    }

    private void ClearServerLog_Click(object sender, RoutedEventArgs e) => NpmServerLog.Text = "";

    private void CopyNetUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_npmServer.NetworkUrl is { } url)
            try { Clipboard.SetText(url); } catch { /* буфер занят — игнорируем */ }
    }

    private void CopyCmd_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(ServerCmdText.Text))
            try { Clipboard.SetText(ServerCmdText.Text); } catch { /* буфер занят — игнорируем */ }
    }

    private void UpdateServerUi()
    {
        bool running = _npmServer.IsRunning;

        ServerStatusText.Text = running ? $"Запущен · порт {_npmServer.Port}" : "Остановлен";
        ServerStatusDot.Fill  = running
            ? new SolidColorBrush(Color.FromRgb(0x4A, 0xD8, 0x95))
            : (Brush)FindResource("MutedBrush");

        StartServerBtn.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StopServerBtn.Visibility  = running ? Visibility.Visible    : Visibility.Collapsed;
        NpmPortBox.IsEnabled      = !running;
        NpmPortLabel.Opacity      = running ? 0.5 : 1.0;

        ServerUrlPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (running)
        {
            ServerNetUrlText.Text   = _npmServer.NetworkUrl ?? "";
            ServerLocalUrlText.Text = _npmServer.LocalUrl ?? "";
            ServerCmdText.Text      = $"npm config set registry {_npmServer.NetworkUrl}/";
        }

        _tray?.RefreshServerItem();
    }

    // ── Администрирование: локальный NuGet-реестр ─────────────────────────

    private void StartNugetServer_Click(object sender, RoutedEventArgs e)
    {
        if (!StartNugetServerCore(out var error) && !string.IsNullOrEmpty(error))
            MessageDialog.Error(this, "NuGet-реестр", error);
    }

    private bool StartNugetServerCore(out string? error)
    {
        error = null;
        var port = ParsePort(NugetPortBox.Text, 5000);

        if (string.IsNullOrWhiteSpace(_settings.NuGetOutputDir))
        {
            error = "Сначала укажите папку NuGet и нажмите «Сохранить пути».";
            return false;
        }

        if (_settings.NugetServerPort != port)
        {
            _settings.NugetServerPort = port;
            SettingsService.Save(_settings);
        }

        NugetServerLog.Text = "";
        AppendNugetServerLog($"$ node nuget-server.mjs  (порт {port}, папка {_settings.NuGetOutputDir})");

        if (!_nugetServer.Start(_settings.NuGetOutputDir, port, _nugetServerLog, out var err))
        {
            error = err ?? "Не удалось запустить реестр.";
            AppendNugetServerLog($"✗ {error}");
            UpdateNugetServerUi();
            return false;
        }

        UpdateNugetServerUi();
        return true;
    }

    private void StopNugetServer_Click(object sender, RoutedEventArgs e)
    {
        _nugetServer.Stop();
        UpdateNugetServerUi();
    }

    private void AppendNugetServerLog(string line)
    {
        NugetServerLog.Text += line + "\n";
        NugetServerLogScroll.ScrollToBottom();
    }

    private void ClearNugetServerLog_Click(object sender, RoutedEventArgs e) => NugetServerLog.Text = "";

    private void CopyNugetNetUrl_Click(object sender, RoutedEventArgs e)
    {
        if (_nugetServer.NetworkUrl is { } url)
            try { Clipboard.SetText(url + "/v3/index.json"); } catch { /* буфер занят */ }
    }

    private void CopyNugetCmd_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(NugetServerCmdText.Text))
            try { Clipboard.SetText(NugetServerCmdText.Text); } catch { /* буфер занят */ }
    }

    private void UpdateNugetServerUi()
    {
        bool running = _nugetServer.IsRunning;

        NugetServerStatusText.Text = running ? $"Запущен · порт {_nugetServer.Port}" : "Остановлен";
        NugetServerStatusDot.Fill  = running
            ? new SolidColorBrush(Color.FromRgb(0x4A, 0xD8, 0x95))
            : (Brush)FindResource("MutedBrush");

        StartNugetServerBtn.Visibility = running ? Visibility.Collapsed : Visibility.Visible;
        StopNugetServerBtn.Visibility  = running ? Visibility.Visible    : Visibility.Collapsed;
        NugetPortBox.IsEnabled         = !running;
        NugetPortLabel.Opacity         = running ? 0.5 : 1.0;

        NugetServerUrlPanel.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
        if (running)
        {
            NugetServerNetUrlText.Text   = _nugetServer.IndexUrl ?? "";
            NugetServerLocalUrlText.Text = $"http://localhost:{_nugetServer.Port}/v3/index.json";
            NugetServerCmdText.Text =
                $"<packageSources>\n  <clear />\n  <add key=\"Local\" value=\"{_nugetServer.IndexUrl}\" allowInsecureConnections=\"true\" />\n</packageSources>";
        }

        _tray?.RefreshServerItem();
    }

    // ── Справка по реестрам ───────────────────────────────────────────────

    private void NpmHelp_Click(object sender, RoutedEventArgs e) =>
        ReadmeWindow.Show(this, "NPM-реестр — справка", NpmHelpMd);

    private void NugetHelp_Click(object sender, RoutedEventArgs e) =>
        ReadmeWindow.Show(this, "NuGet-реестр — справка", NugetHelpMd);

    private const string NpmHelpMd = """
        # NPM-реестр

        ## Для администратора

        ### 1. Подготовьте папку с пакетами

        Каждый пакет должен быть упакован командой `npm pack`. Файлы `.tgz` кладите плоско в одну папку:

        ```
        D:\packages\npm\
            lodash-4.17.21.tgz
            vite-6.3.5.tgz
            @vitejs-plugin-vue-5.2.4.tgz
        ```

        Чтобы скачать пакет вместе со всеми зависимостями на машине с интернетом:

        ```bash
        mkdir C:\tmp\pack && cd C:\tmp\pack
        npm init -y
        npm install vite
        npm pack $(npm ls --parseable --depth=0 | tail -n +2) --pack-destination D:\packages\npm
        ```

        ### 2. Запустите реестр

        1. Укажите путь к папке в разделе **Пути → NPM-папка** и нажмите «Сохранить пути».
        2. Установите нужный порт (по умолчанию **4873**) и нажмите **Запустить**.
        3. Скопируйте URL из поля — он понадобится разработчикам.

        ### 3. Брандмауэр

        Откройте входящий порт на сервере:

        ```
        netsh advfirewall firewall add rule name="NPM Registry" ^
              dir=in action=allow protocol=TCP localport=4873
        ```

        ---

        ## Для разработчика

        Выберите **один** из способов:

        ### Способ 1 — Файл `.npmrc` в проекте (рекомендуется)

        Создайте файл `.npmrc` в корне проекта:

        ```
        registry=http://SERVER:4873/
        ```

        Замените `SERVER` на имя или IP сервера.

        ### Способ 2 — Глобальная настройка

        ```bash
        npm config set registry http://SERVER:4873/
        ```

        Возврат к официальному реестру:

        ```bash
        npm config set registry https://registry.npmjs.org/
        ```

        ### Способ 3 — Разовая установка

        ```bash
        npm install <пакет> --registry http://SERVER:4873/
        ```

        ### Проверка подключения

        ```bash
        npm ping --registry http://SERVER:4873/
        ```
        """;

    private const string NugetHelpMd = """
        # NuGet-реестр

        ## Для администратора

        ### 1. Подготовьте структуру папки

        Пакеты должны лежать строго в формате `<id>\<version>\`:

        ```
        D:\packages\nuget\
            Newtonsoft.Json\
                13.0.3\
                    newtonsoft.json.13.0.3.nupkg
            Microsoft.Extensions.Logging\
                8.0.0\
                    microsoft.extensions.logging.8.0.0.nupkg
        ```

        Скачать пакет вместе с зависимостями на машине с интернетом:

        ```bash
        nuget install Newtonsoft.Json -OutputDirectory D:\packages\nuget -NoHttpCache
        ```

        Или через NuGet CLI со всеми зависимостями:

        ```bash
        dotnet add package Newtonsoft.Json
        # Скопируйте из %USERPROFILE%\.nuget\packages\<id>\<version>\
        ```

        ### 2. Запустите реестр

        1. Укажите путь к папке в разделе **Пути → NuGet-папка** и нажмите «Сохранить пути».
        2. Установите нужный порт (по умолчанию **5000**) и нажмите **Запустить**.
        3. Нажмите **Скопировать** — готовый блок `nuget.config` скопируется в буфер обмена.

        ### 3. Брандмауэр

        ```
        netsh advfirewall firewall add rule name="NuGet Registry" ^
              dir=in action=allow protocol=TCP localport=5000
        ```

        ---

        ## Для разработчика

        Создайте файл `nuget.config` в корне проекта или решения и вставьте в него:

        ```xml
        <?xml version="1.0" encoding="utf-8"?>
        <configuration>
          <packageSources>
            <clear />
            <add key="Local"
                 value="http://SERVER:5000/v3/index.json"
                 allowInsecureConnections="true" />
          </packageSources>
        </configuration>
        ```

        Замените `SERVER` на имя или IP сервера, `5000` — на порт, если он изменён.

        > **Важно:** атрибут `allowInsecureConnections="true"` обязателен — NuGet 6+ по умолчанию запрещает HTTP.

        После этого обычные команды работают без изменений:

        ```bash
        dotnet restore
        dotnet build
        dotnet run
        ```

        ### Глобальная настройка (для всех проектов)

        Отредактируйте `%APPDATA%\NuGet\NuGet.Config`:

        ```xml
        <packageSources>
          <clear />
          <add key="Local"
               value="http://SERVER:5000/v3/index.json"
               allowInsecureConnections="true" />
        </packageSources>
        ```
        """;

    // ── NuGet: поиск ──────────────────────────────────────────────────────

    private void NugetSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) NugetSearch_Click(sender, null!);
    }

    private async void NugetSearch_Click(object sender, RoutedEventArgs e)
    {
        var q = NugetSearchBox.Text.Trim();
        if (string.IsNullOrEmpty(q)) return;

        NugetSearchBtn.IsEnabled = false;
        NugetSelectAllChk.IsChecked = false;
        _nugetItems.Clear();
        NugetCountLabel.Text = "Поиск…";
        NugetSelLabel.Visibility = Visibility.Collapsed;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var packages = await _nuget.SearchAsync(q, cts.Token);

            // Для каждого найденного пакета подтягиваем список всех версий (параллельно, с ограничением).
            var sem = new SemaphoreSlim(6);
            var withVersions = await Task.WhenAll(packages.Select(async p =>
            {
                await sem.WaitAsync(cts.Token);
                try   { return (pkg: p, versions: await _nuget.GetVersionsAsync(p.Id, cts.Token)); }
                finally { sem.Release(); }
            }));

            foreach (var (pkg, versions) in withVersions)
            {
                var list = versions.Count > 0 ? versions : new List<string> { pkg.Version };
                foreach (var v in list)
                    _nugetItems.Add(new PackageItem
                    {
                        Id          = pkg.Id,
                        Version     = v,
                        Description = pkg.Description,
                        Authors     = pkg.Authors,
                        Downloads   = pkg.Downloads
                    });
            }
            NugetCountLabel.Text = $"Пакетов: {packages.Count} · версий: {_nugetItems.Count}";
        }
        catch (Exception ex) { NugetCountLabel.Text = "Ошибка"; NugetLog_Append($"✗ {ex.Message}"); }
        finally  { NugetSearchBtn.IsEnabled = true; }
    }

    // ── NuGet: выбор ──────────────────────────────────────────────────────

    private void NugetSelectAll_Changed(object sender, RoutedEventArgs e)
    {
        var val = NugetSelectAllChk.IsChecked == true;
        foreach (var it in _nugetItems) it.IsSelected = val;
        UpdateNugetSelLabel();
    }

    private void NugetItemCheck_Click(object sender, RoutedEventArgs e)
    {
        UpdateNugetSelLabel();
        SyncSelectAllState(NugetSelectAllChk, _nugetItems);
    }

    private void UpdateNugetSelLabel()
    {
        int n = _nugetItems.Count(x => x.IsSelected);
        NugetSelLabel.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        NugetSelLabel.Text = n > 0 ? $"✓ {n} выбрано" : "";
        NugetDownloadBtn.Content = n > 1 ? $"⬇  Скачать ({n})" : "⬇  Скачать";
    }

    // ── NuGet: скачивание ─────────────────────────────────────────────────

    private async void NugetDownload_Click(object sender, RoutedEventArgs e)
    {
        var selected = _nugetItems.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0) { NugetLog_Append("⚠ Выберите пакеты"); return; }
        var dir = _settings.NuGetOutputDir.Trim();
        if (string.IsNullOrEmpty(dir)) { NugetLog_Append("⚠ Папка не задана — откройте Настройки"); return; }

        _nugetCts = new CancellationTokenSource();
        SetNugetBusy(true);
        NugetLog.Text = "";
        NugetLog_Append($"Скачивание {selected.Count} пак. → {dir}");

        var progress = new Progress<string>(NugetLog_Append);
        try
        {
            foreach (var pkg in selected)
            {
                NugetLog_Append($"▶ {pkg.Id} {pkg.Version}");
                await _nuget.DownloadWithDependenciesAsync(pkg.Id, pkg.Version, dir, progress, _nugetCts.Token);
            }
        }
        catch (OperationCanceledException) { NugetLog_Append("⚠ Отменено"); }
        catch (Exception ex)               { NugetLog_Append($"✗ {ex.Message}"); }
        finally { SetNugetBusy(false); _nugetCts = null; RefreshLocal(); }
    }

    /// <summary>Массовая загрузка по вставленному файлу проекта (со всеми зависимостями).</summary>
    private async void NugetProjectFile_Click(object sender, RoutedEventArgs e)
    {
        var dir = _settings.NuGetOutputDir.Trim();
        if (string.IsNullOrEmpty(dir)) { NugetLog_Append("⚠ Папка не задана — откройте Настройки"); return; }

        var dlg = new BulkImportDialog(BulkImportMode.ProjectFile) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var packages = dlg.Packages;
        _nugetCts = new CancellationTokenSource();
        SetNugetBusy(true);
        NugetLog.Text = "";
        NugetLog_Append($"Файл проекта: пакетов — {packages.Count} → {dir}");

        var progress = new Progress<string>(NugetLog_Append);
        try
        {
            foreach (var (id, range) in packages)
                NugetLog_Append($"▶ {id} {(range.Length > 0 ? range : "(последняя)")}");
            await _nuget.DownloadManyWithDependenciesAsync(packages, dir, progress, _nugetCts.Token);
        }
        catch (OperationCanceledException) { NugetLog_Append("⚠ Отменено"); }
        catch (Exception ex)               { NugetLog_Append($"✗ {ex.Message}"); }
        finally { SetNugetBusy(false); _nugetCts = null; RefreshLocal(); }
    }

    private void NugetCancel_Click(object sender, RoutedEventArgs e) => _nugetCts?.Cancel();

    private void SetNugetBusy(bool busy)
    {
        NugetDownloadBtn.IsEnabled = !busy;
        NugetProjBtn.IsEnabled     = !busy;
        NugetSearchBtn.IsEnabled   = !busy;
        NugetProgress.Visibility   = busy ? Visibility.Visible : Visibility.Collapsed;
        NugetCancelBtn.Visibility  = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NugetLog_Append(string msg)
    {
        NugetLog.Text += msg + "\n";
        NugetLogScroll.ScrollToBottom();
    }

    // ── NPM: поиск ────────────────────────────────────────────────────────

    private void NpmSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) NpmSearch_Click(sender, null!);
    }

    private async void NpmSearch_Click(object sender, RoutedEventArgs e)
    {
        var q = NpmSearchBox.Text.Trim();
        if (string.IsNullOrEmpty(q)) return;

        NpmSearchBtn.IsEnabled = false;
        NpmSelectAllChk.IsChecked = false;
        _npmItems.Clear();
        NpmCountLabel.Text = "Поиск…";
        NpmSelLabel.Visibility = Visibility.Collapsed;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var packages = await _npm.SearchAsync(q, cts.Token);

            // Для каждого найденного пакета подтягиваем список всех версий (параллельно, с ограничением).
            var sem = new SemaphoreSlim(6);
            var withVersions = await Task.WhenAll(packages.Select(async p =>
            {
                await sem.WaitAsync(cts.Token);
                try   { return (pkg: p, versions: await _npm.GetVersionsAsync(p.Id, cts.Token)); }
                finally { sem.Release(); }
            }));

            foreach (var (pkg, versions) in withVersions)
            {
                var list = versions.Count > 0 ? versions : new List<string> { pkg.Version };
                foreach (var v in list)
                    _npmItems.Add(new PackageItem
                    {
                        Id          = pkg.Id,
                        Version     = v,
                        Description = pkg.Description,
                        Authors     = pkg.Authors,
                        Downloads   = pkg.Downloads
                    });
            }
            NpmCountLabel.Text = $"Пакетов: {packages.Count} · версий: {_npmItems.Count}";
        }
        catch (Exception ex) { NpmCountLabel.Text = "Ошибка"; NpmLog_Append($"✗ {ex.Message}"); }
        finally  { NpmSearchBtn.IsEnabled = true; }
    }

    // ── NPM: выбор ────────────────────────────────────────────────────────

    private void NpmSelectAll_Changed(object sender, RoutedEventArgs e)
    {
        var val = NpmSelectAllChk.IsChecked == true;
        foreach (var it in _npmItems) it.IsSelected = val;
        UpdateNpmSelLabel();
    }

    private void NpmItemCheck_Click(object sender, RoutedEventArgs e)
    {
        UpdateNpmSelLabel();
        SyncSelectAllState(NpmSelectAllChk, _npmItems);
    }

    private void UpdateNpmSelLabel()
    {
        int n = _npmItems.Count(x => x.IsSelected);
        NpmSelLabel.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        NpmSelLabel.Text = n > 0 ? $"✓ {n} выбрано" : "";
        NpmDownloadBtn.Content = n > 1 ? $"⬇  Скачать ({n})" : "⬇  Скачать";
    }

    // ── NPM: скачивание ───────────────────────────────────────────────────

    private async void NpmDownload_Click(object sender, RoutedEventArgs e)
    {
        var selected = _npmItems.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0) { NpmLog_Append("⚠ Выберите пакеты"); return; }
        var dir = _settings.NpmOutputDir.Trim();
        if (string.IsNullOrEmpty(dir)) { NpmLog_Append("⚠ Папка не задана — откройте Настройки"); return; }

        _npmCts = new CancellationTokenSource();
        SetNpmBusy(true);
        NpmLog.Text = "";
        NpmLog_Append($"Скачивание {selected.Count} пак. → {dir}");

        var progress = new Progress<string>(NpmLog_Append);
        try
        {
            foreach (var pkg in selected)
            {
                NpmLog_Append($"▶ {pkg.Id}@{pkg.Version}");
                await _npm.DownloadWithDependenciesAsync(pkg.Id, pkg.Version, dir, progress, _npmCts.Token);
            }
        }
        catch (OperationCanceledException) { NpmLog_Append("⚠ Отменено"); }
        catch (Exception ex)               { NpmLog_Append($"✗ {ex.Message}"); }
        finally { SetNpmBusy(false); _npmCts = null; RefreshLocal(); }
    }

    /// <summary>Массовая загрузка по вставленному package.json (со всеми зависимостями).</summary>
    private async void NpmPackageJson_Click(object sender, RoutedEventArgs e)
    {
        var dir = _settings.NpmOutputDir.Trim();
        if (string.IsNullOrEmpty(dir)) { NpmLog_Append("⚠ Папка не задана — откройте Настройки"); return; }

        var dlg = new BulkImportDialog(BulkImportMode.PackageJson) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var packages = dlg.Packages;
        _npmCts = new CancellationTokenSource();
        SetNpmBusy(true);
        NpmLog.Text = "";
        NpmLog_Append($"package.json: пакетов — {packages.Count} → {dir}");

        var progress = new Progress<string>(NpmLog_Append);
        try
        {
            foreach (var (id, range) in packages) NpmLog_Append($"▶ {id}@{range}");
            await _npm.DownloadManyWithDependenciesAsync(packages, dir, progress, _npmCts.Token);
        }
        catch (OperationCanceledException) { NpmLog_Append("⚠ Отменено"); }
        catch (Exception ex)               { NpmLog_Append($"✗ {ex.Message}"); }
        finally { SetNpmBusy(false); _npmCts = null; RefreshLocal(); }
    }

    private void NpmCancel_Click(object sender, RoutedEventArgs e) => _npmCts?.Cancel();

    private void SetNpmBusy(bool busy)
    {
        NpmDownloadBtn.IsEnabled = !busy;
        NpmJsonBtn.IsEnabled     = !busy;
        NpmSearchBtn.IsEnabled   = !busy;
        NpmProgress.Visibility   = busy ? Visibility.Visible : Visibility.Collapsed;
        NpmCancelBtn.Visibility  = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void NpmLog_Append(string msg)
    {
        NpmLog.Text += msg + "\n";
        NpmLogScroll.ScrollToBottom();
    }

    // ── MCP: поиск ────────────────────────────────────────────────────────

    private void McpSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) McpSearch_Click(sender, null!);
    }

    private async void McpSearch_Click(object sender, RoutedEventArgs e)
    {
        var q = McpSearchBox.Text.Trim();

        McpSearchBtn.IsEnabled = false;
        McpSelectAllChk.IsChecked = false;
        _mcpItems.Clear();
        McpCountLabel.Text = "Поиск…";
        McpSelLabel.Visibility = Visibility.Collapsed;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var items = await _mcp.SearchAsync(q, cts.Token);
            foreach (var r in items) _mcpItems.Add(r);
            ApplyMcpSort();
            McpCountLabel.Text = $"Найдено: {_mcpItems.Count}";
        }
        catch (Exception ex) { McpCountLabel.Text = "Ошибка"; McpLog_Append($"✗ {ex.Message}"); }
        finally  { McpSearchBtn.IsEnabled = true; }
    }

    // ── MCP: сортировка ───────────────────────────────────────────────────

    private void McpSort_Click(object sender, RoutedEventArgs e)
    {
        var key = (sender as Button)?.Tag as string ?? "Id";
        if (_mcpSortKey == key) _mcpSortAsc = !_mcpSortAsc;
        else { _mcpSortKey = key; _mcpSortAsc = true; }
        ApplyMcpSort();
        UpdateMcpSortIcons();
        SaveSettings();
    }

    private void ApplyMcpSort()
    {
        var sorted = SortItems(_mcpItems, _mcpSortKey, _mcpSortAsc);
        _mcpItems.Clear();
        foreach (var it in sorted) _mcpItems.Add(it);
    }

    private void UpdateMcpSortIcons()
    {
        SetSortIcon(McpSortIdIcon,  "Id",      _mcpSortKey, _mcpSortAsc);
        SetSortIcon(McpSortVerIcon, "Version", _mcpSortKey, _mcpSortAsc);
    }

    // ── MCP: выбор ────────────────────────────────────────────────────────

    private void McpSelectAll_Changed(object sender, RoutedEventArgs e)
    {
        var val = McpSelectAllChk.IsChecked == true;
        foreach (var it in _mcpItems) it.IsSelected = val;
        UpdateMcpSelLabel();
    }

    private void McpItemCheck_Click(object sender, RoutedEventArgs e)
    {
        UpdateMcpSelLabel();
        SyncSelectAllState(McpSelectAllChk, _mcpItems);
    }

    private void UpdateMcpSelLabel()
    {
        int n = _mcpItems.Count(x => x.IsSelected);
        McpSelLabel.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        McpSelLabel.Text = n > 0 ? $"✓ {n} выбрано" : "";
        McpDownloadBtn.Content = n > 1 ? $"⬇  Скачать ({n})" : "⬇  Скачать";
    }

    // ── MCP: скачивание ───────────────────────────────────────────────────

    private async void McpDownload_Click(object sender, RoutedEventArgs e)
    {
        var selected = _mcpItems.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0) { McpLog_Append("⚠ Выберите пакеты"); return; }
        var dir = _settings.McpOutputDir.Trim();
        if (string.IsNullOrEmpty(dir)) { McpLog_Append("⚠ Папка не задана — откройте Настройки"); return; }

        _mcpCts = new CancellationTokenSource();
        SetMcpBusy(true);
        McpLog.Text = "";
        McpLog_Append($"Скачивание {selected.Count} сервер(ов) → {dir}");

        var progress = new Progress<string>(McpLog_Append);
        try
        {
            foreach (var item in selected.OfType<McpServerItem>())
            {
                McpLog_Append($"▶ {item.Id}  [{item.RegistryType}/{item.Transport}]");
                await _mcp.DownloadServerAsync(item, dir, progress, _mcpCts.Token);
            }
        }
        catch (OperationCanceledException) { McpLog_Append("⚠ Отменено"); }
        catch (Exception ex)               { McpLog_Append($"✗ {ex.Message}"); }
        finally { SetMcpBusy(false); _mcpCts = null; RefreshLocal(); }
    }

    private void McpCancel_Click(object sender, RoutedEventArgs e) => _mcpCts?.Cancel();

    private void SetMcpBusy(bool busy)
    {
        McpDownloadBtn.IsEnabled = !busy;
        McpSearchBtn.IsEnabled   = !busy;
        McpProgress.Visibility   = busy ? Visibility.Visible : Visibility.Collapsed;
        McpCancelBtn.Visibility  = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void McpLog_Append(string msg)
    {
        McpLog.Text += msg + "\n";
        McpLogScroll.ScrollToBottom();
    }

    // ── LLM (Hugging Face): поиск ─────────────────────────────────────────

    private void LlmSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) LlmSearch_Click(sender, null!);
    }

    private async void LlmSearch_Click(object sender, RoutedEventArgs e)
    {
        var q = LlmSearchBox.Text.Trim();

        LlmSearchBtn.IsEnabled = false;
        LlmSelectAllChk.IsChecked = false;
        _llmItems.Clear();
        LlmCountLabel.Text = "Поиск…";
        LlmSelLabel.Visibility = Visibility.Collapsed;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var items = await _hf.SearchAsync(q, cts.Token);
            foreach (var r in items) _llmItems.Add(r);
            ApplyLlmSort();
            LlmCountLabel.Text = $"Найдено: {_llmItems.Count}";
        }
        catch (Exception ex) { LlmCountLabel.Text = "Ошибка"; LlmLog_Append($"✗ {ex.Message}"); }
        finally  { LlmSearchBtn.IsEnabled = true; }
    }

    // ── LLM: сортировка ───────────────────────────────────────────────────

    private void LlmSort_Click(object sender, RoutedEventArgs e)
    {
        var key = (sender as Button)?.Tag as string ?? "Id";
        if (_llmSortKey == key) _llmSortAsc = !_llmSortAsc;
        else { _llmSortKey = key; _llmSortAsc = key != "Downloads"; }
        ApplyLlmSort();
        UpdateLlmSortIcons();
        SaveSettings();
    }

    private void ApplyLlmSort()
    {
        var sorted = SortItems(_llmItems, _llmSortKey, _llmSortAsc);
        _llmItems.Clear();
        foreach (var it in sorted) _llmItems.Add(it);
    }

    private void UpdateLlmSortIcons()
    {
        SetSortIcon(LlmSortIdIcon, "Id",        _llmSortKey, _llmSortAsc);
        SetSortIcon(LlmSortDlIcon, "Downloads", _llmSortKey, _llmSortAsc);
    }

    // ── LLM: выбор ────────────────────────────────────────────────────────

    private void LlmSelectAll_Changed(object sender, RoutedEventArgs e)
    {
        var val = LlmSelectAllChk.IsChecked == true;
        foreach (var it in _llmItems) it.IsSelected = val;
        UpdateLlmSelLabel();
    }

    private void LlmItemCheck_Click(object sender, RoutedEventArgs e)
    {
        UpdateLlmSelLabel();
        SyncSelectAllState(LlmSelectAllChk, _llmItems);
    }

    private void UpdateLlmSelLabel()
    {
        int n = _llmItems.Count(x => x.IsSelected);
        LlmSelLabel.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        LlmSelLabel.Text = n > 0 ? $"✓ {n} выбрано" : "";
        LlmDownloadBtn.Content = n > 1 ? $"⬇  Скачать ({n})" : "⬇  Скачать";
    }

    // ── LLM: скачивание ───────────────────────────────────────────────────

    private async void LlmDownload_Click(object sender, RoutedEventArgs e)
    {
        var selected = _llmItems.Where(x => x.IsSelected).OfType<LlmModelItem>().ToList();
        if (selected.Count == 0) { LlmLog_Append("⚠ Выберите модели"); return; }
        var dir = _settings.HfOutputDir.Trim();
        if (string.IsNullOrEmpty(dir)) { LlmLog_Append("⚠ Папка не задана — откройте Администрирование"); return; }

        _llmCts = new CancellationTokenSource();
        SetLlmBusy(true);
        LlmLog.Text = "";
        LlmLog_Append($"Скачивание {selected.Count} модел(ей) → {dir}");
        LlmLog_Append("Модели большие — загрузка может занять время.");

        var progress = new Progress<string>(LlmLog_Append);
        try
        {
            foreach (var model in selected)
            {
                LlmLog_Append($"▶ {model.Id}");
                await _hf.DownloadModelAsync(model, dir, progress, _llmCts.Token);
            }
        }
        catch (OperationCanceledException) { LlmLog_Append("⚠ Отменено"); }
        catch (Exception ex)               { LlmLog_Append($"✗ {ex.Message}"); }
        finally { SetLlmBusy(false); _llmCts = null; }
    }

    private void LlmCancel_Click(object sender, RoutedEventArgs e) => _llmCts?.Cancel();

    // ── Python (PyPI): поиск ──────────────────────────────────────────────

    private void PyPiSearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) PyPiSearch_Click(sender, null!);
    }

    private async void PyPiSearch_Click(object sender, RoutedEventArgs e)
    {
        var q = PyPiSearchBox.Text.Trim();
        if (string.IsNullOrEmpty(q)) return;

        PyPiSearchBtn.IsEnabled = false;
        PyPiSelectAllChk.IsChecked = false;
        _pypiItems.Clear();
        PyPiCountLabel.Text = "Поиск…";
        PyPiSelLabel.Visibility = Visibility.Collapsed;

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));

            // Поиск по похожести имени (по локальному кэшу имён PyPI).
            var names = await _pypi.SearchAsync(q, cts.Token);
            if (names.Count == 0)
            {
                PyPiCountLabel.Text = "Ничего не найдено";
                return;
            }

            // Для каждого найденного пакета подтягиваем описание и все версии (параллельно).
            var sem = new SemaphoreSlim(6);
            var results = await Task.WhenAll(names.Select(async n =>
            {
                await sem.WaitAsync(cts.Token);
                try   { return await _pypi.GetPackageAsync(n, cts.Token); }
                finally { sem.Release(); }
            }));

            foreach (var r in results)
            {
                if (r is not { } pkg) continue;
                var (info, versions) = pkg;
                var list = versions.Count > 0 ? versions : new List<string> { info.Version };
                foreach (var v in list)
                    _pypiItems.Add(new PackageItem
                    {
                        Id          = info.Id,
                        Version     = v,
                        Description = info.Description,
                        Authors     = info.Authors
                    });
            }
            PyPiCountLabel.Text = $"Пакетов: {names.Count} · версий: {_pypiItems.Count}";
        }
        catch (Exception ex) { PyPiCountLabel.Text = "Ошибка"; PyPiLog_Append($"✗ {ex.Message}"); }
        finally  { PyPiSearchBtn.IsEnabled = true; }
    }

    // ── Python: выбор ─────────────────────────────────────────────────────

    private void PyPiSelectAll_Changed(object sender, RoutedEventArgs e)
    {
        var val = PyPiSelectAllChk.IsChecked == true;
        foreach (var it in _pypiItems) it.IsSelected = val;
        UpdatePyPiSelLabel();
    }

    private void PyPiItemCheck_Click(object sender, RoutedEventArgs e)
    {
        UpdatePyPiSelLabel();
        SyncSelectAllState(PyPiSelectAllChk, _pypiItems);
    }

    private void UpdatePyPiSelLabel()
    {
        int n = _pypiItems.Count(x => x.IsSelected);
        PyPiSelLabel.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        PyPiSelLabel.Text = n > 0 ? $"✓ {n} выбрано" : "";
        PyPiDownloadBtn.Content = n > 1 ? $"⬇  Скачать ({n})" : "⬇  Скачать";
    }

    // ── Python: скачивание ────────────────────────────────────────────────

    private async void PyPiDownload_Click(object sender, RoutedEventArgs e)
    {
        var selected = _pypiItems.Where(x => x.IsSelected).ToList();
        if (selected.Count == 0) { PyPiLog_Append("⚠ Выберите версии"); return; }
        var dir = _settings.PyPiOutputDir.Trim();
        if (string.IsNullOrEmpty(dir)) { PyPiLog_Append("⚠ Папка не задана — откройте Администрирование"); return; }

        _pypiCts = new CancellationTokenSource();
        SetPyPiBusy(true);
        PyPiLog.Text = "";
        PyPiLog_Append($"Скачивание {selected.Count} верс. → {dir}");

        var progress = new Progress<string>(PyPiLog_Append);
        try
        {
            foreach (var pkg in selected)
            {
                PyPiLog_Append($"▶ {pkg.Id} {pkg.Version}");
                await _pypi.DownloadWithDependenciesAsync(pkg.Id, pkg.Version, dir, progress, _pypiCts.Token);
            }
        }
        catch (OperationCanceledException) { PyPiLog_Append("⚠ Отменено"); }
        catch (Exception ex)               { PyPiLog_Append($"✗ {ex.Message}"); }
        finally { SetPyPiBusy(false); _pypiCts = null; RefreshLocal(); }
    }

    /// <summary>Массовая загрузка по requirements.txt / pyproject.toml / Pipfile.</summary>
    private async void PyPiRequirements_Click(object sender, RoutedEventArgs e)
    {
        var dir = _settings.PyPiOutputDir.Trim();
        if (string.IsNullOrEmpty(dir)) { PyPiLog_Append("⚠ Папка не задана — откройте Администрирование"); return; }

        var dlg = new BulkImportDialog(BulkImportMode.PythonRequirements, _settings.PyPiPythonVersion) { Owner = this };
        if (dlg.ShowDialog() != true) return;

        var packages = dlg.Packages;
        _pypiCts = new CancellationTokenSource();
        SetPyPiBusy(true);
        PyPiLog.Text = "";
        PyPiLog_Append($"Файл зависимостей: пакетов — {packages.Count} → {dir}");

        var progress = new Progress<string>(PyPiLog_Append);
        try
        {
            foreach (var (id, spec) in packages)
                PyPiLog_Append($"▶ {id} {(spec.Length > 0 ? spec : "(последняя)")}");
            await _pypi.DownloadManyWithDependenciesAsync(packages, dir, progress, _pypiCts.Token);
        }
        catch (OperationCanceledException) { PyPiLog_Append("⚠ Отменено"); }
        catch (Exception ex)               { PyPiLog_Append($"✗ {ex.Message}"); }
        finally { SetPyPiBusy(false); _pypiCts = null; RefreshLocal(); }
    }

    private void PyPiCancel_Click(object sender, RoutedEventArgs e) => _pypiCts?.Cancel();

    private void SetPyPiBusy(bool busy)
    {
        PyPiDownloadBtn.IsEnabled = !busy;
        PyPiFileBtn.IsEnabled     = !busy;
        PyPiSearchBtn.IsEnabled   = !busy;
        PyPiProgress.Visibility   = busy ? Visibility.Visible : Visibility.Collapsed;
        PyPiCancelBtn.Visibility  = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PyPiLog_Append(string msg)
    {
        PyPiLog.Text += msg + "\n";
        PyPiLogScroll.ScrollToBottom();
    }

    private void SetLlmBusy(bool busy)
    {
        LlmDownloadBtn.IsEnabled = !busy;
        LlmSearchBtn.IsEnabled   = !busy;
        LlmProgress.Visibility   = busy ? Visibility.Visible : Visibility.Collapsed;
        LlmCancelBtn.Visibility  = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LlmLog_Append(string msg)
    {
        LlmLog.Text += msg + "\n";
        LlmLogScroll.ScrollToBottom();
    }

    // ── Скачанные ─────────────────────────────────────────────────────────

    private void RefreshLocal()
    {
        _localAll.Clear();
        try
        {
            _localAll.AddRange(LocalScanService.Scan(_settings.NuGetOutputDir, _settings.NpmOutputDir, _settings.McpOutputDir, _settings.HfOutputDir, _settings.PyPiOutputDir));
        }
        catch { /* недоступная папка — оставляем список пустым */ }
        ApplyLocalFilter();
    }

    private void ApplyLocalFilter()
    {
        if (LocalList is null) return;   // событие может прийти во время InitializeComponent

        _localItems.Clear();
        foreach (var p in _localAll)
            if (_localFilter == "All" || p.Kind == _localFilter)
                _localItems.Add(p);

        UpdateLocalCount();
    }

    private void LocalFilter_Changed(object sender, RoutedEventArgs e)
    {
        _localFilter = (sender as System.Windows.Controls.Primitives.ToggleButton)?.Tag as string ?? "All";
        ApplyLocalFilter();
        SaveSettings();
    }

    // ── Вкладка «Скачать»: переключение источника ─────────────────────────

    private void DownloadSection_Changed(object sender, RoutedEventArgs e)
    {
        var tag = (sender as System.Windows.Controls.Primitives.ToggleButton)?.Tag as string ?? "NuGet";
        if (SecNuget is null) return;   // событие может прийти во время InitializeComponent

        SecNuget.Visibility  = tag == "NuGet"  ? Visibility.Visible : Visibility.Collapsed;
        SecNpm.Visibility    = tag == "NPM"    ? Visibility.Visible : Visibility.Collapsed;
        SecMcp.Visibility    = tag == "MCP"    ? Visibility.Visible : Visibility.Collapsed;
        SecLlm.Visibility    = tag == "LLM"    ? Visibility.Visible : Visibility.Collapsed;
        SecPython.Visibility = tag == "Python" ? Visibility.Visible : Visibility.Collapsed;

        _settings.DownloadSection = tag;
        SaveSettings();
    }

    private void UpdateLocalCount() =>
        LocalCountLabel.Text = _localItems.Count > 0 ? $"Файлов: {_localItems.Count}" : "";

    private void LocalRefresh_Click(object sender, RoutedEventArgs e) => RefreshLocal();

    private void LocalOpenNuget_Click(object sender, RoutedEventArgs e) => OpenFolder(_settings.NuGetOutputDir);
    private void LocalOpenNpm_Click(object sender, RoutedEventArgs e)   => OpenFolder(_settings.NpmOutputDir);
    private void LocalOpenMcp_Click(object sender, RoutedEventArgs e)   => OpenFolder(_settings.McpOutputDir);
    private void LocalOpenLlm_Click(object sender, RoutedEventArgs e)   => OpenFolder(_settings.HfOutputDir);
    private void LocalOpenPyPi_Click(object sender, RoutedEventArgs e)  => OpenFolder(_settings.PyPiOutputDir);

    private void OpenFolder(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir))
        {
            MessageDialog.Info(this, "Папка недоступна",
                "Папка не задана или не найдена. Укажите её в «Администрировании».");
            return;
        }
        try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); } catch { }
    }

    private void LocalReveal_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LocalPackage pkg) return;

        // MCP-сервер, LLM-модель и Python-версия — это папки: открываем их. Пакеты — выделяем файл.
        if (pkg.Kind is "LLM" or "MCP" or "PyPI")
        {
            if (!Directory.Exists(pkg.FullPath)) { RefreshLocal(); return; }
            try { Process.Start(new ProcessStartInfo(pkg.FullPath) { UseShellExecute = true }); } catch { }
            return;
        }
        if (!File.Exists(pkg.FullPath)) { RefreshLocal(); return; }
        try { Process.Start("explorer.exe", $"/select,\"{pkg.FullPath}\""); } catch { }
    }

    private void LocalReadme_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LocalPackage pkg) return;

        string? readme;
        if (pkg.Kind == "LLM")
        {
            var path = System.IO.Path.Combine(pkg.FullPath, "README.md");
            readme = File.Exists(path) ? File.ReadAllText(path) : null;
        }
        else if (pkg.Kind == "MCP")
        {
            // README из основного tarball'а сервера; если его нет — краткое описание.
            readme = !string.IsNullOrEmpty(pkg.MainFile) && File.Exists(pkg.MainFile)
                ? LocalScanService.ReadNpmReadme(pkg.MainFile) : null;
            if (string.IsNullOrWhiteSpace(readme) && !string.IsNullOrWhiteSpace(pkg.Summary))
                readme = pkg.Summary;
        }
        else
        {
            if (!File.Exists(pkg.FullPath)) { RefreshLocal(); return; }
            readme = LocalScanService.ReadNpmReadme(pkg.FullPath);
        }

        if (string.IsNullOrWhiteSpace(readme))
        {
            MessageDialog.Info(this, pkg.Name, "Для этого элемента нет README.");
            return;
        }
        ReadmeWindow.Show(this, $"{pkg.Name} {pkg.Version}".Trim(), readme);
    }

    private void LocalDelete_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not LocalPackage pkg) return;

        bool isFolder = pkg.Kind is "LLM" or "MCP" or "PyPI";
        var what = pkg.Kind switch
        {
            "LLM"  => $"модель «{pkg.Name}» со всеми файлами",
            "MCP"  => $"MCP-сервер «{pkg.Name}» со всеми файлами",
            "PyPI" => $"пакет «{pkg.Name} {pkg.Version}» со всеми файлами",
            _      => $"файл «{pkg.FileName}»"
        };
        if (!MessageDialog.Confirm(this, isFolder ? "Удаление" : "Удаление файла",
                $"Удалить {what}? Действие необратимо.", "Удалить", "Отмена"))
            return;
        try
        {
            if (isFolder)
            {
                if (Directory.Exists(pkg.FullPath)) Directory.Delete(pkg.FullPath, true);
            }
            else if (File.Exists(pkg.FullPath)) File.Delete(pkg.FullPath);

            _localAll.Remove(pkg);
            _localItems.Remove(pkg);
            UpdateLocalCount();
        }
        catch (Exception ex)
        {
            MessageDialog.Error(this, "Ошибка", $"Не удалось удалить:\n{ex.Message}");
        }
    }

    // ── Утилиты ───────────────────────────────────────────────────────────

    private static List<PackageItem> SortItems(
        IEnumerable<PackageItem> items, string key, bool asc)
    {
        return (key switch
        {
            "Version"   => asc ? items.OrderBy(x => x.Version)   : items.OrderByDescending(x => x.Version),
            "Downloads" => asc ? items.OrderBy(x => x.Downloads) : items.OrderByDescending(x => x.Downloads),
            "Authors"   => asc ? items.OrderBy(x => x.Authors)   : items.OrderByDescending(x => x.Authors),
            _           => asc ? items.OrderBy(x => x.Id)        : items.OrderByDescending(x => x.Id),
        }).ToList();
    }

    private static void SetSortIcon(TextBlock icon, string column, string activeKey, bool asc)
    {
        if (column == activeKey)
        {
            icon.Text = asc ? " ▲" : " ▼";
            icon.Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x7C, 0x3A, 0xED));
        }
        else
        {
            icon.Text = "";
        }
    }

    private static void SyncSelectAllState(CheckBox chk, ObservableCollection<PackageItem> items)
    {
        if (items.Count == 0) { chk.IsChecked = false; return; }
        int n = items.Count(x => x.IsSelected);
        chk.IsChecked = n == 0 ? false : n == items.Count ? true : null;
    }
}
