using System;
using System.Globalization;
using Avalonia.Data.Converters;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Ui.Converters;

/// <summary>
/// Displays an <see cref="EmAnalysisKind"/> as the name the USER knows it by, via
/// <see cref="EmKernelRegistry.ChoiceLabel"/> — the single source of that naming, shared with the
/// prose in <c>EmKernelRegistry.Choose</c>, which tells the user to pick an analysis BY NAME.
///
/// <para><b>Why this exists at all (owner request, 2026-08-09).</b> The EM Setup dropdown had no
/// <c>ItemTemplate</c>, so it rendered <c>EmAnalysisKind.ToString()</c> — "Auto", "CrossSection",
/// "Planar" — which are our own enum spellings and mean nothing in particular to someone deciding
/// which EM analysis to run. Naming the analysis by what it SOLVES is the whole point.</para>
///
/// <para>View-only, exactly like <see cref="ToLowerStringConverter"/>: the ComboBox's
/// <c>SelectedItem</c> stays bound to the enum itself, so nothing here can affect what is persisted
/// in the <c>.cem</c>.</para>
/// </summary>
public sealed class EmAnalysisKindNameConverter : IValueConverter
{
    public static readonly EmAnalysisKindNameConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is EmAnalysisKind kind ? EmKernelRegistry.ChoiceLabel(kind) : value?.ToString();

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException($"{nameof(EmAnalysisKindNameConverter)} does not support ConvertBack.");
}
