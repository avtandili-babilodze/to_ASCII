using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AsciiVision.Core.Configuration;

namespace AsciiVision.App.Converters;

/// <summary>Binds a RadioButton to one enum value: IsChecked="{Binding Mode, Converter=..., ConverterParameter=Value}".</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is string name && string.Equals(value.ToString(), name, StringComparison.Ordinal);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}

/// <summary>Visible when the enum value matches any of the '|'-separated names in the parameter.</summary>
public sealed class EnumToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null || parameter is not string names)
        {
            return Visibility.Collapsed;
        }

        bool invert = names.StartsWith('!');
        var match = names.TrimStart('!').Split('|').Contains(value.ToString());
        return match ^ invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) ^ Invert ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool hasValue = value is not null && (value is not string s || !string.IsNullOrWhiteSpace(s));
        return hasValue ^ Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>"#RRGGBB" text → brush (transparent when invalid).</summary>
public sealed class HexToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (ColorUtil.TryParseHex(value as string, out var argb))
        {
            var brush = new SolidColorBrush(Color.FromRgb(ColorUtil.R(argb), ColorUtil.G(argb), ColorUtil.B(argb)));
            brush.Freeze();
            return brush;
        }

        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class NotificationSeverityToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        string key = value?.ToString() switch
        {
            "Error" => "DangerBrush",
            "Warning" => "WarningBrush",
            "Success" => "SuccessBrush",
            _ => "AccentBrush",
        };
        return Application.Current.TryFindResource(key) ?? Brushes.SteelBlue;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
