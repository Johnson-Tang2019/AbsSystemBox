# AbsSystemBox

面向 Windows 11 x64 的原生系统工具箱，采用 C#、.NET 10、WinUI 3，
实现内存观察、自主文件名索引与搜索、目录大小分析和受控规则。
不依赖 Everything、WizTree、Mem Reduct 或 Fluent Reduct 运行。

**当前源码：普通目录索引/搜索/空间分析、桌面悬浮球与用户后台/Agent 接口。** 已有真实内存/进程监控，以及用户选定文件夹的索引、取消/暂停、筛选搜索、分页和结果操作。
保留七页导航、粉色毛玻璃外观及浅色/深色/跟随系统主题。规则支持校验、模拟、提案、审批和版本回滚，默认只通知。阈值/定期清理需单独启用。普通目录大小分析见 [大小口径](docs/STORAGE.md)，CLI/MCP 见 [Agent 接口](docs/AGENT_API.md)。生产 NTFS 高速增量索引和可选特权服务尚未完成。
已发布安装器仍为 M3 的 preview.1，尚未把最新源码重新发布为安装更新。
已提供 Windows 11 x64 预览安装版，支持直接覆盖升级并保留用户配置。下载见 [Releases](https://github.com/Johnson-Tang2019/AbsSystemBox/releases)，步骤见 [安装与更新](docs/INSTALL.md)。进度以 [开发状态](docs/STATUS.md) 为准。

内存页进入时开始采样，每轮完成后等待 5 秒再刷新；可停止、手动刷新和取消当前读取。
离开页面停止该页的采样；M3 另有窗口生命周期的只读规则观察器，关闭窗口停止。历史最多保留 60 点，进程列表显示可读进程中工作集最高的 200 项。
权限不足、退出或不可读的进程分别计数；不申请调试权限，不提供结束进程。阈值/定期工作集清理默认关闭，需桌面白名单与确认，见 [清理说明](docs/CLEANUP.md)。
工作集包含共享页，私有提交不等于私有工作集，不能把进程列直接相加当作总内存。
具体口径与对照方法见 [内存测量说明](docs/MEMORY.md)。

## 构建与测试

安装 [微软 .NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)，
版本由 `global.json` 固定。运行时不能代替 SDK。
完整 Solution 需要 Windows x64 开发环境，建议使用 Visual Studio 2026 和 Windows SDK。
UI 基于微软官方 WinUI 模板，依赖版本已固定并保存 NuGet 锁文件。
纯核心与 ViewModel 测试不需要图形桌面或管理员权限。

```powershell
dotnet restore SysBox.slnx --locked-mode
dotnet build SysBox.slnx -c Debug --no-restore
# Only run a newly added, not-yet-accepted test group.
dotnet test tests/SysBox.UnitTests -c Debug --no-build --no-restore --filter FullyQualifiedName~NewTestGroup
```

也可运行 `./scripts/build.ps1` 和 `./scripts/test.ps1`。
脚本优先使用存在于 `.tools/dotnet/` 的本地 SDK，否则使用 PATH 中的 `dotnet`。
本地 SDK 与依赖缓存不提交，也不属于应用运行时。

## 启动桌面界面

构建完成后，在 PowerShell 中运行：

```powershell
& ./src/SysBox.App/bin/x64/Debug/net10.0-windows10.0.26100.0/win-x64/SysBox.App.exe
```

当前使用 unpackaged、自包含的 .NET 和 Windows App SDK 开发输出。
请保留整个输出目录，不能只复制一个 EXE。无需部署 MSIX、注册服务或以管理员身份启动。
在 Visual Studio 中，将 `SysBox.App` 设为启动项目，选择 x64 和 `SysBox.App (Unpackaged)` 配置。

仅 Debug 构建可显式传入 `--demo` 预览模拟数据，窗口会持续显示“示例预览”警示。
正常模式调用 Windows 只读 API；Release 忽略该参数，且不编译模拟数据实现。
主题与悬浮球设置持久保存；托盘常驻默认关闭。独立用户后台按需启动，完全退出桌面时停止。
CLI/MCP 使用同一后台，见 [Agent 接口](docs/AGENT_API.md)。仅当前用户的登录启动可在设置中单独启用。

原生界面自动化（需要可交互 Windows 桌面）：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/ui-smoke.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/ui-smoke.ps1 -Demo
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/ui-smoke.ps1 -Configuration Release -Demo
# Optional: remove administrator privileges for a test run without changing system settings.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ./scripts/ui-smoke.ps1 -RestrictedUser
```

运行报告和尽力捕获的本应用窗口图像写入 `artifacts/ui/`；PrintWindow 可能遗漏 DirectComposition 客户区，黑色图像不能当作渲染通过证据。
详见 [界面设计与验收范围](docs/UX.md)。

## 开发资料

- [实施手册](docs/SYSBOX_MASTER_PLAN.md)：原始开发规划，示例代码不是实现结果。
- [架构](docs/ARCHITECTURE.md)、[接口](docs/API.md)、[安全边界](docs/SECURITY.md)。
- [路线图](docs/ROADMAP.md)、[贡献指南](CONTRIBUTING.md)。

仓库名称为 AbsSystemBox，内部程序集沿用手册的 `SysBox.*` 名称。
只有用户主动选择目录并开始索引才读取文件元数据；开发验收只扫描隔离样本。不配置高权限服务，内存清理默认关闭。

## 许可证

Copyright (C) 2026 Johnson-Tang2019 and AbsSystemBox contributors.

本项目采用 **GPL-3.0-or-later**，即 GNU GPL 第 3 版或任何后续版本。
完整原文见 [LICENSE](LICENSE)，项目声明见 [COPYING.md](COPYING.md)，
依赖声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。本项目不提供担保。

## 规则与充电信息

规则页提供完整 YAML 编辑、字段错误、历史 dry-run、带版本/哈希的待审提案、取消/拒绝/批准和回滚。
YAML 规则继续只通知/模拟，批准仅更新规则。新增独立的桌面阈值/定期工作集清理，详见 [清理说明](docs/CLEANUP.md)；规则安全边界见 [规则说明](docs/RULES.md)。

概览和内存页仅在正在充电时显示电池净充电功率和预计充满时间，沿用粉色毛玻璃样式。
读取复用微软开源 C#/WinRT 的 BatteryReport 封装，未知数据明确显示不可用；细节和真实笔记本验收边界见 [电池说明](docs/BATTERY.md)。
