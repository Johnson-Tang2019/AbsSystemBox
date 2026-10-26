# AbsSystemBox

面向 Windows 11 x64 的原生系统工具箱，计划采用 C#、.NET 10、WinUI 3，
实现内存观察、自主文件名索引与搜索、目录大小分析和受控规则。
不依赖 Everything、WizTree、Mem Reduct 或 Fluent Reduct 运行。

**当前阶段：M0 工程基础。尚无桌面应用、安装器或可用的系统工具功能。**
后续功能以 [开发状态](docs/STATUS.md) 为准。

## 构建与测试

安装 [微软 .NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)，
版本由 `global.json` 固定。运行时不能代替 SDK。当前核心工程不需要管理员权限、
Visual Studio 或 WinUI 工作负载；M1 UI 开发另需相应 Windows 开发工具。

```powershell
dotnet restore SysBox.slnx --locked-mode
dotnet build SysBox.slnx -c Debug --no-restore
dotnet test SysBox.slnx -c Debug --no-build --no-restore
```

也可运行 `./scripts/build.ps1` 和 `./scripts/test.ps1`。
脚本优先使用存在于 `.tools/dotnet/` 的本地 SDK，否则使用 PATH 中的 `dotnet`。
本地 SDK 与依赖缓存不提交，也不属于应用运行时。

## 开发资料

- [实施手册](docs/SYSBOX_MASTER_PLAN.md)：原始开发规划，示例代码不是实现结果。
- [架构](docs/ARCHITECTURE.md)、[接口](docs/API.md)、[安全边界](docs/SECURITY.md)。
- [路线图](docs/ROADMAP.md)、[贡献指南](CONTRIBUTING.md)。

仓库名称为 AbsSystemBox，内部程序集沿用手册的 `SysBox.*` 名称。
本阶段不扫描用户文件、不配置规则或后台服务。

## 许可证

Copyright (C) 2026 Johnson-Tang2019 and AbsSystemBox contributors.

本项目采用 **GPL-3.0-or-later**，即 GNU GPL 第 3 版或任何后续版本。
完整原文见 [LICENSE](LICENSE)，项目声明见 [COPYING.md](COPYING.md)，
依赖声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。本项目不提供担保。
