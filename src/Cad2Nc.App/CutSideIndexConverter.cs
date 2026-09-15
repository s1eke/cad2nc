using Cad2Nc.Core;
using Microsoft.UI.Xaml.Data;

namespace Cad2Nc.App;
public sealed class CutSideIndexConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is CutSide.Inside ? 1 : 0;
    public object ConvertBack(object value, Type targetType, object parameter, string language) => value is int index && index == 1 ? CutSide.Inside : CutSide.Outside;
}

public sealed class StockLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is true ? "母板候选" : "";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class DiagnosticSeverityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value switch
    {
        DiagnosticSeverity.Error => "错误",
        DiagnosticSeverity.Warning => "注意",
        _ => "提示"
    };
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}

public sealed class CutSideLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language) => value is CutSide.Inside ? "内侧" : "外侧";
    public object ConvertBack(object value, Type targetType, object parameter, string language) => throw new NotSupportedException();
}
