# M3 本地验收记录

2026-10-26，Windows 11 x64。报告由最终 TRX 和原生 UI Automation 结果提取，剔除机器名、用户名、路径、进程清单及原始内存数据。
Debug/Release 各 107 项通过；构建各 0 警告、0 错误。NuGet locked restore 通过。

复现顺序（项目 SDK 由 global.json 指定）：

```powershell
dotnet restore SysBox.slnx --locked-mode
dotnet build SysBox.slnx -c Debug --no-restore -m:1
dotnet test SysBox.slnx -c Debug --no-build --no-restore --logger trx
dotnet build SysBox.slnx -c Release --no-restore -m:1
dotnet test SysBox.slnx -c Release --no-build --no-restore --logger trx
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/rules-smoke.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/rules-smoke.ps1 -Reopen
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/battery-smoke.ps1 -Demo
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/battery-smoke.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/battery-smoke.ps1 -Configuration Release -RestrictedUser
```

规则初次流程需要新的隔离 artifacts/ui/m3-rule-store-final，重启流程使用同一目录。
报告记录实际窗口启动、UI 交互、持久化版本及退出，不将构建等同于运行验收。
Release 使用受限非管理员令牌，携带 --demo 仍使用真实数据；详情见对应 JSON。
本机真实报告无电池，充电显示数值仅在显式 Debug 示例中验证；还需物理充电笔记本验证。
不提供本次像素验收截图：现有 PrintWindow 无法可靠捕获 DirectComposition 客户区。高 DPI、多屏、独立账户和干净机器场景仍待验收。
