// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using SysBox.Abstractions;
using SysBox.Native;

namespace SysBox.Memory;

public sealed class WindowsMemoryMonitor(IWindowsMemoryApi api) : IMemoryMonitor
{
    public Task<Result<MemorySnapshot>> ReadAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Read(cancellationToken), cancellationToken);

    private Result<MemorySnapshot> Read(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!api.IsSupported)
            return Result<MemorySnapshot>.Failure(new(ErrorCodes.Unavailable, "Windows memory APIs are unavailable."));
        try
        {
            var physical = api.ReadPhysical();
            if (physical.TotalPhysical == 0 || physical.AvailablePhysical > physical.TotalPhysical)
                return Result<MemorySnapshot>.Failure(new(ErrorCodes.Unavailable, "Invalid physical memory counters."));
            ct.ThrowIfCancellationRequested();
            ulong? total = null;
            ulong? limit = null;
            try
            {
                var performance = api.ReadPerformance();
                if (performance.PageSize > 0 && performance.CommitLimit > 0 && performance.CommitTotal <= performance.CommitLimit)
                {
                    var totalBytes = checked((ulong)performance.CommitTotal * (ulong)performance.PageSize);
                    var limitBytes = checked((ulong)performance.CommitLimit * (ulong)performance.PageSize);
                    total = totalBytes;
                    limit = limitBytes;
                }
            }
            catch (Win32Exception) { /* Physical data remains useful when commit accounting is unavailable. */ }
            catch (OverflowException) { /* Do not publish wrapped page counters. */ }
            ct.ThrowIfCancellationRequested();
            return Result<MemorySnapshot>.Success(new(DateTimeOffset.UtcNow,
                physical.TotalPhysical, physical.AvailablePhysical, total, limit,
                total.HasValue ? DataCompleteness.Complete : DataCompleteness.Partial));
        }
        catch (Win32Exception error)
        {
            return Result<MemorySnapshot>.Failure(new(
                error.NativeErrorCode == 5 ? ErrorCodes.AccessDenied : ErrorCodes.Unavailable,
                "System memory counters could not be read.", retryable: true));
        }
    }
}
