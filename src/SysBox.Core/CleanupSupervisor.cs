// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text.Json;
using SysBox.Abstractions;
namespace SysBox.Core;

public sealed class CleanupSupervisor
{
    private sealed record Store(CleanupPolicy Policy, CleanupAttempt[] Audit, DateTimeOffset[] Runs);
    private readonly string path; private readonly object gate = new(); private readonly IMemoryMonitor memory; private readonly IWorkingSetTrimmer trimmer; private readonly Func<bool> canExecute;
    private Store state = new(new(), [], []); private CleanupSchedule schedule = new(); private long scheduleOrigin = Stopwatch.GetTimestamp(); private string status = "清理未启用。";
    public CleanupSupervisor(string directory, IMemoryMonitor memory, IWorkingSetTrimmer trimmer, Func<bool>? canExecute = null)
    {
        path = Path.Combine(directory, "cleanup.json");
        this.memory = memory;
        this.trimmer = trimmer;
        this.canExecute = canExecute ?? (() => true);
        try
        {
            if (File.Exists(path))
            {
                if (new FileInfo(path).Length > 262144)
                    throw new IOException();
                state = JsonSerializer.Deserialize<Store>(File.ReadAllText(path)) ?? throw new IOException();
                if (state.Policy is null || state.Audit is null || state.Runs is null || state.Audit.Length > 128 || state.Runs.Length > 24 || state.Audit.Any(a => a is null))
                    throw new IOException();
                CleanupSchedule.Validate(state.Policy);
                if (state.Policy.Enabled)
                    status = "已恢复桌面确认的清理设置，等待触发条件。";
            }
        }
        catch { state = new(new(), [], []); status = "清理配置损坏，已停用；原文件保留。"; }
    }
    public CleanupState Read()
    {
        lock (gate)
            return new(state.Policy, state.Policy.Enabled && !canExecute() ? "没有活动桌面会话，自动清理不会执行。" : status, state.Audit.ToArray());
    }
    public CleanupState Configure(CleanupPolicy policy)
    {
        CleanupSchedule.Validate(policy);
        policy = policy with
        {
            AllowedProcesses = policy.AllowedProcesses?.Select(p => p.ToLowerInvariant()).Distinct().ToArray()
        };
        lock (gate)
        {
            var next = state with
            {
                Policy = policy
            };
            Save(next);
            state = next;
            schedule = new();
            scheduleOrigin = Stopwatch.GetTimestamp();
            status = policy.Enabled ? "已启用，等待持续压力或定时条件；仅处理白名单后台进程。" : "清理已停用。";
            return Read();
        }
    }
    private void Save(Store value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(file, value);
            file.Flush(true);
        }
        File.Move(temporary, path, true);
    }
    public async Task RunAsync(CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(5000, token);
                CleanupPolicy policy;
                lock (gate)
                    policy = state.Policy;
                if (!policy.Enabled || !canExecute())
                    continue;
                var reading = await memory.ReadAsync(token);
                if (!reading.IsSuccess || reading.Value.TotalPhysicalBytes == 0)
                    continue;
                var usage = 100.0 * (reading.Value.TotalPhysicalBytes - reading.Value.AvailablePhysicalBytes) / reading.Value.TotalPhysicalBytes;
                lock (gate)
                {
                    if (!ReferenceEquals(policy, state.Policy) || !policy.Enabled || !canExecute())
                        continue;
                    var now = DateTimeOffset.UtcNow;
                    var recent = state.Runs.Where(t => now - t < TimeSpan.FromHours(1)).ToArray();
                    if (recent.Length >= 6 || state.Runs.LastOrDefault() is var last && last != default && now - last < TimeSpan.FromSeconds(policy.CooldownSeconds))
                        continue;
                    if (!schedule.ShouldRun(policy, Stopwatch.GetElapsedTime(scheduleOrigin), usage))
                        continue;
                    try
                    {
                        var next = state with
                        {
                            Runs = state.Runs.Append(now).TakeLast(24).ToArray()
                        };
                        Save(next);
                        state = next;
                    }
                    catch { state = state with { Policy = policy with { Enabled = false } }; status = "审计不可写，清理已停用。"; continue; }
                    // Configuration and execution share this gate so disable cannot race a queued cleanup.
                    IReadOnlyList<CleanupAttempt> attempts;
                    try
                    {
                        attempts = trimmer.TrimAllowed(policy, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch { state = state with { Policy = policy with { Enabled = false } }; status = "执行异常，后续清理已停用。"; try { Save(state); } catch { } continue; }
                    var updated = state with
                    {
                        Audit = state.Audit.Concat(attempts).TakeLast(128).ToArray()
                    };
                    try
                    {
                        Save(updated);
                        state = updated;
                        status = $"{now.ToLocalTime():HH:mm:ss} · 检查 {attempts.Count} 项，成功 {attempts.Count(a => a.Success)}；冷却保护已生效。";
                    }
                    catch { state = updated with { Policy = policy with { Enabled = false } }; status = "清理后审计写入失败，后续动作已停用；运行前记录已保存。"; }
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}
