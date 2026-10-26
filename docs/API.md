# M0–M2 核心与界面客户端契约

当前仅有 C# 接口，无 CLI、IPC、MCP、HTTP 服务。
协议版本常量为 `1.0`；对外服务正式接入时再冻结兼容性。

| 接口 | 返回 | 当前行为 |
|---|---|---|
| IMemoryMonitor.ReadAsync(ct) | Task&lt;Result&lt;MemorySnapshot&gt;&gt; | WindowsMemoryMonitor 在后台读取真实系统内存 |
| IProcessMemoryMonitor.ReadAsync(ct) | Task&lt;Result&lt;ProcessMemorySnapshot&gt;&gt; | WindowsProcessMemoryMonitor 读取可访问进程并按工作集降序返回前 200 项 |
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

## M1 同进程客户端

`SysBox.Core.IDashboardClient` 暴露 `IsDemo` 与 `ReadAsync(CancellationToken)`，
返回 `Task<Result<MemorySnapshot>>`。`DashboardClient` 委托注入的 IMemoryMonitor。
这不是新的跨进程协议，没有增加监听端口。

`SysBox.Presentation.DashboardViewModel` 暴露 RefreshCommand、CancelCommand、IsBusy、
ErrorMessage/HasError、Status、Snapshot、MemorySummary。Deactivate 取消任务并使旧结果失效。
若客户端返回 NOT_IMPLEMENTED，只显示中文未就绪状态，不转换为 0 或示例读数。
读取异常使用通用错误提示，不将异常中的私有路径直接显示到界面。

`SysBox.App.Services.ThemeService` 为 UI 会话提供 System/Light/Dark 主题；不写用户配置。
Debug 的 `--demo` 只用于布局验收，不是正式功能或稳定 CLI 接口；Release 不启用它。

## M2 系统与进程内存

WindowsMemoryMonitor：GlobalMemoryStatusEx 提供物理总量/可用量，K32GetPerformanceInfo 的
CommitTotal/CommitLimit 乘 PageSize 后填入提交字节。只获得物理信息时 Completeness=Partial、
两个提交字段为空；物理计数无效时返回结构化失败，不能用 0 冒充成功。

ProcessMemoryEntry 包含 ProcessId、Name、ImagePath?、StartTimeUtc?、WorkingSetBytes、PrivateBytes。
IsIdentityVerified 表示取得启动时间；每次采样替换整批条目，不按 PID 复用旧名称或身份。
路径不可取时名称回退为 PID，启动时间不可取时标为未验证。

ProcessMemorySnapshot 的 Entries 为可读进程中工作集最高的至多 200 项。
EnumeratedCount、ReadableCount、AccessDeniedCount、UnavailableCount、OtherErrorCount、BudgetSkippedCount
记录覆盖率；EnumerationComplete=false 表示 PID 枚举达到 65536 上限。
可读数量含未显示的条目。被拒读、已退出、不可读、合作预算跳过都使 Completeness=Partial。
进程收集使用 3 秒合作预算，在每个进程之间检查取消/预算；不承诺中断正在执行的同步 Windows 调用。
该完整度描述内存计数读取覆盖率，可选路径/启动时间仍可能为空。

MemoryViewModel 同时调用两个只读端口；进程采集失败不抹掉成功的系统读数。
每轮结束后等待 5 秒，互不重叠。Stop 取消计时器及当前读取并保留最近数据；Deactivate 还清空页面数据。
History 为最多 60 个时间戳/物理占用百分比。采集失败时保留上次成功读数和时间，不追加历史。
Processes 集合实例稳定，最多复用 200 个行模型；行对象只是界面槽位，真实身份始终来自该行完整的 ProcessMemoryEntry。
所有界面绑定在捕获的 UI 上下文更新，原生读取在 Task.Run 后台线程完成。
