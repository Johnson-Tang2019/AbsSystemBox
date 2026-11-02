# CLI 与 MCP

后台为当前用户的 `SysBox.CoreHost.exe`，不要求 UI 常开或安装外部搜索工具。
首次请求按固定路径启动后台；空闲两分钟退出。索引只能读取桌面已选范围，Agent 不能批准新范围。
本地用户管道有显式 SID ACL、网络 SID 拒绝、客户端/服务端 PID、SID、会话和映像路径校验。
映像检查是本机进程来源检查，不是对同一用户可修改程序文件的密码学隔离；没有管理员代理。

安装后的 CLI 位于 `app\cli\sysbox.exe`。PowerShell 示例：

```powershell
$sysbox = "$env:LOCALAPPDATA\Programs\AbsSystemBox\app\cli\sysbox.exe"
& $sysbox status --json
& $sysbox memory status --json
& $sysbox memory processes --json
& $sysbox search 'ext:pdf' --limit 20 --json
& $sysbox index status --json
& $sysbox storage children 'D:\已索引目录' --limit 20 --json
& $sysbox storage analyze 'D:\已索引目录' --json
& $sysbox rules validate '.\proposal.yaml' --json
& $sysbox rules simulate '.\proposal.yaml' --json
& $sysbox rules propose '.\proposal.yaml' --reason '调整通知阈值' --json
& $sysbox rules history --json
& $sysbox rules read --json
& $sysbox cleanup status --json
```

输出始终为单个 JSON，`--json` 可显式指定。外层 `{apiVersion:"1.0",ok:true,data:...}`，
失败 `{apiVersion:"1.0",ok:false,error:{code,message,details,retryable}}`。
内存端口内部保留 `{value,error}` DTO。退出码：0 成功、2 参数、3 服务/授权错误、5 取消/超时。
输出至多 1 MiB，命令最长 120 秒，请求最长 15 秒。进程列表固定前 200 项，搜索/目录 limit 1–200。
Ctrl+C 请求取消；分析支持 `index job <id>` / `index cancel <id>`。
扫描任务 30 秒无轮询自动取消，保留上次已发布快照。

官方 C# MCP SDK 的 stdio 服务位于 `app\mcp\SysBox.Mcp.exe`，客户端启动该绝对路径、参数留空。
stdout 仅协议消息，日志写 stderr。配置示意（请替换用户名/安装位置）：

```json
{"mcpServers":{"sysbox":{"command":"C:\\Users\\USER\\AppData\\Local\\Programs\\AbsSystemBox\\app\\mcp\\SysBox.Mcp.exe","args":[]}}}
```

工具：`sysbox_status`、`memory_status`、`memory_processes`、`file_search`、`index_status`、
`storage_children`、`cleanup_status`、`rules_read`、`rules_history`、`rules_validate`、`rules_simulate`、`rules_propose`。
当前共 12 个工具；rules_read 返回当前有效 YAML/版本/hash，rules_history 返回最近 20 个版本摘要。
MCP 搜索/目录最多 100 项，规则 YAML 最多 64 KiB，模拟使用后台真实有界采样；实际执行始终为 0。
服务错误在工具 JSON 中以 `error` 返回；参数错误由 SDK 返回工具错误。

提案只写入待审记录，15 分钟到期；桌面读取不可变提案后人工确认。
revision/hash 冲突拒绝覆盖；`approved=true`、`administrator=true` 等字段不能授权任何操作。
无批准、回滚批准、清理启用、清理执行、任意命令、文件删除工具。
`ACCESS_DENIED`/`SCOPE_APPROVAL_REQUIRED` 表示需要桌面操作，不应循环重试绕过。

新增集成脚本已完成标准 MCP 客户端握手/工具列举/真实调用；未修改 Codex 的用户级 MCP 配置，
不将协议测试冒充 Codex 桌面应用已连接。完整字段与错误约定见 `API.md`。

## NTFS 状态与授权边界

新增 `sysbox ntfs status` 与 `sysbox ntfs capabilities`，均只读，仍使用既有 JSON/退出码。没有服务时返回 NTFS_SERVICE_UNAVAILABLE，不自动安装、不自动扫描。

Agent 不能调用 ntfs.build/resume/stop；后台以 OS 验证的桌面进程身份授权，不接受 approved/isUi/administrator 字段授权。MCP 保持原只读/模拟/提案工具，没有新增卷级扫描工具。独立管理员授权和整卷隐私说明见 NTFS.md。
