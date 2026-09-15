using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.Json;
using Cad2Nc.Core;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cad2Nc.Application;

public enum GenerationOutcome { Idle, Running, Succeeded, Failed, Canceled, Superseded }

public sealed class ContourRow : ObservableObject
{
    public Contour Contour { get; }
    public string Id => Contour.Id;
    public string Layer => Contour.Layer;
    public double Area => Contour.Area;
    public bool IsOuterCandidate => Contour.IsOuterCandidate;
    public string SizeText => $"{Contour.Bounds.Width:0.###} × {Contour.Bounds.Height:0.###} mm";
    private bool included;
    private CutSide side;
    private bool focused;
    private string toolpathOrderText = "";
    public bool IsIncluded { get => included; set => SetProperty(ref included, value); }
    public CutSide Side { get => side; set => SetProperty(ref side, value); }
    public bool IsFocused { get => focused; set => SetProperty(ref focused, value); }
    public string ToolpathOrderText { get => toolpathOrderText; set => SetProperty(ref toolpathOrderText, value ?? ""); }
    public ContourRow(Contour contour, ContourSelection selection)
    { Contour = contour; included = selection.Included; side = selection.Side; }
}

/// <summary>A UI-independent editing session. Only results for the current revision can be exported.</summary>
public sealed partial class WorkbenchSession : ObservableObject, IDisposable
{
    private readonly ICadImporter importer;
    private readonly Func<PlanningRequest, CancellationToken, Task<ToolpathPlan>> planner;
    private readonly Func<string, CancellationToken, Task<NcDocument>> ncReader;
    private readonly Func<string, ConversionConfig, NcPlaybackProgram> generatedParser;
    private readonly Stack<EditSnapshot> undo = new();
    private readonly Stack<EditSnapshot> redo = new();
    private readonly Dictionary<string, string> inputErrors = new();
    private readonly Dictionary<string, double> rejectedNumbers = new();
    private readonly List<Diagnostic> taskDiagnostics = new();
    private CancellationTokenSource? operation;
    private long operationId;
    private long revision;
    private bool suppressEdits;
    private bool busy;
    private bool loadingFile;
    private bool disposed;
    private string status = "打开 DWG / DXF 设置加工，或打开 NC 查看程序";
    private ConversionConfig config;
    private WorkpieceSetup workpiece = new();
    private ViewState view;
    private EditSnapshot? lastSnapshot;
    private string? savedState;
    private string? nc;
    private BatchNcGenerationResult? batchResult;
    private BatchNcProgram? selectedBatchProgram;
    private bool saving;
    private GenerationOutcome generationOutcome;

    public WorkbenchSession(UserStore? store = null, ICadImporter? importer = null,
        Func<PlanningRequest, CancellationToken, Task<ToolpathPlan>>? planner = null,
        Func<string, CancellationToken, Task<NcDocument>>? ncReader = null,
        Func<string, ConversionConfig, NcPlaybackProgram>? generatedParser = null)
    {
        Store = store ?? new(); this.importer = importer ?? new CadImporter();
        this.planner = planner ?? ((request, token) => Task.Run(() => ToolpathPlanner.Generate(request, token), token));
        this.ncReader = ncReader ?? NcDocumentReader.ReadAsync;
        this.generatedParser = generatedParser ?? NcPlaybackParser.ParseGenerated;
        config = Store.StartupConfig; view = Store.LastView with { Theme = Store.Theme };
        lastSnapshot = Capture();
        if (Store.LoadWarning is { } warning) Diagnostics.Add(new("settings.load", warning, DiagnosticSeverity.Information));
    }
    public UserStore Store { get; }
    public NcPlaybackController Playback { get; } = new();
    public string? PlaybackError { get; private set; }
    public CadDocument? Document { get; private set; }
    public NcDocument? NcDocument { get; private set; }
    public bool IsNcDocument => NcDocument is not null;
    public ToolpathPlan? Plan { get; private set; }
    public BatchNcGenerationResult? BatchResult => batchResult;
    public BatchNcProgram? SelectedBatchProgram => selectedBatchProgram;
    public string? SelectedBatchSheetId => selectedBatchProgram?.SheetContourId;
    public bool IsBatchMode => batchResult is not null;
    public ObservableCollection<ContourRow> Contours { get; } = [];
    public ObservableCollection<Diagnostic> Diagnostics { get; } = [];
    public IReadOnlyList<string> NcLines { get; private set; } = Array.Empty<string>();
    public ConversionConfig Config => config;
    public WorkpieceSetup Workpiece => workpiece;
    /// <summary>Returns the current per-contour machining choices for batch operations.</summary>
    public IReadOnlyDictionary<string, ContourSelection> CurrentSelection => Selection();
    public string? TaskPath { get; private set; }
    public string SourcePath => NcDocument?.SourcePath ?? Document?.SourcePath ?? "";
    public string FileName => string.IsNullOrEmpty(SourcePath) ? "未打开文件" : Path.GetFileName(SourcePath);
    // Keep generated LNC text safe to paste into a controller as well as safe
    // to export.  The internal generated string retains path comments for
    // playback labels; FormatForMachine removes those preview-only lines.
    public string NcCode => NcDocument?.Code ?? (selectedBatchProgram is { } batch
        ? NcGenerator.FormatForMachine(batch.Code, Config, windowsLineEndings: true)
        : nc is { } generated
            ? NcGenerator.FormatForMachine(generated, Config, windowsLineEndings: true)
            : "");
    public long Revision => revision;
    public GenerationOutcome GenerationOutcome
    {
        get => generationOutcome;
        private set => SetProperty(ref generationOutcome, value);
    }
    public string Status { get => status; private set => SetProperty(ref status, value); }
    public bool IsBusy { get => busy; private set { if (SetProperty(ref busy, value)) NotifyAvailability(); } }
    // Kept for callers compiled against the former checklist. Review is now optional and read-only.
    public bool RequiresUnitsConfirmation => false;
    public bool RequiresDiagnosticsConfirmation => false;
    public bool UnitsConfirmed { get => true; set { } }
    public bool DiagnosticsConfirmed { get => true; set { } }
    public bool CanUndo => !IsBusy && undo.Count > 0;
    public bool CanRedo => !IsBusy && redo.Count > 0;
    public bool IsDirty => Document is not null && StateFingerprint() != savedState;
    public bool HasStalePlan =>
        (Plan is not null && (Plan.Revision != revision || nc is null)) ||
        (batchResult is not null && (batchResult.Programs.IsDefaultOrEmpty ||
            batchResult.Programs.Any(program => program.Plan.Revision != revision)));
    public bool CanGenerate => !IsBusy && Document is not null && Contours.Any(c => c.IsIncluded) && ValidationErrors.Count == 0;
    public bool CanSaveTask => !IsBusy && !saving && Document is not null && ValidationErrors.Count == 0;
    public bool CanPlay => !IsBusy && !HasStalePlan && (!IsBatchMode || selectedBatchProgram is not null) && Playback.CanPlay;
    public bool CanCopy => !IsBusy && (IsNcDocument || (CanExport && (!IsBatchMode || selectedBatchProgram is not null)));
    public bool CanExport => !IsBusy && Document is not null && !HasStalePlan && ValidationErrors.Count == 0
        && ((Plan is not null && nc is not null) || (batchResult?.Succeeded == true))
        && Contours.Any(c => c.IsIncluded) && !Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error)
        && !Document.Diagnostics.Any(d => d.Severity == DiagnosticSeverity.Error);
    public IReadOnlyDictionary<string, string> ValidationErrors
    {
        get
        {
            var errors = new Dictionary<string, string>(Config.GetValidationErrors());
            if (Document is not null)
            {
                var multipleSheets = ToolpathPlanner.FindSheetCandidates(Document).Count > 1;
                if (!multipleSheets && Config.OriginMode == OriginMode.Manual && (Workpiece.ManualOrigin is not { } point || !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
                    errors[nameof(WorkpieceSetup.ManualOrigin)] = "请指定有限的 CAD 原点 X、Y 坐标";
                if (Config.OriginMode == OriginMode.SheetLowerLeft && !multipleSheets)
                {
                    try { ToolpathPlanner.ResolveSheetContour(Document, Workpiece); }
                    catch (Exception e) when (e is ArgumentException or ToolpathGenerationException)
                    { errors[nameof(WorkpieceSetup.SheetContourId)] = e.Message; }
                }
            }
            foreach (var pair in inputErrors) errors[pair.Key] = pair.Value;
            return errors;
        }
    }
    public ViewState CurrentView
    {
        get => view;
        set { var sanitized = value.Sanitize(); if (SetProperty(ref view, sanitized)) Store.LastView = view; }
    }

    public void UpdateConfig(ConversionConfig value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value == config) return;
        var changedOrigin = config.OriginMode != value.OriginMode;
        if (changedOrigin && config.OriginMode == OriginMode.Manual)
            foreach (var key in new[] { "ManualOriginX", "ManualOriginY" }) { inputErrors.Remove(key); rejectedNumbers.Remove(key); }
        config = value; Store.LastConfig = value; EnsureSheetSetup(changedOrigin); OnPropertyChanged(nameof(Config)); CommitEdit();
    }
    public void UpdateWorkpiece(WorkpieceSetup value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.ManualOrigin is { } origin && (!double.IsFinite(origin.X) || !double.IsFinite(origin.Y)))
            throw new ArgumentException("手动原点须为有限坐标", nameof(value));
        if (value == workpiece) return;
        var changedSheet = workpiece.SheetContourId != value.SheetContourId;
        workpiece = value;
        if (changedSheet) ExcludeSheet();
        OnPropertyChanged(nameof(Workpiece)); CommitEdit();
    }

    public void UpdateManualOriginCoordinate(string field, double value)
    {
        if (field is not ("ManualOriginX" or "ManualOriginY")) throw new ArgumentException("手动原点字段无效", nameof(field));
        if (!double.IsFinite(value)) { SetInputError(field, "请输入有限的 CAD 原点坐标", value); return; }
        rejectedNumbers[field] = value;
        double Read(string key, double? saved) => rejectedNumbers.TryGetValue(key, out var pending) ? pending : saved ?? double.NaN;
        var x = Read("ManualOriginX", workpiece.ManualOrigin?.X);
        var y = Read("ManualOriginY", workpiece.ManualOrigin?.Y);
        if (double.IsFinite(x) && double.IsFinite(y))
        {
            workpiece = workpiece with { ManualOrigin = new(x, y) };
            foreach (var key in new[] { "ManualOriginX", "ManualOriginY" }) { inputErrors.Remove(key); rejectedNumbers.Remove(key); }
        }
        else
        {
            inputErrors[field] = "请同时填写原点 X、Y";
            var other = field == "ManualOriginX" ? "ManualOriginY" : "ManualOriginX";
            inputErrors[other] = "请输入有限的 CAD 原点坐标";
        }
        OnPropertyChanged(nameof(Workpiece)); CommitEdit();
    }

    private void EnsureSheetSetup(bool exclude = false)
    {
        if (Document is null || Config.OriginMode != OriginMode.SheetLowerLeft) return;
        if (workpiece.SheetContourId is null)
        {
            var candidates = ToolpathPlanner.FindSheetCandidates(Document);
            if (candidates.Count == 1) { workpiece = workpiece with { SheetContourId = candidates[0].Id }; exclude = true; }
        }
        if (exclude) ExcludeSheet();
        OnPropertyChanged(nameof(Workpiece));
    }

    private void ExcludeSheet()
    {
        if (Config.OriginMode != OriginMode.SheetLowerLeft || Workpiece.SheetContourId is null) return;
        var previous = suppressEdits; suppressEdits = true;
        try { foreach (var row in Contours.Where(c => c.Id == Workpiece.SheetContourId)) row.IsIncluded = false; }
        finally { suppressEdits = previous; }
    }
    public bool TryGetRejectedNumber(string field, out double value) => rejectedNumbers.TryGetValue(field, out value);

    public void SetInputError(string field, string? error, double? rejectedValue = null)
    {
        bool errorChanged;
        if (error is null) errorChanged = inputErrors.Remove(field);
        else { errorChanged = inputErrors.GetValueOrDefault(field) != error; inputErrors[field] = error; }
        bool numberChanged;
        if (error is not null && rejectedValue is { } number)
        {
            numberChanged = !rejectedNumbers.TryGetValue(field, out var previous) || !previous.Equals(number);
            rejectedNumbers[field] = number;
        }
        else numberChanged = rejectedNumbers.Remove(field);
        if (!errorChanged && !numberChanged) return;
        CommitEdit();
    }
    public void RestoreDefaults() => ApplyPreset(ConfigurationDefaults.ForProfile(Config.Machine.Profile));
    public void ApplyPreset(ConversionConfig value)
    {
        value.Validate(); inputErrors.Clear(); rejectedNumbers.Clear();
        var changedOrigin = config.OriginMode != value.OriginMode;
        config = value; Store.LastConfig = value;
        EnsureSheetSetup(changedOrigin);
        var previousRevision = revision;
        OnPropertyChanged(nameof(Config)); CommitEdit();
        // Explicit import/reset must invalidate even when the imported numbers equal the current values.
        if (revision == previousRevision) { revision++; Invalidate("加工参数已应用，请重新生成刀路"); }
    }
    public void SetIncluded(IEnumerable<string> ids, bool included)
    { Batch(ids, row => row.IsIncluded = included); }
    public void SetCutSide(IEnumerable<string> ids, CutSide side)
    {
        if (!Enum.IsDefined(side)) throw new ArgumentException("加工侧无效");
        Batch(ids, row => row.Side = side);
    }
    private void Batch(IEnumerable<string> ids, Action<ContourRow> action)
    {
        var set = ids.ToHashSet();
        suppressEdits = true;
        try { foreach (var row in Contours.Where(c => set.Contains(c.Id))) action(row); }
        finally { suppressEdits = false; }
        CommitEdit();
    }
    public void Undo()
    {
        if (!CanUndo) return;
        redo.Push(Capture()); Restore(undo.Pop());
    }
    public void Redo()
    {
        if (!CanRedo) return;
        undo.Push(Capture()); Restore(redo.Pop());
    }
    private void Restore(EditSnapshot snapshot)
    {
        suppressEdits = true;
        try
        {
            config = snapshot.Config; workpiece = snapshot.Workpiece; inputErrors.Clear(); rejectedNumbers.Clear();
            foreach (var error in snapshot.InputErrors) inputErrors[error.Key] = error.Value;
            foreach (var rejected in snapshot.RejectedNumbers) rejectedNumbers[rejected.Key] = rejected.Value;
            foreach (var row in Contours)
                if (snapshot.Selection.TryGetValue(row.Id, out var selection)) { row.IsIncluded = selection.Included; row.Side = selection.Side; }
        }
        finally { suppressEdits = false; }
        Store.LastConfig = config; lastSnapshot = Capture(); revision++;
        OnPropertyChanged(nameof(Config)); OnPropertyChanged(nameof(Workpiece)); Invalidate("编辑已恢复，请重新生成刀路");
    }
    private void RowChanged(object? sender, PropertyChangedEventArgs e)
    { if (e.PropertyName is nameof(ContourRow.IsIncluded) or nameof(ContourRow.Side)) CommitEdit(); }
    private void CommitEdit()
    {
        if (suppressEdits) return;
        var current = Capture();
        if (lastSnapshot is not null && SnapshotsEqual(lastSnapshot, current)) { NotifyAvailability(); return; }
        if (lastSnapshot is not null) undo.Push(lastSnapshot);
        redo.Clear(); lastSnapshot = current; revision++; Invalidate("设置已修改，刀路待更新");
    }
    private void Invalidate(string message)
    {
        if (IsNcDocument)
        {
            Status = "加工参数已更新；当前 NC 文件保持只读";
            OnPropertyChanged(nameof(Revision)); NotifyAvailability(); return;
        }
        var superseded = generationOutcome != global::Cad2Nc.Application.GenerationOutcome.Idle;
        ClearNc(); ResetDiagnostics();
        if (Plan is not null) Diagnostics.Add(new("plan.stale", "画布显示上次生成的结果，请根据当前设置重新生成刀路。", DiagnosticSeverity.Information));
        if (superseded) GenerationOutcome = global::Cad2Nc.Application.GenerationOutcome.Superseded;
        Status = message; OnPropertyChanged(nameof(NcLines)); OnPropertyChanged(nameof(Revision)); NotifyAvailability();
    }

    private void ClearNc()
    {
        generationId++;
        nc = null; batchResult = null; selectedBatchProgram = null; NcLines = Array.Empty<string>(); PlaybackError = null; Playback.Clear();
        OnPropertyChanged(nameof(PlaybackError));
        OnPropertyChanged(nameof(NcCode)); OnPropertyChanged(nameof(NcLines)); OnPropertyChanged(nameof(BatchResult)); OnPropertyChanged(nameof(IsBatchMode));
        OnPropertyChanged(nameof(SelectedBatchProgram)); OnPropertyChanged(nameof(SelectedBatchSheetId));
    }

    public Task LoadCadAsync(string path) => ImportAsync(path, false, null);
    public Task LoadTaskAsync(string taskPath, string? relocatedSource = null) => ImportAsync(taskPath, true, relocatedSource);

    private (CancellationToken Token, long Serial) BeginFileOpen(string message)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var supersedesGeneration = !loadingFile && generationOutcome == global::Cad2Nc.Application.GenerationOutcome.Running;
        operation?.Cancel(); operation?.Dispose();
        operation = new(); var token = operation.Token; var serial = ++operationId;
        if (supersedesGeneration) GenerationOutcome = global::Cad2Nc.Application.GenerationOutcome.Superseded;
        loadingFile = true; Playback.Pause(); IsBusy = true; Status = message;
        return (token, serial);
    }

    private void ReplaceDocument()
    {
        revision++; Document = null; NcDocument = null; Plan = null; workpiece = new(); ClearNc();
        GenerationOutcome = global::Cad2Nc.Application.GenerationOutcome.Idle;
        TaskPath = null; savedState = null; undo.Clear(); redo.Clear(); lastSnapshot = null; inputErrors.Clear(); rejectedNumbers.Clear(); taskDiagnostics.Clear();
        ClearRows(); Diagnostics.Clear();
    }

    private async Task ImportAsync(string path, bool isTask, string? relocatedSource)
    {
        var (token, serial) = BeginFileOpen(isTask ? "正在打开加工任务…" : "正在读取 CAD 并识别闭合轮廓…");
        try
        {
            TaskFile? task = null;
            string? taskPath = null;
            var source = path;
            if (isTask)
            {
                task = await Task.Run(() => TaskStorage.Read(path), token);
                token.ThrowIfCancellationRequested(); if (serial != operationId) return;
                taskPath = Path.GetFullPath(path);
                source = relocatedSource is null ? TaskStorage.ResolveSource(taskPath, task) : Path.GetFullPath(relocatedSource);
                if (!File.Exists(source)) throw new SourceFileMissingException(source);
            }
            var result = await importer.LoadAsync(source, token);
            token.ThrowIfCancellationRequested(); if (serial != operationId) return;
            ReplaceDocument();
            Document = result.Document; TaskPath = taskPath;
            if (task is not null) config = task.Config;
            view = (Store.LastView with { HasViewport = false, Theme = Store.Theme }).Sanitize();
            Store.LastView = view;
            var selection = Document.DefaultSelection;
            bool taskMatches = task is not null && task.SourceHash == Document.SourceHash
                && task.Contours.Length == Document.Contours.Length
                && Document.Contours.All(c => task.Contours.Any(s => s.Id == c.Id && s.Fingerprint == c.Fingerprint));
            if (taskMatches)
            {
                selection = task!.Contours.ToImmutableDictionary(c => c.Id, c => new ContourSelection(c.Included, c.Side));
                workpiece = task.Workpiece;
            }
            foreach (var contour in Document.Contours)
            {
                var row = new ContourRow(contour, selection[contour.Id]); row.PropertyChanged += RowChanged; Contours.Add(row);
            }
            EnsureSheetSetup(!taskMatches);
            if (task is not null && !taskMatches)
                taskDiagnostics.Add(new("task.source_changed", "图纸内容或轮廓标识已变化，已重置加工选择、加工侧和本图原点。", DiagnosticSeverity.Warning));
            lastSnapshot = Capture(); ResetDiagnostics();
            var relocated = task is not null && taskPath is not null && !TaskStorage.ResolveSource(taskPath, task).Equals(Document.SourcePath, StringComparison.OrdinalIgnoreCase);
            savedState = taskMatches && !relocated ? StateFingerprint() : null;
            Store.LastConfig = config; Store.TouchRecent(taskPath ?? source);
            Status = $"已读取 {Contours.Count} 个轮廓，请设置加工范围与参数";
            NotifyDocument();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { if (serial == operationId) Status = "已取消打开，保留原有内容"; }
        catch (Exception e)
        {
            if (serial != operationId) return;
            Status = $"打开失败，保留原有内容：{e.Message}"; throw;
        }
        finally { if (serial == operationId) { loadingFile = false; IsBusy = false; } }
    }

    public async Task LoadNcAsync(string path)
    {
        var (token, serial) = BeginFileOpen("正在读取并解析 NC 程序…");
        try
        {
            var document = await ncReader(path, token);
            token.ThrowIfCancellationRequested(); if (serial != operationId) return;
            ReplaceDocument();
            NcDocument = document; NcLines = document.Lines; PlaybackError = document.PlaybackError;
            if (document.Program is not null) Playback.Load(document.Program);
            view = (Store.LastView with { HasViewport = false, Theme = Store.Theme }).Sanitize();
            Store.LastView = view;
            lastSnapshot = Capture(); Store.TouchRecent(document.SourcePath);
            Status = document.PlaybackError is null ? "NC 程序已打开，可查看参数与轨迹" : "NC 程序已打开，部分指令无法播放；可查看原文与解析提示";
            OnPropertyChanged(nameof(PlaybackError)); NotifyDocument();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        { if (serial == operationId) Status = "已取消打开，保留原有内容"; }
        catch (Exception e)
        {
            if (serial != operationId) return;
            Status = $"NC 文件打开失败，保留原有内容：{e.Message}"; throw;
        }
        finally { if (serial == operationId) { loadingFile = false; IsBusy = false; } }
    }

    public async Task GenerateAsync()
    {
        if (!CanGenerate) throw new InvalidOperationException("请先导入图纸、选择轮廓并修正参数");
        operation?.Dispose(); operation = new(); var token = operation.Token; var serial = ++operationId; loadingFile = false;
        var document = Document!;
        var selection = Selection();
        var request = new PlanningRequest(document, Config, selection, revision, Workpiece);
        var multiBoard = ToolpathPlanner.FindSheetCandidates(document).Count > 1;
        ClearNc(); GenerationOutcome = global::Cad2Nc.Application.GenerationOutcome.Running;
        IsBusy = true; Status = "正在计算成品边与刀心路线…"; ResetDiagnostics();
        OnPropertyChanged(nameof(NcLines));
        try
        {
            if (multiBoard)
            {
                // A machine operation owns one board, so every generated program
                // uses its board lower-left G54 regardless of the single-board
                // origin mode that was previously selected in the task.
                var batchConfig = Config with { OriginMode = OriginMode.SheetLowerLeft };
                var batch = await Task.Run(() => BatchNcGenerator.Generate(document, batchConfig, selection,
                    Path.GetFileNameWithoutExtension(document.SourcePath), request.Revision, token), token);
                token.ThrowIfCancellationRequested();
                if (serial != operationId || request.Revision != revision || Document?.SourceHash != request.Document.SourceHash)
                {
                    if (serial == operationId)
                    {
                        GenerationOutcome = global::Cad2Nc.Application.GenerationOutcome.Superseded;
                        Status = "设置已变化，计算结果已丢弃，请重新生成";
                    }
                    return;
                }
                if (!batch.Succeeded)
                    throw new ToolpathGenerationException(string.Join("；", batch.Diagnostics
                        .Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => d.Message)),
                        batch.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToImmutableArray());
                batchResult = batch;
                Plan = null;
                Diagnostics.Clear();
                foreach (var diagnostic in Document!.Diagnostics.Concat(batch.Diagnostics).Concat(taskDiagnostics).Distinct()) Diagnostics.Add(diagnostic);
                GenerationOutcome = global::Cad2Nc.Application.GenerationOutcome.Succeeded;
                Status = $"已生成 {batch.Programs.Length} 个母板刀路，可预览并拆分导出 NC";
                OnPropertyChanged(nameof(BatchResult)); OnPropertyChanged(nameof(IsBatchMode)); OnPropertyChanged(nameof(Plan));
                NotifyAvailability();
                return;
            }

            var result = await planner(request, token);
            token.ThrowIfCancellationRequested();
            if (serial != operationId || request.Revision != revision || Document?.SourceHash != request.Document.SourceHash)
            {
                if (serial == operationId)
                {
                    GenerationOutcome = global::Cad2Nc.Application.GenerationOutcome.Superseded;
                    Status = "设置已变化，计算结果已丢弃，请重新生成";
                }
                return;
            }
            if (result.Fingerprint != request.Fingerprint || result.Revision != request.Revision)
                throw new InvalidOperationException("规划结果与请求版本不一致");
            if (result.Toolpaths.IsEmpty) throw new ToolpathGenerationException("没有生成可加工的刀路");
            var output = await Task.Run(() =>
            {
                var code = NcGenerator.Generate(result);
                token.ThrowIfCancellationRequested();
                // Generated NC must pass strict validation before any code becomes exportable.
                var playback = generatedParser(code, request.Config);
                return (code, playback);
            }, token);
            token.ThrowIfCancellationRequested();
            if (serial != operationId || request.Revision != revision || Document?.SourceHash != request.Document.SourceHash)
            {
                if (serial == operationId)
                {
                    GenerationOutcome = global::Cad2Nc.Application.GenerationOutcome.Superseded;
                    Status = "设置已变化，计算结果已丢弃，请重新生成";
                }
                return;
            }
            Plan = result; nc = output.code; NcLines = output.code.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
            PlaybackError = null;
            Playback.Load(output.playback);
            OnPropertyChanged(nameof(PlaybackError));
            Diagnostics.Clear();
            foreach (var diagnostic in Document!.Diagnostics.Concat(result.Diagnostics).Concat(taskDiagnostics).Distinct()) Diagnostics.Add(diagnostic);
            GenerationOutcome = global::Cad2Nc.Application.GenerationOutcome.Succeeded;
            Status = "刀路已生成，可预览、播放、复制和保存 NC";
            OnPropertyChanged(nameof(Plan)); OnPropertyChanged(nameof(NcLines)); OnPropertyChanged(nameof(NcCode)); NotifyAvailability();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (serial == operationId)
            {
                ClearNc(); ResetDiagnostics();
                if (Plan is not null) Diagnostics.Add(new("plan.stale", "画布显示上次生成的结果，请根据当前设置重新生成刀路。", DiagnosticSeverity.Information));
                GenerationOutcome = global::Cad2Nc.Application.GenerationOutcome.Canceled;
                Status = "已取消生成，当前结果不可导出";
                OnPropertyChanged(nameof(NcLines)); OnPropertyChanged(nameof(Revision)); NotifyAvailability();
            }
        }
        catch (Exception e)
        {
            if (serial != operationId) return;
            ClearNc(); GenerationOutcome = global::Cad2Nc.Application.GenerationOutcome.Failed; Status = "生成失败，请检查诊断";
            if (e is ToolpathGenerationException { Diagnostics.IsEmpty: false } failure)
                foreach (var diagnostic in failure.Diagnostics) Diagnostics.Add(diagnostic);
            else Diagnostics.Add(new("plan.failed", e.Message, DiagnosticSeverity.Error));
            OnPropertyChanged(nameof(NcLines)); NotifyAvailability(); throw;
        }
        finally { if (serial == operationId) IsBusy = false; }
    }
    public void Cancel()
    {
        if (!IsBusy || operation is null) return;
        operation.Cancel();
        if (!loadingFile) { revision++; ClearNc(); }
        Status = "正在取消，等待当前处理阶段结束…";
        OnPropertyChanged(nameof(NcLines)); NotifyAvailability();
    }
    public async Task SaveTaskAsync(string path)
    {
        if (saving) throw new InvalidOperationException("任务正在保存，请稍候");
        if (Document is null || IsBusy) throw new InvalidOperationException("当前没有可保存的任务");
        if (ValidationErrors.Count > 0) throw new InvalidOperationException("请先修正参数再保存任务");
        var document = Document; var settings = Config; var setup = Workpiece; var selection = Selection(); var savedView = view; var fingerprint = StateFingerprint();
        saving = true; NotifyAvailability();
        try
        {
            await Task.Run(() => TaskStorage.Save(path, document, settings, selection, savedView, setup));
            if (ReferenceEquals(Document, document))
            {
                TaskPath = Path.GetFullPath(path);
                savedState = fingerprint;
                Store.TouchRecent(TaskPath);
                Status = "任务已保存";
                OnPropertyChanged(nameof(TaskPath)); OnPropertyChanged(nameof(IsDirty));
            }
        }
        finally { saving = false; NotifyAvailability(); }
    }
    public Task ExportNcAsync(string path)
    {
        if (!CanExport) throw new InvalidOperationException("当前刀路尚未生成、已过期或存在需要修正的错误");
        var request = new PlanningRequest(Document!, Config, Selection(), revision, Workpiece);
        if (Plan!.Fingerprint != request.Fingerprint || Plan.Revision != revision) throw new InvalidOperationException("参数版本已变化，请重新生成");
        // The final eligibility check and atomic write occur together on the owning UI thread.
        NcGenerator.Write(path, Plan);
        Status = $"已导出 {Path.GetFileName(path)}";
        return Task.CompletedTask;
    }

    /// <summary>Generates one independently validated NC program for each detected motherboard.</summary>
    public async Task<BatchNcGenerationResult> GenerateBatchAsync(CancellationToken cancellationToken = default)
    {
        if (Document is null || IsBusy) throw new InvalidOperationException("请先导入图纸并等待当前操作完成");
        if (ValidationErrors.Count > 0) throw new InvalidOperationException("请先修正参数再批量生成 NC");
        var document = Document; var settings = Config with { OriginMode = OriginMode.SheetLowerLeft }; var selection = Selection(); var expectedRevision = revision;
        var result = await Task.Run(() => BatchNcGenerator.Generate(document, settings, selection,
            Path.GetFileNameWithoutExtension(document.SourcePath), expectedRevision, cancellationToken), cancellationToken);
        if (!ReferenceEquals(document, Document) || expectedRevision != revision)
            throw new OperationCanceledException("图纸或参数已变化，批量结果已丢弃");
        return result;
    }

    /// <summary>Writes all validated motherboard programs into a directory.</summary>
    public async Task<ImmutableArray<string>> ExportBatchNcAsync(string directory, CancellationToken cancellationToken = default)
    {
        var result = IsBatchMode && CanExport && batchResult is not null
            ? batchResult
            : await GenerateBatchAsync(cancellationToken);
        var paths = BatchNcGenerator.Write(directory, result);
        Status = $"已导出 {paths.Length} 个 NC 文件";
        return paths;
    }

    /// <summary>Selects one generated motherboard program for NC preview and playback.</summary>
    public void SelectBatchProgram(string? sheetContourId)
    {
        if (!IsBatchMode || batchResult is null)
        {
            if (sheetContourId is not null) throw new InvalidOperationException("当前没有可选的多母板结果");
            return;
        }

        BatchNcProgram? selected = null;
        if (sheetContourId is not null)
        {
            selected = batchResult.Programs.FirstOrDefault(program =>
                string.Equals(program.SheetContourId, sheetContourId, StringComparison.Ordinal));
            if (selected is null) throw new ArgumentException($"未找到母板 {sheetContourId} 的 NC 程序", nameof(sheetContourId));
        }

        if (ReferenceEquals(selectedBatchProgram, selected)) return;
        Playback.Pause();
        selectedBatchProgram = selected;
        NcLines = selected is null
            ? Array.Empty<string>()
            : selected.Code.Split('\n').Select(line => line.TrimEnd('\r')).ToArray();
        PlaybackError = null;
        if (selected is null) Playback.Clear();
        else Playback.Load(selected.Playback);
        Status = selected is null ? "已显示全部母板静态预览" : $"已选择母板 {selected.SheetContourId}，可预览并播放 NC";
        OnPropertyChanged(nameof(SelectedBatchProgram)); OnPropertyChanged(nameof(SelectedBatchSheetId));
        OnPropertyChanged(nameof(NcLines)); OnPropertyChanged(nameof(NcCode)); OnPropertyChanged(nameof(PlaybackError));
        NotifyAvailability();
    }
    private ImmutableDictionary<string, ContourSelection> Selection() =>
        Contours.ToImmutableDictionary(c => c.Id, c => new ContourSelection(c.IsIncluded, c.Side));
    private EditSnapshot Capture() => new(config, workpiece, Selection(), inputErrors.ToImmutableDictionary(), rejectedNumbers.ToImmutableDictionary());
    private static bool SnapshotsEqual(EditSnapshot a, EditSnapshot b) => a.Config == b.Config && a.Workpiece == b.Workpiece && a.Selection.Count == b.Selection.Count && a.Selection.All(p => b.Selection.TryGetValue(p.Key, out var other) && p.Value == other)
        && a.InputErrors.Count == b.InputErrors.Count && a.InputErrors.All(p => b.InputErrors.TryGetValue(p.Key, out var other) && p.Value == other)
        && a.RejectedNumbers.Count == b.RejectedNumbers.Count && a.RejectedNumbers.All(p => b.RejectedNumbers.TryGetValue(p.Key, out var other) && p.Value.Equals(other));
    private string StateFingerprint() => JsonSerializer.Serialize(new
    {
        Config, Workpiece, Selection = Selection().OrderBy(p => p.Key),
        InputErrors = inputErrors.OrderBy(p => p.Key),
        // This is only an in-memory dirty-state comparison; task/config persistence never receives raw input.
        // Format as strings so a NumberBox's NaN/Infinity sentinel cannot break JSON serialization.
        RejectedNumbers = rejectedNumbers.OrderBy(p => p.Key).Select(p => new { p.Key, Value = p.Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture) })
    });
    private void ResetDiagnostics()
    { Diagnostics.Clear(); if (Document is not null) foreach (var diagnostic in Document.Diagnostics) Diagnostics.Add(diagnostic); foreach (var diagnostic in taskDiagnostics) Diagnostics.Add(diagnostic); }
    private void ClearRows()
    { foreach (var row in Contours) row.PropertyChanged -= RowChanged; Contours.Clear(); }
    private void NotifyAvailability()
    {
        foreach (var name in new[] { nameof(CanGenerate), nameof(CanSaveTask), nameof(CanExport), nameof(CanCopy), nameof(CanPlay), nameof(CanUndo), nameof(CanRedo), nameof(IsDirty), nameof(HasStalePlan), nameof(ValidationErrors), nameof(InspectionItems), nameof(PendingInspectionCount), nameof(DiagnosticsConfirmed) }) OnPropertyChanged(name);
    }
    private void NotifyDocument()
    {
        foreach (var name in new[] { nameof(Document), nameof(NcDocument), nameof(IsNcDocument), nameof(Plan), nameof(Config), nameof(Workpiece), nameof(NcLines), nameof(NcCode), nameof(TaskPath), nameof(FileName), nameof(SourcePath), nameof(CurrentView), nameof(RequiresUnitsConfirmation), nameof(RequiresDiagnosticsConfirmation), nameof(Revision) }) OnPropertyChanged(name);
        NotifyAvailability();
    }
    public void Dispose()
    { if (disposed) return; disposed = true; ++operationId; operation?.Cancel(); operation?.Dispose(); Playback.Clear(); ClearRows(); }
    private sealed record EditSnapshot(ConversionConfig Config, WorkpieceSetup Workpiece, ImmutableDictionary<string, ContourSelection> Selection,
        ImmutableDictionary<string, string> InputErrors, ImmutableDictionary<string, double> RejectedNumbers);
}
