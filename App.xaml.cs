using System.Threading;
using System.Windows;

namespace PackageDownloader;

public partial class App : Application
{
    private TrayService? _tray;
    private MainWindow?  _main;

    // ── Защита от повторного запуска (один экземпляр на сессию пользователя) ──
    // Имена привязаны к UpgradeCode, чтобы быть уникальными для приложения.
    private const string MutexName     = @"Local\PackageDownloader.SingleInstance.D66E3709-2619-4950-BEC3-8494115A8AF8";
    private const string ShowEventName = @"Local\PackageDownloader.ShowWindow.D66E3709-2619-4950-BEC3-8494115A8AF8";
    private Mutex?           _instanceMutex;
    private EventWaitHandle? _showEvent;

    private void App_Startup(object sender, StartupEventArgs e)
    {
        // Если экземпляр уже запущен — просим его показать окно и тихо выходим.
        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var ev))
                    ev.Set();   // первый экземпляр развернёт своё окно
            }
            catch { /* первый экземпляр ещё не успел создать событие — не критично */ }

            _instanceMutex = null;   // владельцем мьютекса остаётся первый экземпляр
            Shutdown();
            return;
        }

        // Первый экземпляр: слушаем сигнал от будущих запусков, чтобы показать окно.
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var listener = new Thread(ShowEventLoop) { IsBackground = true, Name = "SingleInstanceListener" };
        listener.Start();

        // Окно создаём, но НЕ показываем — приложение живёт в системном трее.
        _main = new MainWindow();

        _tray = new TrayService(
            isNpmRunning:   () => _main!.IsServerRunning,
            toggleNpm:      () => _main!.TrayToggleServer(),
            isNugetRunning: () => _main!.IsNugetRunning,
            toggleNuget:    () => _main!.TrayToggleNuget(),
            showApp:        ShowMainWindow,
            exit:           ExitApp);

        _main.SetTray(_tray);

        // Гарантируем удаление значка из трея на ВСЕХ перехватываемых путях завершения,
        // чтобы не оставалось «призрачных» значков (вторая, третья копия и т.п.).
        DispatcherUnhandledException             += (_, _) => CleanupTray();
        SessionEnding                            += (_, _) => CleanupTray();
        AppDomain.CurrentDomain.ProcessExit      += (_, _) => CleanupTray();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => CleanupTray();
    }

    private readonly object _cleanupLock = new();
    private bool _cleaned;

    /// <summary>Идемпотентно убирает значок трея (может вызываться из нескольких обработчиков завершения).</summary>
    private void CleanupTray()
    {
        lock (_cleanupLock)
        {
            if (_cleaned) return;
            _cleaned = true;
        }
        try { _tray?.Dispose(); } catch { /* при аварийном завершении игнорируем */ }
        _tray = null;
    }

    /// <summary>Фоновое ожидание сигнала «показать окно» от повторно запущенного экземпляра.</summary>
    private void ShowEventLoop()
    {
        var ev = _showEvent;
        if (ev is null) return;
        try
        {
            while (ev.WaitOne())
                Dispatcher.BeginInvoke(ShowMainWindow);
        }
        catch (ObjectDisposedException) { /* выход из приложения */ }
        catch (AbandonedMutexException) { /* не относится к событию, игнорируем */ }
    }

    private void ShowMainWindow()
    {
        if (_main is null) return;
        _main.Show();
        if (_main.WindowState == WindowState.Minimized) _main.WindowState = WindowState.Normal;
        _main.Activate();
        _main.Topmost = true;
        _main.Topmost = false;
        _main.Focus();
    }

    private void ExitApp()
    {
        _main?.PrepareExit();
        CleanupTray();

        _showEvent?.Dispose();
        _showEvent = null;
        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch { /* не владеем — игнорируем */ }
            _instanceMutex.Dispose();
            _instanceMutex = null;
        }

        Shutdown();
    }
}
