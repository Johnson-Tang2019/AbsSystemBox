# 开发状态

更新日期：2026-10-26。当前范围：M0 工程基础已实现并完成本地验证。

## 已实现

- GPL-3.0-or-later 原文、项目授权声明和包授权记录。
- SysBox.slnx，7 个 .NET 10 类库、UnitTests 工程，无循环项目引用。
- Result/Error、VolumeInfo、FileEntry、MemorySnapshot、搜索与存储 DTO。
- 内存、搜索、存储接口与明确报告 NOT_IMPLEMENTED 的空实现。
- 参数边界、结果状态、64 位字符串身份和取消行为的测试。
- SDK 固定、NuGet 锁文件、构建/测试脚本、Windows CI 配置。
- 原始手册归档到 docs/SYSBOX_MASTER_PLAN.md；原 doc/ 中资料保留。

## 当前环境

- Windows 11 x64，OS 10.0.26200。
- Git 2.52.0.windows.1。
- 项目本地 .NET SDK 10.0.403，下载包经微软元数据 SHA-512 校验。
- 原 PATH 中仅有 .NET 运行时；本地 SDK 未加入系统 PATH。
- 发现 Visual Studio Community 2022、Community 2026（18.6.11806.211）及 Build Tools 2026。
- Windows SDK include 目录：10.0.26100.0。
- 本地 SDK 无附加 .NET workload；Visual Studio WinUI 组件尚未核实。

## 验证

- NuGet restore 已通过，精确版本见 packages.lock.json。
- `dotnet build SysBox.slnx -c Debug --no-restore`：通过，0 警告、0 错误。
- `dotnet test SysBox.slnx -c Debug --no-build --no-restore -m:1`：14 通过，0 失败、0 跳过。
- `scripts/build.ps1`、`scripts/test.ps1`：锁定依赖还原、构建和 14 项测试均通过。
- 受限执行环境的首次并行构建曾在部分输出后退出，未报告代码错误；
  单进程构建及正常进程权限下的标准并行构建均通过。脚本用单进程减少环境差异。
- GitHub CI 尚未运行；当前没有 UI、运行时或另一台机器的验收结果。

## 未实现

WinUI 3 界面、真实内存采集、规则引擎、目录索引与搜索、NTFS/USN、磁盘分析、
CoreHost、IPC、CLI/MCP、特权服务、安装包均未实现。
Core/FileIndex/Rules 当前仅为模块边界，不能作为可用功能调用。

## 下一任务

M1.1：核实 WinUI 3 开发环境，使用官方模板建立可构建的主窗口与导航。
不提前加入内存清理、全盘扫描或管理员服务。
