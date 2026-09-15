# CAD2NC 应用图标

`CAD2NC-source.png` 是 imagegen 内置工具生成、简化并透明抠图的原始图像；`CAD2NC.png` 是界面使用的 256 像素图像；`CAD2NC.ico` 包含 16、24、32、48、64、128、256 像素的 32 位透明图标。

通过 `pwsh scripts/prepare_icons.ps1` 从源图重新生成 PNG/ICO。脚本仅使用 Windows System.Drawing 缩放和封装，不调用图像模型，不需要 Python。可传入 `-PreviewPath build/icon-preview.png` 生成实际尺寸的明暗背景检查图。

生成方式：内置 imagegen，2026-09-04。设计提示：

> Windows CAD2NC application icon: navy rounded-square, cyan machined plate contour and a simplified thick milling cutter silhouette; one thick pale-cyan flowing toolpath with a circular endpoint. Broad simple shapes, bold negative space, very restrained gradient, no text, centered square composition with clear padding and a genuinely transparent exterior. Recognizable at small desktop/taskbar sizes.

最后一次编辑提示：

> BACKGROUND EXTRACTION ONLY. Remove the entire light grey/white checkerboard background OUTSIDE the blue rounded-square icon and replace it with actual alpha=0 TRANSPARENCY in the delivered PNG. Preserve the icon artwork, geometry, navy fill, cyan cutter and lines exactly. Do not draw a checkerboard. Do not draw a white or black background. The output MUST be a transparent PNG cutout, with the exterior pixels transparent, not RGB. Keep the original square canvas and icon position. Make clean antialiased edges.
