// Pure, Avalonia-free scale-factor/dimension linking logic for ScaleDialog — extracted specifically
// so this (previously buggy) bit of live two-way field synchronization can be unit-tested directly,
// without a TextBox or the Avalonia app host (Window subclasses cannot be constructed headlessly in
// this project's test suite). ScaleDialog.axaml.cs is a thin shim over this: on LostFocus/Enter it
// calls Edit(field, text); on success it loops the four fields and assigns DisplayFor(field) where
// non-null.
//
// docs/sonnet-briefs/brief-L1h-fix-scale-dialog-width.md (third report on this bug) root-caused the
// prior two "fixes" to the WRONG layer: the exact-factor math here was already correct, but the shim
// (untestable, since it's a Window) held policy — which field is "authoritative" and must not be
// overwritten — and that policy lived nowhere durable, so a stray per-keystroke TextChanged re-entrancy
// could silently re-derive the exact factor from an already-rounded display string. The fix moves that
// policy INTO this testable class: Edit() records which field the caller just committed as the
// authoritative one, and DisplayFor() returns null for it — "the field the user last edited is never
// written back" — so the untestable shim has no decision left to get wrong, only a loop.
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>Which of the four linked fields was most recently committed by the user.</summary>
public enum ScaleField { FactorX, FactorY, Width, Height }

public sealed class ScaleFieldLinker
{
    private readonly long _origWidthDbu;
    private readonly long _origHeightDbu;
    private readonly LayoutUnit _displayUnit;
    private readonly int _dbuPerMicron;

    public bool IsUniform { get; set; } = true;

    /// <summary>The exact, full-precision factors — the ONLY source of truth for committing a scale.
    /// <see cref="FactorText"/>/<see cref="FactorYText"/> round these to 4 decimal places for DISPLAY
    /// only; re-parsing that rounded string back into a factor (what an earlier version of this dialog
    /// did) is exactly the bug this split exists to make impossible — a literal "Width = 400" would
    /// silently commit as a few tenths of a unit off once re-derived through the truncated text.</summary>
    public double FactorX { get; private set; } = 1.0;
    public double FactorY { get; private set; } = 1.0;

    /// <summary>The field the caller most recently committed via <see cref="Edit"/> — never null after
    /// the first edit. <see cref="DisplayFor"/> returns null for this field: it must not be written
    /// back, no matter how many times a refresh loop runs, because doing so is how a stray/late event
    /// re-derives the exact factor from an already-rounded display string.</summary>
    public ScaleField? AuthoritativeField { get; private set; }

    public ScaleFieldLinker(long origWidthDbu, long origHeightDbu, LayoutUnit displayUnit, int dbuPerMicron)
    {
        _origWidthDbu = Math.Max(origWidthDbu, 1);
        _origHeightDbu = Math.Max(origHeightDbu, 1);
        _displayUnit = displayUnit;
        _dbuPerMicron = dbuPerMicron;
    }

    public string FactorText => FactorX.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
    public string FactorYText => FactorY.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
    public string WidthText => LayoutUnits.Format(ScaledWidthDbu, _displayUnit, _dbuPerMicron);
    public string HeightText => LayoutUnits.Format(ScaledHeightDbu, _displayUnit, _dbuPerMicron);

    public long ScaledWidthDbu => (long)Math.Round(_origWidthDbu * FactorX, MidpointRounding.AwayFromZero);
    public long ScaledHeightDbu => (long)Math.Round(_origHeightDbu * FactorY, MidpointRounding.AwayFromZero);

    /// <summary>The display string for <paramref name="field"/>, or null when it is the authoritative
    /// field — the caller must not write this field's box, ever, until the user edits a different one.
    /// This is R-fix-2/R-fix-3's invariant expressed as an API: there is no flag to forget to pass.</summary>
    public string? DisplayFor(ScaleField field) => field == AuthoritativeField ? null : RawDisplayFor(field);

    private string RawDisplayFor(ScaleField field) => field switch
    {
        ScaleField.FactorX => FactorText,
        ScaleField.FactorY => FactorYText,
        ScaleField.Width => WidthText,
        ScaleField.Height => HeightText,
        _ => "",
    };

    /// <summary>Records a user commit (LostFocus/Enter, never TextChanged) on <paramref name="field"/>,
    /// updates the exact factor(s), and marks <paramref name="field"/> authoritative. Returns false
    /// (state unchanged, <see cref="AuthoritativeField"/> untouched) on unparseable/non-positive input.
    ///
    /// <b>R-fix-5:</b> when <paramref name="text"/> already equals what <paramref name="field"/> is
    /// CURRENTLY displaying, this is a no-op — nothing is re-derived, <see cref="AuthoritativeField"/>
    /// does not change. This matters even with the LostFocus/Enter convention (R-fix-1) and R-fix-2's
    /// "never write the authoritative field" rule both in place: tabbing through a field the user never
    /// typed into still fires LostFocus with its box holding whatever the last refresh wrote — an
    /// already-rounded display string. Re-deriving a factor from THAT would silently reintroduce the
    /// exact corruption this whole fix exists to prevent, just via a different trigger (Tab instead of
    /// a stray TextChanged). Treating "nothing actually changed" as "do nothing" closes that path too.</summary>
    public bool Edit(ScaleField field, string text)
    {
        if (string.Equals(text, RawDisplayFor(field), StringComparison.Ordinal)) return true;
        return field switch
        {
            ScaleField.FactorX => TrySetFactorXText(text),
            ScaleField.FactorY => TrySetFactorYText(text),
            ScaleField.Width => TrySetWidthText(text),
            ScaleField.Height => TrySetHeightText(text),
            _ => false,
        };
    }

    /// <summary>User typed directly into the Factor box.</summary>
    private bool TrySetFactorXText(string text)
    {
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fx) || fx <= 0)
            return false;
        FactorX = fx;
        if (IsUniform) FactorY = fx;
        AuthoritativeField = ScaleField.FactorX;
        return true;
    }

    /// <summary>User typed directly into the Factor Y box (only reachable when Uniform is off).</summary>
    private bool TrySetFactorYText(string text)
    {
        if (!double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fy) || fy <= 0)
            return false;
        FactorY = fy;
        AuthoritativeField = ScaleField.FactorY;
        return true;
    }

    /// <summary>User typed a dimension directly into the Width box — the factor is derived EXACTLY
    /// (double division, no intermediate rounding) from the parsed DBU value and the ORIGINAL width,
    /// never from any already-rounded display text.</summary>
    private bool TrySetWidthText(string text)
    {
        if (!LayoutUnits.TryParse(text, _displayUnit, _dbuPerMicron, out long w) || w <= 0)
            return false;
        double fx = (double)w / _origWidthDbu;
        FactorX = fx;
        if (IsUniform) FactorY = fx;
        AuthoritativeField = ScaleField.Width;
        return true;
    }

    /// <summary>User typed a dimension directly into the Height box — same exactness guarantee as
    /// <see cref="TrySetWidthText"/>.</summary>
    private bool TrySetHeightText(string text)
    {
        if (!LayoutUnits.TryParse(text, _displayUnit, _dbuPerMicron, out long h) || h <= 0)
            return false;
        double fy = (double)h / _origHeightDbu;
        FactorY = fy;
        if (IsUniform) FactorX = fy;
        AuthoritativeField = ScaleField.Height;
        return true;
    }
}
