// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SysBox.Abstractions;
using System.Collections.ObjectModel;

namespace SysBox.Presentation;

public interface ISamplingDelay
{
    Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken);
}

public sealed class SamplingDelay : ISamplingDelay
{
    public Task WaitAsync(TimeSpan interval, CancellationToken cancellationToken) => Task.Delay(interval, cancellationToken);
}

public sealed class MemoryViewModel : ObservableObject
{
    public static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(5);
    private readonly IMemoryMonitor memory;
    private readonly IProcessMemoryMonitor processes;
    private readonly ISamplingDelay delay;
    private readonly MemoryHistory history = new();
    private CancellationTokenSource? loopCancellation;
    private CancellationTokenSource? readCancellation;
    private int generation;
    private bool isBusy;
    private bool isMonitoring;
    private string? errorMessage;
    private string status = "等待采样。";
    private MemorySnapshot? snapshot;
    private readonly List<ProcessMemoryRow> rowPool = [];
    private string processStatus = "尚未读取进程列表。";

    public MemoryViewModel(IMemoryMonitor memory, IProcessMemoryMonitor processes, ISamplingDelay delay)
    {
        this.memory = memory;
        this.processes = processes;
        this.delay = delay;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        ToggleMonitoringCommand = new RelayCommand(() => { if (IsMonitoring) Stop(); else Start(); });
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand ToggleMonitoringCommand { get; }
    public Task MonitoringTask { get; private set; } = Task.CompletedTask;
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (!SetProperty(ref isBusy, value)) return;
            RefreshCommand.NotifyCanExecuteChanged();
            CancelCommand.NotifyCanExecuteChanged();
        }
    }
    public bool IsMonitoring
    {
        get => isMonitoring;
        private set
        {
            if (!SetProperty(ref isMonitoring, value)) return;
            OnPropertyChanged(nameof(MonitoringButtonText));
            OnPropertyChanged(nameof(MonitoringStatus));
        }
    }
    public string MonitoringButtonText => IsMonitoring ? "停止自动刷新" : "开始自动刷新";
    public string MonitoringStatus => IsMonitoring ? "每 5 秒刷新 · 只读" : "自动刷新已停止";
    public string? ErrorMessage
    {
        get => errorMessage;
        private set { if (SetProperty(ref errorMessage, value)) OnPropertyChanged(nameof(HasError)); }
    }
    public bool HasError => ErrorMessage is not null;
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public string ProcessStatus { get => processStatus; private set => SetProperty(ref processStatus, value); }
    public MemorySnapshot? Snapshot => snapshot;
    public ObservableCollection<ProcessMemoryRow> Processes { get; } = [];
    public IReadOnlyList<MemoryHistoryPoint> History => history.Points;
    public string SampleCount => $"样本 {history.Points.Count} / {history.Capacity}";
    public string UsageText => MemoryFormatting.Percent(MemoryFormatting.PhysicalPercentage(snapshot));
    public double UsagePercent => MemoryFormatting.PhysicalPercentage(snapshot) ?? 0;
    public string TotalText => MemoryFormatting.GiB(snapshot?.TotalPhysicalBytes);
    public string TotalSummary => $"总量 {TotalText}";
    public string AvailableText => MemoryFormatting.GiB(snapshot?.AvailablePhysicalBytes);
    public string UsedText => snapshot is { } data && data.AvailablePhysicalBytes <= data.TotalPhysicalBytes
        ? $"已用 {MemoryFormatting.GiB(data.TotalPhysicalBytes - data.AvailablePhysicalBytes)}" : "—";
    public string CommitText => snapshot?.CommitTotalBytes is { } total && snapshot.CommitLimitBytes is { } limit
        ? $"{MemoryFormatting.GiB(total)} / {MemoryFormatting.GiB(limit)}" : "提交量暂不可用";
    public string LastUpdated => snapshot is { } data ? $"最近采样 {data.TimestampUtc.ToLocalTime():HH:mm:ss}" : "尚未采样";

    public void Start()
    {
        if (IsMonitoring) return;
        IsMonitoring = true;
        var cancellation = new CancellationTokenSource();
        loopCancellation = cancellation;
        MonitoringTask = MonitorAsync(cancellation);
    }

    private async Task MonitorAsync(CancellationTokenSource cancellation)
    {
        using (cancellation)
        {
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    await RefreshAsync();
                    await delay.WaitAsync(SampleInterval, cancellation.Token);
                }
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
            finally
            {
                if (ReferenceEquals(loopCancellation, cancellation))
                {
                    loopCancellation = null;
                    IsMonitoring = false;
                }
            }
        }
    }

    public async Task RefreshAsync()
    {
        if (IsBusy) return;
        var requestGeneration = ++generation;
        using var cancellation = new CancellationTokenSource();
        readCancellation = cancellation;
        IsBusy = true;
        ErrorMessage = null;
        Status = "正在读取内存与进程状态…";
        try
        {
            var memoryTask = ReadSafely(() => memory.ReadAsync(cancellation.Token), cancellation.Token);
            var processTask = ReadSafely(() => processes.ReadAsync(cancellation.Token), cancellation.Token);
            await Task.WhenAll(memoryTask, processTask);
            if (requestGeneration != generation || cancellation.IsCancellationRequested) return;
            var physical = await memoryTask;
            if (physical.IsSuccess)
            {
                snapshot = physical.Value;
                history.Add(snapshot);
                NotifySnapshot();
                Status = snapshot.Completeness == DataCompleteness.Complete ? "已更新，只读采样。" : "物理内存已更新，提交量暂不可用。";
            }
            else
            {
                ErrorMessage = "无法读取系统内存，请稍后重试。";
                Status = snapshot is null ? "未获得系统读数。" : "本次读取失败，保留上次采样及时间。";
            }
            var process = await processTask;
            if (process.IsSuccess)
            {
                var data = process.Value;
                UpdateProcesses(data.Entries);
                ProcessStatus = $"已读取 {data.ReadableCount} / {data.EnumeratedCount} 个进程，显示工作集前 {Processes.Count} 项。权限不足 {data.AccessDeniedCount}，退出或不可读 {data.UnavailableCount}，其他失败 {data.OtherErrorCount}，超时跳过 {data.BudgetSkippedCount}。"
                    + (data.EnumerationComplete ? "" : " 进程枚举达到上限，结果不完整。");
            }
            else
            {
                UpdateProcesses([]);
                ProcessStatus = "进程列表读取失败，可稍后重试；系统内存仍独立采样。";
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (requestGeneration == generation)
            {
                readCancellation = null;
                IsBusy = false;
            }
        }
    }

    private static async Task<Result<T>> ReadSafely<T>(Func<Task<Result<T>>> read, CancellationToken ct) where T : notnull
    {
        try { return await read(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return Result<T>.Failure(new(ErrorCodes.Unavailable, "Collection failed.")); }
    }

    public void Cancel()
    {
        readCancellation?.Cancel();
        if (IsBusy) Status = "已取消本次读取。";
    }

    public void Stop()
    {
        ++generation;
        loopCancellation?.Cancel();
        loopCancellation = null;
        readCancellation?.Cancel();
        readCancellation = null;
        IsMonitoring = false;
        IsBusy = false;
        Status = "已停止自动刷新，保留最近采样。";
    }

    public void Deactivate()
    {
        Stop();
        snapshot = null;
        history.Clear();
        UpdateProcesses([]);
        ProcessStatus = "尚未读取进程列表。";
        ErrorMessage = null;
        NotifySnapshot();
    }

    private void NotifySnapshot()
    {
        foreach (var name in new[] { nameof(Snapshot), nameof(UsageText), nameof(UsagePercent), nameof(TotalText), nameof(TotalSummary),
            nameof(AvailableText), nameof(UsedText), nameof(CommitText), nameof(LastUpdated), nameof(History), nameof(SampleCount) })
            OnPropertyChanged(name);
    }

    private void UpdateProcesses(IReadOnlyList<ProcessMemoryEntry> entries)
    {
        var count = Math.Min(entries.Count, ProcessMemorySnapshot.MaximumEntries);
        while (rowPool.Count < count) rowPool.Add(new ProcessMemoryRow());
        for (var index = 0; index < rowPool.Count; index++)
            rowPool[index].Update(index < count ? entries[index] : null);
        if (count == 0) Processes.Clear();
        else
        {
            while (Processes.Count > count) Processes.RemoveAt(Processes.Count - 1);
            while (Processes.Count < count) Processes.Add(rowPool[Processes.Count]);
        }
    }
}
