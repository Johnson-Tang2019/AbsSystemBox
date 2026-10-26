# M0 核心契约

当前仅有 C# 接口，无 CLI、IPC、MCP、HTTP 服务。
协议版本常量为 `1.0`；对外服务正式接入时再冻结兼容性。

| 接口 | 返回 | 当前行为 |
|---|---|---|
| IMemoryMonitor.ReadAsync(ct) | Task&lt;Result&lt;MemorySnapshot&gt;&gt; | Unavailable 实现返回 NOT_IMPLEMENTED |
| IFileSearchService.SearchAsync(request, ct) | IAsyncEnumerable&lt;FileSearchHit&gt; | Unavailable 实现调用时抛 SysBoxOperationException |
| IStorageAnalyzer.AnalyzeAsync(path, ct) | Task&lt;Result&lt;FolderSummary&gt;&gt; | Unavailable 实现返回 NOT_IMPLEMENTED |

所有接口传入 CancellationToken；已取消的 token 抛 OperationCanceledException。
流式搜索运行时错误使用 SysBoxOperationException.Error 携带结构化错误。
Result 成功时只有 Value；失败时只有 Error，读取失败的 Value 会抛 InvalidOperationException。
SysBoxError 包含 Code、Message、Details、Retryable；Code/Message 不允许空白。
错误码预留 NOT_IMPLEMENTED、INVALID_ARGUMENT、ACCESS_DENIED、UNAVAILABLE。
构造契约的参数错误使用标准 .NET 参数异常；服务边界后续统一转换。

DTO 的大小单位为字节，时间为 UTC DateTimeOffset，ID 为字符串以保留 64 位精度。
FileId/ParentFileId/大小可空，表示不可获得，不能当作 0。
MemorySnapshot 中 CommitTotalBytes/CommitLimitBytes 与物理内存独立。
DataCompleteness = Unknown/Partial/Complete；不能把部分数据标成完整。
FolderSummary 当前口径为目录项的逻辑字节，未承诺占用空间或硬链接去重。
FileSearchRequest 默认最多 100 条，允许范围 1–1000；查询文本不能为空白。
序列化策略与分页协议将在对应里程碑实现；当前不承诺 JSON 字段命名。
