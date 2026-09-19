using System;
using System.Globalization;
using CircuitRF.Design.Smith;
using CircuitRF.Ui.Matching;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// The panel's last two cards: the constant-Q arcs and the swept band
/// (<c>brief-smith-9-q-and-sweep.md</c> <c>R-smith9-2</c>, <c>R-smith9-4</c>;
/// <c>docs/design/smith-chart.md</c> §4.4, §3.6).
/// </summary>
/// <remarks>
/// <b>Two small, independent features that both answer the same question</b> — <i>how narrowband is
/// this really</i> — and neither of them evaluates anything: the arcs are
/// <see cref="SmithQArcs"/>'s closed form and the band is <see cref="SmithBand"/>'s walk, both below
/// the firewall so brief 10's headless render draws exactly what this window does.
///
/// <para><b>Every value here goes through <see cref="Edit"/></b>, which is the single place "one
/// gesture is one undo entry" is enforced, and <b>every one of them is an <c>InlineEditText</c></b>
/// (<c>R-smith4-5</c>, owner instruction) — which is what these string properties are for. A field
/// whose text does not parse changes nothing and snaps back, exactly as
/// <see cref="ChartZ0Entry"/> does.</para>
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

    // ── the swept band (R-smith9-4) ──────────────────────────────────────────

    /// <summary>
    /// Draw the band. <b>Off by default (owner decision)</b>, because this tool's premise is that
    /// bandwidth is not the question — the band is what makes it visible when somebody wants to ask
    /// it anyway, and it costs one <c>Evaluate</c> per point.
    /// </summary>
    public bool SweepEnabled
    {
        get => _design.Sweep.Enabled;
        set
        {
            if (_design.Sweep.Enabled == value) return;

            Edit(value ? "Show swept band" : "Hide swept band", () =>
            {
                _design.Sweep.Enabled = value;

                // A BAND WITH NOWHERE TO GO IS A REFUSAL (SmithDesign.Refusal), so turning one on for
                // the first time seeds it from the generator table rather than from zero. The table's
                // own span is the honest default: it is exactly the band this tool can answer for.
                if (value && !(_design.Sweep.StartHz < _design.Sweep.StopHz))
                    SeedBandFromTable();
            });

            OnPropertyChanged();
            OnPropertyChanged(nameof(SweepStartEntry));
            OnPropertyChanged(nameof(SweepStopEntry));
        }
    }

    /// <summary>
    /// The band's ends, in the table's span when it has one and ±10 % of the design frequency when it
    /// does not — a single-row table is one impedance, flat, so every frequency is legal against it
    /// and the span is not a bound.
    /// </summary>
    private void SeedBandFromTable()
    {
        if (_design.Generator.Rows.Count > 1 && _design.Generator.Span is { } span)
        {
            _design.Sweep.StartHz = span.StartHz;
            _design.Sweep.StopHz  = span.StopHz;
            return;
        }

        double f = _design.Chart.DesignFrequencyHz;
        if (!(f > 0)) return;

        _design.Sweep.StartHz = f * 0.9;
        _design.Sweep.StopHz  = f * 1.1;
    }

    /// <summary>The band's start. <b>Hertz in the document</b>, spelled by
    /// <see cref="MatchValueFormat"/> here — the strip's own spelling, so the field and the clamp note
    /// cannot disagree about which frequency they mean.</summary>
    public string SweepStartEntry
    {
        get => MatchValueFormat.FormatWithUnit(_design.Sweep.StartHz, MatchQuantity.Frequency,
                                               MatchValueFormat.AutoUnit, 6);
        set => SetBandFrequency(value, "Edit band start", hz => _design.Sweep.StartHz = hz);
    }

    /// <inheritdoc cref="SweepStartEntry"/>
    public string SweepStopEntry
    {
        get => MatchValueFormat.FormatWithUnit(_design.Sweep.StopHz, MatchQuantity.Frequency,
                                               MatchValueFormat.AutoUnit, 6);
        set => SetBandFrequency(value, "Edit band stop", hz => _design.Sweep.StopHz = hz);
    }

    private void SetBandFrequency(string text, string description, Action<double> apply)
    {
        bool ok = MatchValueFormat.TryParseWithUnit(text, MatchQuantity.Frequency, "GHz",
                                                    out double hz, out _) && hz > 0;
        if (ok) Edit(description, () => apply(hz));

        OnPropertyChanged(nameof(SweepStartEntry));
        OnPropertyChanged(nameof(SweepStopEntry));
        if (!ok) RefreshDerived();
    }

    /// <summary>How many points the band is walked at. Two is the fewest that draws one, which is
    /// <see cref="SmithDesign.Refusal"/>'s own rule rather than a second copy of it.</summary>
    public string SweepPointsEntry
    {
        get => _design.Sweep.Points.ToString(CultureInfo.CurrentCulture);
        set
        {
            bool ok = int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out int n)
                   || int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out n);

            if (ok && n >= 2) Edit("Edit band points", () => _design.Sweep.Points = n);

            OnPropertyChanged();
            if (!ok) RefreshDerived();
        }
    }
}
