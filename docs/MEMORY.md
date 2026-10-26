# M2 内存测量说明

## 指标与口径

| 显示项 | 数据来源 | 口径 |
|---|---|---|
| 物理总量 | GlobalMemoryStatusEx / ullTotalPhys | 系统可见物理内存，不是内存条标称容量 |
| 可用物理内存 | ullAvailPhys | 可立即用于分配的内存，包含可回收的 standby 等页面，不等同于完全空闲 |
| 已用 / 使用率 | (总量 − 可用量) / 总量 | 本应用明确的派生口径，不能直接等同于任务管理器所有“正在使用”子项 |
| 系统提交量 / 限制 | K32GetPerformanceInfo / CommitTotal、CommitLimit × PageSize | 页数转字节；不使用 MEMORYSTATUSEX 的 pagefile 字段推算 |
| 进程工作集 | K32GetProcessMemoryInfo / WorkingSetSize | 当前驻留工作集，包含共享页，不能直接相加当作总 RAM |
| 进程私有提交 | PROCESS_MEMORY_COUNTERS_EX.PrivateUsage | 私有提交字节，既不是私有工作集，也不是实际交换文件占用 |

界面使用 GiB（2^30 字节）、MiB（2^20 字节），百分比保留一位小数。
系统与进程分别采样，进程之间也依次读取，整批不是原子快照；进程创建/退出和系统负载会造成差异。
身份使用 PID + 启动时间；启动时间不可取时标记未验证。列表每次整体更新，不凭 PID 继承历史身份。
路径可选，仅放在当前界面的悬停提示中。

## 与系统工具比较

任务管理器“性能 → 内存”的总量、可用、已提交应方向一致；注意 GB/GiB、舍入、采样时刻以及栏目定义。
进程比较应在“详细信息”中选择工作集/提交大小，不能拿默认的活动私有工作集列直接对比本应用的总工作集。
本次自动化使用 Win32_OperatingSystem.TotalVisibleMemorySize（KiB → 字节）及
Win32_PerfFormattedData_PerfOS_Memory 的 AvailableBytes、CommittedBytes、CommitLimit 作独立对照，
没有在任务管理器 GUI 中逐项抄录数字。动态量允许因非同时读取而变化；总量按显示舍入核对。

## 有界与取消

- 页面可停止/恢复自动刷新、手动刷新、取消当前读取；完成一轮后等待 5 秒，不重叠采样。
- 历史最多 60 点，按真实时间绘图，离开页面清空。物理采集失败不追加伪造的零读数。
- PID 枚举从 1024 项增长到最多 65536 项，达到上限则明确标记枚举不完整。
- 进程遍历有 3 秒合作预算；每个进程之间检查取消。单个同步系统调用不能强行中断。
- 对本次成功读取的条目排序，最多显示前 200 项，并保留可读/拒绝访问/不可读/其他失败/预算跳过计数。
- 仅查询权限，无 VM_READ、调试特权、清理或结束进程动作；句柄均用 SafeProcessHandle 释放。

## 本机验证及边界

构建、测试、真实运行与权限验证结果见 STATUS.md，机器可读报告在 docs/validation/m2/。
本机 UAC 关闭，普通桌面入口也是管理员；因此另外用仅对子进程有效的受限非管理员令牌进行验证。
独立标准用户登录、另一台机器、高 DPI、多显示器及长时间压力测试仍需单独验收。
短时连续采样与页面循环的资源观察不代表长期性能结论，也没有调用 GC.Collect 或清空工作集干扰结果。
真实进程名称/路径截图只留在本地 artifacts；仓库截图仅展示汇总内存信息。

本次最终 Release 受限非管理员运行：90 秒采样点增至 22；随后 30 轮页面切换的私有提交
164.17 → 168.02 MiB（空闲 167.97 MiB），句柄 1364 → 1318，有自然回落，未见持续线性增长。
实现使用固定页面视图及最多 200 个稳定行模型，只更新变化的进程记录，避免不断替换整批界面对象。

## 官方接口依据

- [GlobalMemoryStatusEx](https://learn.microsoft.com/en-us/windows/win32/api/sysinfoapi/nf-sysinfoapi-globalmemorystatusex)
- [PERFORMANCE_INFORMATION](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-performance_information)
- [PROCESS_MEMORY_COUNTERS_EX](https://learn.microsoft.com/en-us/windows/win32/api/psapi/ns-psapi-process_memory_counters_ex)
- [GetProcessMemoryInfo 的查询权限](https://learn.microsoft.com/en-us/windows/win32/api/psapi/nf-psapi-getprocessmemoryinfo)
