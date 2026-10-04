using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PackageDownloader.Services;

/// <summary>
/// Привязывает дочерние процессы серверов (cmd → node) к Job-объекту Windows с флагом
/// JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE. Когда наш процесс завершается — штатно или
/// аварийно (Диспетчер задач, краш, обрыв) — операционная система автоматически
/// завершает все процессы job'а. Это исключает «осиротевшие» node.exe, которые иначе
/// продолжали бы держать порты 4873/5000 и приводили к ошибке EADDRINUSE при следующем запуске.
/// </summary>
internal static class ChildProcessGuard
{
    // Дескриптор намеренно НЕ закрывается: он живёт до конца процесса. При выходе приложения
    // ОС закрывает его и по флагу KILL_ON_JOB_CLOSE завершает все процессы внутри job'а.
    private static readonly IntPtr _job = CreateGuardJob();

    /// <summary>Помещает процесс (и его будущих потомков, напр. node под cmd) в защитный job.</summary>
    public static void Register(Process p)
    {
        if (_job == IntPtr.Zero) return;
        try { AssignProcessToJobObject(_job, p.Handle); } catch { /* не критично — обычный Kill всё равно сработает */ }
    }

    private static IntPtr CreateGuardJob()
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            }
        };

        int length = Marshal.SizeOf(info);
        IntPtr ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)length))
                return IntPtr.Zero;
        }
        finally { Marshal.FreeHGlobal(ptr); }

        return job;
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    private const int  JobObjectExtendedLimitInformation  = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr a, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoType, IntPtr info, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long    PerProcessUserTimeLimit;
        public long    PerJobUserTimeLimit;
        public uint    LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint    ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint    PriorityClass;
        public uint    SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
