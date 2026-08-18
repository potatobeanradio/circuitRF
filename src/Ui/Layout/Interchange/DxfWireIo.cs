using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Layout.Interchange;

/// <summary>
/// Wirebond geometry in DXF — the bridge to the assembly house (wbond.md §9.4).
///
/// <para><b>Both halves live in one file on purpose.</b> The layer-name convention, the XDATA
/// application name, the 3D-polyline flags and the end-cap circles are a single agreement between a
/// writer and a reader; splitting them across two files is how a round trip quietly stops closing.
/// The round-trip test drives this file in both directions.</para>
///
/// <h3>The convention</h3>
/// <list type="bullet">
/// <item><b>One DXF layer per wire group</b>, named <c>Wires_&lt;group&gt;</c>. The prefix is what
/// identifies wire geometry on import; the suffix is the array name, so groups survive the round
/// trip by name rather than by position.</item>
/// <item><b>Each wire is one 3D polyline</b> (<c>POLYLINE</c> with the 3D flag, <c>VERTEX</c> per
/// point, <c>SEQEND</c>) — not an <c>LWPOLYLINE</c>, which is 2D by definition and would silently
/// drop the loop height, the one coordinate a bond wire is actually about.</item>
/// <item><b>Diameter and material ride as XDATA</b> under the application name
/// <c>CIRCUITRF_WBOND</c>. XDATA is the DXF mechanism for exactly this — per-entity data a foreign
/// application attaches without disturbing anything else — and a reader that does not know the
/// application name ignores it rather than choking.</item>
/// <item><b>A filled circle at each foot</b>, on the same layer, at the wire's own diameter — so the
/// bond footprints are visible in a viewer that draws no line weight, which is most of them.</item>
/// </list>
///
/// <para><b>GDSII is deliberately not offered for wires.</b> Assembly houses do not work in GDSII; the
/// format has no 3D polyline and no notion of a diameter, so a wire would have to be flattened to a
/// meaningless 2D trace. Effort spent there buys nothing.</para>
/// </summary>
public static class DxfWireIo
{
    /// <summary>The layer-name prefix that identifies wire geometry, both directions.</summary>
    public const string LayerPrefix = "Wires_";

    /// <summary>XDATA application name carrying the per-wire diameter and material.</summary>
    public const string XdataAppName = "CIRCUITRF_WBOND";

    /// <summary>Segments used to approximate the foot circle's HATCH boundary.</summary>
    private const int FootCircleSegments = 32;

    /// <summary>The DXF layer name for a wBond array.</summary>
    public static string LayerNameFor(string arrayName) => LayerPrefix + Sanitize(arrayName);

    /// <summary>The array name a wire layer refers to, or null when the layer is not a wire layer.</summary>
    public static string? ArrayNameFrom(string layerName) =>
        layerName.StartsWith(LayerPrefix, StringComparison.OrdinalIgnoreCase)
            ? layerName[LayerPrefix.Length..]
            : null;

    /// <summary>Every wire layer a design will write — needed by the LAYER table.</summary>
    public static IReadOnlyList<string> LayerNames(WBondDesign design) =>
        [.. design.Arrays.Where(a => a.Wires.Count > 0).Select(a => LayerNameFor(a.Name))];

    // ================================================================ export

    /// <summary>
    /// Writes every wire in <paramref name="design"/> into the currently-open ENTITIES section.
    /// </summary>
    /// <returns>The number of wires written.</returns>
    /// <param name="dbuToDrawingUnit">
    /// The layout's own DBU-to-drawing-unit factor, already derived from the file's <c>$INSUNITS</c>.
    /// </param>
    /// <param name="dbuPerMicron">
    /// The HOST LAYOUT's resolution. <b>This is load-bearing and easy to miss:</b> a wire point is
    /// stored in NANOMETRES while the rest of the writer works in the layout's own database units, and
    /// the two coincide only at the 1,000 DBU/µm default. Writing a wire coordinate as if it were
    /// already DBU produces a file that is exactly right on a default layout and silently wrong by a
    /// factor of the resolution on any other — the same bridge failure this codebase already shipped
    /// once in the renderer (see the WB-C3 note in src/Ui/CLAUDE.md).
    /// </param>
    public static int WriteWires(
        DxfGroupWriter w, WBondDesign design, double dbuToDrawingUnit, int dbuPerMicron,
        DxfHandles handles, string ownerHandle)
    {
        ArgumentNullException.ThrowIfNull(w);
        ArgumentNullException.ThrowIfNull(design);

        // One factor, computed once: nanometres straight to the drawing units the file is written in.
        // Every wire coordinate, diameter and foot circle below goes through this and nothing else.
        double nmToDrawing = NmToDrawingUnit(dbuToDrawingUnit, dbuPerMicron);

        int written = 0;

        foreach (var array in design.Arrays)
        {
            string layer = LayerNameFor(array.Name);

            foreach (var wire in array.Wires)
            {
                if (wire.Points.Count < 2) continue;

                WritePolyline3d(w, wire, layer, nmToDrawing, handles, ownerHandle);

                // The two feet, as filled circles at the wire's own diameter.
                WriteFilledCircle(w, wire.Points[0], wire.DiameterNm / 2.0, layer,
                                  nmToDrawing, handles, ownerHandle);
                WriteFilledCircle(w, wire.Points[^1], wire.DiameterNm / 2.0, layer,
                                  nmToDrawing, handles, ownerHandle);

                written++;
            }
        }

        return written;
    }

    /// <summary>
    /// One wire as a 3D polyline. The 70-flag bit 8 is what makes it 3D; without it a reader is
    /// entitled to treat the Z groups as decoration and flatten the loop.
    /// </summary>
    private static void WritePolyline3d(
        DxfGroupWriter w, Wire wire, string layer, double nmToDrawing,
        DxfHandles handles, string ownerHandle)
    {
        DxfWriter.WriteEntityHeader(w, "POLYLINE", handles, ownerHandle, layer, "AcDb3dPolyline");

        // A POLYLINE's own "location" group is a required placeholder; the real geometry is in the
        // VERTEX entities that follow.
        w.WriteInt(66, 1);            // vertices follow
        w.WriteDouble(10, 0.0);
        w.WriteDouble(20, 0.0);
        w.WriteDouble(30, 0.0);
        w.WriteInt(70, 8);            // 8 = 3D polyline (bit 1 unset: open, which a bond wire is)

        WriteWireXdata(w, wire, nmToDrawing);

        foreach (var p in wire.Points)
        {
            DxfWriter.WriteEntityHeader(w, "VERTEX", handles, ownerHandle, layer, "AcDbVertex");
            w.WriteString(100, "AcDb3dPolylineVertex");
            w.WriteDouble(10, p.X * nmToDrawing);
            w.WriteDouble(20, p.Y * nmToDrawing);
            w.WriteDouble(30, p.Z * nmToDrawing);
            w.WriteInt(70, 32);       // 32 = 3D polyline vertex
        }

        DxfWriter.WriteEntityHeader(w, "SEQEND", handles, ownerHandle, layer, "AcDbEntity");
    }

    /// <summary>Diameter and material, as XDATA a foreign reader is free to ignore.</summary>
    private static void WriteWireXdata(DxfGroupWriter w, Wire wire, double nmToDrawing)
    {
        w.WriteString(1001, XdataAppName);
        w.WriteString(1000, wire.Material);
        w.WriteDouble(1040, wire.DiameterNm * nmToDrawing);
    }

    /// <summary>
    /// A filled circle: a <c>CIRCLE</c> for the outline plus a solid <c>HATCH</c> whose boundary is a
    /// polygonal approximation of the same circle.
    ///
    /// <para><b>DXF has no filled-circle primitive</b>, and the alternatives are worse: a wide
    /// zero-length polyline renders differently in every viewer, and a <c>SOLID</c> is a
    /// quadrilateral. Outline-plus-hatch is what CAD tools themselves emit, so it draws correctly
    /// everywhere — and a reader that ignores hatches still sees the circle.</para>
    ///
    /// <para>The boundary is a POLYLINE-type hatch loop rather than a circular-arc edge because the
    /// polyline form is the one every reader implements; a 32-segment approximation of a 1-mil pad is
    /// visually exact and structurally simple.</para>
    /// </summary>
    private static void WriteFilledCircle(
        DxfGroupWriter w, Point3 centre, double radiusNm, string layer,
        double nmToDrawing, DxfHandles handles, string ownerHandle)
    {
        if (radiusNm <= 0) return;

        double cx = centre.X * nmToDrawing;
        double cy = centre.Y * nmToDrawing;
        double r = radiusNm * nmToDrawing;

        DxfWriter.WriteEntityHeader(w, "CIRCLE", handles, ownerHandle, layer, "AcDbCircle");
        w.WriteDouble(10, cx);
        w.WriteDouble(20, cy);
        w.WriteDouble(30, centre.Z * nmToDrawing);
        w.WriteDouble(40, r);

        DxfWriter.WriteEntityHeader(w, "HATCH", handles, ownerHandle, layer, "AcDbHatch");
        w.WriteDouble(10, 0.0);                 // elevation point
        w.WriteDouble(20, 0.0);
        w.WriteDouble(30, 0.0);
        w.WriteDouble(210, 0.0);                // extrusion direction
        w.WriteDouble(220, 0.0);
        w.WriteDouble(230, 1.0);
        w.WriteString(2, "SOLID");
        w.WriteInt(70, 1);                      // solid fill
        w.WriteInt(71, 0);                      // non-associative
        w.WriteInt(91, 1);                      // one boundary path

        // Boundary path: 1 = external, 2 = polyline. (Transposing these two bits is a real bug this
        // codebase has already made once, in the Gerber writer — see src/Ui/CLAUDE.md.)
        w.WriteInt(92, 1 | 2);
        w.WriteInt(72, 0);                      // no bulges
        w.WriteInt(73, 1);                      // closed
        w.WriteInt(93, FootCircleSegments);

        for (int i = 0; i < FootCircleSegments; i++)
        {
            double a = 2.0 * Math.PI * i / FootCircleSegments;
            w.WriteDouble(10, cx + r * Math.Cos(a));
            w.WriteDouble(20, cy + r * Math.Sin(a));
        }

        w.WriteInt(97, 0);                      // no source boundary objects
        w.WriteInt(75, 0);                      // hatch style: odd parity
        w.WriteInt(76, 1);                      // predefined pattern
        w.WriteDouble(47, Math.Max(r / 10.0, 1e-6));
        w.WriteInt(98, 0);                      // no seed points
    }

    /// <summary>
    /// Nanometres to drawing units — the ONE factor wire export uses, and the exact inverse of
    /// <see cref="ToNm"/> on the import side, so a round trip closes by construction.
    /// </summary>
    /// <para>Algebraically this reduces to <c>1 / nmPerDrawingUnit</c> — the layout's own resolution
    /// cancels out, exactly as it must, since neither a wire nor a DXF is expressed in database units.
    /// It is derived from <paramref name="dbuToDrawingUnit"/> anyway because that is the factor the
    /// writer already holds, and deriving it keeps one source rather than two that could disagree.</para>
    public static double NmToDrawingUnit(double dbuToDrawingUnit, int dbuPerMicron) =>
        dbuPerMicron <= 0
            ? dbuToDrawingUnit
            : dbuToDrawingUnit * dbuPerMicron / 1000.0;

    // ================================================================ import

    /// <summary>One 3D polyline recovered from a wire layer.</summary>
    public sealed record WirePolyline(
        string LayerName,
        IReadOnlyList<(double X, double Y, double Z)> Points,
        double? DiameterDrawingUnits,
        string? Material);

    /// <summary>
    /// Builds a <see cref="WBondDesign"/> from the wire polylines a reader recovered, grouping them
    /// into arrays by layer name.
    ///
    /// <para>An imported wire is an ordinary wire: its polyline IS its shape, exactly as for one
    /// drawn here. That used to need saying — wires arrived "free", bound to no loop profile — but
    /// with the profile object removed (2026-08-18) there is no other kind of wire to distinguish it
    /// from.</para>
    /// </summary>
    /// <param name="nmPerDrawingUnit">
    /// Nanometres in one drawing unit — a direct property of the file's own <c>$INSUNITS</c>
    /// (<see cref="DxfUnits.NanometersPerDrawingUnit"/>).
    ///
    /// <para><b>The layout's DBU resolution deliberately does not appear here.</b> A wire is stored in
    /// nanometres and a DXF is written in drawing units; routing that through the host layout's
    /// database units would be a longer road with one more scale for a caller to get wrong — and it
    /// did, in the first version of this method's own test.</para>
    /// </param>
    public static WBondDesign BuildDesign(
        IReadOnlyList<WirePolyline> polylines, double nmPerDrawingUnit)
    {
        ArgumentNullException.ThrowIfNull(polylines);

        var design = new WBondDesign();
        var byArray = new Dictionary<string, WireArray>(StringComparer.OrdinalIgnoreCase);

        foreach (var poly in polylines)
        {
            if (poly.Points.Count < 2) continue;

            string arrayName = ArrayNameFrom(poly.LayerName) ?? poly.LayerName;
            if (arrayName.Length == 0) arrayName = "Wires";

            if (!byArray.TryGetValue(arrayName, out var array))
            {
                array = new WireArray { Name = arrayName };
                byArray[arrayName] = array;
                design.Arrays.Add(array);
            }

            var points = new List<Point3>(poly.Points.Count);
            foreach (var (x, y, z) in poly.Points)
            {
                points.Add(new Point3(
                    ToNm(x, nmPerDrawingUnit),
                    ToNm(y, nmPerDrawingUnit),
                    ToNm(z, nmPerDrawingUnit)));
            }

            long diameterNm = poly.DiameterDrawingUnits is { } d and > 0
                ? ToNm(d, nmPerDrawingUnit)
                : WBondUnits.ToNm(1.0, WBondUnit.Mil);

            design.Arrays[design.Arrays.IndexOf(array)].Wires.Add(new Wire
            {
                Points = points,
                DiameterNm = Math.Max(1, diameterNm),
                Material = ResolveMaterial(poly.Material),
            });
        }

        return design;
    }

    /// <summary>A drawing-unit length to wire nanometres — the exact inverse of the export factor.</summary>
    private static long ToNm(double drawingUnits, double nmPerDrawingUnit) =>
        (long)Math.Round(drawingUnits * nmPerDrawingUnit);

    /// <summary>
    /// Maps an imported material name onto one wBond knows, falling back to the default.
    ///
    /// <para>Falling back rather than inventing a material keeps the conductivity table honest: an
    /// unknown name would otherwise become a material with no σ, and every R in the panel would be
    /// silently wrong.</para>
    /// </summary>
    private static string ResolveMaterial(string? name) =>
        name is { Length: > 0 } && WireMaterials.ByName(name) is { } m
            ? m.Name
            : WireMaterials.Default.Name;

    /// <summary>DXF layer names may not contain these; a group name that does is sanitised on the way out.</summary>
    private static string Sanitize(string name)
    {
        const string illegal = "<>/\\\":;?*|,='`";
        var sb = new System.Text.StringBuilder(name.Length);

        foreach (char c in name)
            sb.Append(illegal.Contains(c) || char.IsControl(c) ? '_' : c);

        string result = sb.ToString().Trim();
        return result.Length == 0 ? "Group" : result;
    }

    /// <summary>Formats a diameter for a diagnostic, in the design's own display unit.</summary>
    public static string DescribeDiameter(long nm, WBondUnit unit) =>
        WBondUnits.FromNm(nm, unit).ToString("0.###", CultureInfo.InvariantCulture) + " " + WBondUnits.Suffix(unit);
}
