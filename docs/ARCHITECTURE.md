# 当前架构（M2）

当前有 7 个核心类库、Native 和 Presentation 类库、WinUI 桌面应用及一个测试工程。
只运行用户态桌面进程，无后台宿主、服务、IPC 或网络监听。

```text
Core      -> Abstractions
Presentation -> Core, Abstractions, CommunityToolkit.Mvvm
App       -> Presentation, Core, Abstractions, Memory, Microsoft.Extensions.DependencyInjection
Native    -> .NET / kernel32.dll (read-only)
Memory    -> Abstractions, Native
FileIndex -> Abstractions
Rules     -> Abstractions
Search    -> Abstractions, FileIndex
Storage   -> Abstractions, FileIndex
UnitTests -> Abstractions, Memory, Search, Storage, Core, Presentation
```

Abstractions 定义 DTO、Result/Error、内存/搜索/存储接口。
Memory 已实现 WindowsMemoryMonitor 和 WindowsProcessMemoryMonitor。
Search/Storage 的 Unavailable 实现仍返回或抛出 NOT_IMPLEMENTED；FileIndex/Rules 仍是空边界。
Core 提供同进程 DashboardClient，尚未实现后台宿主和配置持久化。
核心契约和 ViewModel 可以在非 Windows 环境测试；App 仅构建 Windows x64。

App 来自官方 `winui` 模板，再按微软指南改为 unpackaged/self-contained。
MainWindow 使用 NavigationView 和 Frame，页面包括概览、内存、文件搜索、磁盘空间、规则、诊断、设置。
ThemeService 为窗口根元素应用 System/Light/Dark；毛玻璃由 DesktopAcrylicBackdrop 提供，
粉色主题资源保存在 Themes/PinkTheme.xaml，状态控件在 Controls/。

App 的启动组合根使用依赖注入。页面只解析 ViewModel、绑定状态和处理界面导航；
Presentation/DashboardViewModel 位于独立纯 .NET 类库，以便对取消和异步竞争进行测试。
Core/IDashboardClient 通过 IMemoryMonitor 调用同进程服务，后续可替换为 IPC 客户端。
普通模式通过 WindowsMemoryApi 调用原生只读 API；Debug --demo 才注入三种模拟客户端/采集器。
Release 通过条件编译排除所有模拟实现。

页面离开、Unloaded 或窗口关闭时取消读取并递增请求代数，丢弃不合作客户端的迟到结果。
Frame 清空同级导航的 BackStack。七个固定页面各缓存一个视图，避免反复重建行模板和界面资源；上限为七个页面。
概览进入时读取一次，内存页拥有自己的 5 秒采样循环。
循环、取消源和 60 点历史属于该页 ViewModel；离开时取消并使迟到结果失效，没有全局监控定时器。
内存页离开时清空数据并停止任务；有界视图保持固定的 ViewModel 绑定，重新进入只启动采样，不反复建立整页绑定。
进程表使用固定的 ObservableCollection 和最多 200 个可复用行模型，相同读数不触发重复绑定更新。
行槽位复用时原子替换整个不可变进程身份记录，离开/失败时清除记录，不保留旧路径或 PID 身份。
设置页的 ActualThemeChanged 订阅在 Unloaded 时解除。

Native 使用顺序布局结构体和 SafeProcessHandle。每个进程只用一个 QUERY_LIMITED_INFORMATION 句柄
读取工作集、私有提交、路径和启动时间；不请求 VM_READ、终止/调整工作集权限或 SeDebugPrivilege。
API 返回的进程集合有界、枚举有上限，进程失败不终止整个批次。

CoreHost、CLI、MCP、可选 Service 尚未建立。
UI 不引用原生扫描代码。后续特权组件须独立审查，不能转发任意命令。

持久化、日志、规则审批与 SQLite 尚未实现。路径、数据结构及目标架构详见实施手册，
不得把规划图理解为当前运行结构。

tests/Windows 中的令牌探针和受限权限启动器仅由开发验收脚本加载，不编译进入应用或开放给 IPC。
