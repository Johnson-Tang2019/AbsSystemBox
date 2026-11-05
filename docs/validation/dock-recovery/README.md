# 悬浮球恢复与概览新流程

2026-11-05，仅验证本轮新增和修复内容，复用已有验收。

- `new-tests.json`：FloatingDockRecoveryTests、DashboardUsageTests 共七项通过；四种缩放下的固定合成面/region 边界、上游状态转换和真实容量占比的未知/异常情况。
- `runtime-ui.json`：`scripts/ui-dock-recovery-smoke.ps1` 的真实普通用户窗口与 SendInput 鼠标输入。四边共 32 次贴边恢复、固定尺寸、GDI 资源数、恢复后的拖动、单击/右键及概览渐变条；无实际清理/整卷扫描。
- `*-restored.png`：最终通过流程的球体截图，仅百分比，粉色主题。
- `builds.json`、`source-ci.json`：本地构建与对应源码的 Windows CI；未调度已接受测试。
- `local-upgrade.json`、`publication.json`：最终安装包的覆盖安装、配置保留、新概览运行及公开附件哈希。实际安装的应用保留运行，不把本记录当作退出验证。

实际窗口记录为 96 DPI；1.25/1.75/2 倍缩放只做几何测试。尚未实际执行双击后的管理员清理，不将既有 helper 授权记录当作本轮清理效果。失败的调试流程均不作为通过证据。
