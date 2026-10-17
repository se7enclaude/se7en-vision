using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PhoneRemote.Tools;

/// <summary>
/// Helper programs shipped inside the exe (cloudflared.exe, ffmpeg.exe). During development they are
/// taken from the repository's tools folder; once published they are unpacked into the data folder.
/// </summary>
public static class BundledTools
{
    private static readonly object Gate = new();

    /// <param name="name">Resource and file name, e.g. "ffmpeg.exe".</param>
    /// <param name="devPaths">Paths relative to the repository root, e.g. "tools/ffmpeg/ffmpeg.exe".</param>
    public static string? Find(string name, params string[] devPaths)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; dir != null && i < 7; i++, dir = dir.Parent)
        {
            foreach (var rel in devPaths.Prepend(name))
            {
                var candidate = Path.Combine(dir.FullName, rel);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return Extract(name);
    }

    private static string? Extract(string name)
    {
        lock (Gate)
        {
            using var res = typeof(BundledTools).Assembly.GetManifestResourceStream(name);
            if (res == null) return null;
            var target = Path.Combine(AppInfo.DataDir, name);
            if (File.Exists(target) && new FileInfo(target).Length == res.Length) return target;

            Directory.CreateDirectory(AppInfo.DataDir);
            var temp = target + ".tmp";
            using (var file = File.Create(temp)) res.CopyTo(file);
            File.Move(temp, target, overwrite: true);
            return target;
        }
    }
}

/// <summary>A job object with KILL_ON_JOB_CLOSE: child processes end whenever the app exits, even after a crash.</summary>
public static class ChildProcessJob
{
    private static readonly Lazy<IntPtr> Job = new(() =>
    {
        var job = CreateJobObject(IntPtr.Zero, null);
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        int size = Marshal.SizeOf(info);
        IntPtr ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(job, JobObjectExtendedLimitInformation, ptr, (uint)size);
        }
        finally { Marshal.FreeHGlobal(ptr); }
        return job;
    });

    public static void Attach(Process p)
    {
        try { AssignProcessToJobObject(Job.Value, p.Handle); } catch { }
    }

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS { public ulong a, b, c, d, e, f; }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
