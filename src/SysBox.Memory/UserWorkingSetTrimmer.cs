// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using SysBox.Abstractions;
using SysBox.Native;
namespace SysBox.Memory;

public sealed class UserWorkingSetTrimmer(IProcessMemoryMonitor processes) : IWorkingSetTrimmer
{
    public IReadOnlyList<CleanupAttempt> TrimAllowed(CleanupPolicy policy, CancellationToken token)
    {
        if (!policy.Enabled || !Enum.IsDefined(policy.ProcessScope) || policy.MaximumTargets is < 1 or > 2)
            return [];
        var snapshot = processes.ReadAsync(token).GetAwaiter().GetResult();
        if (!snapshot.IsSuccess)
            throw new SysBoxOperationException(snapshot.Error!);
        var result = new List<CleanupAttempt>();
        var budget = Stopwatch.StartNew();
        foreach (var entry in snapshot.Value.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (budget.Elapsed > TimeSpan.FromSeconds(3) || result.Count >= policy.MaximumTargets)
                break;
            if (entry.ImagePath is null || entry.StartTimeUtc is null || entry.WorkingSetBytes < (ulong)policy.MinimumWorkingSetMb * 1048576)
                continue;
            var name = Path.GetFileName(entry.ImagePath);
            if (!policy.AllowsProcess(name) || WorkingSetAccess.IsProtectedName(name))
                continue;
            try
            {
                result.Add(WorkingSetAccess.Trim(entry.ProcessId, entry.StartTimeUtc.Value, name, (ulong)policy.MinimumWorkingSetMb * 1048576));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { result.Add(new(entry.ProcessId, name, DateTimeOffset.UtcNow, null, null, false, "进程已退出或身份不可验证。")); }
        }
        return result;
    }
}
