# 桌面、用户后台与内存调度新增验证

日期：2026-11-02，Windows 开发机，.NET SDK 10.0.403。
开发版本 0.2.0-preview.1 未发布；旧安装器/本机安装保持原样。

沿用 M0–M7 已验收报告，没有运行旧 157 项测试或旧的完整 UI 流程。
`new-tests.json` 汇总 24 个新增通过用例，不是重新运行的全量套件结果。

| 新增范围 | 用例数 | 组名 |
|---|---:|---|
| 桌面配置/工作区限制 | 2 | desktop-new |
| 阈值/定期/冷却纯调度 | 4 | cleanup-schedule-new |
| 通信帧与 Agent 授权拒绝 | 2 | transport-new |
| 原生工作集实际收缩 | 1 | cleanup-native-new |
| 清理配置保存/损坏停用 | 3 | cleanup-persistence-new |
| NTFS 离线事务/回放/恢复/清缓存 | 6 | ntfs-sync-new、ntfs-recovery-new、ntfs-clear-new |
| 空方法请求隔离 | 1 | malformed-peer-new |
| 规则显示缓存 | 1 | remote-rule-display-new |
| 观察器异步缓存与取消 | 2 | remote-observation-new |
| 真实卷容量/目录覆盖计数 | 1 | volume-read-new |
| 调度循环、租约与执行前持久化 | 1 | cleanup-loop-new |

最后一项使用受控采样和记录执行器，确认无桌面租约不读取/执行、触发前先保存限流记录、关闭后停止读取。
它不对系统进程做清理。原生实际动作由专用受限权限 WorkingSetFixture 独立验证：
不足 120 秒拒绝，达到年龄后真实调用成功，工作集下降；不将其当作物理 RAM 释放量。
没有对用户日常应用进行清理实验。

真实 UI 与协议摘要：

- `desktop-flow.json`：受限桌面悬浮球真实百分比、快捷搜索空索引、清理确认取消、退出。
  早期摘要的 tray 字样已纠正：通知图标添加被拒绝，不能声称托盘通过。
- `cleanup-delta-flow.json`：受限 UI 确认 fixture-only 定期策略、保存、立即停用；系统文件选择器真实保存脱敏 JSON。
  接续失败脚本的未完成步骤，没有重跑已通过的清理流程；结束后窗口/后台退出，未记录该续接进程的退出码。
- `host-sharing-flow.json`：受限 UI 经新管道发布一个文件的任务；脚本别名问题修正后只接续 CLI 部分。
  CLI 在后台重启后读取相同持久索引/13 字节大小快照，规则 DTO 校验/模拟/提案成功，生效 revision 保持 1。
- `agent-flow.json`：标准 stdio 握手、原十个工具、真实内存/清理状态调用。
- `rule-read-flow.json`：后续只测试新增 rules_read/rules_history；当前十二个工具，当前 YAML/hash 与 CLI 一致，未重跑旧调用。

已修复：受限 GUI 的控制台子进程在进入 .NET 前失败（后台改 WinExe）；
身份查询使用最小权限句柄；后台不继承 CLI/MCP 标准句柄，避免拖延 stdout EOF。
规则观察异步有界缓存，绑定属性不在 UI 每秒同步等待管道。

未验收的边界：

- 热键注册/保存完成，实按唤起未通过验收。独立 SendInput 探针为 0/error=5，系统未接收注入。
  具体边界见 `keyboard-input.json`，没有更改系统权限设置来绕过。
- 受限令牌托盘失败时明确降级并关闭隐藏常驻；没有重启用户 Explorer 测试恢复。
- 登录启动只读状态，未为开发 EXE 写入 HKCU Run 或进行真实重新登录。
- 无物理充电笔记本、独立无 SDK 机器、多屏完整矩阵、百万文件基准或正式签名验收。
- NTFS 仅模拟接口，未创建/挂载/格式化 VHD，没有真实卷 Enum/Read、Journal 对账或特权服务验收。
- 未构建/发布新安装器、打 tag、替换历史 Release 或覆盖安装。helper 负载/占用保护待最终包装阶段验证。

构建结果见 `builds.json`，仅证明编译。原始 TRX、含本机路径的失败诊断和文件选择器过程保留本地 artifacts，不提交私有内容。
