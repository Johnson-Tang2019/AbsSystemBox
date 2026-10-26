// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using SysBox.Abstractions;
using SysBox.Core;
namespace SysBox.Presentation;

public sealed class BatteryViewModel(IBatteryMonitor monitor) : ObservableObject
{
    private CancellationTokenSource? cancellation;
    private int generation;
    public BatteryReading? Reading { get; private set; }
    public BatteryEstimate Estimate { get; private set; } = new(false,null,null);
    public bool IsCharging => Estimate.IsCharging;
    public string PowerText => Estimate.NetChargeWatts is {} watts ? $"{watts:F1} W" : "硬件未提供充电功率";
    public string TimeText => Estimate.TimeToFull is {} time ? time.TotalMinutes < 1 ? "预计不到 1 分钟" : $"预计约 {Math.Ceiling(time.TotalMinutes):F0} 分钟充满" : "暂无法估算充满时间";
    public Task Completion { get; private set; } = Task.CompletedTask;
    public void Start()
    {
        if(cancellation is not null) return;
        cancellation = new();
        Completion = RunAsync(++generation,cancellation.Token);
    }
    private async Task RunAsync(int request,CancellationToken token)
    {
        try
        {
            while(!token.IsCancellationRequested)
            {
                await RefreshAsync(request,token);
                await Task.Delay(TimeSpan.FromSeconds(5),token);
            }
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested) { }
    }
    public Task RefreshAsync(CancellationToken token = default) => RefreshAsync(generation,token);
    private async Task RefreshAsync(int request,CancellationToken token)
    {
        var reading = await monitor.ReadAsync(token);
        if(request != generation || token.IsCancellationRequested) return;
        Reading = reading;
        Estimate = BatteryEstimate.From(reading);
        OnPropertyChanged(nameof(Reading)); OnPropertyChanged(nameof(IsCharging));
        OnPropertyChanged(nameof(PowerText)); OnPropertyChanged(nameof(TimeText));
    }
    public void Deactivate()
    {
        generation++;
        cancellation?.Cancel(); cancellation?.Dispose(); cancellation=null;
        Reading=null; Estimate=new(false,null,null); OnPropertyChanged(nameof(IsCharging));
    }
}
