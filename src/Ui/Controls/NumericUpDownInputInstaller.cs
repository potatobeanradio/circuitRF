// Avalonia's NumericUpDown parses its own text, and its two defaults are wrong for this application
// in the same direction — towards reading a typo as a number rather than refusing it.
//
//   ParsingNumberStyle defaults to NumberStyles.Any, which admits a GROUP separator, and NumberFormat
//   defaults to the machine's CurrentCulture. Measured, not assumed: on an en-US machine
//   `double.TryParse("4,4", NumberStyles.Any, en-US)` returns 44. So a user typing four-point-four
//   into any of the 14 views that use this control got FORTY-FOUR — no error, no revert, a plausible
//   number. On a German machine `1.234` reads as 1234 by the same mechanism, in the other direction.
//
// Three corrections, none of them in the 14 XAML files that use the control:
//
//   1. Float, not Any — no thousands separator is admitted in any culture, so a grouped number is
//      refused instead of being read as one of its two possible meanings.
//   2. NumberFormatInfo.InvariantInfo, so the reading does not depend on the machine at all.
//   3. A decimal comma in the text is rewritten to a point before the control parses it, by the same
//      NumericText rule every other input field in circuitRF uses. That is what makes `4,4` mean four
//      point four here as well.
//
// 1 and 2 are SETTERS on the application-scope NumericUpDown style in Styles/CircuitRfStyles.axaml,
// not overridden defaults: OverrideDefaultValue refuses a property on its own declaring type
// ("Metadata is already set for ParsingNumberStyle on Avalonia.Controls.NumericUpDown"), which is
// worth knowing before trying it again. 3 has to be here, because a style cannot rewrite a value.
//
// Rewriting Text terminates on its own: the rewritten string has no comma left for the rule to act
// on, so the second notification is a no-op. No reentrancy flag is needed and none is used.
//
// A MODULE INITIALIZER for the reason UiVerilogACacheInstaller gives: src/Ui has three Main methods
// (circuitRF, harmonicaRF, wBond) and this must hold in all three with no startup ordering to get
// wrong.

using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Reactive;

namespace CircuitRF.Ui.Controls;

internal static class NumericUpDownInputInstaller
{
    [ModuleInitializer]
    internal static void Install()
    {
        NumericUpDown.TextProperty.Changed.Subscribe(new AnonymousObserver<
            AvaloniaPropertyChangedEventArgs<string?>>(args =>
        {
            if (args.Sender is not NumericUpDown control) return;
            if (args.NewValue.GetValueOrDefault() is not string typed || !typed.Contains(',')) return;

            string canonical = NumericText.NormalizeDecimalSeparator(typed);
            if (!string.Equals(canonical, typed, StringComparison.Ordinal))
                control.Text = canonical;
        }));
    }
}
