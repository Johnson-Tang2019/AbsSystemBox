// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using SysBox.Abstractions;
using SysBox.Transport;

namespace SysBox.CoreHost;

internal sealed class ScriptSupervisor(string dataDirectory)
{
    private const int MaxScripts = 16;
    private const int MaxLogEntries = 40;
    private readonly object gate = new();
    private readonly string configuration = Path.Combine(dataDirectory, "managed-scripts.json");
    private readonly string history = Path.Combine(dataDirectory, "script-history");
    private Dictionary<Guid, Entry>? entries;
    private bool shuttingDown;

    private sealed class Entry(ScriptDefinition definition)
    {
        public ScriptDefinition Definition = definition;
        public CancellationTokenSource? Cancellation;
        public Task Worker = Task.CompletedTask;
        public string State = "Stopped";
        public int? Pid;
        public DateTimeOffset? Started;
        public DateTimeOffset? Exited;
        public int? ExitCode;
        public DateTimeOffset? RetryAt;
        public int Restarts;
        public string? Error;
        public Queue<ScriptOutput> Output { get; } = new();
    }

    private sealed record Snapshot(string State, DateTimeOffset? Started, DateTimeOffset? Exited,
        int? ExitCode, int Restarts, string? Error, ScriptOutput[] Output);

    public bool HasActiveWork
    {
        get { lock (gate) return entries?.Values.Any(entry => entry.Cancellation is not null) == true; }
    }

    public ScriptStatus[] List(bool desktop)
    {
        lock (gate)
            return Load().Values.Where(entry => desktop || entry.Definition.AllowAgent)
                .Select(entry => Status(entry, desktop)).ToArray();
    }

    public ScriptOutput[] Logs(Guid id, bool desktop)
    {
        lock (gate) return Find(id, desktop).Output.ToArray();
    }

    public ScriptStatus Get(Guid id, bool desktop)
    {
        lock (gate) return Status(Find(id, desktop), desktop);
    }

    public ScriptStatus Save(ScriptDefinition requested)
    {
        lock (gate)
        {
            EnsureAvailable();
            Validate(requested, requireFile: true);
            var items = Load();
            Entry? existing = null;
            if (requested.Id != Guid.Empty)
                existing = Find(requested.Id, true);
            else if (items.Count >= MaxScripts)
                throw Problem("SCRIPT_LIMIT", "最多可登记 16 个脚本。");
            if (existing?.Cancellation is not null)
                throw Problem("SCRIPT_RUNNING", "请先停止脚本再编辑。");

            var saved = requested with
            {
                Id = existing?.Definition.Id ?? Guid.NewGuid(),
                Name = requested.Name.Trim(),
                Path = Path.GetFullPath(requested.Path),
                Enabled = existing?.Definition.Enabled ?? true
            };
            WriteDefinitions(items.Values.Where(entry => entry != existing).Select(entry => entry.Definition).Append(saved));
            var target = existing ?? new Entry(saved);
            target.Definition = saved;
            items[saved.Id] = target;
            return Status(target, true);
        }
    }

    public bool Remove(Guid id)
    {
        lock (gate)
        {
            EnsureAvailable();
            var entry = Find(id, true);
            if (entry.Cancellation is not null)
                throw Problem("SCRIPT_RUNNING", "请先停止脚本再移除。");
            var items = Load();
            WriteDefinitions(items.Values.Where(other => other != entry).Select(other => other.Definition));
            items.Remove(id);
            try { File.Delete(HistoryPath(id)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            return true;
        }
    }

    public ScriptStatus Start(Guid id, bool desktop)
    {
        lock (gate)
        {
            EnsureAvailable();
            var entry = Find(id, desktop);
            if (entry.Cancellation is { IsCancellationRequested: true })
                throw Problem("SCRIPT_STOPPING", "脚本仍在停止，请稍后再启动。");
            if (entry.Cancellation is not null)
                return Status(entry, desktop);
            EnsureInteractiveUser();
            SetEnabled(entry, true);
            Begin(entry);
            return Status(entry, desktop);
        }
    }

    public ScriptStatus Stop(Guid id, bool desktop)
    {
        lock (gate)
        {
            EnsureAvailable();
            var entry = Find(id, desktop);
            SetEnabled(entry, false);
            if (entry.Cancellation is not null)
            {
                entry.State = "Stopping";
                entry.RetryAt = null;
                entry.Cancellation.Cancel();
            }
            else
            {
                entry.State = "Stopped";
                SaveHistory(entry);
            }
            return Status(entry, desktop);
        }
    }

    public int StartForDesktop()
    {
        lock (gate)
        {
            EnsureAvailable();
            var items = Load().Values.Where(entry => entry.Definition.Enabled &&
                entry.Definition.StartOnLogin && entry.Cancellation is null).ToArray();
            if (items.Length > 0)
                EnsureInteractiveUser();
            foreach (var entry in items)
                Begin(entry);
            return items.Length;
        }
    }

    public async Task ShutdownAsync()
    {
        Task[] running;
        lock (gate)
        {
            shuttingDown = true;
            running = entries?.Values.Where(entry => entry.Cancellation is not null).Select(entry =>
            {
                entry.Cancellation!.Cancel();
                return entry.Worker;
            }).ToArray() ?? [];
        }
        try { await Task.WhenAll(running).WaitAsync(TimeSpan.FromSeconds(6)); }
        catch (TimeoutException) { }
    }

    private static ScriptStatus Status(Entry entry, bool desktop) => new(entry.Definition.Id, entry.Definition.Name,
        entry.State, entry.Definition.Enabled, entry.Pid, entry.Started, entry.Exited, entry.ExitCode,
        entry.RetryAt, entry.Restarts, entry.Error, desktop ? entry.Definition : null);

    private Entry Find(Guid id, bool desktop)
    {
        if (!Load().TryGetValue(id, out var entry) || (!desktop && !entry.Definition.AllowAgent))
            throw Problem("SCRIPT_NOT_FOUND", "脚本不存在或尚未开放给 Agent。");
        return entry;
    }

    private void EnsureAvailable()
    {
        if (shuttingDown)
            throw Problem("SCRIPT_HOST_STOPPING", "脚本后台正在退出。");
    }

    private static void EnsureInteractiveUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        using var process = Process.GetCurrentProcess();
        if (identity.IsSystem || process.SessionId == 0)
            throw Problem("SCRIPT_REQUIRES_INTERACTIVE_USER", "脚本托管需要当前登录的 Windows 用户。");
    }

    private void SetEnabled(Entry entry, bool enabled)
    {
        if (entry.Definition.Enabled == enabled)
            return;
        var updated = entry.Definition with { Enabled = enabled };
        WriteDefinitions(Load().Values.Select(item => item == entry ? updated : item.Definition));
        entry.Definition = updated;
    }

    private void Begin(Entry entry)
    {
        var cancellation = new CancellationTokenSource();
        entry.Cancellation = cancellation;
        entry.State = "Starting";
        entry.Restarts = 0;
        entry.Error = null;
        entry.ExitCode = null;
        entry.Output.Clear();
        entry.Worker = Task.Run(() => MonitorAsync(entry, cancellation));
    }

    private async Task MonitorAsync(Entry entry, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        var delay = 2;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var attempt = Stopwatch.GetTimestamp();
                lock (gate)
                {
                    entry.State = "Starting";
                    entry.RetryAt = null;
                }
                int? exitCode = null;
                string? error = null;
                try { exitCode = await ExecuteAsync(entry, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { break; }
                catch (Exception e) when (e is IOException or Win32Exception or InvalidOperationException or SysBoxOperationException or UnauthorizedAccessException or NotSupportedException)
                {
                    error = e.Message;
                }
                lock (gate)
                {
                    entry.Pid = null;
                    entry.Exited = DateTimeOffset.UtcNow;
                    entry.ExitCode = exitCode;
                    entry.Error = error ?? (exitCode is not (null or 0) ? $"脚本以退出码 {exitCode} 结束。" : null);
                    entry.State = entry.Error is null ? "Exited" : "Failed";
                    SaveHistory(entry);
                    if (!entry.Definition.RestartOnExit || token.IsCancellationRequested)
                        return;
                    if (Stopwatch.GetElapsedTime(attempt) >= TimeSpan.FromSeconds(30))
                        delay = 2;
                    entry.State = "Retrying";
                    entry.RetryAt = DateTimeOffset.UtcNow.AddSeconds(delay);
                }
                await Task.Delay(TimeSpan.FromSeconds(delay), token);
                delay = Math.Min(delay * 2, 60);
                lock (gate) entry.Restarts++;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            lock (gate)
            {
                var wasRunning = entry.Pid is not null;
                entry.Pid = null;
                entry.RetryAt = null;
                if (token.IsCancellationRequested)
                {
                    entry.State = "Stopped";
                    if (wasRunning)
                        entry.Exited = DateTimeOffset.UtcNow;
                    SaveHistory(entry);
                }
                entry.Cancellation = null;
                cancellation.Dispose();
            }
        }
    }

    private async Task<int> ExecuteAsync(Entry entry, CancellationToken token)
    {
        var definition = entry.Definition;
        Validate(definition, requireFile: true);
        using var job = new ScriptProcessJob();
        token.ThrowIfCancellationRequested();
        using var process = ScriptChildProcess.Start(StartInfo(definition), job);
        lock (gate)
        {
            entry.Pid = process.Id;
            entry.Started = DateTimeOffset.UtcNow;
            entry.State = token.IsCancellationRequested ? "Stopping" : "Running";
        }
        using var outputLifetime = new CancellationTokenSource();
        var output = ReadOutputAsync(entry, process.Output, "stdout", outputLifetime.Token);
        var standardError = ReadOutputAsync(entry, process.Error, "stderr", outputLifetime.Token);
        try
        {
            await process.WaitAsync(token);
            job.Dispose();
            outputLifetime.CancelAfter(TimeSpan.FromSeconds(2));
            await Task.WhenAll(output, standardError);
            return process.ExitCode;
        }
        finally
        {
            job.Dispose();
            outputLifetime.Cancel();
            await Task.WhenAll(output, standardError);
            await process.WaitAsync();
        }
    }

    private async Task ReadOutputAsync(Entry entry, StreamReader reader, string stream, CancellationToken token)
    {
        var buffer = new char[768];
        try
        {
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
            {
                lock (gate)
                {
                    if (entry.Output.Count >= MaxLogEntries)
                        entry.Output.Dequeue();
                    entry.Output.Enqueue(new(DateTimeOffset.UtcNow, stream, new string(buffer, 0, count)));
                }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private static ProcessStartInfo StartInfo(ScriptDefinition definition)
    {
        var info = new ProcessStartInfo
        {
            FileName = FindInterpreter() ?? throw Problem("SCRIPT_PYTHON_MISSING", "找不到 Python，请将 python.exe 或 py.exe 加入当前用户 PATH。"),
            WorkingDirectory = Path.GetDirectoryName(definition.Path)!
        };
        info.ArgumentList.Add("-u");
        info.ArgumentList.Add("-X");
        info.ArgumentList.Add("utf8");
        info.ArgumentList.Add(definition.Path);
        foreach (var argument in definition.Arguments)
            info.ArgumentList.Add(argument);
        return info;
    }

    private static string? FindInterpreter()
    {
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var file in new[] { "python.exe", "py.exe" })
            foreach (var folder in folders)
            {
                var path = Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"'));
                if (Path.IsPathFullyQualified(path) && !path.EndsWith(@"Microsoft\WindowsApps", StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(Path.Combine(path, file)))
                    return Path.Combine(path, file);
            }
        return null;
    }

    private static void Validate(ScriptDefinition item, bool requireFile)
    {
        if (string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 80 ||
            item.Path is not { Length: > 0 and <= 1024 } || item.Path.Contains('\0') ||
            !Path.IsPathFullyQualified(item.Path) || Path.GetPathRoot(item.Path) is not { Length: 3 } ||
            !Path.GetExtension(item.Path).Equals(".py", StringComparison.OrdinalIgnoreCase) ||
            item.Arguments is null || item.Arguments.Length > 16 ||
            item.Arguments.Any(argument => argument is null || argument.Length > 512 || argument.Contains('\0')))
            throw Problem("SCRIPT_INVALID", "请填写名称和本地 .py 路径，最多 16 个参数。");
        if (requireFile && !File.Exists(item.Path))
            throw Problem("SCRIPT_FILE_MISSING", "脚本文件不存在或当前用户无法读取。");
    }

    private Dictionary<Guid, Entry> Load()
    {
        if (entries is not null)
            return entries;
        var items = new Dictionary<Guid, Entry>();
        try
        {
            var saved = Read<ScriptDefinition[]>(configuration, 1024 * 1024) ?? [];
            if (saved.Length > MaxScripts)
                throw new IOException();
            foreach (var definition in saved)
            {
                if (definition is null || definition.Id == Guid.Empty)
                    throw new IOException();
                Validate(definition, requireFile: false);
                var entry = new Entry(definition);
                ReadHistory(entry);
                if (!items.TryAdd(definition.Id, entry))
                    throw new IOException();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or SysBoxOperationException)
        {
            throw Problem("SCRIPT_CONFIG_INVALID", "无法读取脚本托管配置。原配置已保留。");
        }
        return entries = items;
    }

    private string HistoryPath(Guid id) => Path.Combine(history, id.ToString("N") + ".json");

    private void ReadHistory(Entry entry)
    {
        try
        {
            var saved = Read<Snapshot>(HistoryPath(entry.Definition.Id), 256 * 1024);
            if (saved is null)
                return;
            entry.State = entry.Definition.Enabled && saved.State is ("Failed" or "Exited") ? saved.State : "Stopped";
            entry.Started = saved.Started;
            entry.Exited = saved.Exited;
            entry.ExitCode = saved.ExitCode;
            entry.Error = saved.Error;
            entry.Restarts = saved.Restarts;
            foreach (var line in (saved.Output ?? []).TakeLast(MaxLogEntries))
                if (line?.Text is { Length: <= 768 } && line.Stream is "stdout" or "stderr")
                    entry.Output.Enqueue(line);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
    }

    private void SaveHistory(Entry entry)
    {
        try
        {
            Write(HistoryPath(entry.Definition.Id), new Snapshot(entry.State, entry.Started, entry.Exited,
                entry.ExitCode, entry.Restarts, entry.Error, entry.Output.ToArray()));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private void WriteDefinitions(IEnumerable<ScriptDefinition> items)
    {
        try { Write(configuration, items.ToArray()); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw Problem("SCRIPT_CONFIG_WRITE_FAILED", "无法保存脚本登记。");
        }
    }

    private static T? Read<T>(string path, int limit) where T : class
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > limit)
                throw new IOException();
            return JsonSerializer.Deserialize<T>(stream, Protocol.Json) ?? throw new JsonException();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return null; }
    }

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(value, Protocol.Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static SysBoxOperationException Problem(string code, string text) => new(new(code, text));
}
