// SPDX-License-Identifier: GPL-3.0-or-later
using SysBox.Abstractions;
using SysBox.Rules;

namespace SysBox.Transport;

public sealed class RemoteMemory(RpcClient client) : IMemoryMonitor
{
    public async Task<Result<MemorySnapshot>> ReadAsync(CancellationToken token)
    {
        try
        {
            var value = await client.CallAsync<WireValue<MemorySnapshot>>("memory.status", new
            {
            }, token).ConfigureAwait(false);
            return value.Error is { } e ? Result<MemorySnapshot>.Failure(e) : Result<MemorySnapshot>.Success(value.Value!);
        }
        catch (SysBoxOperationException e) { return Result<MemorySnapshot>.Failure(e.Error); }
    }
}
public sealed class RemoteProcesses(RpcClient client) : IProcessMemoryMonitor
{
    public Task<Result<ProcessMemorySnapshot>> ReadAsync(CancellationToken token) => ReadAsync(ProcessMemorySort.WorkingSet, token);
    public async Task<Result<ProcessMemorySnapshot>> ReadAsync(ProcessMemorySort sort, CancellationToken token)
    {
        try
        {
            var value = await client.CallAsync<WireValue<ProcessMemorySnapshot>>("memory.processes", new
            {
                sort
            }, token).ConfigureAwait(false);
            return value.Error is { } e ? Result<ProcessMemorySnapshot>.Failure(e) : Result<ProcessMemorySnapshot>.Success(value.Value!);
        }
        catch (SysBoxOperationException e) { return Result<ProcessMemorySnapshot>.Failure(e.Error); }
    }
}
public sealed class RemoteSearch(RpcClient client) : IIndexedSearchService
{
    public Task<SearchPageResult> QueryAsync(SearchPageRequest request, CancellationToken token) => client.CallAsync<SearchPageResult>("search", request, token);
}
public sealed class RemoteIndex(RpcClient client) : IFileIndexService
{
    public Task<IReadOnlyList<IndexRoot>> ReadRootsAsync(CancellationToken token) => ReadRoots(token);
    private async Task<IReadOnlyList<IndexRoot>> ReadRoots(CancellationToken token) => await client.CallAsync<IndexRoot[]>("index.status", new { }, token).ConfigureAwait(false);
    public async Task<IReadOnlyList<FileEntry>> ReadEntriesAsync(string root, int limit, CancellationToken token) => await client.CallAsync<FileEntry[]>("index.entries", new { root, limit }, token).ConfigureAwait(false);
    public async Task ClearAsync(CancellationToken token) => _ = await client.CallAsync<bool>("index.clear", new { }, token).ConfigureAwait(false);
    public async Task<IndexProgress> ScanAsync(string root, IndexOptions options, IIndexControl control, IProgress<IndexProgress>? progress, CancellationToken token)
    {
        var id = await client.CallAsync<string>("index.scan", new
        {
            root,
            options
        }, token).ConfigureAwait(false);
        bool? paused = null;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (paused != control.IsPaused)
                {
                    paused = control.IsPaused;
                    await client.CallAsync<JobStatus>("index.control", new
                    {
                        id,
                        action = paused.Value ? "pause" : "resume"
                    }, token).ConfigureAwait(false);
                }
                var state = await client.CallAsync<JobStatus>("index.job", new
                {
                    id
                }, token).ConfigureAwait(false);
                if (state.Progress is { } value)
                    progress?.Report(value);
                if (state.Finished)
                {
                    if (state.Error is { } error)
                        throw new SysBoxOperationException(error);
                    return state.Progress!;
                }
                await Task.Delay(200, token).ConfigureAwait(false);
            }
        }
        catch
        {
            try
            {
                await client.CallAsync<JobStatus>("index.control", new
                {
                    id,
                    action = "cancel"
                }).ConfigureAwait(false);
            }
            catch (SysBoxOperationException) { }
            throw;
        }
    }
}
public sealed class RemoteStorage(RpcClient client, IFileIndexService indexer) : IStorageBrowser
{
    public Task<IndexProgress> AnalyzeAsync(string path, IIndexControl control, IProgress<IndexProgress>? progress, CancellationToken token) => indexer.ScanAsync(path, new(), control, progress, token);
    public Task<StorageView> ReadAsync(string path, StorageSort sort, int offset, int limit, CancellationToken token, string? scopeRoot = null, long? expectedGeneration = null)
        => client.CallAsync<StorageView>("storage.children", new
        {
            path,
            sort,
            offset,
            limit,
            scopeRoot,
            expectedGeneration
        }, token);
}
public sealed class RemoteRules(RpcClient client) : IRuleService, IRuleReview
{
    private T Call<T>(string method, object args)
    {
        try
        {
            return client.Call<T>(method, args);
        }
        catch (SysBoxOperationException e) { throw new RuleException(e.Error.Code, e.Error.Message); }
    }
    public RuleValidation Validate(string yaml) => Call<RuleValidation>("rules.validate", new { yaml });
    public RuleState Read() => Call<RuleState>("rules.read", new { });
    public RuleReport Simulate(string yaml, IReadOnlyList<RuleSample> samples) => Call<RuleReport>("rules.simulate", new { yaml, samples });
    public RuleProposal Propose(string yaml, long expectedRevision, string expectedHash, string reason, IReadOnlyList<RuleSample> samples) => Call<RuleProposal>("rules.propose", new { yaml, revision = expectedRevision, hash = expectedHash, reason, samples });
    public RuleVersion Approve(string proposalId, string exactHash) => Call<RuleVersion>("rules.approve", new { id = proposalId, hash = exactHash });
    public void Reject(string proposalId, string exactHash) => Call<bool>("rules.reject", new { id = proposalId, hash = exactHash });
    public RuleProposal ProposeRollback(long targetRevision, long expectedRevision, string expectedHash, IReadOnlyList<RuleSample> samples) => Call<RuleProposal>("rules.rollback", new { target = targetRevision, revision = expectedRevision, hash = expectedHash, samples });
}
