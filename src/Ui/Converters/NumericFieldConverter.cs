using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace CircuitRF.Ui.Converters;

/// <summary>
/// Converts between <c>decimal?</c> (what <c>NumericUpDown.Value</c> is) and any numeric view-model
/// property (double, int, …).
///
/// <para><b>Both directions have to survive a value the other side cannot represent, and the failure
/// is loud when they do not</b> (owner report, 2026-08-26). A symbol primitive whose radius had gone
/// NaN put <c>System.InvalidCastException: Could not convert '(unset)' … to System.Double</c> into
/// the inspector's R field, in place of the number. Two separate steps produced that one message:
/// <c>Convert.ToDecimal(double.NaN)</c> THROWS (decimal has no NaN), so the field went empty; the
/// empty field then wrote back, and a <c>ConvertBack</c> that answers
/// <c>AvaloniaProperty.UnsetValue</c> is answering "the value is unset", which the binding then
/// dutifully tried to store in a non-nullable <c>double</c> — and reported that it could not.</para>
///
/// <para><b>The right answer for "leave the source alone" is <see cref="BindingOperations.DoNothing"/>,
/// not UnsetValue.</b> One means "there is no value here"; the other means "make no assignment". Only
/// the second is what an empty or unparseable field wants, and the difference is invisible until the
/// target type refuses nulls. Every numeric field in the Properties Inspector shares this converter,
/// so the same error was reachable through any of them, not only the one that was reported.</para>
/// </summary>
public sealed class NumericFieldConverter : IValueConverter
{
    public static readonly NumericFieldConverter Instance = new();

    /// <summary>
    /// VM number → the box. A value decimal cannot hold — NaN, an infinity, or one past decimal's
    /// far smaller range — shows as an EMPTY field rather than as an error: the field is then simply
    /// waiting for a number, which is a state the user can fix by typing one.
    /// </summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return null;
        if (value is double d && !double.IsFinite(d)) return null;
        if (value is float f && !float.IsFinite(f)) return null;
        try   { return (decimal?)System.Convert.ToDecimal(value); }
        catch { return null; }
    }

    /// <summary>
    /// The box → VM number. An empty or unparseable field makes NO assignment, which leaves the
    /// model holding what it already had.
    /// </summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is decimal dec)
        {
            try   { return System.Convert.ChangeType(dec, targetType); }
            catch { return BindingOperations.DoNothing; }
        }
        return BindingOperations.DoNothing;
    }
}
