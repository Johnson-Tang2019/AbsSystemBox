# 当前架构（桌面入口、CoreHost 与 Agent 接口）

WinUI、CLI 与 MCP 通过 SysBox.Transport 连接用户态 CoreHost，所有扫描、数据库、搜索、大小、规则与清理业务由后台持有。
没有 TCP/HTTP 监听，也没有已部署的特权 NTFS 服务。电池为 WinRT 平台薄适配器，桌面托盘/快捷键/工作集操作在 Native。

```text
Core      -> Abstractions
Presentation -> Core, Abstractions, CommunityToolkit.Mvvm
App       -> Presentation, Core, Abstractions, Memory, Rules, Microsoft.Extensions.DependencyInjection
Transport -> Abstractions, Rules (DTO); versioned local pipe, peer verification, fixed host launcher
CoreHost  -> Transport, Core, Memory, Rules, FileIndex, Search, Storage (user-mode Generic Host)
Cli       -> Transport (bounded JSON commands)
Mcp       -> Transport, official ModelContextProtocol SDK (stdio only)
Native    -> Abstractions / kernel32.dll / shell32.dll (read-only metadata and explicit desktop shell actions)
Memory    -> Abstractions, Native
FileIndex -> Abstractions
Rules     -> Abstractions, YamlDotNet
Search    -> Abstractions, FileIndex
Storage   -> Abstractions, FileIndex
UnitTests -> Abstractions, Memory, Search, Storage, Core, Presentation, Rules
NtfsLab   -> Native, FileIndex (isolated probe only)
```

Abstractions 定义 DTO、Result/Error、内存/搜索/存储接口。
Memory 已实现 WindowsMemoryMonitor 和 WindowsProcessMemoryMonitor。
Search 实现 AST/参数化分页查询，Storage 实现共享快照的目录摘要/下一层排行；FileIndex 实现普通目录索引、SQLite 快照与大小汇总；Rules 已实现有界解析、模拟、版本事务和审批。
Core 的 DashboardClient 委托远程 IMemoryMonitor；规则配置持久化由后台 Rules 管理。
核心契约和 ViewModel 可以在非 Windows 环境测试；App 仅构建 Windows x64。

App 来自官方 `winui` 模板，再按微软指南改为 unpackaged/self-contained。
MainWindow 使用 NavigationView 和 Frame，页面包括概览、内存、文件搜索、磁盘空间、规则、诊断、设置。
ThemeService 为窗口根元素应用 System/Light/Dark；毛玻璃由 DesktopAcrylicBackdrop 提供，
粉色主题资源保存在 Themes/PinkTheme.xaml，状态控件在 Controls/。

App 的启动组合根使用依赖注入。页面只解析 ViewModel、绑定状态和处理界面导航；
Presentation/DashboardViewModel 位于独立纯 .NET 类库，以便对取消和异步竞争进行测试。
Core/IDashboardClient 通过 IMemoryMonitor 调用远程后台；以下页面生命周期延续 M2 的有界模型。
普通模式通过 WindowsMemoryApi 调用原生只读 API；Debug --demo 才注入三种模拟客户端/采集器。
Release 通过条件编译排除所有模拟实现。

页面离开、Unloaded 或窗口关闭时取消读取并递增请求代数，丢弃不合作客户端的迟到结果。
Frame 清空同级导航的 BackStack。七个固定页面各缓存一个视图，避免反复重建行模板和界面资源；上限为七个页面。
概览进入时读取一次，内存页拥有自己的 5 秒采样循环。
循环、取消源和 60 点历史属于该页 ViewModel；离开时取消并使迟到结果失效，M2 循环仅属于该页；M3 新增的独立规则观察器见下文。
内存页离开时清空数据并停止任务；有界视图保持固定的 ViewModel 绑定，重新进入只启动采样，不反复建立整页绑定。
进程表使用固定的 ObservableCollection 和最多 200 个可复用行模型，相同读数不触发重复绑定更新。
行槽位复用时原子替换整个不可变进程身份记录，离开/失败时清除记录，不保留旧路径或 PID 身份。
设置页的 ActualThemeChanged 订阅在 Unloaded 时解除。

Native 使用顺序布局结构体和 SafeProcessHandle。每个进程只用一个 QUERY_LIMITED_INFORMATION 句柄
读取工作集、私有提交、路径和启动时间；不请求 VM_READ、终止/调整工作集权限或 SeDebugPrivilege。
API 返回的进程集合有界、枚举有上限，进程失败不终止整个批次。

CoreHost、CLI、MCP 已建立。可选特权 Service 尚未建立。
UI 不引用原生扫描代码。后续特权组件须独立审查，不能转发任意命令。

规则持久化、审计与审批已实现；SQLite 普通目录索引已实现，通用日志尚未实现。路径、数据结构及目标架构详见实施手册，
不得把规划图理解为当前运行结构。

tests/Windows 中的令牌探针和受限权限启动器仅由开发验收脚本加载，不编译进入应用或开放给 IPC。

## M3 规则与电池

Abstractions 定义规则 DTO、只读采样序列和 IRuleService；Presentation/RulesViewModel 编辑与显示报告。
Rules 中 RuleValidator 通过有界 YAML 事件流构造 JSON 树，按嵌入 Schema 和安全语义验证。
RuleSimulator/RuleEngine 不依赖 WinUI 或原生 API，不执行清理。RuleWorkspace 独占锁及原子事务保存有效 YAML、版本、提案和审计。
Agent 传输层只开放校验/模拟/提案；后台根据系统验证的进程身份限制审批。独立 IRuleReview 由 UI 远程适配器调用。
规则页 ContentDialog 显示不可变提案并决定批准/拒绝；取消不应用。回滚也走提案/审批并递增版本。

RuleObserver 在窗口存活期间读取系统内存，保存最长 30 分钟/1024 点的轻量历史，不枚举进程。
这与 M2 内存页的 5 秒系统/进程循环独立；停止内存页不等于禁用通知规则。
单调时钟计算持续时间，UTC 用于展示和跨重启的持久化通知限流；退出取消观察。
仅应用内通知，默认配置始终禁止危险动作，详见 RULES.md。

IBatteryMonitor/BatteryReading 属于 Abstractions。App/Services/WindowsBatteryMonitor 只是 Windows 平台薄适配器，
复用既有 C#/WinRT 的 Battery.AggregateBattery.GetReport，无需在纯 .NET 库加入 Windows 投影。
Core/BatteryEstimate 负责功率/时长边界，Presentation/BatteryViewModel 处理可见性、5 秒轮询和取消。
两个页面使用可复用 BatteryCard；仅 Charging 显示，页面离开停止读取。Debug 模拟显式隔离，Release 不编译模拟电池类型。


## 用户安装与版本

packaging/installer.nsi 生成当前用户 NSIS 安装器，固定产品身份，HKCU 保存版本与安装位置，开始菜单入口稳定。
Directory.Build.props 是统一版本来源。App 用会话互斥体限制重复实例，安装器在运行中拒绝更新；不终止其他进程。
scripts/build-installer.ps1 从干净提交发布 x64、自包含输出，明确补入 WinUI XBF/PRI，携带独立许可证、生成 payload 清单及源码/校验附件。
程序与 %LOCALAPPDATA%\SysBox 用户规则分离。安装器暂存并切换程序目录，按有界清单卸载，不递归删除未知文件；详见 INSTALL.md。

## M4/M5 文件模块

App 注入远程 IFileIndexService/IIndexedSearchService/IStorageBrowser，不持有 IndexDatabase；Presentation 不依赖 SQLite 或 Win32。
FileIndex 管理 schema/连接/单写者/generation，Search 仅打开可信的只读数据库连接，公共业务接口不接受 SQL。
查询在读事务中固定活动快照并按摘要拒绝跨代分页，UI 集合每页替换、有界保留，离开取消并屏蔽迟到结果。
ShellFileActions 的 PIDL 用 SafeHandle 释放，只由本机明确操作调用。具体口径与边界见 INDEXING.md、SEARCH.md。

M6 实验接口 INtfsVolumeReader 与解析器尚未进入 App 组合根。NtfsLab 只查询 Journal 元数据，不能把它视为生产 NTFS 索引或安全服务；剩余工作见 NTFS_LAB.md。

## 普通目录大小

IFileSizeReader 属于 Abstractions，Native/WindowsFileSizeReader 实现属性句柄读取；FileIndex 仅使用端口，UI 不遍历文件系统。
Migration 2/FolderTotalsBuilder 负责大小来源、后序汇总和事务发布。IndexedStorageBrowser 在单读事务返回 StorageView，StorageViewModel 管理页面生命周期与有界导航历史。
TreemapLayout 是纯几何算法，StorageTreemap 仅绘制有界按钮并发送导航事件，不执行扫描。口径与剩余任务见 STORAGE.md。

## 桌面与清理

DesktopController 持有主题/悬浮球/快捷搜索/关闭策略；DesktopPreferenceStore 原子保存偏好。
Native DesktopShell 独立 STA 消息循环拥有 HWND、图标、菜单和热键 SafeHandle，Explorer TaskbarCreated 后重建图标。
托盘拒绝时保留悬浮球与快捷搜索并禁用关闭隐藏，防止无法恢复窗口。快捷键检查全屏前台窗口，不抢占游戏焦点。
悬浮球只读内存、每 5 秒更新、可拖动并限制工作区；不包含一键危险清理。
每 30 秒桌面续期，后台退出撤销租约；无轮询任务 30 秒取消，后台空闲两分钟退出。
后台固定路径启动不继承 CLI/MCP 标准句柄，避免子进程延迟 CLI stdout EOF。

CleanupSchedule 为阈值/定期/冷却纯状态机，CleanupSupervisor 管理持久化和活动桌面租约，UserWorkingSetTrimmer 选择白名单候选。
WorkingSetAccess 使用最小权限 SafeProcessHandle，再检查 PID/启动时间/用户/会话/提升权限/映像/前台后调用原生工作集收缩。
仅桌面确认配置可启用；Agent 布尔参数不能授权。默认关闭、审计失败停用；与 YAML 的通知/提案执行分离，详见 CLEANUP.md。

## NTFS 生产路径（2026-11-02）

App 设置页 -> 用户态 CoreHost -> NtfsLiveIndex / BrokerClient -> 受保护 LocalSystem NtfsService -> 固定 Native 只读操作。服务不加载用户插件、不接受任意命令，也不承担用户数据库写入。Broker 单独项目供 CoreHost 与服务共享传输/授权契约；UI 只调用用户态 RPC。

NtfsLiveIndex 使用共享 IndexDatabase 单写者、SQLite staging 与 NtfsPublisher；初始全量发布和增量批次的目录项/祖先汇总/revision/checkpoint 事务提交。Schema 3 迁移前备份，WAL 查询固定物理 generation 与独立 revision。普通模式成功替换同范围会撤销其旧 NTFS 跟踪。物理身份作为 BLOB，硬链接名称仍适配已有逐目录项 surrogate 行，去重统计按该身份分组。

默认不安装服务、不选择卷。管理员工具配置 SID/映像/卷 GUID；桌面再次确认才建立索引。没有活动桌面租约时不续接；完全退出取消任务。缺口禁止自动全量重扫。原生权限、取消预算、服务更新/回滚及未验收场景见 NTFS.md。

## 0.3 界面与一次性管理员清理

概览由可取消的页面生命周期循环读取用户后台内存/索引/卷能力，不扫描文件；内存页保留后台自动采样，工作集/私有提交排序在截断 200 条之前完成。电池 DTO 增加可空设计容量，健康度由满充/设计容量计算；无电池与不可用区分。

MemoryCleanupPanel 从设置页移到内存页。自动进程清理仍由用户 CoreHost 执行；九类系统清理由独立 SysBox.CleanupHelper 一次性提升，NTFS 服务保持只读。UI 创建随机 nonce 管道，显式 SID/System ACL、Network 拒绝，校验 helper PID/映像；helper 校验同用户/会话及真实 UI 映像，等待原生默认“否”的管理员确认，再调用固定枚举的 native API。没有任意命令、设备控制码或 Agent 授权字段；父进程退出/超时取消后续步骤，互斥量阻止运行中更新。

悬浮窗使用 Win32 subclass 修正最小尺寸与非客户区，直接设置 HWND 边界并转交 SafeHandle 管理的圆形/条形区域，避免 AppWindow.ResizeClient 对旧边框的估算。WinUI 绘制现有粉色主题进度环；64 DIP 球体、14x56 贴边条、鼠标物理坐标拖动及 DPI 变化重排。

0.3.0-preview.2：自动清理的进程选择显式区分历史白名单与保护/排除模式，缺失字段保留旧范围，UI 保存并确认后迁移。PCL 组合只映射既有七项枚举，不引入另一套特权调用。磁盘页面和索引状态在 Presentation 格式化为十进制 GB/MB/KB/B；数据库与 RPC 继续传原始字节，未知分配量和部分覆盖语义不变。
