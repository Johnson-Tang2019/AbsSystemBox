// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Extensions.DependencyInjection;
using SysBox.Transport;
using SysBox.FileIndex;
namespace SysBox.App.Views;

public sealed partial class SettingsPage
{
    private CancellationTokenSource? ntfsCancellation;
    private string? ntfsJob;
    private RpcClient NtfsRpc => App.Services.GetRequiredService<RpcClient>();
    private async Task ReadNtfsAsync()
    {
        string capability;
        try
        {
            var granted = await NtfsRpc.CallAsync<string[]>("ntfs.capabilities", new
            {
            });
            capability = $"只读服务已验证，已授权 {granted.Length} 个卷。";
        }
        catch { capability = "服务未安装或当前用户未授权；普通目录索引仍可用。"; }
        try
        {
            var states = await NtfsRpc.CallAsync<NtfsLiveState[]>("ntfs.status", new
            {
            });
            NtfsStatus.Text = capability + "\n" + string.Join("\n", states.Select(s => $"{s.Root} · {StateText(s.State)} · USN {s.Checkpoint} · 跟踪{(s.Tracking ? "开启" : "关闭")} · {s.Error ?? "无错误"}"));
        }
        catch { NtfsStatus.Text = capability + " 无法读取增量状态，旧索引未改动。"; }
    }
    private async void RefreshNtfs_Click(object sender, RoutedEventArgs args) => await ReadNtfsAsync();
    private static string StateText(string state) => state switch { "Tracking" => "增量跟踪中", "Stopped" => "已停止", "Interrupted" => "暂时中断", "RebuildRequired" => "需要手动重建", "LimitReached" => "达到预算限制", _ => "状态不可用" };
    private void InstallNtfs_Click(object sender, RoutedEventArgs args)
    {
        try
        {
            var root = NtfsRoot.Text.Trim();
            if (!System.Text.RegularExpressions.Regex.IsMatch(root, @"^[A-Za-z]:\\$"))
            {
                NtfsStatus.Text = "请先填写形如 D:\\ 的卷根目录。";
                return;
            }
            var script = System.IO.Path.Combine(AppContext.BaseDirectory, "scripts", "install-ntfs-service.ps1");
            if (!System.IO.File.Exists(script))
            {
                NtfsStatus.Text = "开发输出不安装系统服务；请使用带服务负载的完整发布包。";
                return;
            }
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var shell = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(shell) { UseShellExecute = true, Verb = "runas", WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden, Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -UserSid {identity.User!.Value} -VolumeRoot {root}" })?.Dispose();
            NtfsStatus.Text = "独立安装工具将要求管理员及卷授权确认；完成后点击刷新。不需要重启 Windows。";
        }
        catch { NtfsStatus.Text = "未完成管理员授权，服务配置未确认；普通目录模式仍可用。"; }
    }
    private async void StartNtfs_Click(object sender, RoutedEventArgs args)
    {
        if (ntfsCancellation is not null)
            return;
        var root = NtfsRoot.Text.Trim();
        if (string.IsNullOrWhiteSpace(root))
        {
            NtfsStatus.Text = "请明确填写要索引的本地 NTFS 卷根目录。";
            return;
        }
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "允许整卷 NTFS 元数据索引？", Content = $"目标：{root}\n将读取已授权整卷的文件名、大小、目录结构和硬链接，包括其他账户目录的元数据；不读取正文、不删除文件。\n首次索引最多一百万项、三十分钟，可随时取消。完成后每五秒跟踪 USN；只在桌面运行时续接，完全退出停止。", PrimaryButtonText = "确认建立并跟踪", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "NtfsApproval");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        using var cancellation = new CancellationTokenSource();
        ntfsCancellation = cancellation;
        StartNtfs.IsEnabled = false;
        var finished = false;
        try
        {
            ntfsJob = await NtfsRpc.CallAsync<string>("ntfs.build", new
            {
                root
            }, cancellation.Token);
            while (true)
            {
                var state = await NtfsRpc.CallAsync<JobStatus>("index.job", new
                {
                    id = ntfsJob
                }, cancellation.Token);
                NtfsStatus.Text = state.Progress is { } p ? $"{p.Error} · 已处理文件 {p.Files} / 目录 {p.Directories}" : "等待后台 NTFS 任务…";
                if (state.Error is { } error)
                {
                    NtfsStatus.Text = error.Message;
                    break;
                }
                if (state.Finished)
                {
                    finished = true;
                    await ReadNtfsAsync();
                    break;
                }
                await Task.Delay(250, cancellation.Token);
            }
        }
        catch (OperationCanceledException) { NtfsStatus.Text = "已取消本次索引；旧快照保留。"; }
        catch (Exception e) { NtfsStatus.Text = "NTFS 请求未完成：" + e.Message; }
        finally
        {
            if (!finished && ntfsJob is not null)
                try
                {
                    await NtfsRpc.CallAsync<JobStatus>("index.control", new
                    {
                        id = ntfsJob,
                        action = "cancel"
                    });
                }
                catch { }
            ntfsJob = null;
            ntfsCancellation = null;
            StartNtfs.IsEnabled = true;
        }
    }
    private async void StopNtfs_Click(object sender, RoutedEventArgs args)
    {
        ntfsCancellation?.Cancel();
        try
        {
            await NtfsRpc.CallAsync<bool>("ntfs.stop", new
            {
                root = NtfsRoot.Text.Trim()
            });
            await ReadNtfsAsync();
        }
        catch (Exception e) { NtfsStatus.Text = "停止请求未完成：" + e.Message; }
    }
    private async void ResumeNtfs_Click(object sender, RoutedEventArgs args)
    {
        var root = NtfsRoot.Text.Trim();
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "恢复已授权卷的增量跟踪？", Content = $"目标：{root}\n继续读取此前已建立索引的整卷元数据，每五秒续接 USN。日志缺失时只提示重建，不会自行整卷重扫。", PrimaryButtonText = "恢复跟踪", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            return;
        try
        {
            await NtfsRpc.CallAsync<bool>("ntfs.resume", new
            {
                root
            });
            await ReadNtfsAsync();
        }
        catch (Exception e) { NtfsStatus.Text = "恢复请求未完成：" + e.Message; }
    }
}
