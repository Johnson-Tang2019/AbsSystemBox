# 第三方依赖与授权记录（M3）

版本来自实际还原的 NuGet 锁文件及 SDK 隐式运行时包。项目原创源码为 GPL-3.0-or-later，
下列组件保留其独立授权，不能将 Windows App SDK 的二进制包一概标为 MIT。
当前只提交源码和授权材料，没有发布应用二进制包或安装器。

M3 新增 YamlDotNet 18.1.0（MIT），仅使用低层 YAML 事件解析器，不反序列化任意运行时类型。Native 仅调用 Windows 系统的只读 kernel32 API，不复制或分发系统 DLL。

实际 Release 开发输出的第三方文件、SHA-256 和匹配的上游包路径见 [runtime-files.json](docs/runtime-files.json)。
该清单不包含本项目生成的程序集、XAML 字节码或模板图标；用于授权核对，不是正式发布包清单。

| 项目 | 精确版本 | 许可依据 | 官方来源 | 用途与分发范围 |
|---|---|---|---|---|
| YamlDotNet | 18.1.0 | MIT（[存档](licenses/third-party/YamlDotNet-18.1.0/LICENSE.txt)） | [上游](https://github.com/aaubry/YamlDotNet/tree/748334a8fa7c227740018b284b71ad95cc6b7fc7) | 有界 YAML 事件解析；YamlDotNet.dll |
| CommunityToolkit.Mvvm | 8.4.2 | MIT（[存档](licenses/third-party/CommunityToolkit.Mvvm-8.4.2/License.md)） | [上游](https://github.com/CommunityToolkit/dotnet) | ViewModel 通知和命令；CommunityToolkit.Mvvm.dll |
| Microsoft.CodeCoverage | 17.14.1 | MIT（[存档](licenses/third-party/Microsoft.CodeCoverage-17.14.1/ThirdPartyNotices.txt)） | [上游](https://github.com/microsoft/vstest) | 仅测试基础设施，不随应用分发 |
| Microsoft.Extensions.DependencyInjection | 10.0.14 | MIT（[存档](licenses/third-party/Microsoft.Extensions.DependencyInjection-10.0.14/THIRD-PARTY-NOTICES.TXT)） | [上游](https://github.com/dotnet/dotnet) | 应用直接/传递依赖；随构建输出使用的对应程序集 |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.14 | MIT（[存档](licenses/third-party/Microsoft.Extensions.DependencyInjection.Abstractions-10.0.14/THIRD-PARTY-NOTICES.TXT)） | [上游](https://github.com/dotnet/dotnet) | 应用直接/传递依赖；随构建输出使用的对应程序集 |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT | [上游](https://github.com/microsoft/vstest) | 仅测试基础设施，不随应用分发 |
| microsoft.netcore.app.runtime.win-x64 | 10.0.14 | MIT（[存档](licenses/third-party/microsoft.netcore.app.runtime.win-x64-10.0.14/LICENSE.TXT)） | [上游](https://github.com/dotnet/dotnet) | .NET 自包含运行时；System.*、宿主和本机运行时文件 |
| Microsoft.TestPlatform.ObjectModel | 17.14.1 | MIT | [上游](https://github.com/microsoft/vstest) | 仅测试基础设施，不随应用分发 |
| Microsoft.TestPlatform.TestHost | 17.14.1 | MIT（[存档](licenses/third-party/Microsoft.TestPlatform.TestHost-17.14.1/ThirdPartyNotices.txt)） | [上游](https://github.com/microsoft/vstest) | 仅测试基础设施，不随应用分发 |
| Microsoft.Web.WebView2 | 1.0.3719.77 | 包内许可原文（[存档](licenses/third-party/Microsoft.Web.WebView2-1.0.3719.77/LICENSE.txt)） | [上游](https://aka.ms/webview) | WinUI 传递引用的 SDK/加载器；未使用 WebView2 控件，不附带浏览器 Runtime |
| Microsoft.Windows.AI.MachineLearning | 2.1.74 | 包内许可原文（[存档](licenses/third-party/Microsoft.Windows.AI.MachineLearning-2.1.74/license.txt)） | [上游](https://github.com/microsoft/windowsappsdk) | 应用直接/传递依赖；随构建输出使用的对应程序集 |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2705 | [上游条款](https://aka.ms/WinSDKLicenseURL) | [上游](https://aka.ms/WinSDKProjectURL) | 编译/资源工具；不分发工具本体，SDK 投影按生成输出使用 |
| Microsoft.Windows.SDK.BuildTools.MSIX | 1.7.251221100 | 包内许可原文（[存档](licenses/third-party/Microsoft.Windows.SDK.BuildTools.MSIX-1.7.251221100/NOTICE.txt)） | [上游](https://aka.ms/WinSDKProjectURL) | 编译/资源工具；不分发工具本体，SDK 投影按生成输出使用 |
| microsoft.windows.sdk.net.ref | 10.0.26100.57 | [上游条款](https://aka.ms/WinSDKLicenseURL) | [上游](https://aka.ms/WinSDKProjectURL) | 编译/资源工具；不分发工具本体，SDK 投影按生成输出使用 |
| Microsoft.WindowsAppSDK | 2.5.1 | 包内许可原文（[存档](licenses/third-party/Microsoft.WindowsAppSDK-2.5.1/license.txt)） | [上游](https://github.com/microsoft/windowsappsdk) | WinUI/Windows App SDK；包构建目标放入输出目录的运行组件 |
| Microsoft.WindowsAppSDK.AI | 2.5.5 | 包内许可原文（[存档](licenses/third-party/Microsoft.WindowsAppSDK.AI-2.5.5/license.txt)） | [上游](https://github.com/microsoft/windowsappsdk) | WinUI/Windows App SDK；包构建目标放入输出目录的运行组件 |
| Microsoft.WindowsAppSDK.Base | 2.0.4 | 包内许可原文（[存档](licenses/third-party/Microsoft.WindowsAppSDK.Base-2.0.4/license.txt)） | [上游](https://github.com/microsoft/windowsappsdk) | WinUI/Windows App SDK；包构建目标放入输出目录的运行组件 |
| Microsoft.WindowsAppSDK.DWrite | 2.1.0 | 包内许可原文（[存档](licenses/third-party/Microsoft.WindowsAppSDK.DWrite-2.1.0/license.txt)） | [上游](https://aka.ms/WindowsAppSDK) | WinUI/Windows App SDK；包构建目标放入输出目录的运行组件 |
| Microsoft.WindowsAppSDK.Foundation | 2.3.12 | 包内许可原文（[存档](licenses/third-party/Microsoft.WindowsAppSDK.Foundation-2.3.12/license.txt)） | [上游](https://aka.ms/windowsappsdk) | WinUI/Windows App SDK；包构建目标放入输出目录的运行组件 |
| Microsoft.WindowsAppSDK.InteractiveExperiences | 2.1.9 | 包内许可原文（[存档](licenses/third-party/Microsoft.WindowsAppSDK.InteractiveExperiences-2.1.9/license.txt)） | [上游](https://aka.ms/windowsappsdk) | WinUI/Windows App SDK；包构建目标放入输出目录的运行组件 |
| Microsoft.WindowsAppSDK.ML | 2.1.94 | 包内许可原文（[存档](licenses/third-party/Microsoft.WindowsAppSDK.ML-2.1.94/license.txt)） | [上游](https://github.com/microsoft/windowsml) | WinUI/Windows App SDK；包构建目标放入输出目录的运行组件 |
| Microsoft.WindowsAppSDK.Runtime | 2.5.1 | 包内许可原文（[存档](licenses/third-party/Microsoft.WindowsAppSDK.Runtime-2.5.1/license.txt)） | [上游](https://github.com/microsoft/windowsappsdk) | WinUI/Windows App SDK；包构建目标放入输出目录的运行组件 |
| Microsoft.WindowsAppSDK.Search | 2.5.5 | 包内许可原文（[存档](licenses/third-party/Microsoft.WindowsAppSDK.Search-2.5.5/license.txt)） | [上游](https://github.com/microsoft/windowsappsdk) | WinUI/Windows App SDK；包构建目标放入输出目录的运行组件 |
| Microsoft.WindowsAppSDK.Widgets | 2.0.5 | 包内许可原文（[存档](licenses/third-party/Microsoft.WindowsAppSDK.Widgets-2.0.5/license.txt)） | [上游](https://github.com/microsoft/windowsappsdk) | WinUI/Windows App SDK；包构建目标放入输出目录的运行组件 |
| Microsoft.WindowsAppSDK.WinUI | 2.3.9 | 包内许可原文（[存档](licenses/third-party/Microsoft.WindowsAppSDK.WinUI-2.3.9/license.txt)） | [上游](https://github.com/microsoft/windowsappsdk) | WinUI/Windows App SDK；包构建目标放入输出目录的运行组件 |
| Newtonsoft.Json | 13.0.3 | MIT（[存档](licenses/third-party/Newtonsoft.Json-13.0.3/LICENSE.md)） | [上游](https://github.com/JamesNK/Newtonsoft.Json) | 仅测试基础设施，不随应用分发 |
| System.Numerics.Tensors | 9.0.0 | MIT（[存档](licenses/third-party/System.Numerics.Tensors-9.0.0/LICENSE.TXT)） | [上游](https://github.com/dotnet/runtime) | 应用直接/传递依赖；随构建输出使用的对应程序集 |
| xunit | 2.9.3 | Apache-2.0 | [上游](https://github.com/xunit/xunit) | 仅测试基础设施，不随应用分发 |
| xunit.abstractions | 2.0.3 | [Apache-2.0（上游）](https://github.com/xunit/abstractions.xunit/blob/main/license.txt) | [上游](https://github.com/xunit/xunit) | 仅测试基础设施，不随应用分发 |
| xunit.analyzers | 1.18.0 | Apache-2.0 | [上游](https://github.com/xunit/xunit.analyzers) | 仅测试基础设施，不随应用分发 |
| xunit.assert | 2.9.3 | Apache-2.0 | [上游](https://github.com/xunit/xunit) | 仅测试基础设施，不随应用分发 |
| xunit.core | 2.9.3 | Apache-2.0 | [上游](https://github.com/xunit/xunit) | 仅测试基础设施，不随应用分发 |
| xunit.extensibility.core | 2.9.3 | Apache-2.0 | [上游](https://github.com/xunit/xunit) | 仅测试基础设施，不随应用分发 |
| xunit.extensibility.execution | 2.9.3 | Apache-2.0 | [上游](https://github.com/xunit/xunit) | 仅测试基础设施，不随应用分发 |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0 | [上游](https://github.com/xunit/visualstudio.xunit) | 仅测试基础设施，不随应用分发 |

## 模板、平台与部署

- 微软官方 `Microsoft.WindowsAppSDK.WinUI.CSharp.Templates` 0.0.7-alpha 仅用于生成工程；运行依赖使用还原得到的稳定版本，不使用 Reactor。
  [模板来源](https://github.com/microsoft/WindowsAppSDK/tree/main/dev/Templates/Dotnet)。模板代码/默认图标保留上游 MIT 许可，见 licenses/WindowsAppSDK-source-MIT.txt。
- .NET SDK 10.0.403 是开发工具，不提交、不随应用分发；.NET Runtime 10.0.14 通过自包含输出使用。
  SDK 原始许可和第三方通知见 licenses/dotnet/。
- Windows App SDK 各包附带 Microsoft Software License Terms 及第三方 NOTICE；原文逐包归档，不能用开源仓库的 MIT 替代二进制包条款。
- 当前采用 x64 unpackaged、.NET 与 Windows App SDK 自包含开发输出。不是安装器，也不是已完成分发授权审查的正式发布包。
- 发布阶段须复核实际输出、Microsoft redistributable 条件、GPL 义务及其相容性；本次不更改项目 GPL 授权，也不新增链接例外。
- WebView2 的投影程序集和加载器来自 WinUI 的传递依赖；代码中没有 WebView2 控件，不加载网页或要求 Edge WebView2 Runtime。
- 未复制 Everything、WizTree、Mem Reduct 或 Fluent Reduct 的代码或素材。

发布时须随实际包保留 LICENSE、COPYING.md、本清单和适用的 licenses 原文；新增依赖后更新锁文件并重新核对。

电池读取复用已记录的 Microsoft.Windows.SDK.NET.Ref 10.0.26100.57 中的 Microsoft.Windows.SDK.NET.dll / WinRT.Runtime.dll。
上游 [C#/WinRT 源码](https://github.com/microsoft/CsWinRT) 为 MIT；实际 SDK 包的分发条款仍以上表为准。未新增电池依赖或复制第三方电池工具代码。
