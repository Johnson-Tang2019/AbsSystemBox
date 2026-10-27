// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;

namespace SysBox.FileIndex;

public sealed class IndexControl : IIndexControl
{
    private readonly object gate = new();
    private TaskCompletionSource? paused;
    public bool IsPaused { get { lock (gate) return paused is not null; } }
    public void Pause() { lock (gate) paused ??= new(TaskCreationOptions.RunContinuationsAsynchronously); }
    public void Resume()
    {
        lock (gate) { paused?.TrySetResult(); paused = null; }
    }
    public Task WaitAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) return paused?.Task.WaitAsync(cancellationToken) ?? Task.CompletedTask;
    }
}
