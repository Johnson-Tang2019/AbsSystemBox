// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Security.AccessControl;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SysBox.Abstractions;
using SysBox.Transport;
namespace SysBox.App.Controls;

public sealed partial class MemoryCleanupPanel
{
    private bool systemCleaning;
    private Task<Process?>? pendingLaunch;
    private void SelectFluent_Click(object sender, RoutedEventArgs args) => SelectAreas(MemoryCleanupCatalog.IsDefault);
    private void SelectPcl_Click(object sender, RoutedEventArgs args) => SelectAreas(MemoryCleanupCatalog.IsPclCombination);
    private void SelectAreas(Func<MemoryCleanupArea, bool> selected)
    {
        if (systemCleaning)
            return;
        foreach (var option in AreaOptions.Children.OfType<CheckBox>())
            option.IsChecked = selected((MemoryCleanupArea)option.Tag);
        SystemResult.Text = "已选择组合；尚未执行，执行时仍需本次管理员确认。";
    }
    private void InitializeSystemAreas()
    {
        foreach (var area in MemoryCleanupCatalog.Order)
            AreaOptions.Children.Add(new CheckBox { Content = MemoryCleanupCatalog.Name(area), Tag = area, IsChecked = MemoryCleanupCatalog.IsDefault(area) });
    }
    private async void CleanSystem_Click(object sender, RoutedEventArgs args) => await RequestSystemCleanupAsync();
    public async Task RequestSystemCleanupAsync()
    {
        SystemResult.StartBringIntoView();
        if (systemCleaning)
            return;
        if (pendingLaunch is { IsCompleted: false })
        {
            SystemResult.Text = "请先关闭上一次的管理员授权窗口。";
            return;
        }
        systemCleaning = true;
        try
        {
            if (App.Services.GetRequiredService<SysBox.Core.IDashboardClient>().IsDemo)
            {
                SystemResult.Text = "示例模式不执行内存清理。";
                return;
            }
            var areas = MemoryCleanupCatalog.Validate(AreaOptions.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (MemoryCleanupArea)c.Tag));
            var executable = Path.Combine(AppContext.BaseDirectory, "cleanup", "SysBox.CleanupHelper.exe");
            if (!File.Exists(executable))
            {
                SystemResult.Text = "管理员清理组件未部署，请使用完整安装包。";
                return;
            }
            var nonce = Guid.NewGuid().ToString("N");
            using var identity = WindowsIdentity.GetCurrent();
            var acl = new PipeSecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.SetOwner(identity.User!);
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
            acl.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
            await using var pipe = NamedPipeServerStreamAcl.Create("AbsSystemBox.Cleanup." + nonce, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 16384, 16384, acl);
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            SystemResult.Text = "等待 Windows 管理员授权…";
            var launch = pendingLaunch = Task.Run(() => Process.Start(new ProcessStartInfo(executable, nonce)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            }));
            using var process = await AwaitLaunchAsync(launch, timeout.Token);
            var connected = pipe.WaitForConnectionAsync(timeout.Token);
            if (await Task.WhenAny(connected, process.WaitForExitAsync(timeout.Token)) != connected)
                throw new IOException("管理员组件已退出，未执行本次清理。");
            await connected;
            PeerIdentity.VerifyLaunchedClient(pipe, process, executable);
            SystemResult.Text = "等待本次清理确认或结果…";
            await Protocol.WriteAsync(pipe, new RpcRequest(Protocol.Version, "cleanup.system", JsonSerializer.SerializeToElement(areas, Protocol.Json)), timeout.Token);
            var response = await Protocol.ReadAsync<RpcResponse>(pipe, timeout.Token);
            if (response.Version != Protocol.Version || response.Error is not null)
                throw new IOException("管理员清理未完成。");
            var report = response.Value!.Value.Deserialize<MemoryCleanupReport>(Protocol.Json) ?? throw new IOException();
            SystemResult.Text = report.Cancelled ? "本次清理已取消，未执行系统操作。" : string.Join("\n", report.Results.Select(r => $"{MemoryCleanupCatalog.Name(r.Area)}：{r.Status}")) + "\n" + (report.AvailableChangeBytes is { } change ? $"可用物理内存变化：{change / 1048576.0:+0.0;-0.0;0.0} MiB（包含系统同期变化）" : "可用物理内存变化：未知");
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) { SystemResult.Text = "已取消管理员授权，未执行清理。"; }
        catch (OperationCanceledException) { SystemResult.Text = "等待清理结果超时；不要重复执行，请先确认管理员窗口状态。"; }
        catch (Exception e) { SystemResult.Text = "清理未完成：" + e.Message; }
        finally { systemCleaning = false; }
    }
    private static async Task<Process> AwaitLaunchAsync(Task<Process?> launch, CancellationToken token)
    {
        try
        {
            return await launch.WaitAsync(token) ?? throw new IOException("无法启动管理员组件。");
        }
        catch (OperationCanceledException)
        {
            // UAC owns the dialog; release a late process handle after the pipe has closed.
            _ = launch.ContinueWith(task =>
            {
                if (task.IsCompletedSuccessfully)
                    task.Result?.Dispose();
                else
                    _ = task.Exception;
            }, TaskScheduler.Default);
            throw;
        }
    }
}
