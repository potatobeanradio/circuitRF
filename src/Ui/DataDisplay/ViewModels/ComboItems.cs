// ================================================================
//  ComboItems.cs  —  lightweight wrapper types for typed ComboBoxes
//
//  YAxisItem    — DependentVarFormat entry with a proper label and
//                 IsEnabled flag (Complex is disabled on Rect plots).
//
//  MarkerTypeItem — MarkerType entry paired with a MaterialIconKind
//                   so the marker combo can show icons instead of text.
// ================================================================

using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Material.Icons;
using RfCore;
using CircuitRF.Ui.DataDisplay;

namespace CircuitRF.Ui.DataDisplay.ViewModels;

// ---- YAxisItem -------------------------------------------------------------

public sealed class YAxisItem
{
    public DependentVarFormat Format    { get; }
    public string             Label     { get; }
    public bool               IsEnabled { get; }

    public YAxisItem(DependentVarFormat format, bool enabled = true)
    {
        Format    = format;
        IsEnabled = enabled;

        // DependentVarFormat.Db is the compiler name; show it as "dB".
        Label = format switch
        {
            DependentVarFormat.Db        => "dB",
            DependentVarFormat.Mag       => "Mag",
            DependentVarFormat.Phase     => "Phase°",
            DependentVarFormat.Real      => "Real",
            DependentVarFormat.Imaginary => "Imag",
            DependentVarFormat.Complex   => "Complex",
            _                            => format.ToString()
        };
    }
}

// ---- PrecisionFormatConverter -----------------------------------------------
//  Used in MarkerEditorView to show human-readable names for PrecisionFormat enum values.

public sealed class PrecisionFormatConverter : IValueConverter
{
    public static readonly PrecisionFormatConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is PrecisionFormat f ? f.Description() : value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

// ---- MarkerStyleConverter ---------------------------------------------------
//  Used in MarkerEditorView to show compact S/M/L/XL labels in the Size ComboBox.

public sealed class MarkerStyleConverter : IValueConverter
{
    public static readonly MarkerStyleConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is MarkerStyle s ? s.ShortDescription() : value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotImplementedException();
}

// ---- MarkerTypeItem --------------------------------------------------------

public sealed class MarkerTypeItem
{
    public MarkerType       Value { get; }
    public MaterialIconKind Icon  { get; }

    public MarkerTypeItem(MarkerType value, MaterialIconKind icon)
    {
        Value = value;
        Icon  = icon;
    }
}

// ---- LineModeItem ----------------------------------------------------------
// Merges Line on/off + LineType into a single icon-pick option (§C of brief 7.1d-1-r2).
// IsOff=true → line disabled; IsOff=false → line enabled with the given Type.

public sealed class LineModeItem
{
    public bool     IsOff   { get; }
    public LineType Type    { get; }
    public double   Opacity => IsOff ? 0.25 : 1.0;

    public LineModeItem(bool isOff, LineType type) { IsOff = isOff; Type = type; }
}

// ---- CubeTransformItem -----------------------------------------------------
// Wraps a CubeTransform enum value with a human-readable label.
// Enabled=false marks cube-only entries that are shown greyed for network traces.

public sealed class CubeTransformItem
{
    public CubeTransform Transform { get; }
    public string        Label     { get; }
    public bool          Enabled   { get; }

    public CubeTransformItem(CubeTransform transform, bool enabled = true)
    {
        Transform = transform;
        Enabled   = enabled;
        Label = transform switch
        {
            CubeTransform.None  => "None",
            CubeTransform.dB20  => "dB20",
            CubeTransform.dB10  => "dB10",
            CubeTransform.dB    => "dB",
            CubeTransform.Mag   => "Mag",
            CubeTransform.Phase => "Phase°",
            CubeTransform.Real  => "Real",
            CubeTransform.Imag  => "Imag",
            CubeTransform.Conj  => "Conj",
            _                   => transform.ToString()
        };
    }
}

// ---- SymbolModeItem --------------------------------------------------------
// Merges Marker on/off + MarkerType into a single icon-pick option.

public sealed class SymbolModeItem
{
    public bool             IsOff   { get; }
    public MarkerType       Shape   { get; }
    public MaterialIconKind Icon    { get; }
    public double           Opacity => IsOff ? 0.25 : 1.0;

    public SymbolModeItem(bool isOff, MarkerType shape, MaterialIconKind icon)
    {
        IsOff = isOff; Shape = shape; Icon = icon;
    }
}
