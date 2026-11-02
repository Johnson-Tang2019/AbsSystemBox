// SPDX-License-Identifier: GPL-3.0-or-later
using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using SysBox.Abstractions;
using SysBox.FileIndex;
using SysBox.Memory;
using SysBox.Native;
using SysBox.Rules;
using SysBox.Search;
using SysBox.Storage;
using SysBox.Transport;
using SysBox.Core;

namespace SysBox.CoreHost;

public sealed class CoreServer : BackgroundService
{
    private readonly IHostApplicationLifetime lifetime;
    private readonly WindowsMemoryMonitor memory = new(new WindowsMemoryApi());
    private readonly WindowsProcessMemoryMonitor processes = new(new WindowsMemoryApi());
    private readonly DirectoryWalkIndexer indexer;
    private readonly IndexedSearchService search;
    private readonly IndexedStorageBrowser storage;
    private readonly RuleWorkspace rules;
    private readonly RuleObserver observer;
    private readonly CleanupSupervisor cleanup;
    private readonly bool demo = false;
    private readonly ConcurrentDictionary<string, Job> jobs = new();
    private readonly object jobsGate = new();
    private readonly SemaphoreSlim clients = new(8, 8);
    private long lastRequest = Stopwatch.GetTimestamp();
    private long lastUi;
    private sealed class Job
    {
        public IndexControl Control { get; } = new(); public CancellationTokenSource Cancellation { get; } = new();
        public volatile IndexProgress? Progress; public volatile SysBoxError? Error; public volatile bool Finished;
        public long Touched = Stopwatch.GetTimestamp();
    }
    public CoreServer(IHostApplicationLifetime lifetime)
    {
        this.lifetime = lifetime;
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SysBox");
        var index = Path.Combine(root, "index");
#if DEBUG
        root = Environment.GetEnvironmentVariable("SYSBOX_TEST_RULE_DIRECTORY") ?? root;
        index = Environment.GetEnvironmentVariable("SYSBOX_TEST_INDEX_DIRECTORY") ?? index;
        demo = Environment.GetEnvironmentVariable("SYSBOX_DEMO_MODE") == "1";
#endif
        rules = new(root);
        var database = new IndexDatabase(index);
        indexer = new(database, new WindowsFileSizeReader());
        search = new(database);
        storage = new(database, indexer);
        observer = new(rules, memory);
        cleanup = new(root, memory, new UserWorkingSetTrimmer(processes), () => !demo && Volatile.Read(ref lastUi) != 0 && Stopwatch.GetElapsedTime(Volatile.Read(ref lastUi)) < TimeSpan.FromSeconds(90));
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        observer.Start();
        var cleanupLoop = cleanup.RunAsync(stoppingToken);
        var pending = new List<Task>();
        var idle = Task.Run(async () =>
        {
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(5000, stoppingToken);
                    foreach (var job in jobs.Values)
                        if (!job.Finished && Stopwatch.GetElapsedTime(Volatile.Read(ref job.Touched)) > TimeSpan.FromSeconds(30))
                            job.Cancellation.Cancel();
                    if (Stopwatch.GetElapsedTime(Volatile.Read(ref lastRequest)) > TimeSpan.FromMinutes(2) && jobs.Values.All(j => j.Finished))
                        lifetime.StopApplication();
                }
            }
            catch (OperationCanceledException) { }
        }, stoppingToken);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await clients.WaitAsync(stoppingToken);
                NamedPipeServerStream? pipe = null;
                try
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    var security = new PipeSecurity();
                    security.SetAccessRuleProtection(true, false);
                    security.SetOwner(identity.User!);
                    security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
                    security.AddAccessRule(new PipeAccessRule(identity.User!, PipeAccessRights.FullControl, AccessControlType.Allow));
                    pipe = NamedPipeServerStreamAcl.Create(Protocol.PipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte,
                        PipeOptions.Asynchronous, 16384, 16384, security, HandleInheritability.None);
                    await pipe.WaitForConnectionAsync(stoppingToken);
                    pending.RemoveAll(t => t.IsCompleted);
                    pending.Add(HandleAsync(pipe, stoppingToken));
                    pipe = null;
                }
                catch { pipe?.Dispose(); clients.Release(); throw; }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            foreach (var job in jobs.Values)
                job.Cancellation.Cancel();
            observer.Dispose();
            await Task.WhenAll(pending);
            await idle;
            await cleanupLoop;
        }
    }
    private async Task HandleAsync(NamedPipeServerStream pipe, CancellationToken hostToken)
    {
        await using (pipe)
        using (var requestTime = CancellationTokenSource.CreateLinkedTokenSource(hostToken))
        {
            requestTime.CancelAfter(TimeSpan.FromSeconds(15));
            try
            {
                var image = PeerIdentity.Image(pipe, false);
                var request = await Protocol.ReadAsync<RpcRequest>(pipe, requestTime.Token);
                if (request.Version != Protocol.Version || request.Method is not { Length: >= 1 and <= 64 } || request.Arguments.ValueKind != JsonValueKind.Object)
                    throw new SysBoxOperationException(new("PROTOCOL_VERSION", "请求格式或版本不受支持。"));
                Volatile.Write(ref lastRequest, Stopwatch.GetTimestamp());
                if (PeerIdentity.IsUi(image))
                    Volatile.Write(ref lastUi, Stopwatch.GetTimestamp());
                object? result;
                try
                {
                    result = await DispatchAsync(request.Method, request.Arguments, PeerIdentity.IsUi(image), requestTime.Token);
                }
                catch (Exception e)
                {
                    var error = e switch
                    {
                        SysBoxOperationException s => s.Error,
                        RuleException r => new SysBoxError(r.Code, r.Message),
                        UnauthorizedAccessException => new("ACCESS_DENIED", "此操作只接受可信桌面界面发起的确认。"),
                        OperationCanceledException => new("CANCELLED", "请求已取消。"),
                        _ => new SysBoxError("REQUEST_FAILED", "请求失败，请检查参数、权限或本地存储。")
                    };
                    await Protocol.WriteAsync(pipe, new RpcResponse(Protocol.Version, null, error), requestTime.Token);
                    return;
                }
                await Protocol.WriteAsync(pipe, new RpcResponse(Protocol.Version, JsonSerializer.SerializeToElement(result, Protocol.Json), null), requestTime.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or UnauthorizedAccessException or JsonException or SysBoxOperationException or ArgumentException or System.ComponentModel.Win32Exception)
            {
#if DEBUG
                Console.Error.WriteLine(e.Message);
#endif
            }
            finally { clients.Release(); }
        }
    }
    private static object Pack<T>(Result<T> result) where T : notnull => result.IsSuccess ? new WireValue<T>(result.Value, null) : new WireValue<T>(default, result.Error);
    private async Task<object?> DispatchAsync(string method, JsonElement args, bool ui, CancellationToken token)
    {
        string Text(string name, int maximum = 32767)
        {
            var value = args.GetProperty(name).GetString() ?? throw new ArgumentException();
            if (value.Length > maximum)
                throw new ArgumentException();
            return value;
        }
        T Data<T>() => args.Deserialize<T>(Protocol.Json) ?? throw new ArgumentException();
        void RequireUi()
        {
            if (!ui)
                throw new UnauthorizedAccessException();
        }
        switch (method)
        {
            case "status":
                return new
                {
                    apiVersion = Protocol.Version,
                    processId = Environment.ProcessId,
                    mode = "user",
                    cleanup = cleanup.Read(),
                    ntfsFastIndex = false,
                    jobs = jobs.Values.Count(j => !j.Finished)
                };
            case "diagnostics":
                {
                    using var process = Process.GetCurrentProcess();
                    using var identity = WindowsIdentity.GetCurrent();
                    return new
                    {
                        apiVersion = Protocol.Version,
                        version = typeof(CoreServer).Assembly.GetName().Version?.ToString(),
                        capturedUtc = DateTimeOffset.UtcNow,
                        operatingSystem = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                        architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
                        administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator),
                        privateBytes = process.PrivateMemorySize64,
                        handles = process.HandleCount,
                        activeJobs = jobs.Values.Count(j => !j.Finished),
                        indexedScopes = (await indexer.ReadRootsAsync(token)).Count,
                        ntfsFastIndex = false,
                        cleanupEnabled = cleanup.Read().Policy.Enabled
                    };
                }
            case "memory.status":
                return Pack(await memory.ReadAsync(token));
            case "memory.processes":
                return Pack(await processes.ReadAsync(token));
            case "cleanup.status":
                return cleanup.Read();
            case "cleanup.configure":
                RequireUi();
                if (demo)
                    throw new SysBoxOperationException(new("DEMO_READ_ONLY", "示例模式不允许启用实际清理。"));
                return cleanup.Configure(Data<CleanupPolicy>());
            case "search":
                return await search.QueryAsync(Data<SearchPageRequest>(), token);
            case "index.status":
                return await indexer.ReadRootsAsync(token);
            case "volumes":
                {
                    var capacities = await VolumeCapacityReader.ReadAsync(token);
                    try
                    {
                        var roots = await indexer.ReadRootsAsync(token);
                        return capacities.Select(v => v with { IndexedScopeCount = roots.Count(r => string.Equals(Path.GetPathRoot(r.Root), v.Root, StringComparison.OrdinalIgnoreCase)) }).ToArray();
                    }
                    catch (SysBoxOperationException) { return capacities; }
                }
            case "index.entries":
                return await indexer.ReadEntriesAsync(Text("root"), args.GetProperty("limit").GetInt32(), token);
            case "index.clear":
                RequireUi();
                await indexer.ClearAsync(token);
                return true;
            case "index.scan":
                {
                    var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Text("root")));
                    if (!ui && !(await indexer.ReadRootsAsync(token)).Any(r => r.Root == root))
                        throw new SysBoxOperationException(new("SCOPE_APPROVAL_REQUIRED", "请先在桌面界面选择并索引此目录。"));
                    var options = args.TryGetProperty("options", out var configured) ? configured.Deserialize<IndexOptions>(Protocol.Json) ?? throw new ArgumentException() : new IndexOptions();
                    string id;
                    Job job;
                    lock (jobsGate)
                    {
                        foreach (var old in jobs.Where(p => p.Value.Finished).Take(Math.Max(0, jobs.Count - 16)))
                            if (jobs.TryRemove(old.Key, out var removed))
                                removed.Cancellation.Dispose();
                        if (jobs.Count >= 32)
                            throw new SysBoxOperationException(new("JOB_LIMIT", "后台任务过多，请稍后再试。"));
                        id = Guid.NewGuid().ToString("N");
                        job = new Job();
                        jobs[id] = job;
                    }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            job.Progress = await indexer.ScanAsync(root, options, job.Control, new JobProgress(p => job.Progress = p), job.Cancellation.Token);
                        }
                        catch (OperationCanceledException) { job.Error = new("CANCELLED", "扫描已取消，旧快照保留。"); }
                        catch (Exception) { job.Error = new("SCAN_FAILED", "扫描失败，旧快照保留。"); }
                        finally { job.Finished = true; }
                    });
                    return id;
                }
            case "index.job":
            case "index.control":
                {
                    if (!jobs.TryGetValue(Text("id", 32), out var job))
                        throw new SysBoxOperationException(new("JOB_NOT_FOUND", "任务不存在或已过期。"));
                    Volatile.Write(ref job.Touched, Stopwatch.GetTimestamp());
                    if (method == "index.control")
                        switch (Text("action", 12))
                        {
                            case "pause":
                                job.Control.Pause();
                                break;
                            case "resume":
                                job.Control.Resume();
                                break;
                            case "cancel":
                                job.Cancellation.Cancel();
                                break;
                            default:
                                throw new ArgumentException();
                        }
                    return new JobStatus(Text("id", 32), job.Finished, job.Progress, job.Error);
                }
            case "storage.children":
                return await storage.ReadAsync(Text("path"), (StorageSort)args.GetProperty("sort").GetInt32(), args.GetProperty("offset").GetInt32(), args.GetProperty("limit").GetInt32(), token,
                args.TryGetProperty("scopeRoot", out var scope) && scope.ValueKind == JsonValueKind.String ? scope.GetString() : null, args.TryGetProperty("expectedGeneration", out var gen) && gen.ValueKind == JsonValueKind.Number ? gen.GetInt64() : null);
            case "rules.read":
                return rules.Read();
            case "rules.validate":
                return rules.Validate(Text("yaml", 65536));
            case "rules.simulate":
                return rules.Simulate(Text("yaml", 65536), args.GetProperty("samples").Deserialize<RuleSample[]>(Protocol.Json) ?? []);
            case "rules.propose":
                return rules.Propose(Text("yaml", 65536), args.GetProperty("revision").GetInt64(), Text("hash", 64), Text("reason", 1024), args.GetProperty("samples").Deserialize<RuleSample[]>(Protocol.Json) ?? []);
            case "rules.approve":
                RequireUi();
                return rules.Approve(Text("id", 64), Text("hash", 64));
            case "rules.reject":
                RequireUi();
                rules.Reject(Text("id", 64), Text("hash", 64));
                return true;
            case "rules.rollback":
                RequireUi();
                return rules.ProposeRollback(args.GetProperty("target").GetInt64(), args.GetProperty("revision").GetInt64(), Text("hash", 64), args.GetProperty("samples").Deserialize<RuleSample[]>(Protocol.Json) ?? []);
            case "rules.observation":
                return new Observation(observer.Status, observer.Notification, observer.Samples.ToArray());
            case "host.stop":
                RequireUi();
                _ = Task.Run(async () => { await Task.Delay(100); lifetime.StopApplication(); });
                return true;
            default:
                throw new SysBoxOperationException(new("METHOD_NOT_FOUND", "此接口不存在。"));
        }
    }
    private sealed class JobProgress(Action<IndexProgress> callback) : IProgress<IndexProgress>
    {
        public void Report(IndexProgress value) => callback(value);
    }
}
