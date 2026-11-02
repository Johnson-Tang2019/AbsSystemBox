// SPDX-License-Identifier: GPL-3.0-or-later
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SysBox.CoreHost;
using SysBox.Transport;

using var componentActivity = new Mutex(false, "Local\\AbsSystemBox.CoreHost");

#if DEBUG
var diagnosticDirectory = Environment.GetEnvironmentVariable("SYSBOX_TEST_RULE_DIRECTORY");
if (diagnosticDirectory is not null) { Directory.CreateDirectory(diagnosticDirectory); File.WriteAllText(Path.Combine(diagnosticDirectory, "host-entered.txt"), "Entered " + Environment.ProcessId); }
#endif
try
{
    using var mutex = new Mutex(false, Protocol.PipeName + ".Instance");
    bool acquired;
    try
    {
        acquired = mutex.WaitOne(0);
    }
    catch (AbandonedMutexException) { acquired = true; }
    if (!acquired)
        return;
    try
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Services.AddHostedService<CoreServer>();
        builder.Build().Run();
    }
    finally { mutex.ReleaseMutex(); }
}
catch (Exception error)
{
#if DEBUG
    if (diagnosticDirectory is not null)
        File.WriteAllText(Path.Combine(diagnosticDirectory, "host-failed.txt"), error.ToString());
#endif
    Console.Error.WriteLine(error.GetType().Name + ": 后台无法启动。检查本机存储权限与管道设置。");
    Environment.ExitCode = 1;
}
