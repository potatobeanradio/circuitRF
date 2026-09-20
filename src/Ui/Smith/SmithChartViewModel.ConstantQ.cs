using System.Globalization;
using CircuitRF.Ui.Matching;
using CommunityToolkit.Mvvm.Input;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// The constant-Q arcs (<c>brief-smith-9-q-and-sweep.md</c> <c>R-smith9-2</c>;
/// <c>docs/design/smith-chart.md</c> §4.4).
/// </summary>
/// <remarks>
/// <b>It evaluates nothing</b>: the arcs are <c>SmithQArcs</c>'s closed form, below the firewall, so
/// brief 10's headless render draws exactly what this window does.
///
/// <para><b>The swept band used to live here too and no longer exists as a setting</b> (owner
/// instruction, 2026-09-19). It is always drawn, across the generator table's own span, so there is
/// no checkbox, no start, no stop and no point count — see <c>SmithBand</c>, where the reason is.</para>
///
/// <para><b>Every value here goes through <see cref="Edit"/></b>, which is the single place "one
/// gesture is one undo entry" is enforced.</para>
/// </remarks>
public sealed partial class SmithChartViewModel
{
    // ── the constant-Q arcs ──────────────────────────────────────────────────

    /// <summary>Draw the pair. <b>Off by default</b> — the arcs are a ruler laid over the work, and
    /// a tool that opened with one already on the chart would be answering a question nobody had
    /// asked yet.</summary>
    public bool ConstantQEnabled
    {
        get => _design.ConstantQ.Enabled;
        set
        {
            if (_design.ConstantQ.Enabled == value) return;
            Edit(value ? "Show constant-Q arcs" : "Hide constant-Q arcs",
                 () => _design.ConstantQ.Enabled = value);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Flips <see cref="ConstantQEnabled"/> — what the <b>Q</b> button in the chart's top-left corner
    /// does (owner instruction, 2026-09-19).
    /// </summary>
    /// <remarks>
    /// <b>A command rather than a two-way binding</b>, because the control is a square toolbar
    /// button like every other one in this window and a <c>Button</c> has no checked state to bind;
    /// what says it is on is its <c>ToolActive</c> class, which the same property drives. The undo
    /// entry and the dirty mark are <see cref="ConstantQEnabled"/>'s own and are unchanged.
    /// </remarks>
    [RelayCommand]
    private void ToggleConstantQ() => ConstantQEnabled = !ConstantQEnabled;

    /// <summary>
    /// Q itself, <c>|x|/r</c> — <b>a bare number with no unit</b>, because it is a ratio of two
    /// reactances-over-resistances and giving it one would invite the reader to look for an ohm in it.
    /// </summary>
    /// <remarks>
    /// The same value the drag writes (<see cref="DragQTo"/>), so typing 3 and dragging to 3 leave
    /// the document in the same state — there is one Q and one place it is stored.
    /// </remarks>
    public string ConstantQEntry
    {
        get => MatchValueFormat.Significant(_design.ConstantQ.Q, 4);
        set
        {
            bool ok = double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out double q)
                   || double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out q);

            if (ok && double.IsFinite(q) && q > 0)
                Edit("Edit Q", () => _design.ConstantQ.Q = q);

            OnPropertyChanged();
            if (!ok) RefreshDerived();
        }
    }
}
