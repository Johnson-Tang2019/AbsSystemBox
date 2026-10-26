// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SysBox.Native;

public sealed record ProcessIdBatch(IReadOnlyList<uint> Ids, bool IsComplete);
public sealed record NativeProcessSample(uint ProcessId, string? ImagePath, DateTimeOffset? StartTimeUtc,
    ulong WorkingSetBytes, ulong PrivateBytes);

// This seam permits deterministic failure/race tests without querying or changing other processes.
public interface IWindowsMemoryApi
{
    bool IsSupported { get; }
    MemoryStatusEx ReadPhysical();
    PerformanceInformation ReadPerformance();
    ProcessIdBatch EnumerateProcessIds(CancellationToken cancellationToken);
    NativeProcessSample ReadProcess(uint processId);
}

public sealed class WindowsMemoryApi : IWindowsMemoryApi
{
    public bool IsSupported => OperatingSystem.IsWindows();

    public MemoryStatusEx ReadPhysical()
    {
        EnsureWindows();
        var data = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!MemoryInterop.GlobalMemoryStatusEx(ref data)) throw LastError();
        return data;
    }

    public PerformanceInformation ReadPerformance()
    {
        EnsureWindows();
        var data = new PerformanceInformation { Size = (uint)Marshal.SizeOf<PerformanceInformation>() };
        if (!MemoryInterop.GetPerformanceInfo(ref data, data.Size)) throw LastError();
        return data;
    }

    public ProcessIdBatch EnumerateProcessIds(CancellationToken cancellationToken)
    {
        EnsureWindows();
        const int maximum = 65536;
        for (var capacity = 1024; ; capacity = Math.Min(maximum, capacity * 2))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = new uint[capacity];
            var bytes = checked((uint)(ids.Length * sizeof(uint)));
            if (!MemoryInterop.EnumProcesses(ids, bytes, out var used)) throw LastError();
            if (used > bytes || used % sizeof(uint) != 0) throw new InvalidDataException("Invalid PID buffer length.");
            if (used < bytes || capacity == maximum)
                return new(ids.AsSpan(0, (int)(used / sizeof(uint))).ToArray(), used < bytes);
        }
    }

    public NativeProcessSample ReadProcess(uint processId)
    {
        EnsureWindows();
        // No VM_READ, terminate, working-set adjustment or debug privilege is requested.
        using var handle = MemoryInterop.OpenProcess(MemoryInterop.QueryLimitedInformation, false, processId);
        if (handle.IsInvalid) throw LastError();
        var data = new ProcessMemoryCountersEx { Size = (uint)Marshal.SizeOf<ProcessMemoryCountersEx>() };
        if (!MemoryInterop.GetProcessMemoryInfo(handle, ref data, data.Size)) throw LastError();

        DateTimeOffset? started = null;
        if (MemoryInterop.GetProcessTimes(handle, out var creation, out var exit, out _, out _))
        {
            if (exit != 0) throw new Win32Exception(1168);
            try { started = new DateTimeOffset(DateTime.FromFileTimeUtc(creation)); }
            catch (ArgumentOutOfRangeException) { /* Unknown identity is preserved as null. */ }
        }
        // Image name is optional; failure must not discard a successful memory reading.
        var buffer = new char[512];
        var length = (uint)buffer.Length;
        string? path = null;
        var named = MemoryInterop.QueryFullProcessImageName(handle, 0, buffer, ref length);
        if (!named && Marshal.GetLastWin32Error() == 122)
        {
            buffer = new char[32768];
            length = (uint)buffer.Length;
            named = MemoryInterop.QueryFullProcessImageName(handle, 0, buffer, ref length);
        }
        if (named && length <= buffer.Length)
            path = new string(buffer, 0, (int)length);
        return new(processId, path, started, (ulong)data.WorkingSetSize, (ulong)data.PrivateUsage);
    }

    private static Win32Exception LastError() => new(Marshal.GetLastWin32Error());
    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows is required.");
    }
}
