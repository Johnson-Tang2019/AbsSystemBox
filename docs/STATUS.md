# 开发状态

更新日期：2026-10-26。当前范围：M1 主窗口与导航已实现，完成本地构建和运行验证。
高 DPI、IDE 和独立安装验证的边界见下文及 UX.md，不将其标记为已通过。

## 已实现

- GPL-3.0-or-later 原文、项目授权声明和包授权记录。
- SysBox.slnx，7 个 .NET 10 类库、UnitTests 工程，无循环项目引用。
- Result/Error、VolumeInfo、FileEntry、MemorySnapshot、搜索与存储 DTO。
- 内存、搜索、存储接口与明确报告 NOT_IMPLEMENTED 的空实现。
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
- `scripts/test.ps1` 与 `scripts/test.ps1 -Configuration Release`：各 21 通过、0 失败、0 跳过。
- `scripts/ui-smoke.ps1`：普通 Debug 实际启动、三轮七页导航、三种主题、未就绪状态和正常退出通过。
- `scripts/ui-smoke.ps1 -Demo`：Debug 示例警示、加载、取消、导航离开、重新读取通过。
- `scripts/ui-smoke.ps1 -Configuration Release -Demo`：示例参数不起作用，正常未实现状态通过。
- 三种运行模式均正常关闭，进程以 0 退出。报告归档在 docs/validation/m1/，原始截图在 artifacts/ui/。
- 最终桌面测得 HWND DPI=96、XAML 缩放 100%，1920×1080 单显示器；另验证 900×740 物理像素窄窗口。
- 第三方 Release 输出核对：515 个文件与实际 NuGet 包按 SHA-256 匹配，无未归属第三方文件，见 runtime-files.json。
- 本次推送后的 GitHub CI 结果以 Actions 为准，本地验证不代替远端结果。

## 尚待验收的环境场景

- 150%/200% 显示缩放及多显示器移动；早期截图不作为高 DPI 通过证据。
- 系统主题实时变更（已验证应用内三种选择与跟随系统当前主题）、高对比度。
- Visual Studio F5：已提供 Unpackaged 启动配置，实际验证为直接启动构建 EXE。
- 干净 Windows 机器、Windows 10、安装/升级/卸载、正式分发授权与性能验收。

## 未实现

真实内存采集、规则引擎、目录索引与搜索、NTFS/USN、磁盘分析、
CoreHost、IPC、CLI/MCP、特权服务、安装包均未实现。
Core/FileIndex/Rules 当前仅为模块边界，不能作为可用功能调用。

## 下一任务

M2.1：真实只读 Windows 内存采集，先核对 GlobalMemoryStatusEx/GetPerformanceInfo 的口径与布局。
保持不执行内存清理、不扫描全盘、不创建管理员服务；M1 环境验收缺口继续保留。
