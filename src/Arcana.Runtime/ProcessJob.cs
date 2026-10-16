using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Arcana.Runtime;

/// <summary>
/// A Windows job object that kills everything assigned to it when this host process ends, however it ends
/// (Ctrl+C, crash, Task Manager). One job per host; the engine and its workers join it through the engine process.
/// </summary>
public static class ProcessJob
{
    private static readonly IntPtr Job = Create();

    /// <summary>Puts the process (and so its future children) in the job. A no-op off Windows or if the job failed.</summary>
    public static void Add(Process process)
    {
        if (Job == IntPtr.Zero) return;
        try { AssignProcessToJobObject(Job, process.Handle); }
        catch (InvalidOperationException) { } // already exited
    }

    private static IntPtr Create()
    {
        if (!OperatingSystem.IsWindows()) return IntPtr.Zero;
        var job = CreateJobObject(IntPtr.Zero, null);
        if (job == IntPtr.Zero) return IntPtr.Zero;
        var info = new ExtendedLimits { Basic = { LimitFlags = KillOnJobClose } };
        var size = Marshal.SizeOf<ExtendedLimits>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            // The job handle is deliberately never closed: the OS closing it at host exit is what kills the children.
            return SetInformationJobObject(job, ExtendedLimitInformation, buffer, (uint)size) ? job : IntPtr.Zero;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private const uint KillOnJobClose = 0x2000;
    private const int ExtendedLimitInformation = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters { public ulong Read, Write, Other, ReadBytes, WriteBytes, OtherBytes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
}
