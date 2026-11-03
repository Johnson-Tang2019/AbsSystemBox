# 0.3.0-preview.2 验证记录

- `verification.json`：四项新增测试和本轮 Debug/Release 构建；旧业务验收沿用，未重复运行。
- `runtime-ui.json`：真实普通用户窗口的新流程，只分析仓库 artifacts 内 1,201,500 字节专用文件夹；未操作系统缓存或真实整卷。
- `local-upgrade.json`：最终安装包覆盖安装、清单哈希、配置保留与已安装新窗口/后台运行。
- `publication.json`：公开发布及四个远端附件大小、SHA-256 校验。
- `source-ci.json`：对应源码的 Windows CI 记录。

0.3 首轮六项新测试、真实内存/电池/悬浮球界面及 helper 拒绝验证见相邻 ui-revision 目录。悬浮球实际验收为 96 DPI 下窗口/客户区 64x64、文本边界完整；其他缩放仅几何单元测试。未将系统缓存/Standby 操作、真实 NTFS 卷或 SCM 生命周期记为已通过。开始菜单只确认快捷方式文件与目标，未宣称搜索已收录。
