# 开发与发布检查

使用仓库 `global.json` 固定的 SDK 和 PowerShell 7。`build.ps1` 执行锁定还原、Release 回归、许可归档验证、自包含发布、发布 EXE 转换和安装包编译。正常构建使用官方 NuGet；本地工具缓存不是源码包的一部分。

## 版本规则

`Directory.Build.props` 中的 `Version` 是唯一版本来源，当前为 `2.7.0`。按语义化版本 `主版本.次版本.修订号` 递增：

| 改动 | 示例 |
| --- | --- |
| 兼容的问题修复 | `2.7.0 → 2.7.1` |
| 兼容的新功能 | `2.7.0 → 2.8.0` |
| 不兼容的任务、配置格式或主要使用方式变更 | `2.7.0 → 3.0.0` |

圆孔自动内侧加工是兼容的新功能，因此从 `2.6.4` 升为 `2.7.0`。能够读取并迁移旧任务或配置的格式升级，不单独构成必须升主版本的理由。任务和设置的 schema 版本独立于应用版本。

当前发布工具只接受三段非负整数的稳定版本，每段范围为 `0–65534`，不允许多余前导零；暂不支持 `-beta` 等预发布后缀或构建元数据。`AssemblyVersion`、`FileVersion` 和 Windows 应用清单自动派生为 `$(Version).0`；应用清单从模板在构建时生成，不再手动维护版本字段。安装器、CLI 摘要、示例和发布检查均读取同一版本。

## GitHub 自动检查与发布

Windows 工作流在 push、pull request 和手动触发时运行两个独立作业：完整构建及示例复现、依赖审计。构建和审计只有 `contents: read` 权限。建议将两个作业都设为合并前必需检查；测试报告会保存在 Actions 的构建产物中。

推送 `v主版本.次版本.修订号` tag 时，两个作业全部通过后，额外的发布作业自动创建 GitHub Release，提供 Windows x64 安装 EXE、对应源码 ZIP 及两份 SHA-256 校验文件。发布作业单独使用 `contents: write` 和 GitHub 自动提供的 `GITHUB_TOKEN`，无需额外配置个人访问令牌；分支 push、pull request 和手动检查不会发布。

tag 必须与 `Directory.Build.props` 中的 `Version` 完全对应，并提供 `docs/RELEASE-版本.md`。当前 `2.7.0` 对应 `v2.7.0`，安装包名为 `CAD2NC-Setup-2.7.0-x64.exe`。发布步骤：

1. 修改唯一的 `Version`，编写更新说明，更新当前文档和示例；历史版本说明及示例保留原内容。
2. 运行 `./build.ps1`、`./scripts/generate_example.ps1 -VerifyOnly`、`./scripts/test_release_workflow.ps1` 和 `./scripts/audit_dependencies.ps1`，确认构建与检查通过。
3. 提交并推送改动，确认该提交的远端构建与审计通过，再给同一提交创建并推送 tag：

```powershell
$version = ([xml](Get-Content Directory.Build.props -Raw)).Project.PropertyGroup.Version
git tag -a "v$version" -m "CAD2NC $version"
git push origin "v$version"
```

4. tag 工作流完成后，在 GitHub Release 核对版本、更新说明、安装 EXE、源码 ZIP 及两份 SHA-256 校验文件。

版本不匹配、测试失败、审计不可用、安装包或源码校验失败都会阻止发布。公开前会再次核对远端 tag 仍指向本次验证的提交，避免测试后 tag 被移动而发布错误内容。上传先写草稿，全部附件上传成功才公开；失败后可在 Actions 重跑以继续同一草稿。`./scripts/test_release_workflow.ps1` 使用模拟 GitHub CLI 检查失败处理，不会创建真实 Release。

已发布版本保持固定：不移动或重建 tag，不替换公开 Release 的附件；需要修正时递增版本并发布新 tag。发布脚本遇到已公开的 Release 会保留原下载文件。这是仓库发布约定与脚本保护，不代表已启用 GitHub 服务端的 tag 保护或不可变 Release 设置。

独立审计命令：

```powershell
./scripts/audit_dependencies.ps1
```

审计使用显式官方数据源，覆盖直接和传递依赖、所有严重级别；NU1900–NU1905 全部视为错误。服务不可用也失败，不会报告“无漏洞”，不会自动升级依赖。普通构建关闭内联审计以与该作业分开；构建成功不能代替审计成功。机制见 [NuGet 官方审计说明](https://learn.microsoft.com/en-us/nuget/concepts/auditing-packages)。

## 许可及源码包

还原后可单独运行 `./scripts/verify_licenses.ps1`。它核对 App 还原资产中的全部包及下载依赖、逐版本许可与元数据、字节数和 SHA-256。许可归档保持原始字节，不做 Git 换行转换。

构建和发布校验共用 `scripts/release_source_files.ps1`；源码包包含仓库配置、CI、全部文档和公开示例，排除编译缓存及本地用户文件。源码 ZIP 和安装包均生成 SHA-256 文件。发行时将安装包、源码 ZIP 和校验文件放到 GitHub Releases；不要提交 dist 目录。

## 历史 UI 验证工具

`test_workbench_v23.ps1` 和 `test_workbench_playback.ps1` 是 2.5 工作台的历史工具，断言包含旧控件、播放文字和工艺配置，不是当前版本验收套件。需要图纸的模式须显式传入 `-Drawing`，不会读取作者电脑的默认路径。播放脚本要求 4 个轮廓，其中 3 个加工轮廓和 1 个母板；综合脚本需要足够轮廓以检查多选等交互。原私有验收图纸未随仓库提供。

`test_workbench_v23.ps1 -Part NcPreview` 无需 `-Drawing`。历史脚本只能在匹配版本及输入下使用，截图和人工操作结果须另外记录；不能以核心测试通过声称这些 UI 脚本也已通过。
