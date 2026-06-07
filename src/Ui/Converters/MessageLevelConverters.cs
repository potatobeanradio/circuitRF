using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Material.Icons;
using CircuitRF.Ui.Messages;

namespace CircuitRF.Ui.Converters;

/// <summary>Maps MessageLevel to a Material Icon kind. Icon carries semantic meaning
/// independently of color — never rely on color alone (accessibility requirement).</summary>
public class MessageLevelToIconConverter : IValueConverter
{
    public static readonly MessageLevelToIconConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is MessageLevel level
            ? level switch
            {
                MessageLevel.Error   => MaterialIconKind.AlertCircle,
                MessageLevel.Warning => MaterialIconKind.AlertOutline,
                MessageLevel.Success => MaterialIconKind.CheckCircle,
                MessageLevel.Info    => MaterialIconKind.InformationOutline,
                _                    => MaterialIconKind.InformationOutline,
            }
            : MaterialIconKind.InformationOutline;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Maps MessageLevel to a semantic foreground Brush.
/// Colors complement the icon — the icon alone must convey meaning for accessibility.</summary>
public class MessageLevelToColorConverter : IValueConverter
{
    public static readonly MessageLevelToColorConverter Instance = new();

    // Accessible colors for both light and dark themes (WCAG AA contrast against typical panel BG).
    private static readonly SolidColorBrush ErrorBrush   = new(Color.FromRgb(0xE5, 0x39, 0x35));  // red
    private static readonly SolidColorBrush WarningBrush = new(Color.FromRgb(0xF5, 0x7C, 0x00));  // amber
    private static readonly SolidColorBrush SuccessBrush = new(Color.FromRgb(0x2E, 0x7D, 0x32));  // green
    private static readonly SolidColorBrush InfoBrush    = new(Color.FromRgb(0x01, 0x57, 0x9B));  // blue

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is MessageLevel level
            ? (IBrush)(level switch
            {
                MessageLevel.Error   => ErrorBrush,
                MessageLevel.Warning => WarningBrush,
                MessageLevel.Success => SuccessBrush,
                MessageLevel.Info    => InfoBrush,
                _                    => InfoBrush,
            })
            : (IBrush)InfoBrush;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>Returns true if a nullable string has a value (for file-link visibility).</summary>
public class IsNotNullConverter : IValueConverter
{
    public static readonly IsNotNullConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
