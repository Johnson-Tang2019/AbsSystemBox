# 核心与界面客户端契约（M5）

当前仅有 C# 接口，无 CLI、IPC、MCP、HTTP 服务。
协议版本常量为 `1.0`；对外服务正式接入时再冻结兼容性。

| 接口 | 返回 | 当前行为 |
|---|---|---|
| IMemoryMonitor.ReadAsync(ct) | Task&lt;Result&lt;MemorySnapshot&gt;&gt; | WindowsMemoryMonitor 在后台读取真实系统内存 |
| IProcessMemoryMonitor.ReadAsync(ct) | Task&lt;Result&lt;ProcessMemorySnapshot&gt;&gt; | WindowsProcessMemoryMonitor 读取可访问进程并按工作集降序返回前 200 项 |
| IFileSearchService.SearchAsync(request, ct) | IAsyncEnumerable&lt;FileSearchHit&gt; | IndexedSearchService 按有界分页流式返回；旧 Unavailable 类仅回归测试保留 |
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

## M3 内部规则端口

IRuleService.Validate(yaml) 返回 RuleValidation/RuleIssue(code,path,message)。
Simulate(yaml,samples) 返回绑定 YAML SHA-256 和 revision 的 RuleReport，包含每个决定的时间、状态、原因与通知描述，ActuallyExecuted 恒为 0。
Read() 返回 RuleState，Propose(yaml,expectedRevision,expectedHash,reason,samples) 保存待审 RuleProposal，不改变 Active。
提案带不可变 YAML/hash、基准版本/hash、原因、15 分钟到期和模拟快照。
RuleSample.Elapsed 是单调时间；MemorySnapshot.TimestampUtc 仅展示。模拟至多接收 1024 样本。

错误以 RuleException.Code 表达，包括 INVALID_RULE、CONFLICT_REVISION、HASH_MISMATCH、PROPOSAL_EXPIRED、
PROPOSAL_REVIEWED、PROPOSAL_LIMIT、STORE_IO、STORE_ACCESS、STORE_CORRUPT、STORE_LIMIT。
字段错误码包括 INPUT_SIZE、YAML_LIMIT、YAML_DEPTH、YAML_UNSAFE、YAML_DUPLICATE、YAML_SYNTAX、RULE_SCHEMA、RULE_REQUIRED、RULE_UNKNOWN、RULE_SEMANTIC。
IRuleReview.Approve/Reject/ProposeRollback 只供可信 WinUI 使用，不在 IRuleService/facade 中，不接收 approved:Boolean。
接口当前为同进程 .NET 契约，尚无 HTTP、CLI、MCP 或 IPC；不是已发布的 Agent 工具。
RULES.md 说明持久化文件、数据上限及审批边界；AGENT_API.md 说明未来 Agent 只能使用的内部工作流。

## 电池只读端口

IBatteryMonitor.ReadAsync(token) 返回 BatteryReading，State 区分 Charging/Discharging/Idle/NotPresent/Unavailable。
ChargeRateMilliwatts、FullChargeCapacityMilliwattHours、RemainingCapacityMilliwattHours 均可空，单位不可混淆。
Core/BatteryEstimate.From 只在正在充电时生成正的净功率及有效的当前速率充满估算；无数据保持 null。
不是充电器总功率，没有电池控制操作。BATTERY.md 记录上游开源投影、测量口径和硬件验收边界。

## M4 索引端口

IFileIndexService.ScanAsync(root,options,control,progress,token) 返回 IndexProgress。
IndexOptions 限制条目/深度/排除路径；IIndexControl 可 Pause/Resume，等待可取消。
ReadRootsAsync 返回最多 256 个范围与最后尝试报告、活动 generation；ReadEntriesAsync(root,limit,token) 仅返回活动快照，limit 为 1–1000。
ClearAsync 清理当前用户数据库内索引元数据，UI 调用前显示确认，不涉及源文件。
IndexRunState 分 Running/Paused/Complete/Partial/Cancelled/Failed/LimitReached。
报告分别记录文件/目录数、逻辑字节、权限错误、I/O 错误、跳过链接、排除数、开始/更新时间。
普通目录身份不是 NTFS 卷/文件身份，FileId 与 ParentFileId 为空，详见 INDEXING.md。
存储/锁错误可抛 IOException/SqliteException，UI 提供中文错误；取消按启动时机返回 Cancelled 或抛 OperationCanceledException。

## M5 搜索端口

IIndexedSearchService.QueryAsync(SearchPageRequest,token) 返回 SearchPageResult。
请求含 Query、Sort、Offset、Limit、Snapshot、IncludeHidden；默认每页 100 条，最多 200 条，offset 最大 100000。
后续页携带首次响应的 Snapshot；活动 generation 变化返回 INDEX_CHANGED。
响应含 Hits、HasMore、Snapshot、Completeness、中文 Notice 及 ElapsedMilliseconds，不执行无界 COUNT。
QUERY_INVALID 为语法/预算错误，INDEX_NOT_READY 区分无索引，QUERY_BUDGET 为 SQLite 合作时限。
查询运行错误仍通过 SysBoxOperationException 或存储异常表达；取消使用 OperationCanceledException。
SEARCH.md 是语法/单位规范。仅提供本进程接口，未开放 CLI/MCP 或任意 SQL。
