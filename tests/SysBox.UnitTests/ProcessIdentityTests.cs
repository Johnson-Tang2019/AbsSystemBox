// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Memory;
using SysBox.Native;

namespace SysBox.UnitTests;

public sealed class ProcessIdentityTests
{
    [Fact]
    public async Task ReusedPidCarriesNewStartTimeAndNeverReusesPriorNameOrCounters()
    {
        var started = DateTimeOffset.UtcNow;
        var calls = 0;
        var api = new FakeMemoryApi
        {
            Ids = [42],
            Process = pid => ++calls == 1
                ? new NativeProcessSample(pid, "old.exe", started, 100, 200)
                : new NativeProcessSample(pid, "new.exe", started.AddMinutes(1), 300, 400)
        };
        var monitor = new WindowsProcessMemoryMonitor(api);
        var first = (await monitor.ReadAsync(CancellationToken.None)).Value.Entries.Single();
        var second = (await monitor.ReadAsync(CancellationToken.None)).Value.Entries.Single();
        Assert.Equal(first.ProcessId, second.ProcessId);
        Assert.NotEqual(first.StartTimeUtc, second.StartTimeUtc);
        Assert.NotEqual(first.Name, second.Name);
        Assert.Equal(300UL, second.WorkingSetBytes);
    }
}
