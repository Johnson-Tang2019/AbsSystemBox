// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SysBox.Native;
using SysBox.NtfsBroker;
using System.Security.Principal;

using var identity = WindowsIdentity.GetCurrent();
if (identity.User?.Value != "S-1-5-18" || !string.Equals(Environment.ProcessPath, BrokerPolicy.ServiceImage, StringComparison.OrdinalIgnoreCase))
    throw new UnauthorizedAccessException("只读卷服务只能从受保护的安装位置由服务控制管理器启动。");
BrokerPolicy.VerifyProtected(BrokerPolicy.ServiceImage);
var policy = BrokerPolicy.Load();
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { Args = args, ContentRootPath = AppContext.BaseDirectory });
builder.Services.AddWindowsService(options => options.ServiceName = "AbsSystemBox.Ntfs");
builder.Logging.ClearProviders();
builder.Logging.AddEventLog(options => options.SourceName = "AbsSystemBox.Ntfs");
builder.Services.AddSingleton(new BrokerServer(policy, WindowsNtfsVolumeReader.OpenAuthorizedVolume));
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();

sealed class Worker(BrokerServer server) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken token) => server.RunAsync(token);
}
