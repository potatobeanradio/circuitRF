// ================================================================
//  SmithOverlayMigration.cs  —  a brief-8 overlay row, as a trace
//  config
//
//  brief-smith-12-overlays-via-the-inspector.md R-smith12-4c.
//
//  This is the whole of what is left of `SmithOverlayResolver`, and it
//  is left because the alternative is a user's own `.csmith` quietly
//  losing its reference data. Nothing shipped carries an overlay, so it
//  is cheap insurance rather than a feature — and it runs once: the
//  reader hands over the old rows, this turns them into trace configs,
//  and the next write is in the new shape with no old block in it.
// ================================================================

using System.Globalization;
using CircuitRF.Design.Smith;
using CircuitRF.Render.DataDisplay;
using RfCore;

namespace CircuitRF.Render.Smith;

public static class SmithOverlayMigration
{
    /// <summary>
    /// Migrates every <see cref="SmithDesign.LegacyOverlays"/> row into
    /// <see cref="SmithDesign.Overlays"/>, in order, and clears the old list.
    /// </summary>
    /// <remarks>
    /// <b>Called by whoever opened the document</b> — the window and the <c>smith</c> verb both —
    /// because the mapping needs <c>TraceConfig</c> and <c>src/Design</c> cannot see it. A caller
    /// that does not call this draws no overlays and writes none; calling it twice is a no-op.
    ///
    /// <para><paramref name="sources"/> is consulted for ONE thing: a brief-8 CUBE row spelled its
    /// quantity as <c>S11</c>, and turning that into the cube spec a trace card holds needs the
    /// run's own group prefix. Null, or a source that is not open, leaves the spec in the
    /// group-less form, which is right for a Touchstone-derived cube and is what the card would
    /// show for re-picking otherwise.</para>
    /// </remarks>
    public static void Apply(SmithDesign design, IPlotDataSources? sources = null)
    {
        ArgumentNullException.ThrowIfNull(design);
        if (design.LegacyOverlays.Count == 0) return;

        var migrated = design.LegacyOverlays.Select(o => SmithOverlays.Write(ToConfig(o, sources)))
                                            .ToList();
        design.LegacyOverlays.Clear();

        // AT THE FRONT, in their own order: brief 8 drew overlays last and a document that carried
        // both shapes at once cannot exist (the reader sorts one file's rows into two lists, and
        // only an old file has old rows), so this only ever runs on a list that is empty.
        for (int i = 0; i < migrated.Count; i++) design.Overlays.Insert(i, migrated[i]);
    }

    /// <summary>
    /// One brief-8 row as the trace config that draws the same curve.
    /// </summary>
    /// <remarks>
    /// <b>Two of the seven properties do not survive, and both losses are deliberate.</b>
    /// <c>Visible</c> goes with no replacement (<c>R-smith12-6</c>): <c>TraceProperties.Enabled</c>
    /// is read by nothing and a hidden trace would still sit in the trace list, the legend and the
    /// Add Marker menu — on a Data Display you delete the trace, and here too. A hidden row is
    /// therefore migrated as a VISIBLE trace rather than dropped, because losing the reference
    /// altogether is the worse of the two surprises. <c>ColorHex</c> goes because a `.cdd` stores a
    /// colour as an INDEX into the palette and never as an ARGB value — which is the property that
    /// made the palette change in RND-4 cost no saved file — and the row's own colour was not
    /// settable from the panel in the first place, so a hand-edited hex falls back to the palette.
    /// </remarks>
    public static TraceConfig ToConfig(SmithOverlayRef overlay, IPlotDataSources? sources = null)
    {
        ArgumentNullException.ThrowIfNull(overlay);

        TryParseDerived(overlay.Derived, out var derived);

        var cfg = new TraceConfig
        {
            Derived    = derived,
            YAxis      = DependentVarFormat.Complex,
            // R-smith12-7: renormalization to the chart's Z₀ is the requirement and not the option.
            // The trace's own Z0OverrideEnabled is the single gate on it, and `Fill` writes the
            // chart's number into Z0 on every rebuild — so the override is all this has to carry.
            Z0Override = overlay.Renormalize,
            // §5.7's reason, now a field on the config rather than a column on a panel.
            ExcludeFromAutoscale = !overlay.IncludeInAutoscale,
            Properties = new TracePropertiesConfig
            {
                LineEnabled = true,
                LineWidth   = 1.0,
                LineType    = overlay.Dashed ? LineType.Dashed : LineType.Solid,
            },
        };

        if (overlay.SourceKind == SmithOverlaySource.TouchstoneFile)
        {
            cfg.SourcePath = overlay.Source;
            if (derived == DerivedParameters.None
                && TryParseQuantity(overlay.Quantity, out var matrix, out int row, out int col))
            {
                cfg.MatrixType = matrix;
                cfg.Row        = row;
                cfg.Col        = col;
            }
            return cfg;
        }

        // ── a cube in an open data set ───────────────────────────────────────
        //
        //  The Data Display's own "alias::spec" spelling, so one string can name both. The row's own
        //  Quantity WINS when it has one — the combined form is what a paste or a hand-edited file
        //  carries.
        string sourceRef = overlay.Source;
        string spec      = overlay.Quantity.Trim();

        int sep = sourceRef.IndexOf("::", StringComparison.Ordinal);
        if (sep >= 0)
        {
            if (string.IsNullOrWhiteSpace(spec)) spec = sourceRef[(sep + 2)..].Trim();
            sourceRef = sourceRef[..sep].Trim();
        }
        if (string.IsNullOrWhiteSpace(spec)) spec = "S11";

        cfg.SourcePath = sourceRef;

        // A DERIVED quantity is not cube-bound, and that is the trace card's own rule: BuildPath
        // tests IsCubeBound BEFORE IsDerived, so a cube-bound trace with Derived set goes down the
        // cube path and the metric is never computed. A derived row resolves against the source's
        // NETWORK view instead — which for a simulated run is what NetworkFor assembles from its S
        // cube — so it stays network-bound here and carries no CubeName at all.
        if (derived != DerivedParameters.None) return cfg;

        cfg.CubeName   = CubeSpec(spec, sourceRef, sources);
        cfg.Expression = cfg.CubeName;
        return cfg;
    }

    /// <summary>
    /// <c>S11</c> as the bracket spec a trace card holds, or the string verbatim when it is already
    /// one.
    /// </summary>
    /// <remarks>
    /// <b>1-BASED PORT NUMBERS on both sides</b>, which is the whole of the sparam-port-indexing
    /// rule: <c>S[:, 2, 1]</c> IS S₂₁, and reading those digits as indices draws the wrong element
    /// in complete silence — invisible on a reciprocal part.
    /// </remarks>
    private static string CubeSpec(string spec, string sourceRef, IPlotDataSources? sources)
    {
        if (!LooksLikePortPair(spec)) return spec;
        if (!TryParseQuantity(spec, out var matrix, out int row, out int col)) return spec;

        string bare = matrix == MatrixType.S ? RfCore.Data.NetworkMetrics.SCubeName
                    : matrix == MatrixType.Z ? "Z" : "Y";

        string group = "";
        if (sources?.ResolveAbs(sourceRef) is { } abs && sources.DataFor(abs) is { } ds
            && RfCore.Data.NetworkMetrics.FindSCubeSpec(ds) is { } sCubeRef)
        {
            int dot = sCubeRef.LastIndexOf('.');
            if (dot > 0) group = sCubeRef[..(dot + 1)];
        }

        return $"{group}{bare}[:, {row + 1}, {col + 1}]";
    }

    /// <summary>Whether a quantity string is the <c>S11</c> / <c>Z21</c> shape rather than a cube
    /// slice a user typed. <c>S[:, 2, 1]</c> must reach the cube parser untouched.</summary>
    private static bool LooksLikePortPair(string? q)
    {
        if (q is not { Length: 3 }) return false;
        return "SZYszy".Contains(q[0]) && char.IsDigit(q[1]) && char.IsDigit(q[2]);
    }

    /// <summary>A <c>DerivedParameters</c> member name, case-insensitively; an empty string is
    /// <c>None</c>, so a row that carries no answer is not a refusal.</summary>
    public static bool TryParseDerived(string? name, out DerivedParameters derived)
    {
        if (string.IsNullOrWhiteSpace(name)) { derived = DerivedParameters.None; return true; }
        return Enum.TryParse(name.Trim(), ignoreCase: true, out derived);
    }

    /// <summary>
    /// <c>S11</c>, <c>S21</c>, <c>Z11</c>, <c>Y21</c> — a matrix letter and an ordered pair of
    /// <b>1-based PORT NUMBERS</b>. <c>Trace.Row</c>/<c>Col</c> are 0-based, so the conversion
    /// happens exactly here.
    /// </summary>
    public static bool TryParseQuantity(string? quantity, out MatrixType matrix,
                                        out int row, out int col)
    {
        matrix = MatrixType.S;
        row    = 0;
        col    = 0;

        if (string.IsNullOrWhiteSpace(quantity)) return true;

        string q = quantity.Trim().Replace(" ", "").Replace("(", "").Replace(")", "").Replace(",", "");
        if (!"SZYszy".Contains(q[0])) return false;

        matrix = char.ToUpperInvariant(q[0]) switch
        {
            'Z' => MatrixType.Z,
            'Y' => MatrixType.Y,
            _   => MatrixType.S,
        };

        string digits = q[1..];
        if (digits.Length != 2
            || !int.TryParse(digits[..1], NumberStyles.None, CultureInfo.InvariantCulture, out int i)
            || !int.TryParse(digits[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int j)
            || i < 1 || j < 1)
            return false;

        row = i - 1;
        col = j - 1;
        return true;
    }
}
