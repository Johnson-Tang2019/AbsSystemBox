// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using SysBox.Transport;
namespace SysBox.ProtocolTests;

public sealed class RemoteObservationFeaturesTests
{
    [Fact]
    public async Task PropertyReadsNeverWaitForAnUnresponsiveHost()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<Observation>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var observation = new RemoteObservation(token => { started.TrySetResult(); return response.Task.WaitAsync(token); });
        var clock = Stopwatch.StartNew();
        Assert.Contains("连接", observation.Status);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(observation.Samples);
        Assert.False(response.Task.IsCompleted);
        response.SetResult(new("fixture observation", "fixture notification", []));
        for (var i = 0; i < 100 && observation.Status != "fixture observation"; i++)
            await Task.Delay(10);
        Assert.Equal("fixture observation", observation.Status);
        Assert.Equal("fixture notification", observation.Notification);
    }
    [Fact]
    public async Task DisposalCancelsAnInflightReadAndDoesNotScheduleAnother()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var observation = new RemoteObservation(async token =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return new("unexpected", "", []);
            }
            finally { stopped.TrySetResult(); }
        });
        _ = observation.Status;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        observation.Dispose();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains("停止", observation.Status);
        Assert.Empty(observation.Samples);
        Assert.Equal(1, calls);
    }
}
