# 安装、覆盖更新与版本

预览版面向 Windows 11 x64。下载 GitHub Release 的 win-x64-setup.exe，运行后按提示安装。
安装包包含 .NET/Windows App SDK 运行组件，无需预先安装 SDK、注册服务或申请管理员权限。
当前尚未购买代码签名证书，安装包未签名，Windows 可能显示未知发布者提示；请核对官方仓库来源和 SHA256SUMS.txt。
没有设置自动下载、静默后台更新、开机启动或系统清理。

默认安装到当前用户 `%LOCALAPPDATA%\Programs\AbsSystemBox`，在开始菜单及“已安装的应用”中登记。
只修改当前用户登记，不提供所有用户/管理员安装模式。
应用主体位于安装目录 app 子目录；可从开始菜单或 app/SysBox.App.exe 启动。

## 直接覆盖更新

下载更新版本安装程序，先关闭应用，再直接运行，自动识别此前目录和固定产品身份。
无需先卸载。只保留一项已安装应用登记和稳定开始菜单入口。同版本再次安装用于修复；低版本安装程序拒绝降级。
安装程序先在 app.incoming 完整提取，再切换 app/app.previous；切换失败尝试恢复旧应用。
中断后再次安装可恢复 app.previous。目录未归属文件不递归删除，异常暂存目录会要求检查而非强制覆盖。
没有完整掉电/磁盘故障恢复或另一台机器验收保证，预览版验证范围见 STATUS.md。

用户数据在 `%LOCALAPPDATA%\SysBox`，与程序文件分离。覆盖安装、同版本修复和卸载均保留规则、草案、审批审计及回滚快照。
应用内“规则版本回滚”只恢复安全配置，不等于安装包程序版本降级。

卸载通过“已安装的应用”或安装目录 Uninstall.exe。卸载只删除清单中且仍位于本产品目录内的程序文件及快捷方式，
不递归删除未知文件，不接触用户规则目录；保留的未知文件可能导致安装目录仍存在。

## 维护版本

Directory.Build.props 是统一版本来源：Version 为语义版本，FileVersion 为四段 Windows 数字版本，AssemblyVersion 保持兼容的程序集版本。
每次正式更新同时递增 Version 与 FileVersion；预览计数例如 0.1.0-preview.1 → 0.1.0-preview.2 对应 0.1.0.1 → 0.1.0.2。
产品身份 AbsSystemBox.JohnsonTang2019 不随版本改变。诊断页显示语义版本，卸载登记同时保存四段数字版本，升级比较使用数字版本。
规则文件自己的 revision/schema_version 独立管理，不能用程序版本覆盖用户规则修订。

构建安装包：在 Windows 开发环境执行 scripts/build-installer.ps1，要求干净、已提交的源码。
脚本固定 NSIS 3.12 并按官方公布 SHA-256 核验下载；项目内工具缓存不提交。
发布完整自包含 x64 payload、保留全部第三方许可证，生成安装包、源码 ZIP、manifest.json 和 SHA256SUMS.txt。
本地安装器验证才允许 -AllowDirty；带 sourceDirty:true 的产物不得发布。

静默安装需要同时传入 `/S /ACCEPTLICENSE`，表示操作方明确接受安装页所附许可；`/D=绝对目录` 如使用需放在最后。
验收不会安装测试旧版作为公开附件；公开包仅从干净提交构建，manifest 记录 sourceDirty=false 与精确源码修订。
