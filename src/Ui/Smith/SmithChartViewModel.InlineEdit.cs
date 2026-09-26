using System;
using System.Linq;
using CircuitRF.Design.Smith;
using CircuitRF.Ui.Matching;
using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Smith;

/// <summary>What one double-clicked label in the network pane writes.</summary>
public enum SmithInlineEditField
{
    /// <summary>The instance name — the strip's own Name field, reached from the drawing.</summary>
    Name,

    /// <summary>One slider-backed parameter: R, L, C, a line's Z₀ or its electrical length.</summary>
    Parameter,

    /// <summary>
    /// A line's <c>F</c> — the frequency its electrical length is quoted at, which has no slider.
    /// </summary>
    ReferenceFrequency,
}

/// <summary>
/// One resolved inline edit: which element, which of its values, and where the editor opens.
/// </summary>
/// <param name="ElementIndex">The element's index in <see cref="SmithDesign.Elements"/>.</param>
/// <param name="ElementName">Its name when the editor opened — what a commit re-checks against.</param>
/// <param name="ComponentId">The drawn component, for the canvas to re-anchor the editor on.</param>
/// <param name="Row">The label row the editor sits on: 1 for the name, 2+ for a shown parameter.</param>
/// <param name="Field">What the typed text is written to.</param>
/// <param name="Parameter">Which parameter, when <paramref name="Field"/> is
/// <see cref="SmithInlineEditField.Parameter"/>.</param>
/// <param name="Unit">The unit a bare number is read in — the one the label is drawn in.</param>
/// <param name="SeedText">What the editor opens holding — computed from the value, never the label.</param>
public sealed record SmithInlineEditTarget(
    int ElementIndex, string ElementName, string ComponentId, int Row,
    SmithInlineEditField Field, SmithParameter Parameter, string Unit, string SeedText);

/// <summary>
/// The network pane's inline value editor — the Match Designer's, on this tool's drawing.
/// </summary>
/// <remarks>
/// <b>A line's <c>F</c> had no door at all.</b> It is drawn on every TLIN and stub, the stranded-F_ref
/// note tells the user to "Edit F on the element", and yet the strip's rows are
/// <see cref="SmithComponentMap.Parameters"/>' — the parameters a gripper can drag — which a
/// reference frequency is not. Double-clicking a label on the drawing now edits what the label says,
/// whichever of the three doors it is.
///
/// <para><b>A slider-backed value goes through its slider row</b>, a temporary one when the strip is
/// showing a different element. That is not a convenience: a typed value outside the row's range
/// must WIDEN the range before the value lands, or the slider's own coercion clamps it and writes the
/// clamped value back — the Match Designer's defect, recorded in <c>src/Ui/Match/RESOLVED.md</c>. The
/// row already does that in the right order, and a second copy of it here would be the one that
/// drifted.</para>
/// </remarks>
public sealed partial class SmithChartViewModel
{
    /// <summary>
    /// Resolves a double-click on the network pane to the one value it edits, or null when the thing
    /// hit is not something the drawing can set.
    /// </summary>
    /// <remarks>
    /// <b>A click on the glyph opens the element's ACTIVE parameter</b> — the one the strip marks and
    /// the chart's gripper drags — because at the zoom this pane frames a cascade at, the symbol is
    /// the target a user can hit and a label row is a few pixels tall. The generator, the load pin and
    /// a shunt arm's ground are not elements and resolve to nothing; the generator is edited in its
    /// own table. A file element's <c>File</c> label resolves to nothing too: a file is chosen, not
    /// typed.
    /// </remarks>
    public SmithInlineEditTarget? ResolveInlineEdit(SchematicHitTest.HitResult hit)
    {
        if (string.IsNullOrEmpty(hit.Id)) return null;
        if (!Network.ElementIndexByComponentId.TryGetValue(hit.Id, out int index)) return null;
        if (ElementAt(index) is not { } e) return null;
        if (Network.Edit.FindComponent(hit.Id) is not { } comp) return null;

        string? paramName = hit.Kind switch
        {
            SchematicHitTest.HitKind.ComponentName  => null,
            SchematicHitTest.HitKind.ComponentParam => comp.Parameters.ElementAtOrDefault(hit.SubIndex)?.Name,
            SchematicHitTest.HitKind.Component      => LabelName(SmithComponentMap.ActiveParameterOf(e), e.Kind),
            _                                       => "",
        };

        if (paramName is null)
            return new SmithInlineEditTarget(index, e.Name, hit.Id, 1, SmithInlineEditField.Name,
                                             SmithParameter.None, "", e.Name);
        if (paramName.Length == 0) return null;

        // The label row, counted exactly as EditableComponent.BuildRenderModel lays them out: type,
        // name, then every shown parameter with an expression.
        int shown = comp.LabelParameters()
                        .Where(p => !string.IsNullOrEmpty(p.Expression))
                        .Select(p => p.Name)
                        .ToList()
                        .IndexOf(paramName);
        if (shown < 0) return null;
        int row = 2 + shown;

        if (SmithComponentMap.IsLine(e.Kind) && paramName == "F")
        {
            var (text, unit) = MatchValueFormat.Format(
                e.Values.ReferenceFrequencyHz, MatchQuantity.Frequency, MatchValueFormat.AutoUnit, 5);
            return new SmithInlineEditTarget(index, e.Name, hit.Id, row,
                                             SmithInlineEditField.ReferenceFrequency,
                                             SmithParameter.None, unit, $"{text} {unit}".Trim());
        }

        var parameter = SmithComponentMap.Parameters(e.Kind)
                                         .FirstOrDefault(p => LabelName(p, e.Kind) == paramName,
                                                         SmithParameter.None);
        if (parameter == SmithParameter.None) return null;

        var rowVm = SliderRowFor(index, parameter);
        return new SmithInlineEditTarget(index, e.Name, hit.Id, row, SmithInlineEditField.Parameter,
                                         parameter, "", rowVm.ValueEntry);
    }

    /// <summary>
    /// Applies one inline edit. Returns true when something was written; false leaves the design
    /// alone, with the reason in <see cref="StripNotice"/> when there is one worth saying.
    /// </summary>
    public bool CommitInlineEdit(SmithInlineEditTarget target, string? text)
    {
        ArgumentNullException.ThrowIfNull(target);

        string typed = (text ?? "").Trim();
        if (typed.Length == 0) return false;

        // Re-committing the seed is not an edit. The seed is ROUNDED to the label's digits, so writing
        // it back would move a 50.0004 Ω line to 50 Ω and push an undo entry nobody asked for — the
        // Match Designer's own "I changed nothing and got a message" report.
        if (string.Equals(typed, target.SeedText, StringComparison.Ordinal)) return false;

        // The element the editor was opened on. An undo or a reorder while the box was open can put a
        // different element at that index, and a value typed about one part must not land on another.
        if (ElementAt(target.ElementIndex) is not { } e
         || !string.Equals(e.Name, target.ElementName, StringComparison.Ordinal))
            return false;

        switch (target.Field)
        {
            case SmithInlineEditField.Name:
                return RenameElement(e, typed);

            case SmithInlineEditField.ReferenceFrequency:
            {
                if (!MatchValueFormat.TryParseWithUnit(typed, MatchQuantity.Frequency, target.Unit,
                                                       out double hz, out _))
                {
                    StripNotice = $"\"{typed}\" is not a frequency F on {e.Name} can read.";
                    return false;
                }
                if (!(hz > 0) || !double.IsFinite(hz))
                {
                    StripNotice = $"F on {e.Name} must be greater than zero — it is the frequency the "
                                + "electrical length is quoted at.";
                    return false;
                }

                Edit($"Edit F on {e.Name}", () => e.Values.ReferenceFrequencyHz = hz);
                return true;
            }

            default:
            {
                var row = SliderRowFor(target.ElementIndex, target.Parameter);
                if (!row.TryParseEntry(typed, out _))
                {
                    StripNotice = $"\"{typed}\" is not a value {row.Label} on {e.Name} can read.";
                    return false;
                }

                row.ValueEntry = typed;
                return true;
            }
        }
    }

    /// <summary>
    /// The strip's row for one parameter when the strip is showing it, and a fresh one otherwise —
    /// see the type's remarks on why a typed value goes through a row at all.
    /// </summary>
    private SmithSliderRowViewModel SliderRowFor(int elementIndex, SmithParameter parameter)
        => SliderRows.FirstOrDefault(r => r.ElementIndex == elementIndex && r.Parameter == parameter)
        ?? new SmithSliderRowViewModel(this, elementIndex, parameter);

    /// <summary>
    /// The name a parameter is DRAWN under — <see cref="SmithNetworkModel"/>'s spelling, which is the
    /// component registry's and not the strip's (the strip says Z₀ where the drawing says Z).
    /// </summary>
    private static string LabelName(SmithParameter p, SmithElementKind kind) => p switch
    {
        SmithParameter.R                => "R",
        SmithParameter.L                => "L",
        SmithParameter.C                => "C",
        SmithParameter.Z0 when SmithComponentMap.IsLine(kind) => "Z",
        SmithParameter.ElectricalLength => "E",
        _                               => "",
    };
}
