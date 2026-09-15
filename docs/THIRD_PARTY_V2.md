# CAD2NC 2.6.4 第三方组件

CAD2NC 自有代码的原有授权为 GNU GPL v3，完整文本见 `LICENSE`。第三方组件各自适用下列原始条款。本清单适用于当前 C# 构建。

许可归档见 `LICENSES/v2`，包含逐版本的原始 NuGet 元数据、完整许可和第三方声明。`manifest.json` 记录每个文件的来源 URL、SHA-256 和字节数。下表依据实际使用版本的 NuGet 包元数据与包内文本，不以 GitHub 仓库的源码许可证替代 NuGet 二进制包条款。

| 组件 | 锁定版本 | 实际包许可与归档内容 |
| --- | --- | --- |
| ACadSharp | 3.7.1 | NuGet 声明 MIT；包内无许可文件，归档 [v3.7.1 原始 LICENSE](https://raw.githubusercontent.com/DomCR/ACadSharp/v3.7.1/LICENSE)。 |
| NetTopologySuite | 2.6.0 | NuGet 声明 BSD-3-Clause；归档 [v2.6.0 完整 License.md](https://raw.githubusercontent.com/NetTopologySuite/NetTopologySuite/v2.6.0/License.md)，同时保留 JTS、RTools.Util、High Speed Priority Queue 的声明。 |
| CommunityToolkit.Mvvm | 8.4.0 | MIT；包内 `License.md`、`ThirdPartyNotices.txt`。 |
| Microsoft.Graphics.Win2D | 1.4.0 | NuGet 指定 Microsoft Win2D 软件许可条款，要求接受许可；完整原页存档见下文。 |
| Microsoft.WindowsAppSDK、Microsoft.WindowsAppSDK.Runtime | 2.4.0 | 包内 Microsoft Windows App SDK 软件许可条款、`NOTICE.txt`。 |
| Microsoft.WindowsAppSDK.Base | 2.0.4 | 包内 Microsoft Windows App SDK 软件许可条款、`NOTICE.txt`。 |
| Microsoft.WindowsAppSDK.Foundation | 2.3.9 | 包内 Microsoft Windows App SDK 软件许可条款。 |
| Microsoft.WindowsAppSDK.WinUI | 2.3.6 | 包内 Microsoft Windows App SDK 软件许可条款、根目录和 `tools` 下的声明。 |
| Microsoft.WindowsAppSDK.AI、Microsoft.WindowsAppSDK.Search | 2.4.4 | 各包内 Microsoft Windows App SDK 软件许可条款。 |
| Microsoft.WindowsAppSDK.DWrite | 2.1.0 | 包内 Microsoft Windows App SDK 软件许可条款。 |
| Microsoft.WindowsAppSDK.InteractiveExperiences | 2.1.6 | 包内 Microsoft Windows App SDK 软件许可条款。 |
| Microsoft.WindowsAppSDK.Widgets | 2.0.5 | 包内 Microsoft Windows App SDK 软件许可条款。 |
| Microsoft.WindowsAppSDK.ML、Microsoft.Windows.AI.MachineLearning | 2.1.74 | 各包内 Windows App SDK / Windows Machine Learning 对应条款及 `ThirdPartyNotices.txt`。 |
| Microsoft.Web.WebView2 | 1.0.3719.77 | 包内 Microsoft 版权及再分发条款 `LICENSE.txt`、`NOTICE.txt`。 |
| System.Numerics.Tensors | 9.0.0 | MIT；包内 `LICENSE.TXT`、`THIRD-PARTY-NOTICES.TXT`。 |
| QRCoder | 1.6.0 | MIT；NuGet 包无许可正文，归档包元数据指定的源码提交 bd980577640c47f8bb881cf24c8443415a579d36 中的 LICENSE.txt 及包元数据。 |
| System.Drawing.Common、Microsoft.Win32.SystemEvents | 6.0.0 | QRCoder 的 Windows 目标传递依赖；MIT，归档对应版本包内 LICENSE.TXT、THIRD-PARTY-NOTICES.TXT 及包元数据。 |
| Microsoft.Windows.SDK.NET.Ref | 10.0.26100.57 | NuGet 指定 Microsoft Windows SDK 条款；归档其许可 URL 返回的原始 `sdk_license.rtf`。 |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.4654 | 构建依赖；NuGet 指定 Microsoft Windows SDK 条款，归档原始 `sdk_license.rtf`。 |
| Microsoft.Windows.SDK.BuildTools.MSIX | 1.7.251221100 | 构建依赖；包内 `sdk_license.txt`、`NOTICE.txt`。 |
| Microsoft.NETCore.App.Runtime.win-x64 | 10.0.11 | .NET 自包含运行时；MIT、完整 `THIRD-PARTY-NOTICES.TXT`。 |
| Microsoft.AspNetCore.App.Runtime.win-x64、Microsoft.WindowsDesktop.App.Runtime.win-x64 | 10.0.11 | SDK 下载依赖；归档各包内 MIT 许可及随包存在的第三方声明，不代表 CAD2NC 会发布这两个完整运行时。 |

Win2D 1.4.0 的 `.nuspec` 指向 `http://www.microsoft.com/web/webpi/eula/eula_win2d_10012014.htm`。截至 2026-09-04，该地址重定向到无关的 Microsoft Learn 页面；上游已有 [issue #997](https://github.com/microsoft/Win2D/issues/997)。本归档保存该微软原页的 [2025-01-13 历史快照](https://web.archive.org/web/20250113180923id_/https://www.microsoft.com/web/webpi/eula/eula_win2d_10012014.htm)，原页最后修改于 2018-05-30，保留完整 12 节及法语附文，HTML 原编码为 `windows-1252`。此文件为原页存档，不能标成当前在线页面或源码 MIT 许可。

Windows SDK 两个仅提供许可 URL 的包均指向 [WinSDKLicenseURL](https://aka.ms/WinSDKLicenseURL)，归档其实际返回的 [微软原始 RTF](https://download.microsoft.com/download/0/F/F/0FF2B061-47DD-4F55-89B6-FD1D8C44F14D/sdk_license.rtf)。WinRT 投影来自隐式 `Microsoft.Windows.SDK.NET.Ref` 包，其许可不能由某个 C#/WinRT 源码仓库的 MIT 文件替代。

版本清单涵盖 App 的 `project.assets.json` 中全部 NuGet `libraries` 和 SDK `downloadDependencies`，并保守保留构建依赖的许可。普通 NuGet 依赖由 `packages.lock.json` 锁定；隐式运行时和 Windows SDK 引用包另由 SDK、目标框架及实际还原资产决定。发布构建会复制此归档，并收集还原包中的许可和声明原文。使用的 SDK 为 10.0.400，运行时为 10.0.11。

归档许可文本不改变各组件的授权范围，也不构成许可证之间兼容性的结论；Microsoft 包中的可分发代码、分发限制及第三方材料条款仍以原文为准。

发布包和当前源码树不包含旧 Python 加工实现、ezdxf、Shapely、GEOS 或 LibreDWG 运行时。`scripts/prepare_dotnet.py` 与 `scripts/nuget_bridge.py` 仅用于受限开发环境准备，不进入发布程序。移除旧依赖不改变 CAD2NC 自有代码已有的 GPL 授权。
