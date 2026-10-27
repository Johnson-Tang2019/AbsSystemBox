# 开发状态

更新日期：2026-10-27。当前完成 M4 普通目录索引、SQLite 事务快照、暂停/取消及原生界面。M3 规则及充电显示保留，默认仅通知，无实际内存清理。
高 DPI、IDE 和独立安装验证的边界见下文及 UX.md，不将其标记为已通过。

## 已实现

- GPL-3.0-or-later 原文、项目授权声明和包授权记录。
- SysBox.slnx，7 个 .NET 10 类库、UnitTests 工程，无循环项目引用。
- Result/Error、VolumeInfo、FileEntry、MemorySnapshot、搜索与存储 DTO。
- 内存、搜索、存储接口；历史空实现用于回归测试，搜索和存储仍未实现。
- 参数边界、结果状态、64 位字符串身份和取消行为的测试。
- SDK 固定、NuGet 锁文件、构建/测试脚本、Windows CI 配置。
- 原始手册归档到 docs/SYSBOX_MASTER_PLAN.md；原 doc/ 中资料保留。
- 微软官方 WinUI 模板生成 SysBox.App，按官方文档切换为 x64 unpackaged 自包含开发输出。
- 主窗口、NavigationView 和概览/内存/文件搜索/磁盘空间/规则/诊断/设置七页。
- 粉色主题、Desktop Acrylic 半透明毛玻璃、扁平卡片、深色/浅色/跟随系统。
- 统一 OperationStatus 的加载、取消、未就绪提示；页面正文换行、滚动和响应式卡片。
- IDashboardClient、独立 Presentation ViewModel 和依赖注入；导航/关闭取消任务，迟到结果失效。
- Debug --demo 显式启用并显示持续警示；Release 不编译模拟客户端。
- 原生 UI Automation 验收脚本、真实窗口截图和新增 7 项生命周期测试。
- Native 的 GlobalMemoryStatusEx、K32GetPerformanceInfo、K32EnumProcesses/GetProcessMemoryInfo 只读封装。
- SafeProcessHandle 单进程句柄读取内存、路径和启动时间，只请求 QUERY_LIMITED_INFORMATION。
- 真实物理总量/可用量/使用率、系统提交量/限制；提交失败时物理信息仍可用并标记部分数据。
- 进程工作集/私有提交列表，按工作集降序显示前 200 项；覆盖率、权限拒绝和退出/不可读独立计数。
- MemoryViewModel 的 5 秒刷新、停止/恢复、取消、60 点有界历史及迟到结果隔离。
- 粉色毛玻璃内存卡片、历史曲线和虚拟化进程表；离开清数据并停止采样，固定绑定避免重复建立整页绑定。
- 七个固定页面各缓存一个视图，避免重复重建行模板与界面资源，离开内存页仍清空数据并停止任务。
- 概览自动读取一次真实状态；Debug 示例模式替换全部三种采集器，Release 不编译模拟实现。
- Win32 布局、页大小换算、溢出、权限/退出、PID 重用、取消、停止重启与句柄释放测试。

## M3 新增实现

- YamlDotNet 有界事件解析、正式 DTO/JSON Schema、逐字段类型/语义/安全校验；恶意、超大及未知配置明确拒绝。
- all/any 条件、单调持续时间、间断重计、缺失提交量保守处理、冷却及每小时通知限制的纯函数模拟。
- 窗口生命周期的只读规则观察、最长 30 分钟/1024 点历史、应用内通知和跨重启/版本的持久化通知限流。
- 带 revision/SHA-256/模拟快照的 15 分钟待审提案、可信 UI 取消/拒绝/批准、冲突检测、原子存储和有界审计。
- 版本快照与需要审批的回滚；恢复旧内容生成新 revision，保留 YAML 格式与注释。
- 原生规则页编辑、字段错误、模拟时间/冷却/排除原因、完整旧/新配置审批、版本选择，保留粉色毛玻璃。
- 概览/内存页仅在充电时显示电池净充电功率及预计充满时间；复用已有微软开源 C#/WinRT 电池报告封装，无新增电池包或驱动。
- 不可用功率/容量明确提示；电池拔插、迟到结果、未知值及估算边界测试。诊断页可见报告状态。

## M3 验证

- 锁定依赖还原成功；最终完整 Debug/Release 构建各 0 警告、0 错误。
- 全部测试：Debug/Release 各 107 通过、0 失败、0 跳过，报告在 docs/validation/m3/。
- 最终 Debug 真实窗口：无效 YAML 不影响 v1、真实历史模拟、提案不生效、取消、拒绝、批准 v2、实际只读通知及审计均通过。
- 回滚先提案再审批，生成 v3 并恢复原阈值；导航及实际关闭后重启恢复版本/审计通过。审批测试仅写 artifacts 隔离存储。
- Debug 真实 BatteryReport=NotPresent，独立 Win32_Battery 数量为 0；概览/内存卡片隐藏且导航/退出正常。
- Debug 明确示例预览：45.0 W / 预计 60 分钟卡片在两页显示，导航和退出通过。
- 最终 Release 受限非管理员令牌：isElevated=false、isAdministrator=false；真实电池报告与默认 v1 规则观察正常。
  携带 --demo 仍读取真实数据，模拟电池类型不编译到 Release，实际退出 code 0。
- 本机无电池，未将示例功率或单元测试当作物理笔记本充电验收。仍需在充电笔记本核对实际功率/估算。
- 当前 Release 第三方输出 516 个文件均通过 SHA-256 匹配，无未归属项；YamlDotNet MIT 原文已归档。
- M2 提交的 GitHub Actions 已核实成功；M3 本次推送后的远端结果另行查看，不用本地结果替代。

规则存储/审批及边界见 RULES.md，电池数据来源/单位及估算边界见 BATTERY.md。仅通知，没有清理执行器或特权接口。

## 当前环境

- Windows 11 x64，OS 10.0.26200。
- Git 2.52.0.windows.1。
- 项目本地 .NET SDK 10.0.403，下载包经微软元数据 SHA-512 校验。
- 原 PATH 中仅有 .NET 运行时；本地 SDK 未加入系统 PATH。
- 发现 Visual Studio Community 2022、Community 2026（18.6.11806.211）及 Build Tools 2026。
- Windows SDK include 目录：10.0.26100.0。
- 官方模板包 Microsoft.WindowsAppSDK.WinUI.CSharp.Templates 0.0.7-alpha；只使用 WinUI Blank App。
- Windows App SDK 2.5.1、Windows SDK BuildTools 10.0.28000.2705（由包提供编译工具）。
- CommunityToolkit.Mvvm 8.4.2、Microsoft.Extensions.DependencyInjection 10.0.14、YamlDotNet 18.1.0（MIT）。
- 自包含 .NET Runtime 10.0.14；开发依赖均已锁定。Visual Studio WinUI 工作负载未通过 IDE 验收。

## M2 历史验证

- `scripts/build.ps1` 与 `scripts/build.ps1 -Configuration Release`：锁定依赖还原及完整构建通过，各 0 警告、0 错误。
- 全部单元/Windows 集成测试：Debug/Release 各 44 通过、0 失败、0 跳过。
- 本地 Windows 集成测试实际读取物理/提交量及自身进程，重复 100 次句柄读取无累积。
- Debug 真实界面：七页导航、三种主题、五秒刷新、停止后冻结、手动采样、进程表和退出通过。
- 受限非管理员令牌：isElevated=false、isAdministrator=false；真实系统读数和可访问进程正常，拒读条目单独统计。
- 最终 Release 携带 --demo、使用受限非管理员令牌仍读取真实数据，示例模式未启用。
- 连续采样 90 秒：采样点正常增加至 22；末 25 秒私有提交约 163.5 MiB，未强制 GC。
- 随后 30 次进入/离开内存页：私有提交首轮 164.17 MiB、末轮 168.02 MiB、空闲 167.97 MiB，期间自然回落；句柄 1364 → 1318。
- 此短测未见持续线性增长，不等同于长期压力或另一台机器验收。Debug 显式示例模式的加载/取消/导航也通过。
- M1 历史报告在 docs/validation/m1/，M2 最终报告将归档 docs/validation/m2/；实际进程名/路径截图只在本地 artifacts。
- 最终桌面测得 HWND DPI=96、XAML 缩放 100%，1920×1080 单显示器；另验证 900×740 物理像素窄窗口。
- M2 没有新增第三方包；Release 第三方输出仍为 515 个 SHA-256 匹配文件，无未归属项，见 runtime-files.json。
- 本次推送后的 GitHub CI 结果以 Actions 为准，本地验证不代替远端结果。

## 尚待验收的环境场景

- 150%/200% 显示缩放及多显示器移动；早期截图不作为高 DPI 通过证据。
- 系统主题实时变更（已验证应用内三种选择与跟随系统当前主题）、高对比度。
- Visual Studio F5：已提供 Unpackaged 启动配置，实际验证为直接启动构建 EXE。
- 干净 Windows 机器、Windows 10、跨多代版本升级、掉电/磁盘故障恢复、正式签名与长期性能验收。
- 独立标准用户登录。本机 UAC 已关闭，没有改动系统设置；受限非管理员子进程验证与独立账户验收分开记录。
- 任务管理器 GUI 逐项抄数与长时间压力验证；本次用独立 OS/CIM 性能计数器作数值对照。
- 最终像素截图：当前 PrintWindow 捕获缺失 DirectComposition 客户区，未作为通过证据；UI Automation 的实际启动与数据交互已通过。

## 未实现

内存清理（明确不加入）、完整搜索、NTFS/USN、磁盘分析、
CoreHost、IPC、CLI/MCP、特权服务仍未实现；已新增当前用户预览安装包。
Core 仍使用同进程客户端；FileIndex 已实现普通目录索引，Rules 已实现 M3 内部服务。

## 下一任务

M5：受限查询解析、分页搜索与结果操作；不加入内存清理，保留既有环境验收缺口。


## 0.1.0-preview.1 安装发布

- Directory.Build.props 统一语义版本、Windows 四段版本和程序集版本；固定产品身份识别已有目录，直接覆盖升级，无需先卸载。
- NSIS 3.12 编译器按官方 SHA-256 核验，使用 zlib；许可证及第三方条款原样附带。安装为当前用户，不申请管理员权限。
- 完整提取暂存后切换 app/app.previous，失败尝试恢复；拒绝运行中更新、并发安装及降级，同版本支持修复。
- 按清单清理程序文件，验证路径范围并跳过重解析目录；未知文件、用户规则/审批/版本快照保留。
- 修复标准 publish 遗漏本应用 XBF/PRI 的问题，构建目标及安装打包脚本均检查必需界面资源。
- 受限非管理员令牌实际通过 preview.0 → preview.1 覆盖升级、真实窗口版本显示、规则观察/内存读数、重复实例、修复、降级拦截及卸载验证。
- 两个安装版本实际退出 code 0；已有规则文件逐字节保持，未知文件保留。测试目录内旧版本的已归属废弃文件被移除。
- 最终源码仍通过 Debug/Release 各 107 项测试。安装器本地流程记录在 docs/validation/release/；独立标准账户与干净机器仍待验证。
- 预览版未签名，首次公开附件包括安装器、对应源码、文件清单与 SHA-256；发布入口为 GitHub Releases。

安装与版本维护说明见 INSTALL.md。程序版本回退与 M3 配置回滚独立；下一开发范围仍为 M4 普通目录索引。

已公开发布 [v0.1.0-preview.1](https://github.com/Johnson-Tang2019/AbsSystemBox/releases/tag/v0.1.0-preview.1)。四个远端附件 SHA-256 与本地验收文件一致。
按用户要求在本机当前用户默认目录正式安装，非管理员身份实际启动/导航/退出通过，开始菜单入口保留；593 个已安装 payload 文件哈希全部匹配发布清单。
发布源码修订 5a63a27 的 GitHub CI（含新增 publish 资源检查）已通过；本机安装汇总见 docs/validation/release/local-install.json。

## M4 普通目录索引

- DirectoryWalkIndexer 显式栈、暂停/恢复、进度、取消、重解析点跳过、排除缓存、条目/深度预算。
- SQLite Migration 1、WAL/外键、单写者、256 条批次、暂存/活动 generation 原子切换；失败与取消保留旧索引。
- 真实中文/空格/长路径和大小对账、移动/改名重扫、多范围、junction、取消、写锁与中断暂存测试。
- 原生文件夹选择器、输入路径、扫描进度、100 条预览、已有范围列表与带确认的缓存清除；粉色毛玻璃保持。
- Debug 全量构建通过；Release 应用构建通过，最终 Debug/Release 测试各 115 通过。受限非管理员真实 WinUI 扫描/重扫/确认/清缓存/导航/退出通过。
- 本阶段开发只扫描隔离样本，未扫描用户磁盘。原来安装的 preview.1 尚未升级为本次源码。
- 具体口径、可恢复性和未验收场景见 INDEXING.md；数据库目录范围身份不冒充 NTFS 卷/文件身份。
