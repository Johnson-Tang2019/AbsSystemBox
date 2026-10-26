# 当前架构（M1）

当前有 7 个核心类库、一个独立的 Presentation 类库、WinUI 桌面应用和一个测试工程。
只运行用户态桌面进程，无后台宿主、服务、IPC 或网络监听。

```text
Core      -> Abstractions
Presentation -> Core, Abstractions, CommunityToolkit.Mvvm
App       -> Presentation, Core, Abstractions, Memory, Microsoft.Extensions.DependencyInjection
Memory    -> Abstractions
FileIndex -> Abstractions
Rules     -> Abstractions
Search    -> Abstractions, FileIndex
Storage   -> Abstractions, FileIndex
UnitTests -> Abstractions, Memory, Search, Storage, Core, Presentation
```

Abstractions 定义 DTO、Result/Error、内存/搜索/存储接口。
Memory、Search、Storage 提供名称明确的 Unavailable 实现，返回或抛出
`NOT_IMPLEMENTED`，不生成示例系统数据。Core、FileIndex、Rules 当前是空模块边界。
核心契约和 ViewModel 可以在非 Windows 环境测试；App 仅构建 Windows x64。

App 来自官方 `winui` 模板，再按微软指南改为 unpackaged/self-contained。
MainWindow 使用 NavigationView 和 Frame，页面包括概览、内存、文件搜索、磁盘空间、规则、诊断、设置。
ThemeService 为窗口根元素应用 System/Light/Dark；毛玻璃由 DesktopAcrylicBackdrop 提供，
粉色主题资源保存在 Themes/PinkTheme.xaml，状态控件在 Controls/。

App 的启动组合根使用依赖注入。页面只解析 ViewModel、绑定状态和处理界面导航；
Presentation/DashboardViewModel 位于独立纯 .NET 类库，以便对取消和异步竞争进行测试。
Core/IDashboardClient 通过 IMemoryMonitor 调用同进程服务，后续可替换为 IPC 客户端。
普通模式调用 UnavailableMemoryMonitor；Debug --demo 时才注入 DemoDashboardClient。
Release 通过条件编译排除模拟实现。

页面离开、Unloaded 或窗口关闭时取消读取并递增请求代数，丢弃不合作客户端的迟到结果。
Frame 清空同级导航的 BackStack，不缓存页面；没有定时器或常驻订阅。
设置页的 ActualThemeChanged 订阅在 Unloaded 时解除。

CoreHost、Native、CLI、MCP、可选 Service 尚未建立。
UI 不引用原生扫描代码。后续特权组件须独立审查，不能转发任意命令。

持久化、日志、规则审批与 SQLite 尚未实现。路径、数据结构及目标架构详见实施手册，
不得把规划图理解为当前运行结构。
