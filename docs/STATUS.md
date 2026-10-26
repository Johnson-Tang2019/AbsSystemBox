# 开发状态

更新日期：2026-10-26。当前范围：M2 只读内存与进程列表已实现，完成本地构建、测试和真实运行验证。
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

## 当前环境

- Windows 11 x64，OS 10.0.26200。
- Git 2.52.0.windows.1。
- 项目本地 .NET SDK 10.0.403，下载包经微软元数据 SHA-512 校验。
- 原 PATH 中仅有 .NET 运行时；本地 SDK 未加入系统 PATH。
- 发现 Visual Studio Community 2022、Community 2026（18.6.11806.211）及 Build Tools 2026。
- Windows SDK include 目录：10.0.26100.0。
- 官方模板包 Microsoft.WindowsAppSDK.WinUI.CSharp.Templates 0.0.7-alpha；只使用 WinUI Blank App。
- Windows App SDK 2.5.1、Windows SDK BuildTools 10.0.28000.2705（由包提供编译工具）。
- CommunityToolkit.Mvvm 8.4.2、Microsoft.Extensions.DependencyInjection 10.0.14。
- 自包含 .NET Runtime 10.0.14；开发依赖均已锁定。Visual Studio WinUI 工作负载未通过 IDE 验收。

## 验证

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
- 干净 Windows 机器、Windows 10、安装/升级/卸载、正式分发授权与性能验收。
- 独立标准用户登录。本机 UAC 已关闭，没有改动系统设置；受限非管理员子进程验证与独立账户验收分开记录。
- 任务管理器 GUI 逐项抄数与长时间压力验证；本次用独立 OS/CIM 性能计数器作数值对照。
- 最终像素截图：当前 PrintWindow 捕获缺失 DirectComposition 客户区，未作为通过证据；UI Automation 的实际启动与数据交互已通过。

## 未实现

内存清理（本阶段明确不加入）、规则引擎、目录索引与搜索、NTFS/USN、磁盘分析、
CoreHost、IPC、CLI/MCP、特权服务、安装包均未实现。
Core 仅有同进程客户端，FileIndex/Rules 仍为模块边界。

## 下一任务

M3：规则校验与模拟，保持默认只通知，不加入内存清理或管理员服务；既有环境验收缺口继续保留。
