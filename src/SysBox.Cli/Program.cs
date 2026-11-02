// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using SysBox.Abstractions;
using SysBox.Transport;

using var componentActivity = new Mutex(false, "Local\\AbsSystemBox.Agent");

using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(120));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
var client = new RpcClient();
var words = args.ToList();
words.Remove("--json");
try
{
    string Option(string name, string fallback)
    {
        var index = words.IndexOf(name);
        if (index < 0)
            return fallback;
        if (index + 1 >= words.Count)
            throw new ArgumentException("选项缺少值：" + name);
        var value = words[index + 1];
        words.RemoveRange(index, 2);
        return value;
    }
    var limit = int.Parse(Option("--limit", "50"));
    if (limit is < 1 or > 200)
        throw new ArgumentException("limit 必须在 1–200 之间。");
    var reason = Option("--reason", "由 CLI 提交，等待桌面审批。");
    async Task<T> Call<T>(string method, object value) => await client.CallAsync<T>(method, value, cancellation.Token);
    string Yaml()
    {
        if (words.Count < 3)
            throw new ArgumentException("请指定 YAML 文件。");
        var file = new FileInfo(words[2]);
        using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 65536)
            throw new ArgumentException("YAML 文件超过 64 KiB。");
        using var text = new StreamReader(stream);
        return text.ReadToEnd();
    }
    object result;
    switch (string.Join(' ', words.Take(2)))
    {
        case "memory status":
            result = await Call<JsonElement>("memory.status", new
            {
            });
            break;
        case "memory processes":
            result = await Call<JsonElement>("memory.processes", new
            {
            });
            break;
        case "index status":
            result = await Call<JsonElement>("index.status", new
            {
            });
            break;
        case "index job":
            result = await Call<JsonElement>("index.job", new
            {
                id = words.ElementAtOrDefault(2) ?? throw new ArgumentException("请指定任务 ID。")
            });
            break;
        case "index cancel":
            result = await Call<JsonElement>("index.control", new
            {
                id = words.ElementAtOrDefault(2) ?? throw new ArgumentException(),
                action = "cancel"
            });
            break;
        case "cleanup status":
            result = await Call<JsonElement>("cleanup.status", new
            {
            });
            break;
        case "ntfs status":
            result = await Call<JsonElement>("ntfs.status", new
            {
            });
            break;
        case "ntfs capabilities":
            result = await Call<JsonElement>("ntfs.capabilities", new
            {
            });
            break;
        case "storage children":
            result = await Call<JsonElement>("storage.children", new
            {
                path = words.ElementAtOrDefault(2) ?? throw new ArgumentException("请指定目录。"),
                sort = 0,
                offset = 0,
                limit
            });
            break;
        case "storage analyze":
            {
                var id = await Call<string>("index.scan", new
                {
                    root = words.ElementAtOrDefault(2) ?? throw new ArgumentException("请指定已有授权目录。")
                });
                try
                {
                    while (true)
                    {
                        var state = await Call<JobStatus>("index.job", new
                        {
                            id
                        });
                        if (state.Error is { } error)
                            throw new SysBoxOperationException(error);
                        if (state.Finished)
                        {
                            result = state;
                            break;
                        }
                        await Task.Delay(250, cancellation.Token);
                    }
                }
                catch { try { await client.CallAsync<JobStatus>("index.control", new { id, action = "cancel" }); } catch { } throw; }
                break;
            }
        case "rules validate":
            result = await Call<JsonElement>("rules.validate", new
            {
                yaml = Yaml()
            });
            break;
        case "rules simulate":
            {
                var observation = await Call<Observation>("rules.observation", new
                {
                });
                result = await Call<JsonElement>("rules.simulate", new
                {
                    yaml = Yaml(),
                    samples = observation.Samples
                });
                break;
            }
        case "rules propose":
            {
                var state = await Call<RuleState>("rules.read", new
                {
                });
                var observation = await Call<Observation>("rules.observation", new
                {
                });
                result = await Call<JsonElement>("rules.propose", new
                {
                    yaml = Yaml(),
                    revision = state.Active.Revision,
                    hash = state.Active.Sha256,
                    reason,
                    samples = observation.Samples
                });
                break;
            }
        case "rules read":
            result = (await Call<RuleState>("rules.read", new
            {
            })).Active;
            break;
        case "rules history":
            {
                var state = await Call<RuleState>("rules.read", new
                {
                });
                result = new
                {
                    activeRevision = state.Active.Revision,
                    versions = state.Versions.TakeLast(Math.Min(limit, 100)).Select(v => new { v.Revision, v.Sha256, v.CreatedUtc, v.Summary }),
                    pending = state.Proposals.Where(p => p.Status == "Pending").Select(p => new { p.Id, p.Sha256, p.ExpiresUtc, p.Status })
                };
                break;
            }
        default:
            if (words.FirstOrDefault() == "status")
                result = await Call<JsonElement>("status", new
                {
                });
            else if (words.FirstOrDefault() == "diagnostics")
                result = await Call<JsonElement>("diagnostics", new
                {
                });
            else if (words.FirstOrDefault() == "search")
                result = await Call<JsonElement>("search", new SearchPageRequest(string.Join(' ', words.Skip(1)), Limit: limit));
            else
                throw new ArgumentException("用法：sysbox status | memory status/processes | search 查询 | index status/job/cancel | storage children/analyze 目录 | rules validate/simulate/propose 文件 | rules history | cleanup status。审批与启用清理只在桌面界面操作。");
            break;
    }
    if (result is JsonElement element && element.ValueKind == JsonValueKind.Object && element.TryGetProperty("error", out var nestedError) && nestedError.ValueKind == JsonValueKind.Object)
        throw new SysBoxOperationException(nestedError.Deserialize<SysBoxError>(Protocol.Json)!);
    var json = JsonSerializer.Serialize(new
    {
        apiVersion = Protocol.Version,
        ok = true,
        data = result
    }, Protocol.Json);
    if (System.Text.Encoding.UTF8.GetByteCount(json) > 1048576)
        throw new SysBoxOperationException(new("OUTPUT_LIMIT", "输出超过 1 MiB，请减少结果数量。"));
    Console.WriteLine(json);
    return 0;
}
catch (Exception error)
{
    var failure = error switch
    {
        SysBoxOperationException e => e.Error,
        OperationCanceledException => new SysBoxError("CANCELLED", "命令取消或超时。"),
        _ => new SysBoxError("INVALID_ARGUMENT", error.Message)
    };
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        apiVersion = Protocol.Version,
        ok = false,
        error = failure
    }, Protocol.Json));
    return failure.Code == "CANCELLED" ? 5 : failure.Code == "INVALID_ARGUMENT" ? 2 : 3;
}
