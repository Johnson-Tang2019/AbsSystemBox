// SPDX-License-Identifier: GPL-3.0-or-later
using CommunityToolkit.Mvvm.ComponentModel;
using SysBox.Abstractions;
using SysBox.Core;
namespace SysBox.Presentation;

public sealed class CoolingDisplayViewModel(ICoolingDisplayTransport transport, ICoolingSensorSource sensors, CoolingDisplayPreferenceStore store) : ObservableObject
{
    private CancellationTokenSource? cancellation;
    private int generation;
    private bool refreshing;
    private bool loaded;
    private bool pageDetection;
    private CancellationTokenSource? refreshCancellation;
    public CoolingDevice[] Devices { get; private set; } = [];
    public CoolingSensor[] Temperatures { get; private set; } = [];
    public CoolingSensor[] Fans { get; private set; } = [];
    public CoolingDisplaySettings Settings { get; set; } = new();
    public string Status { get; private set; } = "尚未连接";
    public string SensorStatus { get; private set; } = "尚未读取传感器";
    public string LastWrite { get; private set; } = "尚未发送";
    public bool IsRunning => cancellation is not null;
    public Task Completion { get; private set; } = Task.CompletedTask;
    public void Load()
    {
        if (loaded) return;
        loaded = true;
        try { Settings = store.Load(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException)
        { SetStatus("设置读取失败：" + e.Message); }
    }
    public void Save()
    {
        try { store.Save(Settings); SetStatus("设置已保存。"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        { SetStatus("设置保存失败：" + e.Message); }
    }
    public async Task RefreshAsync(bool pageOwned = true)
    {
        if (refreshing || IsRunning) return;
        refreshing = true;
        pageDetection = pageOwned;
        var request = generation;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        refreshCancellation = deadline;
        try
        {
            var devices = await transport.DiscoverAsync(deadline.Token).WaitAsync(deadline.Token);
            if (request != generation) return;
            Devices = devices;
            OnPropertyChanged(nameof(Devices));
            try
            {
                var snapshot = await sensors.ReadAsync(deadline.Token).WaitAsync(deadline.Token);
                if (request != generation) return;
                UpdateSensors(snapshot);
                var selected = CoolingDisplayDefaults.Select(Settings, devices, snapshot.Sensors);
                if (selected != Settings) { store.Save(selected); Settings = selected; }
            }
            catch (Exception e) when (!deadline.IsCancellationRequested)
            {
                if (request != generation) return;
                UpdateSensors(new(DateTimeOffset.UtcNow, [], "传感器不可用：" + e.Message));
                if (!Settings.ManualValues) { SetStatus("检测失败：" + e.Message); return; }
                Settings = CoolingDisplayDefaults.Select(Settings, devices, []);
            }
            SetStatus(devices.Length == 0 ? "未发现兼容水冷屏。" : "设备已检测，选择读数后开始同步。");
        }
        catch (Exception e) { if (request == generation) SetStatus("检测失败：" + e.Message); }
        finally { refreshCancellation = null; refreshing = false; }
    }
    public Task StartAtApplicationLaunchAsync()
    {
        Load();
        if (Settings.AutoStart && !IsRunning && Completion.IsCompleted)
            BeginSynchronization(Settings);
        return Task.CompletedTask;
    }
    public void CancelPageDetection()
    {
        if (IsRunning || !pageDetection) return;
        generation++;
        refreshCancellation?.Cancel();
    }
    public void Start()
    {
        if (IsRunning || refreshing || !Completion.IsCompleted) return;
        var device = Devices.FirstOrDefault(d => d.DeviceId == Settings.DeviceId);
        if (device is null) { SetStatus("请先检测并选择水冷屏。"); return; }
        var settings = Settings;
        try { _ = GoldenFiledDisplayProtocol.Encode(settings.ManualTemperature, settings.ManualFanRpm); }
        catch (ArgumentException) { SetStatus("手动温度须为 0～255，转速须为 0～65535。单位分别为 ℃ 和 RPM。"); return; }
        BeginSynchronization(settings);
    }
    private void BeginSynchronization(CoolingDisplaySettings settings)
    {
        cancellation = new();
        OnPropertyChanged(nameof(IsRunning));
        Completion = RunAsync(settings, ++generation, cancellation.Token);
    }
    private async Task RunAsync(CoolingDisplaySettings settings, int request, CancellationToken token)
    {
        var retrySeconds = 1;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    SetStatus("正在连接水冷屏…");
                    using var connectDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                    connectDeadline.CancelAfter(TimeSpan.FromSeconds(20));
                    var devices = await transport.DiscoverAsync(connectDeadline.Token).WaitAsync(connectDeadline.Token);
                    if (request != generation || token.IsCancellationRequested) return;
                    Devices = devices;
                    OnPropertyChanged(nameof(Devices));
                    var initial = settings.ManualValues ? null : await sensors.ReadAsync(connectDeadline.Token).WaitAsync(connectDeadline.Token);
                    if (request != generation || token.IsCancellationRequested) return;
                    if (initial is not null) UpdateSensors(initial);
                    var selected = CoolingDisplayDefaults.Select(settings, devices, initial?.Sensors ?? []);
                    if (selected != settings)
                    {
                        store.Save(selected);
                        settings = selected;
                        Settings = selected;
                    }
                    var device = devices.Single(d => d.DeviceId == settings.DeviceId);
                    using var connection = await transport.ConnectAsync(device, connectDeadline.Token);
                    await Task.Delay(TimeSpan.FromSeconds(3), token);
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        int temperature, fan;
                        if (settings.ManualValues) { temperature = settings.ManualTemperature; fan = settings.ManualFanRpm; }
                        else
                        {
                            using var sampleDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                            sampleDeadline.CancelAfter(TimeSpan.FromSeconds(15));
                            var snapshot = await sensors.ReadAsync(sampleDeadline.Token).WaitAsync(sampleDeadline.Token);
                            if (request != generation || token.IsCancellationRequested) return;
                            UpdateSensors(snapshot);
                            (temperature, fan) = Resolve(snapshot, settings, DateTimeOffset.UtcNow);
                        }
                        if (request != generation || token.IsCancellationRequested) return;
                        await connection.WriteAsync(temperature, fan, token);
                        if (request != generation || token.IsCancellationRequested) return;
                        retrySeconds = 1;
                        LastWrite = $"{(settings.ManualValues ? "手动显示值" : "传感器读数")}：{temperature} ℃ · {fan} RPM · {DateTimeOffset.Now:HH:mm:ss} 已写入串口（屏幕显示尚需核对）";
                        OnPropertyChanged(nameof(LastWrite));
                        SetStatus("正在同步 · 每秒一次");
                        await Task.Delay(1000, token);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is IOException or InvalidDataException or TimeoutException or OperationCanceledException or System.ComponentModel.Win32Exception)
                {
                    if (request != generation || token.IsCancellationRequested) return;
                    SetStatus($"连接暂不可用，正在自动恢复（{retrySeconds} 秒后重试）：{error.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(retrySeconds), token);
                    retrySeconds = Math.Min(retrySeconds * 2, 15);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception e) { if (request == generation) SetStatus("同步已停止：" + e.Message); }
        finally
        {
            if (request == generation)
            {
                cancellation?.Dispose(); cancellation = null;
                OnPropertyChanged(nameof(IsRunning));
            }
        }
    }
    public static (int Temperature, int FanRpm) Resolve(CoolingSensorSnapshot snapshot, CoolingDisplaySettings settings, DateTimeOffset now)
    {
        var age = now - snapshot.SampledAt;
        if (age < TimeSpan.FromSeconds(-1) || age > TimeSpan.FromSeconds(5)) throw new InvalidDataException("传感器读数已过期。");
        var temperature = snapshot.Sensors.FirstOrDefault(s => s.Id == settings.TemperatureSensorId && s.Kind == CoolingSensorKind.Temperature);
        var fan = snapshot.Sensors.FirstOrDefault(s => s.Id == settings.FanSensorId && s.Kind == CoolingSensorKind.Fan);
        if (temperature is null || fan is null) throw new InvalidDataException("所选温度或风扇传感器不可用，请重新选择。");
        if (!double.IsFinite(temperature.Value) || !double.IsFinite(fan.Value) || temperature.Value < 0 || temperature.Value > 255 || fan.Value < 0 || fan.Value > 65535)
            throw new InvalidDataException("传感器读数超出屏幕可显示范围。");
        return ((int)temperature.Value, (int)fan.Value);
    }
    private void UpdateSensors(CoolingSensorSnapshot snapshot)
    {
        Temperatures = snapshot.Sensors.Where(s => s.Kind == CoolingSensorKind.Temperature).Take(256).ToArray();
        Fans = snapshot.Sensors.Where(s => s.Kind == CoolingSensorKind.Fan).Take(256).ToArray();
        SensorStatus = snapshot.Message;
        OnPropertyChanged(nameof(Temperatures)); OnPropertyChanged(nameof(Fans)); OnPropertyChanged(nameof(SensorStatus));
    }
    public void Deactivate()
    {
        generation++;
        refreshCancellation?.Cancel();
        cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
        OnPropertyChanged(nameof(IsRunning));
        SetStatus("同步已停止。设备可能保留最后显示值。");
    }
    private void SetStatus(string value) { Status = value; OnPropertyChanged(nameof(Status)); }
}
