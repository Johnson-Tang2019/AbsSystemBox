# 外设设置设计与开源调研

2026-11-19 · 只读设备识别

SysBox 主导航增加「外设」，与「水冷显示」相邻。首轮目标是识别用户手上的迈从鼠标、狼蛛键盘和飞智手柄，读取能可靠取得的设备状态，再按实机验证逐项开放板载设置。支持范围按**完整型号、硬件版本、固件、连接方式和具体功能**记录。

## 开源项目与可复用范围

下表的验证来自上游项目。SysBox 的 Windows 接入和用户手上的设备仍待实机核对。

| 品牌和型号 | 可用证据及候选来源 |
| --- | --- |
| 迈从 L7 Pro、A7 Pro | alexfrih 的 `mchose-linux`（MIT）用 L7 Pro 的 8 kHz 接收器验证了设备协议，可参考电量、DPI 档位、回报率、抬升高度和休眠。zzxzzk115 的分支（MIT）另有 A7 Pro 接收器和预设写入、回读的实测记录。其余 A5、AX5、M7 等变体按具体型号核对。见 [L7 Pro 协议](https://github.com/alexfrih/mchose-linux/blob/main/PROTOCOL.md)、[A7 Pro 实测范围](https://github.com/zzxzzk115/mchose-control-linux/blob/main/README.en.md)。 |
| 迈从 G3 V2、K7 Ultra | G3 V2 用另一套协议。OpenMHub（MIT）列出有线和 2.4G 的电量、固件及鼠标设置，鼠标拨到 2.4G 档时 USB 线可能只充电。aipno 的 `mchose-linux`（MIT）明确实测 K7 Ultra 读写，接收器为 `3837:1001`，固件为 `5.28.5.0`。见 [OpenMHub 支持列表](https://github.com/brayanspagnol/OpenMHub#supported-devices)、[K7 Ultra 实测记录](https://github.com/aipno/mchose-linux#readme)。 |
| 狼蛛原版 ANSI F75 | AULA Studio（MIT）在 Linux 有线下验证了检测、读取、灯光亮度修改与恢复。逐键 RGB、改键等已实现，仍需逐项实测。Aula-Manager（MIT）记录了 F75 2.4G 灯效、亮度和速度的实机回读。见 [AULA Studio 的验证范围](https://github.com/Dahuum/aula-studio#what-works)、[Aula-Manager 协议笔记](https://github.com/free-soldier28/Aula-Manager/blob/main/docs/PROTOCOL.md)。 |
| 狼蛛 F75 Max、F87/F99 系列 | AulaF75Driver（GPL-3.0）针对 EPOMAKER × AULA F75 Max，2.4G 实测覆盖灯光与电量，已有键位、宏和设置无法回读。F87 Pro 有 `aula-f87-rgb`（MIT）的 2.4G 板载灯效证据，有线路径待实测。F87/F99 候选 `AulaSofwareLinux` 的代码为 MIT。F99 Pro 专用项目的完整配置走 2.4G，有线只识别，复用源码前需补查授权。见 [AulaF75Driver](https://github.com/DonAsako/AulaF75Driver)、[F87 Pro 灯效项目](https://github.com/leetrat/aula-f87-rgb)、[AulaSofwareLinux](https://github.com/yhuikzdtguioaert/AulaSofwareLinux)、[F99 Pro 协议项目](https://github.com/dnviti/aula-x99-pro-driver-linux)。 |
| 狼蛛 WIN 60 HE MAX 磁轴 | `aula-win60he-tool`（MIT）记录了该型号 USB 静态和逐键灯效的实机写入、回读和恢复。快速触发等参数按功能另行验证。见 [WIN 60 HE MAX 配置项目](https://github.com/PrimeBuild-pc/aula-win60he-tool)。 |
| 飞智八爪鱼 4 / 5 | 八爪鱼 4（Apex 4）有实机验证的 Space Station for Mac（MIT），覆盖摇杆、扳机和配置档等。八爪鱼 5（Apex 5）优先参考 OpenFlydigi 的实机记录、MIT 协议代码及 CC0 协议文档，覆盖摇杆、改键、灯效和板载配置。见 [Space Station for Mac](https://github.com/uiltonlopes/flydigi-space-station-mac)、[OpenFlydigi 的支持范围](https://github.com/mkaliaha/openflydigi#supported-hardware)、[OpenFlydigi 文件授权](https://github.com/mkaliaha/openflydigi/blob/main/LICENSE)。 |
| 飞智黑武士 5 Pro | 优先参考 Windows 的 `vader5-pro-mapper`（MIT）。在标准版固件 `7.1.5.4` 记录了 2.4G 板载改键、完整回读和恢复，以及有线重连后的持久性。摇杆死区、曲线和灯效另有 OpenVader（MIT）在 macOS 有线下的实测。见 [Windows 板载改键](https://github.com/bohewu/vader5-pro-mapper#compatibility)、[OpenVader 实测范围](https://github.com/FilipVajgand/openvader#status)。 |
| 飞智黑武士 3/4 Pro、冰原狼 2 | 黑武士 3 Pro、冰原狼 2 可核对 `flydigictl`（GPL-3.0）。黑武士 4 Pro 有线固件 `6.9.5.5` 有旧协议实机读取固件及背键的记录。另有 Windows 2.4G 下的扳机震动和临时配置证据，完整板载设置仍待核对。见 [flydigictl](https://github.com/pipe01/flydigictl)、[型号与协议记录](https://github.com/uiltonlopes/flydigi-space-station-mac/blob/main/docs/adding-a-controller.md)、[Flydigi Trigger Bridge](https://github.com/Mee6ix/flydigi-trigger-bridge)。 |

飞智的输入预览、电脑端映射、板载设置是不同能力。SDL 的 Flydigi 实现可辅助核对按键、电量和体感等输入能力。要开放「保存到设备」，需有对应型号的写入和回读证据。见 [SDL Flydigi HID 驱动](https://github.com/libsdl-org/SDL/blob/main/src/joystick/hidapi/SDL_hidapi_flydigi.c)。

Windows 平台接入可参考 `vader5-pro-mapper` 和八爪鱼 5 的 `apex-triggers`。后者展示普通权限下的 HID 通信，扳机效果只存于工作内存。见 [Vader 5 Pro 的 Windows 实现](https://github.com/bohewu/vader5-pro-mapper)、[Apex Triggers](https://github.com/STRENCH0/apex-triggers)。

复用顺序为已验证的协议文档、同型号的协议实现、针对缺项的本机兼容性验证。以 C# 接入 Windows HID。采用上游代码时按实际文件核对许可证，记录提交号、来源和原文，并维护 `THIRD_PARTY_NOTICES.md`。

## 页面设计

「外设」内有「设备总览」「鼠标」「键盘」「手柄」四个子页。总览按类型列出 Windows 当前可见的设备，点击一张设备卡片打开该设备所属的详情子页。详情一次显示一台，遇到同类多台时用设备选择器切换。刷新后保留原设备选择，设备消失时显示提示，重连后如识别键一致即可恢复详情。沿用现有 WinUI 3 控件、主题和键盘焦点规则。

```text
外设                                      [刷新设备]
[设备总览]     [鼠标]     [键盘]     [手柄]

设备总览的设备卡片 → 对应子页的设备详情
设备详情             设备选择器
                    厂商、型号、识别依据、连接、VID/PID
```

| 设备 | 实机确认后优先验证 | 后续按型号开放 |
| --- | --- | --- |
| 鼠标 | 读取 DPI 档位和回报率，先开放一个 DPI 档位修改 | 改键、回报率修改、抬升高度、休眠 |
| 键盘 | 读取灯效，先开放单项亮度修改和恢复 | 颜色、灯效、逐键 RGB、改键。磁轴型号的触发行程另立适配 |
| 手柄 | 查看板载配置槽；已验证才开放一个配置参数。输入预览可显示按键、摇杆和扳机 | 背键、死区、曲线、震动、扳机。各功能按固件和模式验证 |

只读页面显示 Windows 设备身份和已接入的板载读数。鼠标包含 DPI 档位、回报率、抬升高度、休眠、防抖、当前配置组和按键映射。键盘包含灯效、亮度、速度、灯效颜色、逐键 RGB 和键位映射。没有可用数据时显示具体状态。选中 DPI 档位、物理按键或键位只改变当前查看项。

进入配置阶段后，可配设备显示当前值及未应用修改。用户点击「应用到设备」才写入，回读匹配后提示「已应用」。编辑期间切换设备时提示放弃草稿。保存到设备失败后保留草稿和原始快照，重连后重新读取。

## 实现结构

| 层 | 只读阶段的文件和职责 |
| --- | --- |
| WinUI | `SysBox.App/MainWindow.xaml`、`MainWindow.xaml.cs`、`App.xaml.cs` 和新增 `Views/PeripheralsPage.xaml(.cs)`。接入导航、依赖注入和现有页面停用流程。 |
| 页面状态 | `SysBox.Presentation/PeripheralsViewModel.cs`。处理异步刷新、子页与设备选择、错误反馈。离页取消并丢弃旧会话迟到的数据。 |
| 契约 | `SysBox.Abstractions/Peripherals.cs`。提供可取消的设备枚举接口及结果类型，包含本地设备键、类型、连接方式、型号及识别依据。 |
| Windows 与协议 | `SysBox.Native/Peripherals/`。用 SetupAPI 枚举在场 HID 集合并查询设备属性。本地按容器和接口归并，同型号多台设备保留独立身份。有线键盘身份查询按 VID/PID、USB 拓扑、厂商集合、报表长度和实际产品描述选择接口。 |
| 新功能验证 | `PeripheralsFeaturesTests` 和 `PeripheralIdentityFeaturesTests` 覆盖路由选择、刷新失败与恢复、丢弃旧结果、组合接口归并和型号证据。 |

Windows 的键鼠输入集合由系统独占。枚举阶段可使用无数据访问权限的句柄读取元数据。部分 F75、F87 Pro、F99 Pro 接收器共用 `3554:FA09`，型号和归属不明时维持待识别状态。硬件配置仅访问已匹配的厂商集合，使用 `SafeHandle` 并验证报告类型、ID、长度和响应内容。Windows 传输要证明操作可取消或有明确执行上限，再接入页面。手柄的 XInput、DInput 与 Switch 模式分别确认厂商接口，只有通用手柄信息时仅显示已知状态。见 [Aula-Manager 协议笔记](https://github.com/free-soldier28/Aula-Manager/blob/main/docs/PROTOCOL.md)、[Microsoft HID 枚举说明](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/finding-and-opening-a-hid-collection)、[Windows HID 集合访问规则](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/hid-architecture)。

外设功能在普通权限的桌面进程运行，打开软件后自动开始读取，进入页面复用成功快照，拔插后可手动刷新。写入由页面上明确的应用操作触发，和检测串行。写入前复核已选设备及会话、原配置、参数范围和写入能力。超时或断线后先重新读取状态，避免重复保存。固件、配对、恢复出厂、宏注入、虚拟手柄及对外设备控制接口留待单独评估。

## 里程碑与验收

1. **确认设备并做只读页。** 记录三台设备的完整型号和连接方式，补齐固件、接口和设备身份。在 Windows 普通用户下验收导航、枚举、拔插时的身份变化、接收器与设备状态、未知状态和离页取消。确认无板载配置写入。USB 优先作为验证起点，协议明确仅支持 2.4G 时从该模式开始。
2. **验证一台设备的单项设置。** 以用户实际型号覆盖最好的一台为首试，迈从优先考虑 DPI，狼蛛优先考虑灯光亮度，飞智先读配置槽。用正常使用状态核对读取值；写一项、回读、恢复、回读并检查其他设置；冷重连后验证是否持久。对照官方工具时串行使用，避免同时访问同一接口。用测试传输覆盖超时、设备替换和部分失败；实机记录按型号、固件、模式、功能留档。
3. **接入另外两台并补缺项。** 已有来源不足时，先查对应型号的官方资料和公开协议，再用用户自己的设备、厂商工具和目标 USB 接口抓取对照。记录识别/读取基线、一次单变量修改、恢复原值，按固件和连接模式比较请求与响应。验收依次走只读、可恢复的单项写入和重连验证。验证不足的功能保持只读。

## 2026-11-19 验证记录

- Release 解决方案和 Debug x64 桌面构建均通过，13 项外设定向测试通过。Windows 实际界面验证了四个子页、点设备卡片进入详情及手动刷新。
- Windows 枚举了 28 个活动 HID 接口，得到 8 条独立设备记录。验证了普通会话的 HID 集合元数据读取，以及句柄被拒绝时使用 Windows 硬件 ID 的回退路径。
- 迈从检测到接收器 `5253:1020`。上游有该接收器配合 L7 Pro 的验证记录，当前只读元数据仍不足以确认配对鼠标本体。键盘检测到共享标识的 `3554:FA09` 接收器，品牌及具体型号待确认。本次未枚举到 HID 手柄，仅以非 HID 模式呈现的手柄尚不在枚举范围内。见 [迈从 L7 Pro 协议](https://github.com/alexfrih/mchose-linux/blob/main/PROTOCOL.md)。
- 下一步用三台实机分别验证有线或常用模式的连接及拔插，记录完整型号、固件和连接方式，再选择第一台设备验证可恢复的单项设置。

## 2026-11-19 有线键盘身份核对

- 键盘有线 USB 为 `258A:010C`，Windows 产品字符串为 `Gaming Keyboard`。有线实机返回完整身份 `03000000010B`，与 F87 Pro 的厂商资源匹配。外设页按这个精确值识别「狼蛛 F87 Pro」。固件版本仍待核对。见 [AULA Studio 多型号记录](https://github.com/Dahuum/aula-studio/blob/main/docs/MULTI_MODEL_RESEARCH.md)。
- 同时观察到 `3554:FA09` 接收器。只按 Windows 枚举显示，其身份独立于已通过有线确认的键盘。
- 迈从 `5253:1020` 的接收器产品名为 `MCHOSE K7 Ultra`，另有单独的 MagDock `3837:1012`。唤醒后从接收器验证厂商集合 `FF01:0001`、65 字节 Feature Report、在线位和本体 ID `3837:4150`。隔离 WinUI 开发版显示固件 `5.28.4.0`、电量 90–91%、第 5 档 2800 DPI、8000 Hz 板载回报率设置、2 mm 抬升高度和 3 分钟休眠。回报率来自设备配置。见 [K7 Ultra 协议记录](https://github.com/aipno/mchose-linux/blob/main/docs/PROTOCOL.md)。
- 有线 F87 Pro `03000000010B` 返回匹配的 `0x84` 配置报告，开发版显示灯效模式 1。鼠标按键映射和键盘逐键 RGB、键位映射仍待核对。键盘颜色、亮度和速度字段在确认编码后再显示读数。见 [F87 读取契约](https://github.com/Dahuum/aula-studio/blob/main/docs/F87_READ_CONTRACT.md)。

## 2026-11-19 板载设置切换

- K7 Ultra 固件 `5.28.4.0` 经 `5253:1020` 2.4G 接收器验证。以固定功能报告写第 5 档，设备回读 2800 → 3000 → 2800 DPI。验证双轴、活动档、其余 DPI 档位和配置组保持预期。参考 [迈从 DPI 协议](https://github.com/aipno/mchose-linux/blob/main/src/mouse/protocol.rs#L292-L301)。鼠标附带的 MagDock 灯环另有接口。见 [官方产品资料](https://www.mchose.store/products/mchose-k7-ultra-lightweight-wireless-gaming-mouse?variant=50269487268135)。
- F87 Pro 有线 `010B` 可切换受支持灯效、亮度、速度和板载配色。用 Windows 实机回读验证了灯效、亮度、速度和红绿配色，并恢复测试前的四项设置。参考 [F87 Pro 开源实现（MIT）](https://github.com/meursault03/aula-f87pro-linux/blob/825a91a46d9b4573c7bfebdeb90a607b7638b709/src/lib.rs#L96-L143) 和 [灯效能力表](https://github.com/meursault03/aula-f87pro-linux/blob/825a91a46d9b4573c7bfebdeb90a607b7638b709/src/gui/editor.rs)。
- 逐键 RGB、键位映射、鼠标其他可写参数和 MagDock 灯环仍待按各自型号和接口验证。
