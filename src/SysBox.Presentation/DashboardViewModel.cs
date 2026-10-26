// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SysBox.Abstractions;
using SysBox.Core;

namespace SysBox.Presentation;

public sealed class DashboardViewModel : ObservableObject
{
    private readonly IDashboardClient client;
    private CancellationTokenSource? activeRequest;
    private int generation;
    private bool isBusy;
    private string? errorMessage;
    private string status = "内存观察尚未实现，当前没有系统读数。";
    private MemorySnapshot? snapshot;

    public DashboardViewModel(IDashboardClient client)
    {
        this.client = client;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
    }

    public IAsyncRelayCommand RefreshCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public bool IsDemo => client.IsDemo;
    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                RefreshCommand.NotifyCanExecuteChanged();
                CancelCommand.NotifyCanExecuteChanged();
            }
        }
    }
    public string? ErrorMessage
    {
        get => errorMessage;
        private set
        {
            if (SetProperty(ref errorMessage, value)) OnPropertyChanged(nameof(HasError));
        }
    }
    public bool HasError => ErrorMessage is not null;
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public MemorySnapshot? Snapshot
    {
        get => snapshot;
        private set
        {
            if (SetProperty(ref snapshot, value)) OnPropertyChanged(nameof(MemorySummary));
        }
    }
    public string MemorySummary => Snapshot is { } data
        ? $"{data.AvailablePhysicalBytes / 1073741824.0:F1} / {data.TotalPhysicalBytes / 1073741824.0:F1} GiB 可用"
        : "尚未接入";

    public async Task RefreshAsync()
    {
        var requestGeneration = ++generation;
        activeRequest?.Cancel();
        using var request = new CancellationTokenSource();
        activeRequest = request;
        ErrorMessage = null;
        Snapshot = null;
        IsBusy = true;
        Status = "正在读取状态…";
        try
        {
            var result = await client.ReadAsync(request.Token);
            if (requestGeneration != generation || request.IsCancellationRequested) return;
            if (result.IsSuccess)
            {
                Snapshot = result.Value;
                Status = IsDemo ? "示例数据，仅供界面预览。" : "状态已更新。";
            }
            else
            {
                ErrorMessage = result.Error!.Code == ErrorCodes.NotImplemented
                    ? "内存观察尚未实现，接入后才能读取系统状态。"
                    : "暂时无法读取状态，请稍后重试。";
                Status = "未获得系统读数。";
            }
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested)
        {
            if (requestGeneration == generation) Status = "已取消读取。";
        }
        catch (Exception)
        {
            if (requestGeneration == generation && !request.IsCancellationRequested)
            {
                ErrorMessage = "读取状态时出现问题，请稍后重试。";
                Status = "未获得系统读数。";
            }
        }
        finally
        {
            if (requestGeneration == generation)
            {
                activeRequest = null;
                IsBusy = false;
            }
        }
    }

    public void Cancel()
    {
        activeRequest?.Cancel();
        if (IsBusy) Status = "已取消读取。";
    }

    // Invalidate results even when a client does not cooperate with cancellation.
    public void Deactivate()
    {
        ++generation;
        activeRequest?.Cancel();
        activeRequest = null;
        IsBusy = false;
        Snapshot = null;
        ErrorMessage = null;
        Status = "内存观察尚未实现，当前没有系统读数。";
    }
}
