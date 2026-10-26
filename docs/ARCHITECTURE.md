# 当前架构（M0）

当前仅有 7 个纯 .NET 10 类库和一个测试工程，未启动任何进程或监听端口。

```text
Core      -> Abstractions
Memory    -> Abstractions
FileIndex -> Abstractions
Rules     -> Abstractions
Search    -> Abstractions, FileIndex
Storage   -> Abstractions, FileIndex
UnitTests -> Abstractions, Memory, Search, Storage
```

Abstractions 定义 DTO、Result/Error、内存/搜索/存储接口。
Memory、Search、Storage 提供名称明确的 Unavailable 实现，返回或抛出
`NOT_IMPLEMENTED`，不生成示例系统数据。Core、FileIndex、Rules 当前是空模块边界。
核心契约可以在非 Windows 环境测试，但最终桌面产品只面向 Windows。

规划中的 App（WinUI 3）、CoreHost、Native、CLI、MCP、可选 Service 尚未建立。
M0–M3 可同进程调用接口；后续通过 Core 客户端接入用户级命名管道。
UI 不引用原生扫描代码。后续特权组件须独立审查，不能转发任意命令。

持久化、日志、规则审批与 SQLite 尚未实现。路径、数据结构及目标架构详见实施手册，
不得把规划图理解为当前运行结构。
