// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using SysBox.Abstractions;
namespace SysBox.Transport;

public sealed class RemoteObservation : IRuleObservation, IDisposable
{
    private readonly Func<CancellationToken, Task<Observation>> reader;
    private readonly object gate = new();
    private readonly CancellationTokenSource cancellation = new();
    private Observation cached = new("正在连接后台规则观察器。", "尚无后台通知。", []);
    private long updated; private bool reading, disposed;
    public RemoteObservation(RpcClient client) : this(token => client.CallAsync<Observation>("rules.observation", new { }, token)) { }
    internal RemoteObservation(Func<CancellationToken, Task<Observation>> reader) => this.reader = reader;
    private Observation Read()
    {
        lock (gate)
        {
            if (!disposed && !reading && (updated == 0 || Stopwatch.GetElapsedTime(updated) >= TimeSpan.FromSeconds(1)))
            {
                reading = true;
                updated = Stopwatch.GetTimestamp();
                _ = Task.Run(RefreshAsync);
            }
            return cached;
        }
    }
    private async Task RefreshAsync()
    {
        try
        {
            var result = await reader(cancellation.Token).ConfigureAwait(false);
            lock (gate)
                if (!disposed)
                    cached = result;
        }
        catch (OperationCanceledException) when (disposed) { }
        catch
        {
            lock (gate)
                if (!disposed)
                    cached = new("后台观察不可用，将在下次读取时重连。", "后台通知未更新。", []);
        }
        finally { lock (gate) reading = false; }
    }
    public string Status => Read().Status;
    public string Notification => Read().Notification;
    public IReadOnlyList<RuleSample> Samples => Array.AsReadOnly(Read().Samples);
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
                return;
            disposed = true;
            cached = new("后台观察已停止。", "", []);
        }
        cancellation.Cancel();
        cancellation.Dispose();
    }
}
