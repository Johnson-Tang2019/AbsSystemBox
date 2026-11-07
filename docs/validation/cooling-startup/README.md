# 0.3.1 自动水冷显示与传感器服务

日期：2026-11-07。前置14项水冷测试复用 `../cooling-display`，用户报告实机测试通过；没有将该反馈扩写为本轮逐项运行日志。

新增验证：

- `CoolingStartupFeaturesTests`：4通过。精确唯一 Core Max / CPU Fan、已保存身份优先、缺失/重名/错误类型拒绝、默认关闭、自动同步跨页面持续到显式停止。
- `SensorServiceFeaturesTests`：3通过。受保护策略、授权用户与交互UI身份、固定空参数白名单、拒绝Agent权限标志、ACL保护及Network拒绝。
- `SensorServiceEndpointTests`：1通过。真实管道PID必须匹配SCM运行中固定服务，普通可访问管道无法冒充；实机另确认该校验在普通用户下可通过，无需打开LocalSystem进程。
- 最终Release完整构建：0警告、0错误。服务校验增强后只重跑上述3项对应新测试。
- 真实Windows PowerShell5.1：安装/移除脚本BOM、中文和JSON UTF8解码及纯路径验证兼容，未用语法检查代替真实安装。

原始新测试证据位于 `artifacts/tests/cooling-startup/startup.trx` 和 `service-final.trx`。配置 `config/validation-scope.txt` 无剩余新自动测试组；CI push仅构建，测试仅允许显式手动过滤器。

首次部署发现两项环境兼容问题并修复：Windows PowerShell 5.1 的原生命令引号影响服务注册，已改为结构化 New-Service / CIM；普通用户不能打开 LocalSystem 进程，客户端已改为 SCM 与真实管道 PID 绑定。对应新证据为 `endpoint.trx`；原测试不重复派发。

受保护服务安装、真实普通用户客户端、开机登记与实体同步属于部署后的新流程。正式交付补录记录实际结果；尚未重启时不能声称完成真实开机验收。保护路径部署保留旧负载/授权备份，卸载维护只取消注册、不递归删除未知文件。
