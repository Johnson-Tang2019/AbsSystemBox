// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SysBox.CoreHost;

internal sealed class ScriptProcessJob : IDisposable
{
    private readonly SafeFileHandle handle;

    public ScriptProcessJob()
    {
        if (IntPtr.Size != 8 || Marshal.SizeOf<JobLimits>() != 144)
            throw new PlatformNotSupportedException("脚本托管要求 Windows x64。");
        handle = CreateJobObjectW(IntPtr.Zero, null);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 拒绝创建脚本进程组。");
        var limits = new JobLimits
        {
            Basic = new BasicLimits { Flags = 0x00002000 } // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        };
        if (!SetInformationJobObject(handle, 9, in limits, 144))
        {
            var code = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(code, $"无法配置脚本进程组（Windows 错误 {code}）。");
        }
    }

    internal SafeFileHandle Handle => handle;

    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimits
    {
        public long PerProcessTime;
        public long PerJobTime;
        public uint Flags;
        public nuint MinimumWorkingSet;
        public nuint MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperations, WriteOperations, OtherOperations;
        public ulong ReadBytes, WriteBytes, OtherBytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobLimits
    {
        public BasicLimits Basic;
        public IoCounters Io;
        public nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass, in JobLimits limits, uint length);

}
