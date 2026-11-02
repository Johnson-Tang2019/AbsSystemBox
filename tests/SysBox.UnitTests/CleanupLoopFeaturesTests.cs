// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using SysBox.Abstractions;
using SysBox.Core;
namespace SysBox.UnitTests;

public sealed class CleanupLoopFeaturesTests
{
    private sealed class ControlledMemory : IMemoryMonitor
    {
        public int Reads;
        public Task<Result<MemorySnapshot>> ReadAsync(CancellationToken token)
        {
            Interlocked.Increment(ref Reads);
            return Task.FromResult(Result<MemorySnapshot>.Success(new(DateTimeOffset.UtcNow, 100, 10, null, null, DataCompleteness.Partial)));
        }
    }
    private sealed class RecordingExecutor(string path) : IWorkingSetTrimmer
    {
        public readonly TaskCompletionSource Reached = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool PersistedBeforeExecution; public int Calls;
        public IReadOnlyList<CleanupAttempt> TrimAllowed(CleanupPolicy policy, CancellationToken token)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            PersistedBeforeExecution = document.RootElement.GetProperty("Runs").GetArrayLength() == 1;
            Interlocked.Increment(ref Calls);
            Reached.TrySetResult();
            return [];
        }
    }
    [Fact(Timeout = 45000)]
    public async Task LiveSupervisorRequiresDesktopLeasePersistsBeforeExecutionAndStopsReadingWhenDisabled()
    {
        var root = Path.Combine(Path.GetTempPath(), "SysBox-loop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var cancellation = new CancellationTokenSource();
        var memory = new ControlledMemory();
        var executor = new RecordingExecutor(Path.Combine(root, "cleanup.json"));
        var lease = 0;
        var supervisor = new CleanupSupervisor(root, memory, executor, () => Volatile.Read(ref lease) == 1);
        supervisor.Configure(new(Enabled: true, ThresholdPercent: 50, HoldSeconds: 10, CooldownSeconds: 60, AllowedProcesses: ["fixture.exe"]));
        var loop = supervisor.RunAsync(cancellation.Token);
        try
        {
            await Task.Delay(5500);
            Assert.Equal(0, Volatile.Read(ref memory.Reads));
            Assert.Equal(0, executor.Calls);
            Volatile.Write(ref lease, 1);
            await executor.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(executor.PersistedBeforeExecution);
            supervisor.Configure(supervisor.Read().Policy with
            {
                Enabled = false
            });
            var reads = Volatile.Read(ref memory.Reads);
            await Task.Delay(5500);
            Assert.Equal(reads, Volatile.Read(ref memory.Reads));
            Assert.Equal(1, executor.Calls);
            Assert.False(supervisor.Read().Policy.Enabled);
        }
        finally { cancellation.Cancel(); await loop; Directory.Delete(root, true); }
    }
}
