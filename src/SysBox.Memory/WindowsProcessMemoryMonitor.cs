// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using SysBox.Abstractions;
using SysBox.Native;

namespace SysBox.Memory;

public sealed class WindowsProcessMemoryMonitor(IWindowsMemoryApi api) : IProcessMemoryMonitor
{
    public const int DisplayLimit = ProcessMemorySnapshot.MaximumEntries;
    public static readonly TimeSpan CollectionBudget = TimeSpan.FromSeconds(3);

    public Task<Result<ProcessMemorySnapshot>> ReadAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Read(cancellationToken), cancellationToken);

    private Result<ProcessMemorySnapshot> Read(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!api.IsSupported)
            return Result<ProcessMemorySnapshot>.Failure(new(ErrorCodes.Unavailable, "Windows process APIs are unavailable."));
        try
        {
            var batch = api.EnumerateProcessIds(ct);
            var entries = new List<ProcessMemoryEntry>();
            var denied = 0;
            var unavailable = 0;
            var other = 0;
            var skipped = 0;
            var elapsed = Stopwatch.StartNew();
            foreach (var pid in batch.Ids)
            {
                ct.ThrowIfCancellationRequested();
                if (elapsed.Elapsed > CollectionBudget)
                {
                    skipped = batch.Ids.Count - entries.Count - denied - unavailable - other;
                    break;
                }
                try
                {
                    var sample = api.ReadProcess(pid);
                    var name = sample.ImagePath is { Length: > 0 } path ? Path.GetFileNameWithoutExtension(path) : $"PID {pid}";
                    entries.Add(new(pid, name, sample.ImagePath, sample.StartTimeUtc,
                        sample.WorkingSetBytes, sample.PrivateBytes));
                }
                catch (Win32Exception error)
                {
                    if (error.NativeErrorCode == 5) denied++;
                    else if (error.NativeErrorCode is 6 or 87 or 1168 or 299) unavailable++;
                    else other++;
                }
            }
            ct.ThrowIfCancellationRequested();
            return Result<ProcessMemorySnapshot>.Success(new(DateTimeOffset.UtcNow,
                entries.OrderByDescending(x => x.WorkingSetBytes).ThenBy(x => x.ProcessId).Take(DisplayLimit).ToArray(),
                batch.Ids.Count, entries.Count, denied, unavailable, other, skipped, batch.IsComplete,
                batch.IsComplete && denied + unavailable + other + skipped == 0 ? DataCompleteness.Complete : DataCompleteness.Partial));
        }
        catch (Win32Exception)
        {
            return Result<ProcessMemorySnapshot>.Failure(new(ErrorCodes.Unavailable, "Process enumeration failed.", retryable: true));
        }
        catch (InvalidDataException)
        {
            return Result<ProcessMemorySnapshot>.Failure(new(ErrorCodes.Unavailable, "Invalid process enumeration buffer."));
        }
    }
}
