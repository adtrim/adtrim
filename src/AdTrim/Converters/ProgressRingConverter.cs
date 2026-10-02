using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace AdTrim.Converters;

public sealed class ProgressRingConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var progress = value is double number && double.IsFinite(number) ? Math.Clamp(number, 0, 1) : 0;
        if (progress <= 0) return Geometry.Empty;
        if (progress >= 1) return new EllipseGeometry(new Point(92, 92), 88, 88);
        var angle = progress * Math.PI * 2;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(92, 4), false, false);
            context.ArcTo(new Point(92 + 88 * Math.Sin(angle), 92 - 88 * Math.Cos(angle)),
                new Size(88, 88), 0, progress > 0.5, SweepDirection.Clockwise, true, false);
        }
        geometry.Freeze();
        return geometry;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
