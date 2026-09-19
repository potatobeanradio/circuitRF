using CircuitRF.Design.Layout.Pdn;

namespace CircuitRF.Design.RailRf;

/// <summary>
/// One marker on the rail's |Z| curve, as the <c>.crail</c> keeps it (owner, 2026-09-19).
/// </summary>
/// <remarks>
/// <b>A marker is a reading somebody took, and a reading is worth keeping.</b> railRF's results plot
/// is a real Data Display <c>PlotControl</c>, so a double-click puts a marker on a curve and its info
/// box states the frequency and the impedance there — and every one of them was gone the moment the
/// window closed, because the document said nothing about markers at all.
///
/// <para><b>It names a CURVE, not a trace object.</b> Every trace on that plot is rebuilt from the
/// sweep on each re-solve, which is every committed edit, so a marker pinned to a trace reference
/// would be pinned to something that does not survive a keystroke. What survives is what a curve IS —
/// the READING it was taken at and the PORT it is of — which is exactly the key
/// <c>RailRfViewModel.CurveKey</c> already matches curves across a rebuild with. One identity, not
/// two.</para>
///
/// <para><b>The frequency is in HERTZ</b>, like every other frequency in this document: a mark read
/// without its scale once produced a run at 2 Hz that looked entirely normal
/// (src/Engine/RESOLVED.md). It is the marker's CUBE X — what a marker on a cube-bound trace is
/// actually pinned by, and what the renderer resolves it from. <c>Marker.Freq</c> is the other,
/// network-bound spelling and is 0 on every trace this plot holds; the add path in
/// <c>PlotControl.TryAddMarkerNearPoint</c> leaves it so, and restoring does the same.</para>
///
/// <para><b>The info-box position is NULLABLE, and that is not tidiness.</b> A marker that has never
/// been drawn has no box position, and the Data Display spells that <c>NaN</c> — which
/// <c>System.Text.Json</c> refuses to write. Null is "place it where the renderer would", which is a
/// thing this format can say and <c>NaN</c> is not.</para>
/// </remarks>
/// <param name="Model">Which reading's curve — the fast one or the accurate one.</param>
/// <param name="Port">The observation port's own index, as the sweep numbers them.</param>
/// <param name="FrequencyHz">Where on that curve it sits, in hertz.</param>
public sealed record RailMarker(PdnModelKind Model, int Port, double FrequencyHz)
{
    /// <summary>
    /// Which curve of a family trace, where the trace has several. Zero on every curve railRF
    /// builds — a <c>Z[:, n, n]</c> slice iterates no family axis — and kept because it is the other
    /// half of the pair the renderer reads, not because this plot varies it.
    /// </summary>
    public int CurveIndex { get; init; }

    /// <summary>The marker's number — the <c>m3</c> on its glyph and in its info box.</summary>
    public int Index { get; init; } = 1;

    /// <summary>What it is called. Empty means the number, which is what a marker is called until
    /// somebody renames it.</summary>
    public string Name { get; init; } = "";

    /// <summary>Where its info box was dragged to, in the display's own logical coordinates, or null
    /// for one nobody has moved.</summary>
    public double? InfoBoxX { get; init; }

    /// <summary>Companion of <see cref="InfoBoxX"/>. Both or neither.</summary>
    public double? InfoBoxY { get; init; }

    /// <summary>Whether its box is drawn. False still draws the glyph — that is the Data Display's
    /// own distinction, and a marker whose box was closed is not a marker that was removed.</summary>
    public bool ShowInfoBox { get; init; } = true;
}
