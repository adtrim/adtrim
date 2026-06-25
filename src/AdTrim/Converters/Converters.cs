using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AdTrim.Models;

namespace AdTrim.Converters;

/// <summary>
/// Confidence enum → marker brush (Marker.Neutral / High / Medium / Unchanged).
/// </summary>
public sealed class ConfidenceToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            Confidence.High      => "Marker.High",
            Confidence.Medium    => "Marker.Medium",
            Confidence.Low       => "Marker.Low",
            Confidence.Unchanged => "Marker.Unchanged",
            _                    => "Marker.Neutral",
        };
        return Application.Current.TryFindResource(key) ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Triangle fill: confirmed markers use the confidence color; unconfirmed
/// markers use Bg.Surface2 so the stroke reads as outlined.
/// </summary>
public sealed class MarkerFillConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        if (values.Length < 2) return Brushes.Transparent;
        var confidence = (Confidence)values[0];
        var confirmed = (bool)values[1];
        if (confirmed)
        {
            var key = confidence switch
            {
                Confidence.High      => "Marker.High",
                Confidence.Medium    => "Marker.Medium",
                Confidence.Low       => "Marker.Low",
                Confidence.Unchanged => "Marker.Unchanged",
                _                    => "Marker.Neutral",
            };
            return Application.Current.TryFindResource(key) ?? Brushes.Gray;
        }
        return Application.Current.TryFindResource("Bg.Surface2") ?? Brushes.Black;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True → Visible, False → Collapsed.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>StatusKind → brush (Text.Secondary / Success / Warning / Danger).</summary>
public sealed class StatusKindToBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var key = value switch
        {
            AdTrim.Models.StatusKind.Success => "State.Success",
            AdTrim.Models.StatusKind.Warning => "State.Warning",
            AdTrim.Models.StatusKind.Danger  => "State.Danger",
            _                                       => "Text.Secondary",
        };
        return Application.Current.TryFindResource(key) ?? Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>StatusKind → background brush for the banner (tinted, low-alpha).</summary>
public sealed class StatusKindToBannerBgConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value switch
        {
            AdTrim.Models.StatusKind.Danger
                => new SolidColorBrush(Color.FromArgb(0x12, 0xFF, 0x6B, 0x6B)),
            AdTrim.Models.StatusKind.Warning
                => new SolidColorBrush(Color.FromArgb(0x14, 0xF5, 0xB8, 0x4A)),
            AdTrim.Models.StatusKind.Success
                => new SolidColorBrush(Color.FromArgb(0x14, 0x3E, 0xC5, 0xA6)),
            _ => new SolidColorBrush(Color.FromArgb(0x12, 0x4F, 0x8C, 0xFF)),
        };
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>0.0..1.0 → bar width in pixels of a 60-px progress track.</summary>
public sealed class ProgressTrackWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        var pct = value switch
        {
            double d => d,
            float f => (double)f,
            _ => 0.0,
        };
        var totalWidth = parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var w)
            ? w : 60.0;
        return Math.Max(0, Math.Min(totalWidth, pct * totalWidth));
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>True → Collapsed, False → Visible (negated form).</summary>
public sealed class BoolToCollapsedConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool b && b) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Collapsed;
}

/// <summary>
/// Visible when the bound enum value equals the converter parameter (string form).
/// Used to drive the selection-aware timeline toolbar.
/// </summary>
public sealed class EnumEqualsVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() == parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Inverse of EnumEqualsVisibilityConverter.</summary>
public sealed class EnumNotEqualsVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value?.ToString() != parameter?.ToString() ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Formats a microsecond integer time as MM:SS.mmm (milliseconds, three digits).
/// Parameter "noms" drops the millisecond portion.
/// </summary>
public sealed class TimeFormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        long us = value switch
        {
            long l => l,
            int  i => i,
            double d => (long)Math.Round(d * 1_000_000.0),  // legacy seconds path
            _ => 0L,
        };
        var totalSec = us / 1_000_000;
        var h = (int)(totalSec / 3600);
        var m = (int)((totalSec % 3600) / 60);
        var sec = (int)(totalSec % 60);
        // Milliseconds derived from the µs remainder - no double precision wobble.
        var ms = (int)((us % 1_000_000) / 1_000);
        var basePart = h > 0
            ? $"{h}:{m:00}:{sec:00}"
            : $"{m:00}:{sec:00}";
        var p = parameter as string;
        if (p is "noms" or "noframes") return basePart;
        return $"{basePart}.{ms:000}";
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
