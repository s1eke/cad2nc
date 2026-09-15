# CAD2NC 安装器

当前发布版本为 CAD2NC 2.6.4，标准产物为 `dist/CAD2NC-Setup-2.6.4-x64.exe`。安装器版本与应用统一取自 `Directory.Build.props`，构建时检查发布载荷版本一致。

安装器使用未修改的 Inno Setup 7.1.0 x64。编译器与内置简体中文语言文件来自官方版本，不包含在 CAD2NC 运行目录中。完整 Inno Setup 许可随 CAD2NC 安装在 `licenses/InnoSetup-LICENSE.txt`。

- 官方版本：https://github.com/jrsoftware/issrc/releases/tag/is-7_1_0
- 下载：https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe
- SHA-256：`0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f`
- 验证依据：2026-09-04 官方不可变 GitHub release 的 asset digest，下载文件具有有效的 `Pyrsys B.V.` Authenticode 签名。
- 官方验证方法：https://jrsoftware.org/isdl-verify.php
- 原始许可：https://raw.githubusercontent.com/jrsoftware/issrc/is-7_1_0/license.txt
- 编译器便携安装逻辑：https://github.com/jrsoftware/issrc/blob/is-7_1_0/isportable.iss

`AppId=CAD2NC` 是持续升级标识，不随版本变化。GUI 持有的 `Local\CAD2NC.Gui.InstallLock` 与 `AppMutex` 对应：安装/卸载遇到应用运行时要求用户保存并退出，不强制结束进程。安装器保持当前用户权限；应用清单保留 `asInvoker`，桌面拖入图纸时不需要管理员权限。

2.6.4 继续由应用在首次写入相应目录时创建 Windows 实际「文档」下的 `CAD2NC\任务` 或 `CAD2NC\NC`，安装器不预建或搬移用户输出目录。应用允许分别修改默认目录并立即持久化，升级保留现有设置，卸载保留用户任务、NC 和设置。另存为最终后缀与覆盖确认、二维 NC 播放控制及限制见 [使用说明](../docs/PORTABLE.md)。

2.6.4 的工作台采用合并顶部、中央结果抽屉和 NC 源码并排阅读布局，界面操作名称为「保存任务」与「导出 NC」。安装器不重置布局设置：已有合法 CAD 侧栏宽度继续沿用，应用缺省为左 240、右 320 DIP；独立 NC 详情宽度缺省为 480 DIP，仅保存到本机设置。新任务及参数文件为版本 2，旧任务仍按旧版工艺读取。缺少新启动默认字段时，应用采用宝元 LNC 内置配置；用户可明确保存启动默认。当前构建与安装的实际验收范围见 [验证记录](../docs/VALIDATION.md)。

同一个离线安装包自动识别以下场景，欢迎页、确认页、操作按钮、进度页和完成页使用对应说明：

| 电脑上的状态 | 操作 |
| --- | --- |
| 未安装 | 安装 CAD2NC，可选择程序位置和桌面图标。 |
| 旧版本 | 更新 CAD2NC，显示当前版本和更新后的版本。 |
| 相同版本 | 重新安装此版本，用于补齐或恢复程序文件。 |
| 已有安装记录，版本无法读取 | 允许重新安装并补齐程序文件，明确保留任务和设置。 |
| 比安装包更新的版本 | 拒绝覆盖；提示使用相同或更新版本的安装包。静默运行也会拒绝。 |

检测只读取当前用户 64 位视图中的 `CAD2NC_is1` 卸载项，不搜索其他用户或搬移便携版。优先使用合法的 `DisplayVersion`；缺失或异常时依次读取原目录中 `CAD2NC.exe`、`CAD2NC.dll` 的二进制版本。版本按最多四段数字比较，缺少末尾段视为零。缺少 EXE 或目录不会把已有安装误判为首次安装；只在确定原版本更高时拒绝覆盖。安装前再核对一次版本，防止静默流程绕过判断。

更新和重新安装沿用程序位置，不重复显示目录和快捷方式选择页。`UsePreviousTasks` 与 `UsePreviousGroup` 沿用上次安装时的快捷方式选项；用户手动删除的原快捷方式可能按该选项恢复，因此不承诺保留手动修改后的图标状态。

当前安装器未配置产品代码签名证书。上面的有效签名属于构建工具，不表示生成的 CAD2NC 安装包已签名。

`scripts/test_installer_modes.ps1` 提取生产脚本中的纯版本判断函数，使用固定 Inno 编译器执行 38 项合成数据断言，包括更新、同版重装、四段版本、数值排序、降级、异常注册版本以及 EXE / DLL 回退。测试程序没有安装载荷、快捷方式或卸载项，初始化中写入工作区结果后固定返回 `False`，不会执行产品安装或修改用户注册表；临时产物保留在 `build/installer-modes-*`。

`scripts/test_installer.ps1 -InstallerPath <安装包> -StaticOnly` 校验产物哈希、版本以及安装脚本的权限、运行锁、用户数据保护和离线模式约束。不带 `-StaticOnly` 时，还会验证当前 Windows token、HKCU 的实际 SID hive 和用户配置文件归属，只有完整隔离的 CodexSandboxOnline/Offline 账户才能进行工作区临时安装、运行锁、重装、已安装 EXE 转换和卸载测试。测试使用 `/NOICONS`，不会写入桌面/开始菜单，也不会运行 GUI 或修改实际用户设置。若 HKCU 指向 `.Default` 或配置路径指向宿主用户，前置检查会拒绝执行。纯逻辑、编译和静态检查均不能代替真实安装、跨版本更新和快捷方式的隔离环境验收。
