# NTFS 实验组件（M6 部分实现）

用户已选择暂缓 NTFS 卷级验证并继续其他 Windows 功能。创建测试虚拟盘的准备脚本保持未执行；不以普通目录里的硬链接/稀疏文件测试替代卷级对账。

2026-10-27。此阶段没有把卷级读取注入普通 WinUI，也不随安装器注册管理员服务。现有普通目录索引/搜索继续工作。

已实现：

- `INtfsVolumeReader`、UsnJournal/NtfsBatch/UsnRecord 共享契约，身份为不丢失精度的十六进制字符串。
- `UsnBufferParser` 逐记录检查长度、对齐、V2/V3 头部、文件名偏移/UTF-16、64/128 位身份；未知版本和坏缓冲明确拒绝。
- 原生卷句柄使用 SafeFileHandle，仅开放 Query/Enum/Read 三个只读控制码；输入输出以字节偏移和有效返回长度检查，不进行不受控结构强转。
- Journal 查询解析；NTFS ENUM/READ 采用文档支持的 V0 请求，单次输出 64 KiB，READ 不等待未来变更。V3 仅独立解析实验，不声称支持 ReFS。同步 DeviceIoControl 无法立即中断，取消在调用前后检查。
- NtfsJournalGuard 检测 Journal ID 变化、FirstUsn/LowestValidUsn 越过 checkpoint、非法未来 checkpoint。
- 独立 SysBox.NtfsLab 当前只提供 `probe X:`，查询 Journal 元数据，固定枚举数 0，不创建 Journal、不修改卷。
- Windows 开发机 D: 的实际 probe 成功，枚举记录数 0；没有因此进行文件枚举或把它视为隔离测试卷验收。
- 解析测试覆盖 V2/V3、中文/代理对、全部截断边界、错误偏移/长度、未知版本、恶意名称、Journal 缺口和 2000 次有种子缓冲变异。

尚未实现/验证：持久化 NTFS generation、初始快照加变更回放、checkpoint/目录项事务、重命名配对/硬链接一致性、1000 次真实混合变更及服务集成。M6 **未完成**，不能宣称 NTFS 高速索引可用。

Windows 专用实验步骤：

1. 新建与用户磁盘隔离的 NTFS 测试卷。
2. 在该卷验证 Native Enum/Read、权限与普通遍历对账。
3. 实现并验证快照/增量事务及缺口恢复，再接入受控组件。

已准备可审查的 `scripts/new-ntfs-test-volume.ps1`，计划创建 `artifacts/ntfs-lab/sysbox-test-512mb.vhdx`，动态最大 512 MiB，仅在该全新 RAW 文件后备虚拟盘内初始化 GPT、格式化 NTFS、自动分配空闲盘符和创建测试 Journal。脚本拒绝覆盖已有镜像，格式化前通过镜像关联、BusType、精确容量、RAW 状态检查目标；不删除镜像。**本次没有执行创建/分区/格式化脚本，也没有修改现有 C:/D:/E:。** 脚本语法检查不等于已通过创建流程。

这仍是 Windows 测试，不安排其他操作系统验收。现有用户磁盘不能代替隔离卷；AGENTS.md 禁止开发期间默认整卷扫描，后续测试需要对新建测试卷的明确许可。

原生布局依据微软文档：[USN V3](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v3)、[MFT_ENUM_DATA_V0](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-mft_enum_data_v0)、[READ_USN_JOURNAL_DATA_V0](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-read_usn_journal_data_v0)。V1 枚举文档主要面向 ReFS/Server，未把它假定为本机 NTFS 的已验证通路。
