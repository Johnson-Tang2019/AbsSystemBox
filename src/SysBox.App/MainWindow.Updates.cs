// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Win32;
using SysBox.Core;

namespace SysBox.App;

public sealed partial class MainWindow
{
    private static readonly IWebProxy ReleaseProxy = HttpClient.DefaultProxy;
    private static readonly HttpClient ReleaseHttpClient = CreateReleaseClient(useProxy: true);
    private static readonly HttpClient DirectReleaseHttpClient = CreateReleaseClient(useProxy: false);
    private readonly GitHubReleaseUpdates updates = new(ReleaseHttpClient, DirectReleaseHttpClient, ReleaseProxy);
    private readonly CancellationTokenSource updateLifetime = new();
    private Version? currentUpdateVersion;
    private ReleaseUpdate? offeredUpdate;
    private long lastUpdateCheck = long.MinValue;
    private bool checkingUpdate;
    private bool installingUpdate;
    private bool showCheckResult;

    private static HttpClient CreateReleaseClient(bool useProxy) => new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = useProxy,
        Proxy = useProxy ? ReleaseProxy : null
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private void InitializeUpdates()
    {
        if (!DemoBanner.IsOpen && IsRegisteredInstallation())
        {
            var assemblyVersion = typeof(MainWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion.Split('+')[0];
            if (Version.TryParse(assemblyVersion, out var version))
                currentUpdateVersion = version;
        }
        Activated += async (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated && currentUpdateVersion is not null)
                await CheckUpdatesAsync(manual: false);
        };
        Closed += (_, _) => updateLifetime.Cancel();
    }

    public async Task CheckUpdatesAsync(bool manual)
    {
        if (installingUpdate || updateLifetime.IsCancellationRequested)
            return;
        if (currentUpdateVersion is not { } installed)
        {
            if (manual)
                ShowUpdateStatus("无法检查更新", "请从 Windows 安装的正式版使用软件更新。", InfoBarSeverity.Informational);
            return;
        }
        var now = Environment.TickCount64;
        if (checkingUpdate)
        {
            if (manual)
            {
                showCheckResult = true;
                ShowUpdateStatus("正在检查更新", $"当前版本 {installed}", InfoBarSeverity.Informational);
            }
            return;
        }
        if (!manual && lastUpdateCheck != long.MinValue && now - lastUpdateCheck < 300_000)
            return;
        lastUpdateCheck = now;
        checkingUpdate = true;
        showCheckResult = manual;
        if (manual)
            ShowUpdateStatus("正在检查更新", $"当前版本 {installed}", InfoBarSeverity.Informational);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            offeredUpdate = await updates.CheckAsync(installed, deadline.Token);
            if (updateLifetime.IsCancellationRequested)
                return;
            if (offeredUpdate is { } update)
                ShowUpdateStatus($"可更新至 {update.Version}", $"当前版本 {installed}。验证下载后将退出软件并打开安装程序。",
                    InfoBarSeverity.Informational, install: true);
            else if (showCheckResult)
                ShowUpdateStatus("已是最新版本", $"当前版本 {installed}", InfoBarSeverity.Success);
            else
                UpdateBanner.IsOpen = false;
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or OperationCanceledException)
        {
            if (showCheckResult && !updateLifetime.IsCancellationRequested)
                ShowUpdateStatus("暂时无法检查更新", "请检查网络，稍后重试。", InfoBarSeverity.Warning);
        }
        finally { checkingUpdate = false; }
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs args)
    {
        if (installingUpdate || checkingUpdate || offeredUpdate is not { } update)
            return;
        installingUpdate = true;
        var folder = Path.Combine(Path.GetTempPath(), "AbsSystemBox-update-" + Guid.NewGuid().ToString("N"));
        var installer = Path.Combine(folder, GitHubReleaseUpdates.InstallerName(update.Version));
        var launched = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(updateLifetime.Token);
        deadline.CancelAfter(TimeSpan.FromMinutes(4));
        ShowUpdateStatus($"正在下载 {update.Version}", "正在连接官方发布源…", InfoBarSeverity.Informational);
        try
        {
            Directory.CreateDirectory(folder);
            var progress = new Progress<double>(percent =>
            {
                if (installingUpdate && !updateLifetime.IsCancellationRequested)
                    UpdateBanner.Message = $"已下载 {percent:0}%";
            });
            await using (var target = new FileStream(installer, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                await updates.DownloadAsync(update, target, progress, deadline.Token);
            await File.WriteAllTextAsync(installer + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n", deadline.Token);
            await using var verified = new FileStream(installer, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, useAsync: true);
            if (!CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(verified, deadline.Token),
                Convert.FromHexString(update.Sha256)))
                throw new InvalidDataException();
            using var process = await Task.Run(() => Process.Start(new ProcessStartInfo(installer, "/SYSBOXUPDATE")
            {
                UseShellExecute = true,
                WorkingDirectory = folder
            }), deadline.Token) ?? throw new IOException();
            launched = true;
            App.Desktop.Exit();
        }
        catch (Exception error) when (error is HttpRequestException or IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or OperationCanceledException)
        {
            if (!updateLifetime.IsCancellationRequested)
                ShowUpdateStatus("更新未启动", "下载校验或安装程序启动未完成。请重试。", InfoBarSeverity.Warning, install: true);
        }
        finally
        {
            installingUpdate = false;
            if (!launched)
            {
                try { File.Delete(installer); Directory.Delete(folder); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private void ShowUpdateStatus(string title, string message, InfoBarSeverity severity, bool install = false)
    {
        UpdateBanner.Title = title;
        UpdateBanner.Message = message;
        UpdateBanner.Severity = severity;
        InstallUpdateButton.Visibility = install ? Visibility.Visible : Visibility.Collapsed;
        UpdateBanner.IsOpen = true;
    }

    private static bool IsRegisteredInstallation()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\AbsSystemBox.JohnsonTang2019");
            return key?.GetValue("InstallLocation") is string folder && Path.IsPathFullyQualified(folder) &&
                string.Equals(Path.Combine(folder, "app", "SysBox.App.exe"), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (error is IOException or System.Security.SecurityException or UnauthorizedAccessException)
        { return false; }
    }
}
