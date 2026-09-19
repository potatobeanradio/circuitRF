// Markers, kept in the `.crail` (owner, 2026-09-19).
//
// ── WHY THEY HAD TO BE KEPT ───────────────────────────────────────────────────────────────────
//
// railRF's results plot is a real Data Display PlotControl, so a double-click puts a marker on a
// curve and its info box states the frequency and the impedance there. That is a READING somebody
// took — the thing they came to the window for — and the document said nothing about markers at
// all, so every one of them was gone the moment the window closed.
//
// ── THE TWO HALVES, AND THE ONE GUARD BETWEEN THEM ────────────────────────────────────────────
//
// RESTORE runs once per rail, off the tail of RebuildImpedancePlot — the first time that rail's
// curves exist. After that the live Marker objects are what the user is working with, and
// RebuildImpedancePlot's own `Carry` moves them across each re-solve as OBJECTS (its note says why:
// a marker rebuilt as an equal-but-different object loses its selection, its box position and its
// m-number).
//
// CAPTURE runs off DataDisplayViewModel.ContentChanged, which is the SAME channel a `.cdd`
// document's own dirty check runs off. That is deliberate: a hand-maintained list of marker events
// would be a list that misses one — the info-box drag, the marker editor's rename, the close box —
// and each of those is a change the owner asked to see marked. One channel, and it is the Data
// Display's own.
//
// The guard is _markersRestored. Capture must never run before the document's own set has been put
// on the plot, because at that moment the plot has no markers and the document does — and capturing
// would write the empty plot over the saved reading, which is the one failure that loses work
// silently.

using System;
using System.Collections.Generic;
using System.Linq;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Render.DataDisplay;

namespace CircuitRF.Ui.RailRf;

public sealed partial class RailRfViewModel
{
    /// <summary>
    /// The curves whose saved markers have been put on the plot — by <c>CurveKey</c>.
    /// </summary>
    /// <remarks>
    /// <b>Per CURVE and not one flag for the plot</b>, because the curves do not all arrive at once:
    /// a document opens with the fast reading alone and the accurate one appears only when Accuracy
    /// is pressed. A single "restored" flag set on the first rebuild would mean the accurate curve's
    /// saved markers were never put on it, and — worse — that the capture below would then speak for
    /// that curve and delete them.
    /// </remarks>
    private readonly HashSet<string> _markersRestoredFor = new(StringComparer.Ordinal);

    /// <summary>True while this file is itself moving markers, so its own writes do not re-enter
    /// <see cref="CaptureMarkers"/> through <c>ContentChanged</c>.</summary>
    private bool _markersSettling;

    /// <summary>
    /// Forgets that the markers on the plot belong to the rail they were restored for.
    /// </summary>
    /// <remarks>
    /// Called when the document is replaced and when the SELECTED RAIL changes — the two moments
    /// when the plot is about to show a different rail's ports. Without it the markers of the rail
    /// being left behind would be captured onto the rail being moved to: the curve key is the
    /// reading and the port, which both rails have, so nothing about the objects themselves would
    /// say they were the wrong rail's.
    /// </remarks>
    private void ForgetRestoredMarkers() => _markersRestoredFor.Clear();

    /// <summary>
    /// Puts the selected rail's saved markers onto its curves, replacing whatever is on them.
    /// </summary>
    /// <remarks>
    /// <b>Keyed on <c>CurveKey</c>, which is not a second identity.</b> That key — the reading and
    /// the port — is already what <see cref="RebuildImpedancePlot"/> matches curves across a
    /// re-solve with, and it is what <see cref="RailMarker"/> stores. A marker whose curve is not on
    /// the plot (a reading that has not been run, a port the design no longer has) is simply not
    /// restored and is NOT dropped from the document either: pressing Accuracy is what brings the
    /// accurate curve's markers back, and losing them in between would be a silent edit.
    /// </remarks>
    private void RestoreMarkers()
    {
        if (SelectedRail is not { } rail) return;

        bool was = _markersSettling;
        _markersSettling = true;
        try
        {
            foreach (var (key, curve) in _curves)
            {
                // Already the user's — `Carry` moved the live objects across the re-solve and the
                // document was written back from them. Restoring again would replace what is on
                // screen with what was last saved.
                if (!_markersRestoredFor.Add(key)) continue;

                curve.Markers.Clear();

                foreach (var saved in rail.Markers)
                {
                    if (!string.Equals(CurveKey(saved.Model, saved.Port), key, StringComparison.Ordinal))
                        continue;

                    curve.Markers.Add(ToMarker(curve, saved));
                }
            }
        }
        finally
        {
            _markersSettling = was;
        }
    }

    /// <summary>
    /// One saved marker, as the live object the renderer reads.
    /// </summary>
    /// <remarks>
    /// <b>Built exactly as <c>PlotControl.TryAddMarkerNearPoint</c> builds one</b>, because a marker
    /// restored differently from one placed by hand is a marker that draws somewhere else. Two
    /// things that looks surprising and are not:
    ///
    /// <para><c>Freq</c> stays 0. On a CUBE-BOUND trace — which every curve here is
    /// (<c>Trace.IsCubeXMarker</c>) — the position is <c>PositionStatic</c> = (cube X, curve index),
    /// and <c>Freq</c> is the network-bound spelling that nothing on this plot reads.</para>
    ///
    /// <para><c>MarkerKind</c> is derived from the TRACE rather than stored. It follows
    /// <c>IsFamily</c>, and a stored value could disagree with the curve it was restored onto.</para>
    /// </remarks>
    private static Marker ToMarker(Trace curve, RailMarker saved)
    {
        var marker = new Marker(curve, 0.0, isMulti: false, isDelta: false, saved.Index, FreqUnit.MHz)
        {
            MarkerKind     = curve.IsFamily ? MarkerKind.Spectrum : MarkerKind.Polyline,
            PositionStatic = new System.Numerics.Vector2((float)saved.FrequencyHz, saved.CurveIndex),
            ShowInfoBox    = saved.ShowInfoBox,
        };

        if (saved.Name is { Length: > 0 } name) marker.Name = name;

        // A box nobody has dragged has NO position, which the Data Display spells NaN and the
        // document spells absent — and NaN is what DataDisplayViewModel then replaces with its own
        // default placement. Writing 0,0 here instead would pin every reopened box to the corner.
        if (saved.InfoBoxX is { } x && saved.InfoBoxY is { } y)
            marker.InfoBoxPos = new PlotPoint(x, y);

        return marker;
    }

    /// <summary>
    /// Writes the markers on the plot back onto the selected rail, and marks the document.
    /// </summary>
    /// <remarks>
    /// <b>Moving a marker dirties the document</b> — the owner's own instruction, and the reason
    /// this is called from <c>ContentChanged</c> rather than at save time. Dragging a marker changes
    /// its frequency and dragging its info box changes the box position; both land here, both differ
    /// from what is stored, and <see cref="RefreshDirty"/> is what puts the bullet on the title.
    ///
    /// <para><b>It compares before it writes.</b> <c>ContentChanged</c> is a broad channel — a pan,
    /// a redraw request, a selection — so most calls have nothing to say, and a write on every one
    /// of them would re-serialize the document for a frame that changed nothing.</para>
    ///
    /// <para><b>A marker whose curve is not on the plot is KEPT, verbatim.</b> This is the exact
    /// mirror of what <see cref="RestoreMarkers"/> does not restore, and it is what makes the two
    /// safe together: the accurate curve exists only after Accuracy has been pressed, so rebuilding
    /// the list from what is drawn would quietly delete every marker on the reading that is not
    /// currently on screen. It also makes a capture that lands while the plot is being rebuilt —
    /// no curves at all for an instant, or a rail whose markers have not been restored yet — a
    /// no-op by construction rather than by timing.</para>
    /// </remarks>
    public void CaptureMarkers()
    {
        if (_markersSettling) return;
        if (SelectedRail is not { } rail) return;

        // 1. What the plot cannot currently speak for — every curve that is not drawn, and every
        //    curve that is drawn but has not yet been given this rail's saved markers.
        var next = rail.Markers
            .Where(m => !_markersRestoredFor.Contains(CurveKey(m.Model, m.Port)))
            .ToList();

        // 2. What it can.
        foreach (var (key, curve) in _curves)
        {
            if (!_markersRestoredFor.Contains(key)) continue;
            if (!TryParseCurveKey(key, out var model, out int port)) continue;

            foreach (var marker in curve.Markers)
                next.Add(new RailMarker(model, port, marker.PositionStatic.X)
                {
                    CurveIndex  = (int)marker.PositionStatic.Y,
                    Index       = marker.Index,
                    Name        = marker.Name,
                    InfoBoxX    = double.IsFinite(marker.InfoBoxPos.X) ? marker.InfoBoxPos.X : null,
                    InfoBoxY    = double.IsFinite(marker.InfoBoxPos.Y) ? marker.InfoBoxPos.Y : null,
                    ShowInfoBox = marker.ShowInfoBox,
                });
        }

        // A DETERMINISTIC order, so a document saved twice with no edit between is the same bytes
        // and revision control has nothing to show — the rule every other list in this format
        // follows. It is also what makes the comparison below meaningful.
        next = [.. next.OrderBy(m => m.Model)
                       .ThenBy(m => m.Port)
                       .ThenBy(m => m.Index)
                       .ThenBy(m => m.FrequencyHz)];

        if (next.SequenceEqual(rail.Markers)) return;

        rail.Markers.Clear();
        rail.Markers.AddRange(next);
        RefreshDirty();
    }

    /// <summary>The inverse of <see cref="CurveKey"/>. False for anything that is not one.</summary>
    private static bool TryParseCurveKey(string key, out PdnModelKind model, out int port)
    {
        model = PdnModelKind.Fast;
        port  = 0;

        int bar = key.IndexOf('|');
        return bar > 0
            && Enum.TryParse(key[..bar], out model)
            && int.TryParse(key[(bar + 1)..], System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture, out port);
    }
}
