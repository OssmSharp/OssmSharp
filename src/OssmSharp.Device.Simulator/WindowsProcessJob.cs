using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace OssmSharp.Device.Simulator;

/// <summary>
/// Associates the native simulator process with a Windows job that terminates it when ownership ends.
/// </summary>
internal sealed partial class WindowsProcessJob : IDisposable
{
    private const uint JobObjectLimitKillOnJobClose = 0x00002000;
    private readonly SafeFileHandle _handle;

    private WindowsProcessJob(SafeFileHandle handle)
    {
        _handle = handle;
    }

    private enum JobObjectInformationClass
    {
        ExtendedLimitInformation = 9,
    }

    /// <summary>
    /// Closes the job handle, terminating the owned simulator process on Windows.
    /// </summary>
    public void Dispose() => _handle.Dispose();

    /// <summary>
    /// Creates a kill-on-close job and assigns the supplied native process to it on Windows.
    /// </summary>
    /// <param name="process">The native simulator process to own.</param>
    /// <returns>The owning job, or <see langword="null"/> outside Windows.</returns>
    internal static WindowsProcessJob? CreateFor(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw new Win32Exception(Marshal.GetLastPInvokeError(), "The simulator process job could not be created.");
        }

        var job = new WindowsProcessJob(handle);
        try
        {
            var information = new JobObjectExtendedLimitInformation
            {
                BasicLimitInformation = new JobObjectBasicLimitInformation
                {
                    LimitFlags = JobObjectLimitKillOnJobClose,
                },
            };
            if (!SetInformationJobObject(
                handle,
                JobObjectInformationClass.ExtendedLimitInformation,
                in information,
                (uint)Marshal.SizeOf<JobObjectExtendedLimitInformation>()))
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "The simulator process job could not be configured.");
            }

            if (!AssignProcessToJobObject(handle, process.SafeHandle))
            {
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "The simulator process could not be assigned to its job.");
            }

            return job;
        }
        catch
        {
            job.Dispose();
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateJobObject(IntPtr jobAttributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(
        SafeFileHandle job,
        JobObjectInformationClass informationClass,
        in JobObjectExtendedLimitInformation information,
        uint informationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        internal long PerProcessUserTimeLimit;
        internal long PerJobUserTimeLimit;
        internal uint LimitFlags;
        internal UIntPtr MinimumWorkingSetSize;
        internal UIntPtr MaximumWorkingSetSize;
        internal uint ActiveProcessLimit;
        internal UIntPtr Affinity;
        internal uint PriorityClass;
        internal uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        internal ulong ReadOperationCount;
        internal ulong WriteOperationCount;
        internal ulong OtherOperationCount;
        internal ulong ReadTransferCount;
        internal ulong WriteTransferCount;
        internal ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        internal JobObjectBasicLimitInformation BasicLimitInformation;
        internal IoCounters IoInfo;
        internal UIntPtr ProcessMemoryLimit;
        internal UIntPtr JobMemoryLimit;
        internal UIntPtr PeakProcessMemoryUsed;
        internal UIntPtr PeakJobMemoryUsed;
    }
}
