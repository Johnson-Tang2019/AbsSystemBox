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

## 正式交付补录

- 源码40be94bd2f14a33990df9bd43eac671e02a44e27；Windows构建42343173076通过，公开稳定发布v0.3.1的四个附件大小/哈希核对通过。
- 最终安装器100852398字节，SHA256为6979db2907e85a98701b2e34a68a66cf220ce4edb91a70d40ace8efcc0d27304；2548个安装文件按正式manifest验证，10个用户数据文件在安装阶段保留。
- 原生确认后服务注册兼容问题已回滚并恢复；Windows UAC完成后，从管理员保护的已授权暂存负载和记录继续安装，454个文件摘要匹配。服务为LocalSystem、Automatic、固定命令。该受保护同版采样负载源1bb41e3，后续仅安装脚本和客户端连接修复，采样实现未改变。
- 普通界面成功自动选择Intel Core i5-14600K / Core Max与Nuvoton NCT6687D / CPU Fan，真实读数52→56℃和980→998RPM写入，切页持续，最后保留应用运行。稳定传感器ID已保存。
- 登录启动与自动同步偏好已配置，原GoldenFiled自启动有备份并停用；未重启、未验收其他固件及热拔插。

机器本地证据为 `artifacts/local-upgrade-cooling/installation.json`、`service-recovery.log`、`startup.json`、`runtime.json` 和 `artifacts/final-releases/0.3.1/publication.json`。私人授权SID、完整用户设置和原始备份不作为公开附件。
