# PCL 内存清理行为参考

来源：[PCL-Community/PCL-CE 2.10.4 固定源码](https://github.com/PCL-Community/PCL-CE/blob/bc10f7679307e647195ad90c6f50155a29f43445/Plain%20Craft%20Launcher%202/Pages/PageOther/PageOtherTest.xaml.vb)，检查日期 2026-11-03。只观察其中 MemoryOptimizeInternal 列出的操作组合，没有复制源码。

观察到全局工作集、信息类 81 文件缓存、修改页面、完整/低优先级待机列表、注册表协调与内存合并。SysBox 将这七项映射到已有九类目录，继续沿用本项目的固定顺序及 x64 受检缓冲布局；不移植上游结构体、权限获取或内存变化估算方法。组合按钮只修改选择，执行仍要求独立管理员授权和默认“否”的原生确认。

固定历史标签树未检出明确 LICENSE/NOTICE 文件；当前 dev 的许可证不能自动视为旧标签授权，因此本次仅参考可观察行为，无 PCL 代码或二进制进入发行包。原有 Fluent Reduct 适配的 GPL-3.0-or-later 记录继续有效。
