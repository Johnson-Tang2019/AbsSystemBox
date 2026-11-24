// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SysBox.Abstractions;
using SysBox.Native;
using SysBox.Transport;
using Windows.Storage.Pickers;

namespace SysBox.App.Views;

public sealed partial class ScriptHostingPage : Page, IPageLifetime
{
    private readonly RpcClient client = App.Services.GetRequiredService<RpcClient>();
    private readonly DispatcherQueueTimer timer;
    private CancellationTokenSource? page;
    private ScriptStatus? selection;
    private Guid? pendingSelection;
    private bool updatingList;
    private bool refreshing;
    private bool changing;

    private sealed record Row(ScriptStatus Status)
    {
        public string Label => $"{Status.Name} · {StateText(Status.State, Status.Enabled)}";
    }

    public ScriptHostingPage()
    {
        InitializeComponent();
        timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.Tick += async (_, _) => await RefreshAsync();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs args)
    {
        Deactivate();
        page = new();
        try { SystemStartup.IsChecked = StartupRegistration.IsEnabled(); }
        catch (Exception error) { Present(error.Message, InfoBarSeverity.Error); }
        if (App.ScriptStartupError is { } startupError)
            Present(startupError, InfoBarSeverity.Error);
        UpdateControls();
        timer.Start();
        await RefreshAsync();
    }

    private void Page_Unloaded(object sender, RoutedEventArgs args) => Deactivate();

    public void Deactivate()
    {
        timer.Stop();
        page?.Cancel();
        page?.Dispose();
        page = null;
        pendingSelection = null;
    }

    private async Task RefreshAsync(Guid? select = null)
    {
        if (select is not null)
            pendingSelection = select;
        if (page is not { } lifetime || refreshing || (changing && select is null))
            return;
        var token = lifetime.Token;
        var requested = pendingSelection;
        pendingSelection = null;
        refreshing = true;
        try
        {
            var items = await client.CallAsync<ScriptStatus[]>("scripts.list", new { }, token);
            if (token.IsCancellationRequested) return;
            var selectedId = requested ?? selection?.Id;
            var rows = items.Select(item => new Row(item)).ToArray();
            var selected = rows.FirstOrDefault(row => row.Status.Id == selectedId);
            if (Entries.ItemsSource is not Row[] current || requested is not null ||
                !current.Select(row => (row.Status.Id, row.Label)).SequenceEqual(rows.Select(row => (row.Status.Id, row.Label))))
            {
                updatingList = true;
                try
                {
                    Entries.ItemsSource = rows;
                    Entries.SelectedItem = selected;
                }
                finally { updatingList = false; }
            }
            selection = selected?.Status;
            if (requested is not null && selection?.Definition is { } selectedDefinition)
                Fill(selectedDefinition);
            UpdateControls();
            var log = selection is null ? [] : await client.CallAsync<ScriptOutput[]>("scripts.logs", new { id = selectedId }, token);
            if (!token.IsCancellationRequested && selection?.Id == selectedId)
                Output.Text = string.Concat(log.Select(line => $"[{line.Timestamp.ToLocalTime():HH:mm:ss} {line.Stream}] {line.Text}\n"));
        }
        catch (Exception error) when (error is SysBoxOperationException or OperationCanceledException)
        {
            if (!token.IsCancellationRequested)
            {
                pendingSelection ??= requested;
                Present(error.Message, InfoBarSeverity.Error);
            }
        }
        finally { refreshing = false; }
    }

    private void Entries_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (updatingList)
            return;
        selection = (Entries.SelectedItem as Row)?.Status;
        if (selection?.Definition is { } definition)
            Fill(definition);
        Output.Text = "";
        UpdateControls();
        _ = RefreshAsync();
    }

    private void Fill(ScriptDefinition definition)
    {
        ScriptName.Text = definition.Name;
        ScriptPath.Text = definition.Path;
        Arguments.Text = string.Join(Environment.NewLine, definition.Arguments);
        RunOnLogin.IsChecked = definition.StartOnLogin;
        RetryOnExit.IsChecked = definition.RestartOnExit;
        AllowAgent.IsChecked = definition.AllowAgent;
    }

    private void New_Click(object sender, RoutedEventArgs args)
    {
        pendingSelection = null;
        Entries.SelectedItem = null;
        Fill(new(Guid.Empty, "", "", [], false, true, false, true));
        UpdateControls();
        ScriptName.Focus(FocusState.Programmatic);
    }

    private async void Browse_Click(object sender, RoutedEventArgs args)
    {
        var lifetime = page;
        try
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            picker.FileTypeFilter.Add(".ps1");
            picker.FileTypeFilter.Add(".py");
            var file = await picker.PickSingleFileAsync();
            if (file is null || page != lifetime || lifetime is null)
                return;
            ScriptPath.Text = file.Path;
            if (string.IsNullOrWhiteSpace(ScriptName.Text))
                ScriptName.Text = System.IO.Path.GetFileNameWithoutExtension(file.Name);
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            if (page == lifetime) Present("无法打开文件选择器，可直接填写路径。" + error.Message, InfoBarSeverity.Error);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs args)
    {
        var lines = Arguments.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var definition = new ScriptDefinition(selection?.Id ?? Guid.Empty, ScriptName.Text, ScriptPath.Text.Trim(),
            lines.Length == 0 ? [] : lines.Split('\n'), RunOnLogin.IsChecked == true, RetryOnExit.IsChecked == true,
            AllowAgent.IsChecked == true, true);
        await ChangeAsync(async token =>
        {
            var saved = await client.CallAsync<ScriptStatus>("scripts.save", definition, token);
            return saved.Id;
        }, "脚本设置已保存。");
    }

    private async void Start_Click(object sender, RoutedEventArgs args) => await ControlAsync("scripts.start", "已请求启动脚本。");

    private async void Stop_Click(object sender, RoutedEventArgs args) => await ControlAsync("scripts.stop", "已关闭脚本及自动重试。");

    private async Task ControlAsync(string method, string message)
    {
        if (selection is not { } item)
            return;
        await ChangeAsync(async token =>
        {
            await client.CallAsync<ScriptStatus>(method, new { item.Id }, token);
            return item.Id;
        }, message);
    }

    private async void Remove_Click(object sender, RoutedEventArgs args)
    {
        if (selection is not { } item)
            return;
        await ChangeAsync(async token =>
        {
            await client.CallAsync<bool>("scripts.remove", new { item.Id }, token);
            if (!token.IsCancellationRequested)
                New_Click(this, args);
            return Guid.Empty;
        }, "已移除托管，原脚本文件保留。");
    }

    private async Task ChangeAsync(Func<CancellationToken, Task<Guid>> action, string message)
    {
        if (page is not { } lifetime || changing)
            return;
        var token = lifetime.Token;
        changing = true;
        UpdateControls();
        try
        {
            var id = await action(token);
            if (token.IsCancellationRequested) return;
            Present(message, InfoBarSeverity.Success);
            await RefreshAsync(id);
        }
        catch (Exception error) when (error is SysBoxOperationException or OperationCanceledException)
        {
            if (!token.IsCancellationRequested) Present(error.Message, InfoBarSeverity.Error);
        }
        finally
        {
            changing = false;
            if (!token.IsCancellationRequested) UpdateControls();
        }
    }

    private void SystemStartup_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            StartupRegistration.SetEnabled(SystemStartup.IsChecked == true);
            Present("SystemBox 登录启动设置已保存。", InfoBarSeverity.Success);
        }
        catch (Exception error) { Present(error.Message, InfoBarSeverity.Error); }
    }

    private void Present(string message, InfoBarSeverity severity)
    {
        Feedback.Message = message;
        Feedback.Severity = severity;
        Feedback.IsOpen = true;
    }

    private void UpdateControls()
    {
        var active = selection?.State is "Starting" or "Running" or "Retrying" or "Stopping";
        NewButton.IsEnabled = Entries.IsEnabled = !changing;
        StartButton.IsEnabled = selection is not null && !active && !changing;
        StopButton.IsEnabled = selection is { Enabled: true } && !changing;
        RemoveButton.IsEnabled = selection is not null && !active && !changing;
        ScriptName.IsEnabled = ScriptPath.IsEnabled = Arguments.IsEnabled = RunOnLogin.IsEnabled =
            RetryOnExit.IsEnabled = AllowAgent.IsEnabled = BrowseButton.IsEnabled = SaveButton.IsEnabled = !active && !changing;
        RunStatus.Text = selection is { } item
            ? string.Join(" · ", new[] { StateText(item.State, item.Enabled), item.ProcessId is { } pid ? $"PID {pid}" : null,
                item.ExitCode is { } code ? $"最近退出码 {code}" : null, item.RetryAt is { } retry ? $"将在 {retry.ToLocalTime():HH:mm:ss} 重试" : null,
                item.Error }.Where(value => value is not null))
            : "选择脚本查看运行状态，或添加待托管的脚本。";
    }

    private static string StateText(string state, bool enabled) => state switch
    {
        "Starting" => "正在启动",
        "Running" => "运行中",
        "Retrying" => "等待重启",
        "Stopping" => "正在停止",
        "Failed" => "异常退出",
        "Exited" => "运行结束",
        _ => enabled ? "已停止" : "已关闭"
    };
}
