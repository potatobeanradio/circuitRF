using System;
using System.Numerics;
using CircuitRF.Design.Smith;
using CircuitRF.Render.DataDisplay;
using RfCore;

namespace CircuitRF.Render.Smith;

/// <summary>
/// The `.csmith`'s <see cref="SmithMarker"/> block and the Data Display's own <c>Marker</c>, in both
/// directions (<c>brief-smith-8-overlays-markers.md</c> <c>R-smith8-5</c>;
/// <c>docs/design/smith-chart.md</c> §4.5, §7).
/// </summary>
/// <remarks>
/// <b>There is no second marker model and this is not one.</b> <see cref="SmithMarker"/> is the Data
/// Display's <c>MarkerConfig</c> shape, field for field and default for default, and exists only
/// because <c>Marker</c> lives in <c>src/Render</c>, which <c>src/Design</c> is below. So every
/// mapping below is an assignment or one <c>Enum.Parse</c>, and the bytes a `.csmith` writes for a
/// marker are the bytes a `.cdd` writes for the same marker.
///
/// <para><b>An unreadable enum name falls back to the default rather than refusing.</b> A marker is
/// a reading somebody took; losing the whole document because one of them names a style that no
/// longer exists would be the wrong trade, and the position — the part that is actually the
/// reading — survives either way.</para>
///
/// <para><b><see cref="SmithMarker.TraceName"/> is the one field <c>MarkerConfig</c> does not have,
/// and it is not an invention.</b> A `.cdd` nests its markers UNDER their trace, so the association
/// is the file's own structure; a `.csmith` has no trace list to nest them in, because every trace
/// on this chart is rebuilt from the design on each edit. The key is the trace's LABEL rather than
/// its index — an element's name, or an overlay's file and quantity — because an index moves when
/// an element is deleted and a marker that silently jumped to the next curve would be a reading
/// reported against the wrong thing.</para>
/// </remarks>
public static class SmithMarkerBridge
{
    /// <summary>The document's marker as the Data Display's, ready to be added to
    /// <paramref name="trace"/>'s marker list.</summary>
    public static Marker ToMarker(SmithMarker m, Trace trace)
    {
        ArgumentNullException.ThrowIfNull(m);
        ArgumentNullException.ThrowIfNull(trace);

        var marker = new Marker(trace, m.Freq, m.IsMulti, m.IsDelta, m.Index,
                                Parse(m.FreqUnits, FreqUnit.GHz))
        {
            Name                   = m.Name,
            MatrixFormat           = Parse(m.MatrixFormat,          MatrixFormat.MA),
            MatrixFormatImpedance  = Parse(m.MatrixFormatImpedance, MatrixFormat.RI),
            Style                  = Parse(m.Style,                 MarkerStyle.Medium),
            MarkerKind             = Parse(m.MarkerKind,            MarkerKind.Polyline),
            UseNormalizedImpedance = m.UseNormalizedImpedance,
            MaximumFractionDigits  = m.MaximumFractionDigits,
            InfoBoxPos             = new PlotPoint(m.InfoBoxX, m.InfoBoxY),
            PositionStatic         = new Vector2(m.PositionStaticX, m.PositionStaticY),
            FreePosition           = m.FreePosition,
            SnappedToCurve         = m.SnappedToCurve,
            ShowInfoBox            = m.ShowInfoBox,
            ContourSnapped         = m.ContourSnapped,
            VswrEnabled            = m.VswrEnabled,
            VswrValue              = m.VswrValue,
        };

        return marker;
    }

    /// <summary>The Data Display's marker as the document's, keyed to the trace it sits on.</summary>
    public static SmithMarker FromMarker(Marker m, string traceName)
    {
        ArgumentNullException.ThrowIfNull(m);

        return new SmithMarker
        {
            TraceName              = traceName,
            Name                   = m.Name,
            Index                  = m.Index,
            Freq                   = m.Freq,
            FreqUnits              = m.FreqUnits.ToString(),
            MatrixFormat           = m.MatrixFormat.ToString(),
            MatrixFormatImpedance  = m.MatrixFormatImpedance.ToString(),
            Style                  = m.Style.ToString(),
            MarkerKind             = m.MarkerKind.ToString(),
            UseNormalizedImpedance = m.UseNormalizedImpedance,
            MaximumFractionDigits  = m.MaximumFractionDigits,
            // EVERY PERSISTED FLOATING-POINT FIELD IS GUARDED TO A FINITE VALUE, and this is not
            // defensive decoration: Marker.InfoBoxPos DEFAULTS to NaN — "not placed yet" — and
            // System.Text.Json throws outright on NaN and ±∞. SmithDesignIo.SerializeUnvalidated is
            // called on every committed edit to build the undo snapshot, so one freshly-placed
            // marker whose box had not been laid out yet would take the document down mid-edit.
            // The Data Display carries the identical guard for the identical reason
            // (DataDisplayViewModel's own Finite(), and the note beside it).
            InfoBoxX               = Finite(m.InfoBoxPos.X),
            InfoBoxY               = Finite(m.InfoBoxPos.Y),
            IsMulti                = m.IsMulti,
            IsDelta                = m.IsDelta,
            PositionStaticX        = Finite(m.PositionStatic.X),
            PositionStaticY        = Finite(m.PositionStatic.Y),
            FreePosition           = m.FreePosition,
            SnappedToCurve         = m.SnappedToCurve,
            ShowInfoBox            = m.ShowInfoBox,
            ContourSnapped         = m.ContourSnapped,
            VswrEnabled            = m.VswrEnabled,
            VswrValue              = Finite(m.VswrValue, fallback: 2.0),
        };
    }

    private static double Finite(double v, double fallback = 0.0) => double.IsFinite(v) ? v : fallback;
    private static float  Finite(float  v, float  fallback = 0f)  => float.IsFinite(v)  ? v : fallback;

    /// <summary>
    /// The constant-VSWR circle about <paramref name="marker"/>, as its centre and radius in the Γ
    /// plane.
    /// </summary>
    /// <remarks>
    /// <b>A constant-VSWR circle about a marker is NOT centred on that marker unless the marker is
    /// at Γ = 0</b> (<c>R-smith8-6</c>). <c>vswr-locus-gamma-plane.md</c> derives it and
    /// <c>HarmonicaVswrHandle</c>'s header records that it was got wrong once by reading "the
    /// matched point" as "wherever the marker is": a marker at (0.3, −0.2) with VSWR 3 has its true
    /// centre at (0.23, −0.16), which is far outside any grab tolerance.
    ///
    /// <para>So this calls <c>LoadpullSurface.VswrLocus</c> and reads the answer off it, exactly as
    /// <c>HarmonicaVswrHandle.CircleParams</c> does — the θ = 0 and θ = π samples are diametrically
    /// opposite BY CONSTRUCTION, so their midpoint IS the centre and half their separation IS the
    /// radius. Constructing a circle here would be the mistake the correction is about.</para>
    /// </remarks>
    public static (Complex Centre, double Radius) VswrCircle(Complex marker, double vswr, double z0)
    {
        var pts = RfCore.Loadpull.LoadpullSurface.VswrLocus(
            marker, vswr, RfCore.Loadpull.SurfacePlane.Gamma, new Complex(z0, 0.0), nPoints: 2);
        return ((pts[0] + pts[1]) / 2.0, (pts[0] - pts[1]).Magnitude / 2.0);
    }

    private static T Parse<T>(string? name, T fallback) where T : struct, Enum
        => !string.IsNullOrWhiteSpace(name) && Enum.TryParse<T>(name, ignoreCase: true, out var v)
               ? v
               : fallback;
}
