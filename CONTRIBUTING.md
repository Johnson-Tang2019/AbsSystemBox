# 贡献指南

先阅读 `AGENTS.md`、`docs/STATUS.md`、`docs/ARCHITECTURE.md` 和对应手册章节。
每次聚焦一个可验证子任务；不要一次生成整份路线图。
提交的原创贡献按项目的 GPL-3.0-or-later 许可提供，请勿提交无权授权的素材或代码。
引用第三方代码、库或资源时，记录来源、精确版本、许可证及分发内容。

代码注释和标识符使用英文，面向用户的文字使用中文。
启用 nullable、隐式 using、warnings-as-errors；异步操作接收 CancellationToken。
调整契约时同步更新 `docs/API.md` 和必要的行为测试。

提交前运行 `scripts/build.ps1` 和 `scripts/test.ps1`，记录真实结果到 `docs/STATUS.md`。
新增 NuGet 依赖后检查许可证并更新锁文件：

```powershell
dotnet restore SysBox.slnx --use-lock-file --force-evaluate
```

不得提交用户索引、日志、搜索历史、凭据或本地 SDK。
