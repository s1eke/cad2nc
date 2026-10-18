# 开发与发布检查

使用仓库 `global.json` 固定的 SDK 和 PowerShell 7。`build.ps1` 执行锁定还原、Release 回归、许可归档验证、自包含发布、发布 EXE 转换和安装包编译。正常构建使用官方 NuGet；本地工具缓存不是源码包的一部分。

## GitHub 自动检查

Windows 工作流在 push、pull request 和手动触发时运行两个独立作业：完整构建及示例复现、依赖审计。构建和审计只有 `contents: read` 权限。建议将两个作业都设为合并前必需检查；测试报告会保存在 Actions 的构建产物中。

推送 `v主版本.次版本.修订号` tag 时，两个作业全部通过后，额外的发布作业自动创建 GitHub Release，提供 Windows x64 安装 EXE、对应源码 ZIP 及两份 SHA-256 校验文件。发布作业单独使用 `contents: write` 和 GitHub 自动提供的 `GITHUB_TOKEN`，无需额外配置个人访问令牌；分支 push、pull request 和手动检查不会发布。

tag 必须与 `Directory.Build.props` 中的 `Version` 完全对应，`AssemblyVersion`、`FileVersion` 也须同步为该版本加 `.0`，并提供 `docs/RELEASE-版本.md`。例如当前 `2.6.4` 对应 `v2.6.4`。发布前先提交并推送改动，再执行：

```powershell
$version = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version
git tag -a "v$version" -m "CAD2NC $version"
git push origin "v$version"
```

版本不匹配、测试失败、审计不可用、安装包或源码校验失败都会阻止发布。上传先写草稿，全部附件上传成功才公开；失败后可在 Actions 重跑以继续同一草稿。已经公开的 Release 保持原有下载文件，再次发布应使用新的版本和 tag。发布流程的本地验证可运行 `./scripts/test_release_workflow.ps1`，它使用模拟 GitHub CLI，不会创建真实 Release。

独立审计命令：

```powershell
./scripts/audit_dependencies.ps1
```

审计使用显式官方数据源，覆盖直接和传递依赖、所有严重级别；NU1900–NU1905 全部视为错误。服务不可用也失败，不会报告“无漏洞”，不会自动升级依赖。普通构建关闭内联审计以与该作业分开；构建成功不能代替审计成功。机制见 [NuGet 官方审计说明](https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages)。

## 许可及源码包

还原后可单独运行 `./scripts/verify_licenses.ps1`。它核对 App 还原资产中的全部包及下载依赖、逐版本许可与元数据、字节数和 SHA-256。许可归档保持原始字节，不做 Git 换行转换。

构建和发布校验共用 `scripts/release_source_files.ps1`；源码包包含仓库配置、CI、全部文档和公开示例，排除编译缓存及本地用户文件。源码 ZIP 和安装包均生成 SHA-256 文件。发行时将安装包、源码 ZIP 和校验文件放到 GitHub Releases；不要提交 dist 目录。

## 历史 UI 验证工具

`test_workbench_v23.ps1` 和 `test_workbench_playback.ps1` 是 2.5 工作台的历史工具，断言包含旧控件、播放文字和工艺配置，不是 2.6.4 验收套件。需要图纸的模式须显式传入 `-Drawing`，不会读取作者电脑的默认路径。播放脚本要求 4 个轮廓，其中 3 个加工轮廓和 1 个母板；综合脚本需要足够轮廓以检查多选等交互。原私有验收图纸未随仓库提供。

`test_workbench_v23.ps1 -Part NcPreview` 无需 `-Drawing`。历史脚本只能在匹配版本及输入下使用，截图和人工操作结果须另外记录；不能以核心测试通过声称这些 UI 脚本也已通过。
