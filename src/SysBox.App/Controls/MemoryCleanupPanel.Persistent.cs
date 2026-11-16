// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.UI.Xaml;
using SysBox.Abstractions;
using SysBox.NtfsBroker;
using SysBox.Transport;

namespace SysBox.App.Controls;

public sealed partial class MemoryCleanupPanel
{
    private const string PersistentService = "AbsSystemBox.CleanupService";
    private bool managingGrant;

    private async void AuthorizeCleanup_Click(object sender, RoutedEventArgs args) => await ManageGrantAsync(false);
    private async void RevokeCleanup_Click(object sender, RoutedEventArgs args) => await ManageGrantAsync(true);

    private async Task ManageGrantAsync(bool revoke)
    {
        if (managingGrant || systemCleaning || pendingLaunch is { IsCompleted: false } || IsCleanupDemo()) return;
        managingGrant = true;
        try
        {
            var script = Path.Combine(AppContext.BaseDirectory, "scripts", "install-cleanup-service.ps1");
            if (!File.Exists(script)) throw new IOException("清理授权工具缺失，请安装完整版本。");
            using var identity = WindowsIdentity.GetCurrent();
            var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe"))
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-UserSid", identity.User!.Value })
                start.ArgumentList.Add(argument);
            if (revoke)
                start.ArgumentList.Add("-Revoke");
            else
            {
                var areas = SelectedCleanupAreas();
                if (areas.Contains(MemoryCleanupArea.ProcessWorkingSets))
                    throw new InvalidOperationException("逐进程工作集需要在内存页手动执行。请取消勾选该项，再保存悬浮球授权。");
                start.ArgumentList.Add("-Areas");
                start.ArgumentList.Add(string.Join(",", areas.Select(area => (int)area)));
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            SystemResult.Text = "等待管理员确认清理授权…";
            var launch = pendingLaunch = Task.Run(() => Process.Start(start));
            using var process = await AwaitLaunchAsync(launch, deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            if (process.ExitCode == 2) { SystemResult.Text = "已取消，授权未更改。"; return; }
            if (process.ExitCode != 0) throw new IOException("授权工具未成功完成。");
            if (revoke) { SystemResult.Text = "悬浮球直接清理授权已撤销。"; return; }
            var grantedAreas = await CallGrantAsync<MemoryCleanupArea[]>("cleanup.grant.status", deadline.Token);
            SystemResult.Text = "悬浮球双击已授权：" + string.Join("、", grantedAreas.Select(MemoryCleanupCatalog.Name));
        }
        catch (System.ComponentModel.Win32Exception error) when (error.NativeErrorCode == 1223)
        { SystemResult.Text = "已取消 Windows 管理员授权。"; }
        catch (Exception error) { SystemResult.Text = "清理授权未完成：" + error.Message; }
        finally { managingGrant = false; }
    }

    private async Task ExecuteGrantedAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        SystemResult.Text = "正在请求执行已授权的清理项目…";
        ShowCleanupReport(await CallGrantAsync<MemoryCleanupReport>("cleanup.grant.execute", deadline.Token));
    }

    private static async Task<T> CallGrantAsync<T>(string method, CancellationToken token)
    {
        await using var pipe = new NamedPipeClientStream(".", "AbsSystemBox.Cleanup.Persistent.v1", PipeDirection.InOut,
            PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        try { await pipe.ConnectAsync(2000, token); }
        catch (TimeoutException) { throw new IOException("直接清理服务未授权或未运行，请在内存页检查授权。"); }
        var image = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "AbsSystemBox", "CleanupService", "SysBox.CleanupHelper.exe");
        BrokerPeer.VerifyServiceServer(pipe, PersistentService, image, "--service");
        await Protocol.WriteAsync(pipe, new RpcRequest(Protocol.Version, method, JsonSerializer.SerializeToElement(new { }, Protocol.Json)), token);
        var response = await Protocol.ReadAsync<RpcResponse>(pipe, token);
        if (response.Version != Protocol.Version || response.Value is null)
            throw new IOException(response.Error?.Message ?? "清理服务响应无效。");
        return response.Value.Value.Deserialize<T>(Protocol.Json) ?? throw new IOException("清理服务响应为空。");
    }
}
