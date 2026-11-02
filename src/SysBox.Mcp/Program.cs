// SPDX-License-Identifier: GPL-3.0-or-later
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using SysBox.Abstractions;
using SysBox.Transport;

using var componentActivity = new Mutex(false, "Local\\AbsSystemBox.Agent");

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Services.AddSingleton<RpcClient>();
builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
await builder.Build().RunAsync();

[McpServerToolType]
public sealed class SysBoxTools(RpcClient client)
{
    private async Task<string> Call(string method, object args, CancellationToken token)
    {
        try
        {
            return (await client.CallAsync<JsonElement>(method, args, token)).GetRawText();
        }
        catch (SysBoxOperationException e) { return JsonSerializer.Serialize(new { error = e.Error }, Protocol.Json); }
    }
    [McpServerTool(Name = "sysbox_status"), Description("只读：查询当前用户后台状态、能力及清理是否启用。没有扫描或清理副作用。")]
    public Task<string> Status(CancellationToken token) => Call("status", new { }, token);
    [McpServerTool(Name = "memory_status"), Description("只读：真实系统物理和提交内存，不执行清理。")]
    public Task<string> Memory(CancellationToken token) => Call("memory.status", new { }, token);
    [McpServerTool(Name = "memory_processes"), Description("只读：最多 200 个可访问进程的内存快照。")]
    public Task<string> Processes(CancellationToken token) => Call("memory.processes", new { }, token);
    [McpServerTool(Name = "file_search"), Description("只读：搜索用户在桌面批准的目录索引；query 最多 2048 字符，limit 1–100，超时返回错误。不会读取文件正文或新建扫描。")]
    public Task<string> Search(string query, int limit = 50, CancellationToken token = default)
    {
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit));
        return Call("search", new SearchPageRequest(query, Limit: limit), token);
    }
    [McpServerTool(Name = "index_status"), Description("只读：列出已有索引范围和覆盖状态，不启动扫描。")]
    public Task<string> Index(CancellationToken token) => Call("index.status", new { }, token);
    [McpServerTool(Name = "storage_children"), Description("只读：已有大小快照的下一层，最多 100 项；无快照时返回明确错误，不自动扫描。")]
    public Task<string> Storage(string path, int limit = 50, CancellationToken token = default)
    {
        if (limit is < 1 or > 100)
            throw new ArgumentOutOfRangeException(nameof(limit));
        return Call("storage.children", new
        {
            path,
            sort = 0,
            offset = 0,
            limit
        }, token);
    }
    [McpServerTool(Name = "cleanup_status"), Description("只读：查询清理设置与最多 128 条审计。工具不能启用、批准或执行清理。")]
    public Task<string> Cleanup(CancellationToken token) => Call("cleanup.status", new { }, token);
    [McpServerTool(Name = "rules_read"), Description("只读：读取当前有效 YAML、revision 和哈希，便于生成基于当前版本的草案；不返回全部历史 YAML。")]
    public async Task<string> ReadRules(CancellationToken token)
    {
        var state = await client.CallAsync<RuleState>("rules.read", new
        {
        }, token);
        return JsonSerializer.Serialize(state.Active, Protocol.Json);
    }
    [McpServerTool(Name = "rules_history"), Description("只读：最近 20 个规则版本摘要和待审提案标识，不改变规则，不返回历史 YAML。")]
    public async Task<string> RuleHistory(CancellationToken token)
    {
        var state = await client.CallAsync<RuleState>("rules.read", new
        {
        }, token);
        return JsonSerializer.Serialize(new
        {
            activeRevision = state.Active.Revision,
            versions = state.Versions.TakeLast(20).Select(v => new { v.Revision, v.Sha256, v.CreatedUtc, v.Summary }),
            pending = state.Proposals.Where(p => p.Status == "Pending").Select(p => new { p.Id, p.Sha256, p.ExpiresUtc, p.Status })
        }, Protocol.Json);
    }
    [McpServerTool(Name = "rules_validate"), Description("只读校验：检查最多 64 KiB 的规则 YAML，不改变生效规则。")]
    public Task<string> Validate(string yaml, CancellationToken token) => Call("rules.validate", new { yaml }, token);
    [McpServerTool(Name = "rules_simulate"), Description("只读模拟：使用有界真实历史模拟最多 64 KiB YAML，实际执行始终为 0。")]
    public async Task<string> Simulate(string yaml, CancellationToken token)
    {
        var observation = await client.CallAsync<Observation>("rules.observation", new
        {
        }, token);
        return await Call("rules.simulate", new
        {
            yaml,
            samples = observation.Samples
        }, token);
    }
    [McpServerTool(Name = "rules_propose"), Description("有限写入：创建 15 分钟待审提案，不应用规则；使用生效 revision/hash 检测并发冲突，必须由本机桌面确认。不能批准清理。")]
    public async Task<string> Propose(string yaml, string reason, CancellationToken token)
    {
        var state = await client.CallAsync<RuleState>("rules.read", new
        {
        }, token);
        var observation = await client.CallAsync<Observation>("rules.observation", new
        {
        }, token);
        return await Call("rules.propose", new
        {
            yaml,
            reason,
            revision = state.Active.Revision,
            hash = state.Active.Sha256,
            samples = observation.Samples
        }, token);
    }
}
