# 第三方依赖与授权记录

当前为 M0，尚无应用发布包。7 个产品类库没有第三方 NuGet 依赖。
下表为测试工程锁文件中直接及传递依赖，版本和许可证取自实际下载的包元数据。
这些包用于构建/测试，不随未来应用分发；若另行分发测试产物，必须保留包内许可与通知文件。

| 项目 | 精确版本 | 许可证 | 官方源码/项目 | 用途 | 应用随包文件 |
|---|---|---|---|---|---|
| Microsoft.CodeCoverage | 17.14.1 | MIT | [上游](https://github.com/microsoft/vstest) | 测试基础设施 | 无 |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT | [上游](https://github.com/microsoft/vstest) | 测试基础设施 | 无 |
| Microsoft.TestPlatform.ObjectModel | 17.14.1 | MIT | [上游](https://github.com/microsoft/vstest) | 测试基础设施 | 无 |
| Microsoft.TestPlatform.TestHost | 17.14.1 | MIT | [上游](https://github.com/microsoft/vstest) | 测试基础设施 | 无 |
| Newtonsoft.Json | 13.0.3 | MIT | [上游](https://github.com/JamesNK/Newtonsoft.Json) | 测试基础设施 | 无 |
| xunit | 2.9.3 | Apache-2.0 | [上游](https://github.com/xunit/xunit) | 测试基础设施 | 无 |
| xunit.abstractions | 2.0.3 | Apache-2.0（上游核实） | [上游](https://github.com/xunit/abstractions.xunit/blob/main/license.txt) | 测试基础设施 | 无 |
| xunit.analyzers | 1.18.0 | Apache-2.0 | [上游](https://github.com/xunit/xunit.analyzers) | 测试基础设施 | 无 |
| xunit.assert | 2.9.3 | Apache-2.0 | [上游](https://github.com/xunit/xunit) | 测试基础设施 | 无 |
| xunit.core | 2.9.3 | Apache-2.0 | [上游](https://github.com/xunit/xunit) | 测试基础设施 | 无 |
| xunit.extensibility.core | 2.9.3 | Apache-2.0 | [上游](https://github.com/xunit/xunit) | 测试基础设施 | 无 |
| xunit.extensibility.execution | 2.9.3 | Apache-2.0 | [上游](https://github.com/xunit/xunit) | 测试基础设施 | 无 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 | [上游](https://github.com/xunit/visualstudio.xunit) | 测试基础设施 | 无 |

## SDK 和平台

- .NET SDK 10.0.403：仅开发工具，本地 `.tools/dotnet/` 不提交、不随应用分发。
  [官方源码与 MIT 许可](https://github.com/dotnet/sdk)，SDK 自带的第三方声明随原下载包保留。
- 当前仅调用 .NET 基础库，运行时未打包；未来 self-contained 发布需保留实际运行时的许可证和第三方通知。
- Windows / Win32 是系统平台组件，目前没有复制或分发系统 DLL。
- 尚未引入 Windows App SDK、WinUI、SQLite、MVVM、YAML 或 MCP 包，后续引入时记录实际版本及部署条件。

## 独立实现

未复制 Everything、WizTree、Mem Reduct 或 Fluent Reduct 的源码、图标或样式。
实施手册中的链接是参考资料，不表示已集成或分发这些软件。
项目原创部分采用 GPL-3.0-or-later；依赖保留其上游许可。

每次增加或更新包后，核对锁文件、包内许可证和发布目录，更新此清单；不得仅凭本表省略上游通知。
