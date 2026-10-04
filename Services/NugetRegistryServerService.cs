using System.Diagnostics;
using System.IO;
using System.Net;

namespace PackageDownloader.Services;

/// <summary>
/// Запускает локальный NuGet-реестр (Node.js-скрипт nuget-server.mjs) для раздачи
/// .nupkg-пакетов в корпоративной сети с NuGet V3 API.
/// Пользователи добавляют <see cref="NetworkUrl"/>/v3/index.json в nuget.config,
/// после чего dotnet restore / dotnet build находят пакеты автоматически.
/// </summary>
public class NugetRegistryServerService
{
    private Process?           _process;
    private int                _port;
    private IProgress<string>? _log;

    public bool IsRunning => _process is { HasExited: false };
    public int  Port      => _port;

    /// <summary>Срабатывает, когда процесс сервера завершился (в т.ч. сам по себе).
    /// Позволяет обновить UI/значок трея в реальном времени, не дожидаясь открытия меню.</summary>
    public event Action? Exited;

    public string? LocalUrl   => IsRunning ? $"http://localhost:{_port}"          : null;
    public string? NetworkUrl => IsRunning ? $"http://{Dns.GetHostName()}:{_port}" : null;

    /// <summary>URL, который нужно прописать в nuget.config клиента.</summary>
    public string? IndexUrl => IsRunning ? $"{NetworkUrl}/v3/index.json" : null;

    public bool Start(string packagesDir, int port, IProgress<string>? log, out string? error)
    {
        error = null;
        _log  = log;

        if (IsRunning) return true;

        if (string.IsNullOrWhiteSpace(packagesDir) || !Directory.Exists(packagesDir))
        {
            error = "Папка с NuGet-пакетами не найдена. Укажите её в настройках путей.";
            return false;
        }

        var scriptPath = FindScript();
        if (scriptPath is null)
        {
            error = "Файл nuget-server.mjs не найден рядом с приложением.";
            return false;
        }

        _port = port is > 0 and < 65536 ? port : 5000;

        if (IsPortInUse(_port))
        {
            error = $"Порт {_port} уже занят. Возможно, остался процесс node.exe от прошлого запуска — " +
                    "завершите его в Диспетчере задач или укажите другой порт.";
            return false;
        }

        var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName               = "cmd.exe",
                Arguments              = $"/d /c node \"{scriptPath}\"",
                WorkingDirectory       = Path.GetDirectoryName(scriptPath)!,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding  = System.Text.Encoding.UTF8,
                EnvironmentVariables   =
                {
                    ["NUGET_SERVER_PORT"]  = _port.ToString(),
                    ["NUGET_PACKAGES_DIR"] = packagesDir,
                },
            },
            EnableRaisingEvents = true,
        };

        p.OutputDataReceived += (_, e) => { if (e.Data is { } s) _log?.Report(s); };
        p.ErrorDataReceived  += (_, e) =>
        {
            if (e.Data is not { } s) return;
            _log?.Report(s);
            if (s.Contains("EADDRINUSE"))
                _log?.Report($"⚠ Порт {_port} уже занят другим процессом — реестр не запущен.");
        };
        p.Exited += (_, _) =>
        {
            int code = int.MinValue;
            try { code = p.ExitCode; } catch { /* процесс уже освобождён */ }
            _log?.Report($"─────────── NuGet-реестр остановлен (код выхода {code}) ───────────");
            _process = null;
            Exited?.Invoke();
        };

        try
        {
            p.Start();
            _process = p;
            ChildProcessGuard.Register(p);   // привязка к job: node умрёт вместе с приложением
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            return true;
        }
        catch (Exception ex)
        {
            _process = null;
            error = $"Не удалось запустить Node.js: {ex.Message}. Убедитесь, что Node.js установлен.";
            return false;
        }
    }

    public void Stop()
    {
        if (_process is null) return;
        try { _process.Kill(entireProcessTree: true); } catch { /* ignore */ }
        _process = null;
    }

    private static bool IsPortInUse(int port)
    {
        try
        {
            return System.Net.NetworkInformation.IPGlobalProperties
                .GetIPGlobalProperties().GetActiveTcpListeners()
                .Any(ep => ep.Port == port);
        }
        catch { return false; }
    }

    private static string? FindScript()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Resources", "nuget-server.mjs"),
            Path.Combine(AppContext.BaseDirectory, "nuget-server.mjs"),
        };
        return candidates.FirstOrDefault(File.Exists);
    }
}
