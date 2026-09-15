# CAD2NC 2.6.4 license text archive

Initially collected on 2026-09-04; extended on 2026-09-15 for QRCoder and its Windows dependencies from the App project's restored dependency graph. `manifest.json` records the exact package versions, original metadata, source URLs, SHA-256 hashes, byte lengths and collection method for every archived file.

Package directories preserve the original license and notice bytes and their paths inside the corresponding NuGet package. The matching `.nuspec` is included as evidence of the package's declared license. Packages used only during the build are included conservatively; their presence here does not mean that their binaries are included in the release.

The following packages do not contain their own full license text:

- ACadSharp 3.7.1 and NetTopologySuite 2.6.0 declare MIT and BSD-3-Clause respectively. Their complete, matching upstream version-tag license files are archived, including NetTopologySuite's embedded third-party acknowledgements.
- QRCoder 1.6.0 declares MIT. Its LICENSE.txt is archived from commit bd980577640c47f8bb881cf24c8443415a579d36, the exact repository commit recorded in the NuGet metadata.
- Microsoft.Graphics.Win2D 1.4.0 points to a legacy Microsoft EULA URL which currently redirects to unrelated documentation. `eula_win2d_10012014.htm` is the complete archived original Microsoft page from 2025-01-13, last modified on the original server on 2018-05-30. Its original HTML encoding is `windows-1252`. The manifest explicitly identifies the archive URL and original URL. The source repository's MIT license is not used as a substitute for this NuGet EULA.

Microsoft.Windows.SDK.BuildTools 10.0.26100.4654 and Microsoft.Windows.SDK.NET.Ref 10.0.26100.57 also provide their license by URL. Their `sdk_license.rtf` files are the original document returned by that exact Microsoft license URL at collection time. The document is preserved as RTF, without conversion or alteration.

The implicitly downloaded .NET 10.0.11 runtime packs are included alongside the normal NuGet libraries, so that runtime license and third-party notice files are not lost by inspecting only `packages.lock.json`. Every package-provided `LICENSE`, `NOTICE`, `ThirdPartyNotices` and corresponding variant was collected recursively.

This archive records texts and provenance. Each component retains its own license terms; this index does not relicense any component or state that all licenses are mutually compatible. See `docs/THIRD_PARTY_V2.md` for the Chinese component inventory.
