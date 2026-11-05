# 第三方依赖与授权记录（M3 预览版）

版本来自实际还原的 NuGet 锁文件及 SDK 隐式运行时包。项目原创源码为 GPL-3.0-or-later，
下列组件保留其独立授权，不能将 Windows App SDK 的二进制包一概标为 MIT。
新增安装器由 NSIS 3.12 生成，存根及 zlib 压缩模块按 zlib/libpng 许可使用，完整上游许可见 [NSIS COPYING](licenses/third-party/NSIS-3.12/COPYING)。
编译器来自 [官方发布](https://sourceforge.net/projects/nsis/files/NSIS%203/3.12/nsis-3.12.zip/download)，SHA-256 固定在 packaging/version.json；工具本体不随应用安装。

M3 新增 YamlDotNet 18.1.0（MIT），仅使用低层 YAML 事件解析器，不反序列化任意运行时类型。Native 仅调用 Windows 系统的只读 kernel32 API，不复制或分发系统 DLL。

实际 Release 开发输出的第三方文件、SHA-256 和匹配的上游包路径见 [runtime-files.json](docs/runtime-files.json)。
该清单不包含本项目生成的程序集、XAML 字节码或模板图标；用于授权核对，不是正式发布包清单。

| 项目 | 精确版本 | 许可依据 | 官方来源 | 用途与分发范围 |
|---|---|---|---|---|
| Microsoft.Data.Sqlite / Microsoft.Data.Sqlite.Core | 10.0.14 | MIT（[存档](licenses/third-party/Microsoft.Data.Sqlite-10.0.14/LICENSE.txt)） | [上游](https://github.com/dotnet/dotnet/tree/0427b9abbead3fa64c6655eb4290c537397131fa) | 元数据数据库；Microsoft.Data.Sqlite.dll |
| SQLitePCLRaw.core / bundle_e_sqlite3 / provider.e_sqlite3 / lib.e_sqlite3 | 2.1.13 | NuGet 元数据声明 Apache-2.0（[原文](licenses/third-party/SQLitePCLRaw-2.1.13/LICENSE.txt)） | [上游](https://github.com/ericsink/SQLitePCL.raw) | SQLite 托管桥接和本机 e_sqlite3.dll，具体输出见 runtime-files.json |
| SQLite | 由上述固定本机包提供，实际引擎版本随数据库诊断记录 | Public domain（[上游声明存档](licenses/third-party/SQLite/copyright.html)） | [上游](https://www.sqlite.org/copyright.html) | 底层数据库；不读取文件正文，不依赖数据库服务 |
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

- 新用户态后台使用 Microsoft.Extensions.Hosting 10.0.14 及 Extensions 配置/日志依赖，MIT，逐包原文与 authors/copyright 在 `licenses/third-party/*/SOURCE.json`。
- MCP 使用官方 ModelContextProtocol / ModelContextProtocol.Core 2.3.0。包元数据为 Apache-2.0；上游正在由 MIT 过渡，已按所用 commit 归档完整原始 LICENSE（同时保留适用 MIT 内容），没有将许可过渡声明删去。
- 新增运行依赖元数据汇总见 [new-runtime-dependencies.json](docs/new-runtime-dependencies.json)。原有 SQLitePCLRaw 仍使用自身 Apache-2.0 原文，与 MCP 许可分别归档。

- 微软官方 `Microsoft.WindowsAppSDK.WinUI.CSharp.Templates` 0.0.7-alpha 仅用于生成工程；运行依赖使用还原得到的稳定版本，不使用 Reactor。
  [模板来源](https://github.com/microsoft/WindowsAppSDK/tree/main/dev/Templates/Dotnet)。模板代码保留上游 MIT 许可，见 licenses/WindowsAppSDK-source-MIT.txt。
- .NET SDK 10.0.403 是开发工具，不提交、不随应用分发；.NET Runtime 10.0.14 通过自包含输出使用。
  SDK 原始许可和第三方通知见 licenses/dotnet/。
- Windows App SDK 各包附带 Microsoft Software License Terms 及第三方 NOTICE；原文逐包归档，不能用开源仓库的 MIT 替代二进制包条款。
- 当前采用 x64 unpackaged、.NET 与 Windows App SDK 自包含预览安装包。安装页展示项目与第三方许可，并原样保留适用的 licenses/ 通知。
- 预览安装器仅打包 NuGet 放入应用输出的可再分发文件，按对应组件独立条款保留和展示通知；项目 GPL 授权不变，不新增链接例外。
- WebView2 的投影程序集和加载器来自 WinUI 的传递依赖；代码中没有 WebView2 控件，不加载网页或要求 Edge WebView2 Runtime。
- 未复制 Everything、WizTree、Mem Reduct 或 Fluent Reduct 的代码或素材。

发布时须随实际包保留 LICENSE、COPYING.md、本清单和适用的 licenses 原文；新增依赖后更新锁文件并重新核对。

电池读取复用已记录的 Microsoft.Windows.SDK.NET.Ref 10.0.26100.57 中的 Microsoft.Windows.SDK.NET.dll / WinRT.Runtime.dll。
上游 [C#/WinRT 源码](https://github.com/microsoft/CsWinRT) 为 MIT；实际 SDK 包的分发条款仍以上表为准。未新增电池依赖或复制第三方电池工具代码。

## 可选 Windows 服务依赖

新增 Microsoft.Extensions.Hosting.WindowsServices / System.ServiceProcess.ServiceController 10.0.14，MIT。逐包完整许可证、原始 THIRD-PARTY-NOTICES 和 authors/copyright/repository commit 保存在 licenses/third-party 对应目录，汇总见 docs/ntfs-runtime-dependencies.json。沿用已有 Hosting/EventLog 依赖；原有开源与微软二进制独立条款不变。

当前软件图标采用用户提供的粉色工具箱图片，本地透明处理与来源记录见 docs/ASSETS.md；微软模板通知仍予保留。

图标资源重建使用开发工具 Pillow 13.0.0（MIT-CMU），其完整许可证/捆绑通知及包元数据保存在 licenses/devtools/Pillow-13.0.0。Pillow 不作为软件运行依赖，不打包其 Python 代码或动态库。

## Fluent Reduct 规则与行为适配

按用户要求参考 s3hq4y/fluent-reduct 的固定提交 64fa85fb33d9886b8dbd74ff87bd2454e33ba090，将九类内存操作目录/顺序、原生请求布局、进程保护及悬浮窗尺寸/贴边行为移植或适配为 C# / WinUI / Win32。上游 GPL-3.0-or-later，原文与来源文件列表保存在 licenses/third-party/FluentReduct-64fa85f。没有引入 Electron、koffi、Node.js 或上游二进制；原有“不复制 Fluent Reduct”说明仅描述该适配之前的历史实现。

0.3.0-preview.3 进一步直接移植该提交 `src/ui/main/ball.ts` 的状态转换以及 `src/ui/ball/ball.ts` 的拖动/点击交互。原始两份源码和 blob SHA 已存档在上述目录，C# 版本标注改动与出处；WinUI 绘制、固定合成面/命中区域及旧位置数据兼容由本项目适配。主/渲染器不作为 TypeScript 或 Electron 代码执行。

PCL 清理组合仅参考 PCL-Community/PCL-CE 2.10.4（bc10f7679307e647195ad90c6f50155a29f43445）公开展示的七种 Windows 操作，映射到现有的本项目实现。未复制其 VB 源码、结构体、权限代码或二进制；历史标签未检出明确许可文件，不将当前 dev 的 Apache-2.0 反推为旧标签授权。来源记录见 docs/references/PCL_MEMORY.md，无新增 PCL 运行或构建依赖。
