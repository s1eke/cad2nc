namespace Cad2Nc.App;

internal static class NumberInputNormalizer
{
    internal static double Normalize(string field, double value)
    {
        if (!double.IsFinite(value) || field != "LeaveSkin") return value;
        return Math.Max(0, Math.Round(value, 1, MidpointRounding.AwayFromZero));
    }
}
