using Cad2Nc.Application;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Printing;
using Windows.Graphics.Printing;

namespace Cad2Nc.App;

internal interface IBoardLabelPrinter
{
    Task PrintAsync(IntPtr windowHandle, IReadOnlyList<BoardLabelInfo> labels, CancellationToken cancellationToken = default);
    void Dispose();
}

/// <summary>WinUI 3 bridge for the Windows system print preview and printer dialog.</summary>
internal sealed class WindowsBoardLabelPrinter : IBoardLabelPrinter
{
    private PrintDocument? _printDocument;
    private IPrintDocumentSource? _printDocumentSource;
    private PrintManager? _printManager;
    private readonly List<UIElement> _pages = [];
    private IReadOnlyList<BoardLabelInfo> _labels = [];

    public async Task PrintAsync(IntPtr windowHandle, IReadOnlyList<BoardLabelInfo> labels, CancellationToken cancellationToken = default)
    {
        if (labels.Count == 0) throw new ArgumentException("没有可打印的标签。", nameof(labels));
        if (!PrintManager.IsSupported()) throw new PlatformNotSupportedException("当前设备不支持 Windows 打印。");
        cancellationToken.ThrowIfCancellationRequested();
        _labels = labels;
        _pages.Clear();
        foreach (var label in labels)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _pages.Add(await BoardLabelPage.CreateAsync(label));
        }

        Register(windowHandle);
        try
        {
            await Windows.Graphics.Printing.PrintManagerInterop.ShowPrintUIForWindowAsync(windowHandle);
        }
        finally
        {
            Unregister();
        }
    }

    private void Register(IntPtr windowHandle)
    {
        if (_printDocument is not null) throw new InvalidOperationException("打印流程已在进行中。");
        _printManager = Windows.Graphics.Printing.PrintManagerInterop.GetForWindow(windowHandle);
        _printManager.PrintTaskRequested += PrintTaskRequested;
        _printDocument = new PrintDocument();
        _printDocumentSource = _printDocument.DocumentSource;
        _printDocument.Paginate += Paginate;
        _printDocument.GetPreviewPage += GetPreviewPage;
        _printDocument.AddPages += AddPages;
    }

    private void Unregister()
    {
        if (_printDocument is not null)
        {
            _printDocument.Paginate -= Paginate;
            _printDocument.GetPreviewPage -= GetPreviewPage;
            _printDocument.AddPages -= AddPages;
        }
        if (_printManager is not null) _printManager.PrintTaskRequested -= PrintTaskRequested;
        _printDocument = null;
        _printDocumentSource = null;
        _printManager = null;
        _pages.Clear();
        _labels = [];
    }

    public void Dispose() => Unregister();

    private void PrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        if (_printDocumentSource is null) return;
        var title = _labels.Count == 1 ? $"CAD2NC 标签 · {_labels[0].BoardId}" : $"CAD2NC 标签 · {_labels.Count} 张";
        var task = args.Request.CreatePrintTask(title, request => request.SetSource(_printDocumentSource));
        task.Completed += (_, _) => { };
    }

    private void Paginate(object sender, PaginateEventArgs e)
    {
        if (sender is PrintDocument document)
            document.SetPreviewPageCount(_pages.Count, PreviewPageCountType.Final);
    }

    private void GetPreviewPage(object sender, GetPreviewPageEventArgs e)
    {
        if (sender is PrintDocument document && e.PageNumber > 0 && e.PageNumber <= _pages.Count)
            document.SetPreviewPage(e.PageNumber, _pages[e.PageNumber - 1]);
    }

    private void AddPages(object sender, AddPagesEventArgs e)
    {
        if (sender is not PrintDocument document) return;
        foreach (var page in _pages) document.AddPage(page);
        document.AddPagesComplete();
    }
}
