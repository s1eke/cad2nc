# 开发与发布检查

使用仓库 `global.json` 固定的 SDK 和 PowerShell 7。`build.ps1` 执行锁定还原、Release 回归、许可归档验证、自包含发布、发布 EXE 转换和安装包编译。正常构建使用官方 NuGet；本地工具缓存不是源码包的一部分。

## GitHub 自动检查

Windows 工作流在 push、pull request 和手动触发时运行两个独立作业：完整构建及示例复现、依赖审计。权限只有 `contents: read`，不发布 Release。建议将两个作业都设为合并前必需检查。

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
