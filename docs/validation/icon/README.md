# 图标验证

2026-11-03。仅验证新图标与交付变更，没有重跑已验收的业务测试。

- `validation.json`：透明角、多尺寸 ICO、不透明区域原图 RGB 逐点一致。
- `background-and-sizes.png`：深浅背景及 16–96 像素预览，外部白底/毛边没有被保留。
- `embedded-icon.json` / `embedded-exe-icon.png`：使用 Windows Shell 从构建后的真实 EXE 提取图标，确认粉色图像及透明角；文件版本 0.2.0.2。
- Release WinUI 应用构建 0 警告、0 错误。本次没有新增运行依赖，Pillow 仅用于资源生成。
- `installer-icon.*`、`installed-app-icon.*`、`uninstaller-icon.*`：从对应实际 EXE 中提取，均确认粉色图像及透明角。
- `local-upgrade.json`：本机 preview.1 → preview.2，同一产品与目录，1810 个已安装文件哈希匹配、2 个用户数据文件保留，非管理员新版本真实窗口/独立后台/退出通过。
- `release-publication.json`：四个远端附件大小与 SHA-256 与本地一致，公开预览版链接。`ci.json`：发布源码的 Windows 构建/publish 资源检查通过；已验收业务测试没有重跑。
- 未安装可选特权服务，未执行真实卷枚举；不把图标、构建、安装验收等同于 NTFS 实机生命周期或整卷一致性验收。
