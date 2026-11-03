# 图标验证

2026-11-03。仅验证新图标与交付变更，没有重跑已验收的业务测试。

- `validation.json`：透明角、多尺寸 ICO、不透明区域原图 RGB 逐点一致。
- `background-and-sizes.png`：深浅背景及 16–96 像素预览，外部白底/毛边没有被保留。
- `embedded-icon.json` / `embedded-exe-icon.png`：使用 Windows Shell 从构建后的真实 EXE 提取图标，确认粉色图像及透明角；文件版本 0.2.0.2。
- Release WinUI 应用构建 0 警告、0 错误。本次没有新增运行依赖，Pillow 仅用于资源生成。
- 后续安装器、远端附件和本机覆盖更新证据按实际结果另行记录，不把构建或源图验证等同于安装验收。
