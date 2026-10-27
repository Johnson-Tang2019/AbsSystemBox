# 普通目录空间分析（M7 与 M8 磁盘页阶段）

2026-10-27。NTFS 卷级验证按用户要求暂缓；本功能只处理用户主动选择的普通目录。未创建或格式化虚拟盘，没有增删用户文件的功能。

## 数据口径

目录索引和空间分析共享 `%LOCALAPPDATA%\SysBox\index\metadata.db`。WindowsFileSizeReader 使用 FILE_READ_ATTRIBUTES 和 SafeFileHandle 读取 FileStandardInfo 的 EndOfFile、AllocationSize、NumberOfLinks。结构大小检查为 24 字节，BOOLEAN 为单字节；FileAttributeTagInfo 为 8 字节。只打开重解析点本身并拒绝采集链接，不读取正文，不申请备份或调试权限。

原生大小不可得时保留枚举的逻辑长度，分配量保持 NULL；size_state 区分 logical-only / standard-info，记录采集时间。没有 USN 的条目不填造 lastKnownUsn。

逻辑大小和分配量均按目录项累计。硬链接可能重复计数，已识别的多链接入口单独记录；未知链接数不等于没有硬链接，尚无同卷对象去重。稀疏/压缩文件的分配量不由逻辑长度推算；分配量合计不等于卷实际使用空间，不包含全部系统元数据、快照、回收站或不可访问内容。

摘要分别包含已知逻辑字节、已知分配字节、未知分配量文件数、文件/子目录数与覆盖状态。未知分配量不能视为 0；空目录已有完整快照时才显示 0。跳过子树内部文件数未知，故不报告覆盖百分比。完整度保守继承整个扫描范围；逻辑完整度和分配量可用性分别解释。

## 一致性与资源

Migration 2 事务增加大小来源/时间/链接数及 folder_totals。旧 schema 1 仍能搜索；大小页需重扫生成汇总，不能把旧目录冒充 0。迁移不改源文件或规则。

父先子后 entry_id 逆序迭代后序汇总，文件累加直接父级，再向上累加子目录。流式读取，不加载完整目录树。SQLite 求和溢出转 REAL 时拒绝发布。逐项检查和 SQL progress handler 支持汇总取消；暂停适用于目录遍历，最终汇总仅可取消。同步文件系统调用不能保证立即打断。

folder_totals 与 active_generation 同事务发布，取消/失败/超限保留旧快照，未发布暂存下次清理。移动、改名、删除、扩容后需要手动重扫所选范围，重建整代总量；尚无 USN 增量 delta 和祖先链更新，M7 的全部细项未完成。

数据库代际一致不等于操作系统级文件系统快照：各文件在扫描期间分别读取，源目录同时发生变化时仍可能需要再次重扫；没有使用 VSS 或冻结用户文件。

## 接口与界面

IStorageBrowser.AnalyzeAsync 明确发起分析；ReadAsync 只读快照，单读事务返回摘要和下一层。初次读取默认选最长匹配范围；后续逐层导航固定原 scope/generation，防止重叠索引带来父子统计不一致，版本改变返回 STORAGE_CHANGED 要求重新读取。UI 每页 100 条，接口最多 200 条。查询有三秒合作预算及取消检查，锁等待两秒。IStorageAnalyzer 的旧接口仅生成已发布快照摘要，不暗中开始扫描；跳过计数为所在范围的保守值。

磁盘页提供目录选择/输入、分析/重扫、暂停/取消、快照读取、大小/文件数排行、分页、后退/前进/上级和面积图。输入路径与实际快照路径分开显示；离开页面取消并屏蔽迟到结果。

独立 TreemapLayout 算法最多绘制 64 块，合并其他项和当前页以外已知字节；零字节不绘制，部分索引只表示已知部分。按钮可点击目录，提供 Tooltip 和键盘访问，复用粉色主题资源，窄页面上下排列。尚无卷容量列表、可点击面包屑、快捷搜索小窗及完整高 DPI/多屏验收，M8 未全部完成。

## 验证

Windows Debug/Release 构建与测试报告见 validation/m7。覆盖后序汇总、直接文件/空目录/未知分配量、迁移、移动/扩容/删除重扫、取消保留总量、分页、部分覆盖、迟到读取、原生大小读取，以及真实 NTFS 硬链接和稀疏测试文件；未做卷级 NTFS 验证。

非管理员实际窗口验证 100 字节/4 文件样本、面积图进入三层再后退/前进/返回根、移动扩容后重扫 150 字节、空目录无残留图块、搜索共享新路径及 code 0 退出。UIA 不等同于独立机器或最终像素质量验收。

原生依据：[FILE_STANDARD_INFO](https://learn.microsoft.com/en-us/windows/win32/api/winbase/ns-winbase-file_standard_info)、[FILE_INFO_BY_HANDLE_CLASS](https://learn.microsoft.com/en-us/windows/win32/api/minwinbase/ne-minwinbase-file_info_by_handle_class)。未增加第三方依赖。
