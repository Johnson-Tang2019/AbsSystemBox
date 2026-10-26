# 笔记本充电显示

概览和内存页在 Windows 报告 Charging 时显示粉色毛玻璃充电卡片；不充电、无电池、报告不可用时隐藏。
页面可见时每 5 秒读取一次，离开停止；取消后的迟到结果不会重新显示已失效读数。
诊断页可查看读取状态，便于区分无电池和 API 不可用。

读取复用项目已有的微软开源 [C#/WinRT](https://github.com/microsoft/CsWinRT) 投影，
按 [微软 BatteryReport 读取实现](https://learn.microsoft.com/en-us/windows/apps/develop/devices-sensors/get-battery-info)
调用 Battery.AggregateBattery.GetReport()。它聚合系统电池，适用于多电池设备；不读取厂商私有控制器、安装驱动或申请管理员权限。
没有引入 LibreHardwareMonitor 的整机扫描、内核驱动，也没有新增电池 NuGet 包。
上游投影源码为 MIT；项目实际随 SDK 使用的投影/运行时二进制仍按 THIRD_PARTY_NOTICES 中 Windows SDK 包的条款记录。
WindowsBatteryMonitor 只是薄适配器，计算在 Core，显示/生命周期在 Presentation 与 BatteryCard。

- ChargeRateInMilliwatts / 1000 → **电池净充电功率 W**，不能标成充电器输出或插座功率。
- FullChargeCapacityInMilliwattHours 是当前实际满充容量；不用设计容量冒充。
- 预计小时数 = (实际满充容量 − 当前剩余容量) / 当前净充电速率。
- 只有充电状态且正功率、有效容量、剩余不大于满充时才估算。缺失、零/负速率、异常容量或超过 7 天的估算显示不可用。
- 时间向上取整为分钟，少于一分钟单独提示。这是当前速率下的估算，温度、负载、充电限额和临近充满的减速会改变实际时间。
- 有充电状态但无功率/容量时卡片仍显示，明确提示硬件未提供功率或暂无法估算时间；不伪造数值。

Debug --demo 明确显示示例模式，并提供 45.0 W、预计 60 分钟的示例，仅用于界面验收；Release 不包含 DemoBatteryMonitor。
scripts/battery-smoke.ps1 验证概览/内存导航、条件显示和真实 BatteryReport；-Demo 验证示例充电 UI，
-Configuration Release -RestrictedUser 验证受限非管理员实际运行。
本机是无电池台式机，真实测试只能验证 NotPresent 与卡片隐藏，不能声称已在物理笔记本充电时核对功率和充满时间。
估算与拔插状态另有纯数据测试；最终硬件验收仍需一台真实充电笔记本。
