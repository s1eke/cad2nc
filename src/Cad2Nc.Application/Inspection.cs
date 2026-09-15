using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Cad2Nc.Core;

namespace Cad2Nc.Application;

public enum InspectionKind { Automatic, Manual, Guidance }
public enum InspectionScope { Document, Generation }
public enum InspectionStatus { Pending, Passed, NeedsReview, Blocked, Confirmed, Information }

/// <summary>Read-only guidance and diagnostics.</summary>
public sealed record InspectionItem(string Id, string Title, string Summary, string Details,
    InspectionKind Kind, InspectionScope Scope, InspectionStatus Status,
    bool RequiresConfirmation, bool IsConfirmed, bool CanConfirm,
    string? RelatedField, ImmutableArray<string> ContourIds, string ActionLabel,
    long GenerationId, string? DocumentId)
{
    public string? GroupId => Kind is not (InspectionKind.Manual or InspectionKind.Guidance) ? null
        : Scope == InspectionScope.Document ? "review.drawing"
        : Id.StartsWith("review.", StringComparison.Ordinal) ? Id
        : RelatedField switch
        {
            "Contours" or "CutSide" => "review.selection",
            "ToolNumber" or "LengthOffset" or "ToolType" or "Diameter" or "SpindleSpeed" or "SpindleDirection" or "CuttingFeed" or "PlungeFeed" or "CutDirection" or "EdgeAllowance" => "review.tool",
            "CuttingDepth" or "SafeZ" or "LeaveSkin" or "MaxStepDown" or "BoardThickness" or "ZReference" or "EntryMode" or "LayeredCutting" or "PrioritizePartStability" or "RampLength" or "RampFeed" => "review.depth",
            _ => "review.origin"
        };

    public string StatusText => Status switch
    {
        InspectionStatus.Passed => "已通过",
        InspectionStatus.NeedsReview => "请您核对",
        InspectionStatus.Blocked => "需要处理",
        InspectionStatus.Confirmed => "已核对",
        InspectionStatus.Information => "提示",
        _ => "暂无结果"
    };
}

public sealed partial class WorkbenchSession
{
    private long generationId;
    private IEnumerable<Diagnostic> DocumentDiagnostics => GroupDiagnostics(
        (Document?.Diagnostics ?? []).Concat(taskDiagnostics));

    public int PendingInspectionCount => 0;

    public IReadOnlyList<InspectionItem> InspectionItems
    {
        get
        {
            var items = new List<InspectionItem>();
            var ready = Document is not null && Plan is not null && !HasStalePlan && nc is not null && !IsBusy && !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
            var selected = Contours.Where(c => c.IsIncluded).ToArray();
            var config = Config;
            var lncProfile = config.Machine.Profile == MachineProfile.LncRouter;
            var compensationDescription = lncProfile
                ? "软件已计算刀径补偿；宝元 LNC 现场格式不输出 G40，不能再叠加 G41/G42。"
                : "软件已计算刀径补偿并输出 G40，不能再叠加 G41/G42。";
            var controllerModes = lncProfile
                ? "毫米、XY 平面、绝对坐标、每分钟进给、G54、G43 H（宝元 LNC 现场格式不输出 G40）。"
                : "毫米、XY 平面、绝对坐标、每分钟进给、G54、G40、G43 H。";
            var errors = ValidationErrors;
            var drawingBounds = Contours.Count == 0 ? default : new Bounds2(Contours.Min(c => c.Contour.Bounds.MinX), Contours.Min(c => c.Contour.Bounds.MinY), Contours.Max(c => c.Contour.Bounds.MaxX), Contours.Max(c => c.Contour.Bounds.MaxY));
            var depthResult = errors.Count == 0 ? $"板面 Z {config.SurfaceZ:0.###} mm，切入 {config.SurfaceZ - config.Machining.CuttingDepth:0.###} mm，共 {NcGenerator.DepthPasses(config).Count} 遍。" : "参数修正后显示加工遍数。";
            Automatic("auto.document", "图纸读取", Document is null ? "先打开要加工的图纸" : $"已识别 {Contours.Count} 个闭合轮廓",
                "软件根据图纸中的闭合边线识别轮廓。图纸内容是否完整仍需对照原图核对。",
                Document is null ? InspectionStatus.Pending : InspectionStatus.Passed, InspectionScope.Document);
            Automatic("auto.selection", "加工范围", selected.Length == 0 ? "尚未选择加工轮廓" : $"已选择 {selected.Length} 个轮廓",
                "在工作台勾选要加工的轮廓，并分别选择加工外边还是内孔。",
                Document is null ? InspectionStatus.Pending : selected.Length == 0 ? InspectionStatus.Blocked : InspectionStatus.Passed,
                parameterKey: "Contours", ids: selected.Select(c => c.Id).ToImmutableArray());
            Automatic("auto.parameters", "参数格式与范围", errors.Count == 0 ? "参数数值符合软件要求" : $"有 {errors.Count} 项参数需要修改",
                errors.Count == 0 ? "通过数值校验不代表参数适合实际材料、刀具或机床，请按现场工艺核对。" : string.Join("\n", errors.Select(e => $"{FieldTitle(e.Key)}：{e.Value}")),
                errors.Count == 0 ? InspectionStatus.Passed : InspectionStatus.Blocked, parameterKey: errors.Keys.FirstOrDefault());
            Automatic("auto.plan", "刀路与加工程序", ready ? "当前参数的完整加工程序已生成" : HasStalePlan ? "参数已修改，请重新生成刀路" : "生成刀路后检查加工程序",
                "软件检查轮廓关系、刀具间隙和刀路完整性；任何选中轮廓无法加工时都不会输出部分程序。",
                ready ? InspectionStatus.Passed : Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error) ? InspectionStatus.Blocked : InspectionStatus.Pending);
            if (PlaybackError is { } playbackError)
                Automatic("auto.playback", "刀路播放", "当前加工程序无法播放预览", playbackError, InspectionStatus.Information);

            Manual("review.drawing", "图纸与尺寸", Document is null ? "打开图纸后核对" : $"{FileName} · {Contours.Count} 个闭合轮廓 · 单位 mm\n识别轮廓整体尺寸 {drawingBounds.Width:0.###} × {drawingBounds.Height:0.###} mm",
                "对照原图，核对零件形状、数量和关键尺寸；检查是否有遗漏或多余的轮廓。单位不明确时，先根据原图标注核实实际单位，不能仅凭画面大小判断。",
                InspectionScope.Document, "Contours", Contours.Select(c => c.Id).ToImmutableArray());
            if (Document?.UnitsUnspecified == true)
                Manual("drawing.units", "图纸单位未声明", "图纸没有声明单位，目前按毫米解释",
                    "对照原图的一条已知尺寸，核实这里的尺寸确实以毫米表示。若不一致，请在 CAD 中修正单位并重新导入。",
                    InspectionScope.Document, "Contours", Contours.Select(c => c.Id).ToImmutableArray());
            Manual("review.selection", "加工对象", Document is null ? "在工作台选择加工轮廓及内外侧" : $"已选 {selected.Length} 个轮廓：外边 {selected.Count(c => c.Side == CutSide.Outside)} 个，内孔 {selected.Count(c => c.Side == CutSide.Inside)} 个\n未加工 {Contours.Count - selected.Length} 个轮廓；点击“查看关联轮廓”核对已选对象。",
                "在图上逐一核对勾选的加工对象。外边用于零件外轮廓，内孔用于孔或内部开口；确认母板边框和不需要加工的轮廓没有被误选。",
                InspectionScope.Generation, "Contours", selected.Select(c => c.Id).ToImmutableArray());
            Manual("review.tool", "刀具、补偿与进给", Document is null ? "刀号 T、刀长补偿 H、刀径、主轴转速和进给的含义" : $"{config.Tool.ToolType} · 直径 {config.Tool.Diameter:0.###} mm · T{config.Tool.ToolNumber} / H{config.Tool.LengthOffset}\n主轴 {config.Machining.SpindleSpeed} r/min · 切削进给 {config.Machining.CuttingFeed:0.###} mm/min",
                "刀号 T 指定程序使用的刀具，刀长补偿号 H 指向机床刀长补偿表中的记录；H 不是刀具长度数值。T 与 H 可以不同，“H 跟随刀号”用于保持两者编号一致。刀径用于计算刀心路线，应与实际刀具匹配。\n主轴转速以 r/min 表示；切削进给和下刀进给以 mm/min 表示，分别控制平面切削和向下进刀的速度。参数是否适合材料、刀具及机床，需要结合现场工艺判断。" +
                    (Document is null ? "" : $"\n当前主轴方向：{(config.Tool.SpindleDirection == SpindleDirection.Clockwise ? "顺时针" : "逆时针")}；走刀方向：{(config.Machining.CutDirection == CutDirection.Clockwise ? "顺时针" : "逆时针")}；下刀进给 {config.Machining.PlungeFeed:0.###} mm/min；封边吃料 {config.Machining.EdgeAllowance:0.###} mm。"),
                InspectionScope.Generation, "ToolNumber");
            Manual("review.depth", "加工深度与安全高度", Document is null ? "加工深度决定最终 Z 位置；留皮厚度决定是否分两遍加工；安全高度用于抬刀和空移" : $"{depthResult}\n最终深度 Z {config.Machining.CuttingDepth:0.###} mm · 留皮 {config.Machining.LeaveSkin:0.###} mm · 安全高度 Z {config.Machining.SafeZ:0.###} mm",
                $"当前 Z 零点：{(config.Machining.ZReference == ZReference.WorkpieceBottom ? "板底／垫板上表面" : "板材上表面")}；板厚 {config.Machining.BoardThickness:0.###} mm。最终 Z 是工件坐标，切入量为板面 Z 减去最终 Z；板底为零时 Z−0.10 表示切穿后再下切 0.10 mm。\n安全 Z 必须高于板面，当前高出板面 {config.Machining.SafeZ - config.SurfaceZ:0.###} mm。停车 Z 启用时不得低于安全 Z。软件没有读取压板、夹具或机床行程。\n{(config.Machining.LeaveSkin > 0 ? $"先完成所有路线的留皮层，再完成最终层；留皮层位于 Z {config.SurfaceZ - config.Machining.BoardThickness + config.Machining.LeaveSkin:0.###}。" : "当前不留皮；是否分层由「分层加工」决定。")} {(config.Machining.EntryMode == EntryMode.Ramp ? $"沿已补偿轮廓按 {config.Machining.RampLength:0.###} mm 路程斜下刀，到本层深度后完整再走一圈。" : "当前采用垂直下刀。")}",
                InspectionScope.Generation, "CuttingDepth");
            var origin = ready ? $"G54 对应 CAD ({Plan!.Origin.X:0.###}, {Plan.Origin.Y:0.###}) mm\n刀心范围 {Plan.Bounds.Width:0.###} × {Plan.Bounds.Height:0.###} mm" : "生成刀路后显示工件原点和加工范围";
            Manual("review.origin", "原点与刀路", origin,
                "当前原点方式：" + (config.OriginMode switch { OriginMode.SheetLowerLeft => "指定母板左下角；加工选择变化不移动此原点。", OriginMode.Manual => "手动指定图纸中对应机床 X0/Y0 的坐标。", _ => "按所选刀心路线包围框左下角设置原点。" }) + "对照画布 G54 标记核对原点与刀路范围。补偿产生的负 X/Y 会保留，不能用刀心最小值代替母板角点。" + compensationDescription + "\n\n现场核对机床 G54、刀具、H 刀长表与装夹位置。二维播放只显示编程坐标，不包含真实偏置、夹具碰撞、行程或加减速。",
                InspectionScope.Generation, "Contours", selected.Select(c => c.Id).ToImmutableArray());
            Manual("review.controller", "机床与控制器适配", ConfigurationDefaults.Name(config.Machine.Profile),
                controllerModes + $"刀具调用：{(config.Machine.ToolChange == ToolChangeMode.ToolOnly ? "单独 T 刀号，不输出 M06" : "T 刀号与 M06")}。" +
                (config.Machine.ParkAtEnd ? $"结束先停主轴并抬至 Z{config.Machine.ParkZ:0.###}，再移至 X{config.Machine.ParkX:0.###} Y{config.Machine.ParkY:0.###}，期间保留刀长补偿。" : "结束不移动至指定停车点。") +
                (config.Machine.EmitM17 ? "末尾 M17 后接 M30，M17 功能由机床定义，其动作和耗时未模拟。" : "程序以 M30 结束。") +
                "\n\n配置随任务保存；设置里的“保存当前参数为启动默认”只影响以后启动。二维预览不读取机床偏置、实际刀具、装夹或控制器状态，换刀内部动作和时间未模拟。",
                InspectionScope.Generation);

            foreach (var diagnostic in DocumentDiagnostics) AddDiagnostic(diagnostic, InspectionScope.Document);
            var documentItems = (Document?.Diagnostics ?? []).Concat(taskDiagnostics).ToHashSet();
            foreach (var diagnostic in GroupDiagnostics(Diagnostics.Where(d => !documentItems.Contains(d))))
                AddDiagnostic(diagnostic, InspectionScope.Generation);
            return items;

            void Automatic(string id, string title, string summary, string details, InspectionStatus state,
                InspectionScope scope = InspectionScope.Generation, string? parameterKey = null, ImmutableArray<string> ids = default)
                => items.Add(new(id, title, summary, details, InspectionKind.Automatic, scope, state, false, false, false,
                    parameterKey, ids.IsDefault ? [] : ids, ActionFor(parameterKey, ids), generationId, Document?.Id));
            void Manual(string id, string title, string summary, string details, InspectionScope scope,
                string? parameterKey = null, ImmutableArray<string> ids = default)
            {
                items.Add(new(id, title, summary, details, InspectionKind.Guidance, scope, InspectionStatus.Information,
                    false, false, false, parameterKey, ids.IsDefault ? [] : ids,
                    Document is null ? "" : ActionFor(parameterKey, ids), generationId, Document?.Id));
            }
            void AddDiagnostic(Diagnostic diagnostic, InspectionScope scope)
            {
                var (title, guidance) = DiagnosticGuidance(diagnostic);
                var summary = diagnostic.Code.StartsWith("reader.", StringComparison.Ordinal)
                    ? $"共 {diagnostic.Count} 项图纸读取提示。请按下方说明核对；原始信息可用于排查。"
                    : diagnostic.Message.Replace("open or invalid linework", "未闭合或无效边线", StringComparison.Ordinal);
                var details = diagnostic.Remedy ?? guidance;
                if (diagnostic.Code.StartsWith("reader.", StringComparison.Ordinal)) details += "\n\n原始读取信息：\n" + diagnostic.Message;
                Automatic(DiagnosticId(diagnostic), title, summary, details,
                    diagnostic.Severity == DiagnosticSeverity.Error ? InspectionStatus.Blocked : InspectionStatus.Information,
                    scope, diagnostic.RelatedField, diagnostic.ContourIds);
            }
        }
    }

    public void SetInspectionConfirmed(string id, bool confirmed)
    { }

    /// <summary>Compatibility no-op; guidance no longer stores an acknowledgement.</summary>
    public void SetInspectionConfirmed(InspectionItem item, bool confirmed)
    { }

    private static string DiagnosticId(Diagnostic diagnostic) => "diagnostic." + diagnostic.Code + "." +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(diagnostic.Message)))[..16];
    private static string ActionFor(string? parameterKey, ImmutableArray<string> ids) => parameterKey == "Contours" || parameterKey == "CutSide" || !ids.IsDefaultOrEmpty
        ? "查看关联轮廓" : parameterKey is not null ? "修改相关参数" : "";
    private static IEnumerable<Diagnostic> GroupDiagnostics(IEnumerable<Diagnostic> diagnostics) => diagnostics
        .GroupBy(d => (d.Code, d.Severity, d.RequiresConfirmation))
        .Select(g => g.First() with
        {
            Message = string.Join("\n", g.Select(d => d.Message).Distinct().Order(StringComparer.Ordinal)),
            Count = g.Sum(d => d.Count), RelatedContourIds = g.SelectMany(d => d.ContourIds).Distinct().ToImmutableArray()
        });
    private static (string Title, string Guidance) DiagnosticGuidance(Diagnostic diagnostic) => diagnostic.Code switch
    {
        "units.unspecified" => ("图纸单位未声明", "当前按毫米解释，可对照原图的已知尺寸核实。若实际不是毫米，请在 CAD 中修正后重新导入。"),
        "units.converted" => ("图纸单位已换算", "软件已统一换算为毫米，请在“图纸与尺寸”中对照原图尺寸核对。"),
        "geometry.open_linework" => ("部分边线没有组成轮廓", "可对照原图检查所列图层。需要加工的边线应在 CAD 中闭合后重新导入。"),
        "geometry.ignored" => ("部分图形未用于加工", "对照原 CAD 检查忽略的实体类型，确认它们没有包含需要加工的内容。若有遗漏，请在 CAD 中转换为支持的闭合边线并重新导入。"),
        "geometry.elevation" => ("图纸已投影到平面", "核对原 CAD 的轮廓与投影后的图形一致；程序 Z 深度由当前加工参数决定。"),
        "layers.encoding" => ("部分图层名称无法识别", "对照原 CAD，通过形状和尺寸核对图层中的加工对象，避免依赖乱码名称选错轮廓。"),
        "reader.unsupported" or "reader.truncated" => ("请核对图纸是否完整", "对照原 CAD，核对形状、数量及关键尺寸，确认没有遗漏加工内容。下方原始读取信息可用于排查。"),
        "reader.reference" or "reader.notice" => ("图纸读取提示", "这是读取器提供的补充信息，可结合原 CAD 核对。"),
        "task.source_changed" => ("任务引用的图纸已变化", "请重新核对全部加工选择与内外侧设置；任务中的轮廓选择已重置。"),
        "nested_outside" => ("确认这是零件外边还是内孔", "定位关联轮廓并对照原图。如果它是孔，请返回工作台改成内孔加工，然后重新生成；若确实是独立零件，请逐一核对后确认。"),
        "plan.stale" => ("当前刀路需要更新", "返回工作台重新生成刀路，然后重新核对本次加工设置。"),
        "plan.finished_profile" => ("当前吃料值无法保留完整轮廓", "定位轮廓并核对吃料值和加工侧；必要时修正原图。修改后重新生成。"),
        "plan.tool_fit" => ("刀具无法通过部分位置", "定位轮廓并核对刀具直径和加工侧。更换合适刀具或修正加工方案后重新生成。"),
        "plan.outside_clearance" => ("零件之间的刀具间隙不足", "定位关联轮廓，核对内外侧与刀具直径；若排版间隙不足，请调整原图后重新导入。"),
        "plan.inside_relationship" => ("内孔与外边关系需要修改", "定位孔和外轮廓，核对加工侧、吃料值与原图，修正后重新生成。"),
        "plan.workholding_unmodeled" => ("仍需可靠装夹", "稳定加工顺序只能降低零件切断后移动的风险，不能替代压板、真空吸附、桥位或底部薄皮。请在机床上确认夹具和板材固定可靠后再加工。"),
        "plan.stable_order_disabled" => ("传统加工顺序已启用", "当前未启用优先保持零件稳定，逐轮廓加工可能较早切断零件。请核对装夹，或返回工作台启用稳定加工顺序后重新生成。"),
        "plan.stable_entry_fallback" => ("安全下刀点已回退", "没有找到可评分的几何安全点，软件保留了原下刀点。请在预览中核对下刀位置并确认装夹可靠。"),
        "plan.stable_sheet_unknown" => ("母板边界未能确认", "未能唯一确定母板边界，安全下刀点评估仅参考其他成品边。请核对母板设置、下刀位置和装夹。"),
        _ => (diagnostic.Severity == DiagnosticSeverity.Error ? "加工检查未通过" : "加工提示",
            "请根据具体信息修正相关轮廓或参数，然后重新生成。无法在本软件修改的图形需要回到 CAD 中处理。")
    };
    private static string FieldTitle(string parameterKey) => parameterKey switch
    {
        "ToolNumber" => "刀号 T", "LengthOffset" => "刀长补偿号 H", "ToolType" => "刀具名称", "Diameter" => "刀具直径",
        "SpindleSpeed" => "主轴转速", "SpindleDirection" => "主轴方向", "CuttingFeed" => "切削进给", "CutDirection" => "走刀方向",
        "CuttingDepth" => "加工深度", "SafeZ" => "安全高度", "PlungeFeed" => "下刀进给", "LeaveSkin" => "留皮厚度", "MaxStepDown" => "单层下切", "EdgeAllowance" => "封边吃料",
        "BoardThickness" => "板厚", "ZReference" => "Z 零点基准", "EntryMode" => "下刀方式", "LayeredCutting" => "分层切割", "PrioritizePartStability" => "优先保持零件稳定",
        "RampLength" => "斜坡长度", "RampFeed" => "斜下刀进给", "OriginMode" => "XY 原点方式", "SheetContourId" => "母板",
        "ManualOrigin" => "手动原点", "ParkX" => "停车 X", "ParkY" => "停车 Y", "ParkZ" => "停车 Z", "ToolChange" => "刀具调用方式", "Profile" => "机床配置",
        _ => "加工参数"
    };
}
