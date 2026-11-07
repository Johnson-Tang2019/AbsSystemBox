# 水冷显示开发阶段增量验证 — 2026-11-07

本报告保留0.3.1之前的开发阶段结果。0.3.1新增跨页面持续同步、AutoStart偏好和持久采集服务，其新测试见 `../cooling-startup`；不将下面旧的“离页结束”作为正式版生命周期要求。

本轮仅验证新增水冷功能。既有已接受测试、旧界面流程、卷扫描与内存清理均不执行。

| 项目 | 结果 |
|---|---|
| 锁定依赖还原 | 通过；原有包版本/内容哈希未改变，仅项目引用元数据按当前版本更新 |
| Debug / Release 完整构建 | 各 0 警告、0 错误 |
| 新测试过滤 `FullyQualifiedName~CoolingDisplayFeaturesTests` | 14 通过、0 失败、0 跳过 |
| 协议与边界 | 固定头、XOR 负载、Big-endian RPM、拒绝越界 |
| 数据和生命周期 | 缺失/过期/非有限读数拒绝、迟到检测/连接释放、自动失效后无假零帧、无传感器手动模式 |
| 设置 | 原子保存、稳定身份回读、无自动启动或特权授权字段 |
| 真实普通用户新界面 | 检测 COM3；显卡两个实际温度来源；未提升的采集进程 |
| 空闲及离页 | 等待选择 17 秒后仍存活并成功再次读取；离页后退出 |
| 对端身份 | 非 UI 映像的管道服务端被拒绝，无传感器数据返回 |
| Debug 示例保护 | 新水冷页的检测、授权、开始与保存均禁用，未连接或写入真实设备 |
| 实体写入/特权确认 | 未发送显示帧；未自动化点击管理员确认 |

测试用例文件为 `tests/SysBox.UnitTests/CoolingDisplayFeaturesTests.cs`。初次新增页构建发现 StackPanel 不支持 IsEnabled，已改为按控件设置；最终构建通过。UI 验收脚本第一次使用错误的 SelectionPattern 查询方法，修正脚本后通过；不把那次脚本异常记成产品失败或通过。

机器本地原始证据：

- `artifacts/tests/cooling-display/cooling-display-final.trx`
- `artifacts/tests/cooling-display/peer-denial.json`
- `artifacts/tests/cooling-display/demo-guard.json`
- `artifacts/ui/cooling-a62b759b037a49999c2bc6864e9fda9a/report.json`
- `artifacts/ui/cooling-a62b759b037a49999c2bc6864e9fda9a/cooling.png`

上述 UIA 原始下拉框条目还混入了导航 ListItem；温度来源按带硬件前缀的去重项核对，为 `NVIDIA GeForce RTX 5070 Ti · GPU Core` 与 `GPU Memory Junction` 两项。后续脚本已收窄条目过滤，不以混入的导航项计算传感器数量。

采集子组件的 210 个自包含 Release 文件、哈希及对应 NuGet 运行资产见 [sensor-runtime-files.json](sensor-runtime-files.json)，无未归属项。这是开发输出记录，不是已签名安装器或发布许可验收。

用户随后报告水冷实机测试通过；新持久服务、开机同步及其他固件仍分别记录，不把14个软件用例或GPU读取当作这些步骤通过。发布前逐字节核对确认12个内嵌模块全部匹配PawnIO.Modules 0.2.2，源码提交e12a858d952461ee2e919897cacbff7f905fe370；先前依据上游过时README定位的0.1.6已纠正。完整对比与通知见许可证归档。
