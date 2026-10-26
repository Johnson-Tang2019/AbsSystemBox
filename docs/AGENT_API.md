# Agent 规则工作流（M3 内部契约）

当前只有同进程 IRuleService / RuleDraftService，没有可调用的 CLI、MCP、HTTP 或 IPC。
以下描述用于未来受控客户端集成，不能据此宣称 Agent 工具已经上线。

1. Read：读取有效规则的 revision、SHA-256 和 YAML，以及有界历史版本/提案。
2. Validate：只校验；输入不超过 64 KiB，字段安全边界见 RULES.md，无有效配置副作用。
3. Simulate：最多 1024 个只读历史样本，返回逐时间决定、冷却原因和候选通知；dry-run，无清理或状态写入。
4. Propose：提交原基准 revision/hash、草案 YAML、原因和历史样本，保存 15 分钟待审提案及模拟快照。
   仅写用户规则事务存储，不应用；冲突必须重新读取并合并。
5. 人类在 WinUI 看到完整提案后取消、拒绝或批准。Agent 服务不提供审批或回滚执行端口。

禁止向 Agent 暴露 IRuleReview、可信 UI 组合根、approved Boolean 或可由 Agent 生成的确认凭证。
回滚也必须通过 WinUI 提案与审批，生成更高 revision，不直接替换历史文件。
目前的 .NET facade 只划分代码能力；未来传输层仍需独立调用方认证、审批会话绑定及访问控制。
无实际内存清理能力，不能描述成“安全清理全部内存”或预测可释放字节数。
