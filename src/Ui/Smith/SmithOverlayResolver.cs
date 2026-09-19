using System;
using System.Globalization;
using System.IO;
using System.Numerics;
using CircuitRF.Design.Smith;
using CircuitRF.Render.DataDisplay;
using RfCore;
using SkiaSharp;

namespace CircuitRF.Ui.Smith;

/// <summary>
/// Turns one <see cref="SmithOverlayRef"/> into an ordinary Data Display <c>Trace</c>, or into the
/// sentence saying why it could not (<c>brief-smith-8-overlays-markers.md</c> <c>R-smith8-1</c> to
/// <c>R-smith8-4</c>; <c>docs/design/smith-chart.md</c> §5.7).
/// </summary>
/// <remarks>
/// <b>There is no second trace resolver here and there must not be one.</b> A raw S/Z/Y element is a
/// <c>Trace</c> over an <c>SNP</c>, which <c>Trace.BuildPath</c> already draws; a
/// <c>DerivedParameters</c> mode is the same trace with <c>Derived</c> set, which
/// <c>BuildDerivedPath</c> already computes — <c>SourceStabilityCircle</c> and
/// <c>LoadStabilityCircle</c> included, centres, radii and stable-side flag. A cube overlay goes
/// through <see cref="TraceResolve.ResolveCubeTrace"/>, which is the trace card's own resolution and
/// <c>circuitrf render</c>'s. What this file adds is the three things a <c>.csmith</c> says that a
/// <c>.cdd</c> would have said in its own way: where the data is, what quantity to take, and that it
/// is renormalized to the chart's Z₀.
///
/// <para><b>Renormalization is the requirement and not the option</b> (<c>R-smith8-3</c>). A trace
/// that is not renormalized is a curve in the wrong place that looks entirely plausible — a 75 Ω
/// part on a 50 Ω chart lands somewhere perfectly believable and is simply wrong. The mechanism is
/// the trace's OWN <c>Z0OverrideEnabled</c>/<c>Z0</c> pair, which is the single gate on all
/// reference-impedance renormalization of displayed data and covers the SNP path and the cube path
/// alike. Setting it is the whole of the work; computing a renormalization here would be a second
/// copy of <c>RFNetwork.SToS</c>.</para>
///
/// <para><b>A reference that does not resolve marks its row and stops there</b> (<c>R-smith8-2</c>):
/// the document still opens, the rest of the chart still draws, and the sentence names the path. An
/// overlay is reference material, and reference material that is missing must not take the work down
/// with it — which is the opposite of an S1P ELEMENT, whose absence is a refusal because the cascade
/// cannot be evaluated without it.</para>
/// </remarks>
internal static class SmithOverlayResolver
{
    /// <summary>What one row resolved to: a trace, or the sentence for its tooltip. Never both.</summary>
    internal readonly record struct Resolution(Trace? Trace, string? Unresolved)
    {
        internal static Resolution Ok(Trace t)      => new(t, null);
        internal static Resolution No(string why)   => new(null, why);
    }

    /// <summary>
    /// Resolves <paramref name="overlay"/> against the document's folder and, for a cube reference,
    /// against whatever data sources the host has open.
    /// </summary>
    /// <param name="documentDirectory">What a relative Touchstone path resolves against — the
    /// `.cdd` convention, and the one that survives an archived or moved workspace. The pair moves
    /// together and the reference still resolves, which is what <c>R-smith8-2</c> asks for.</param>
    /// <param name="z0Chart">The chart's single reference impedance. Everything is renormalized to
    /// it on the way in unless the row says otherwise.</param>
    /// <param name="sources">The open data sources, for <see cref="SmithOverlaySource.Cube"/>. Null
    /// is an honest "nothing is open", which marks a cube row unresolved rather than throwing.</param>
    internal static Resolution Resolve(
        SmithOverlayRef overlay, string? documentDirectory, double z0Chart,
        IPlotDataSources? sources, int colorIndex)
    {
        ArgumentNullException.ThrowIfNull(overlay);

        if (string.IsNullOrWhiteSpace(overlay.Source))
            return Resolution.No("This overlay names no source — an overlay IS a reference to data "
                               + "somewhere, so there is nothing for it to draw until it has one.");

        if (!TryParseDerived(overlay.Derived, out var derived))
            return Resolution.No($"'{overlay.Derived}' is not a derived quantity circuitRF computes. "
                               + $"The ones it does are: {string.Join(", ", Enum.GetNames<DerivedParameters>())}.");

        return overlay.SourceKind == SmithOverlaySource.TouchstoneFile
            ? ResolveFile(overlay, documentDirectory, z0Chart, derived, colorIndex)
            : ResolveCube(overlay, z0Chart, derived, sources, colorIndex);
    }

    // ── a Touchstone file, by a path relative to the document ────────────────

    private static Resolution ResolveFile(
        SmithOverlayRef overlay, string? documentDirectory, double z0Chart,
        DerivedParameters derived, int colorIndex)
    {
        string raw  = overlay.Source;
        string full = Path.IsPathRooted(raw)
            ? Path.GetFullPath(raw)
            : Path.GetFullPath(Path.Combine(documentDirectory ?? Directory.GetCurrentDirectory(), raw));

        if (!File.Exists(full))
            return Resolution.No($"'{raw}' does not resolve to a file — looked for it at '{full}'. "
                               + "The path is relative to the document, so a document and its "
                               + "overlays move together.");

        SNP snp;
        try { snp = TouchstoneCache.Get(full); }
        catch (Exception ex)
        {
            return Resolution.No($"'{raw}' could not be read as Touchstone: {ex.Message}");
        }

        if (!TryParseQuantity(overlay.Quantity, snp.Ports, derived, out var matrix, out int row, out int col,
                              out string? refusal))
            return Resolution.No(refusal!);

        var trace = new Trace(snp, matrix, row, col, DependentVarFormat.Complex,
                              secondaryAxis: false, Style(overlay, colorIndex))
        {
            Derived    = derived,
            SourcePath = full,
            SourceRef  = raw,
        };

        // R-smith8-3. The trace's own gate, which is what the Data Display's Z0 "Override" checkbox
        // sets and what BOTH the SNP path and the cube path read. Off is a deliberate "show me the
        // file's own numbers" and is the row's to choose, not this code's to decide.
        trace.Z0OverrideEnabled = overlay.Renormalize;
        trace.Z0                = new Complex(z0Chart, 0.0);

        Finish(trace, overlay);
        return Resolution.Ok(trace);
    }

    // ── a cube in an open DataSet ────────────────────────────────────────────

    /// <remarks>
    /// <b>Referenced the way a Data Display trace card references one</b> (<c>R-smith8-2</c>): a
    /// logical source id plus a cube spec, resolved by <see cref="TraceResolve.ResolveCubeTrace"/>
    /// over an <see cref="IPlotDataSources"/> — the same seam <c>circuitrf render</c> implements over
    /// the files a caller named. The spec is parsed by <c>CubeTraceSpecParser</c> inside that call;
    /// nothing here parses one.
    /// </remarks>
    private static Resolution ResolveCube(
        SmithOverlayRef overlay, double z0Chart, DerivedParameters derived,
        IPlotDataSources? sources, int colorIndex)
    {
        if (sources is null)
            return Resolution.No($"'{overlay.Source}' is a cube in an open data set, and there is no "
                               + "data set open. Open the run this overlay came from and it resolves.");

        // The Source carries the data set; the Quantity carries the cube spec within it.
        string sourceRef = overlay.Source;
        string spec      = overlay.Quantity.Trim();

        int sep = sourceRef.IndexOf("::", StringComparison.Ordinal);
        if (sep >= 0)
        {
            // The Data Display's own "alias::spec" spelling, so one string can name both. The row's
            // own Quantity WINS when it has one — the combined form is what a paste or a hand-edited
            // file carries, and silently overriding a cell the user can see would be the worse of
            // the two surprises.
            if (string.IsNullOrWhiteSpace(spec)) spec = sourceRef[(sep + 2)..].Trim();
            sourceRef = sourceRef[..sep].Trim();
        }

        // Empty is S11, exactly as it is for a file — a one-port has nothing else and a two-port
        // overlay on a matching chart is nearly always wanted for its input reflection.
        if (string.IsNullOrWhiteSpace(spec)) spec = "S11";

        if (sources.ResolveAbs(sourceRef) is not { } abs || !sources.Contains(abs))
            return Resolution.No($"'{sourceRef}' is not one of the data sets that are open.");

        // ── a DERIVED quantity is not cube-bound, and that is the trace card's own rule ─────
        //
        //  BuildPath tests IsCubeBound BEFORE IsDerived, so a cube-bound trace with Derived set
        //  goes down the cube path and the metric is never computed. The Data Display resolves a
        //  derived trace against the source's NETWORK view for exactly that reason — including for
        //  a simulated run, whose S cube NetworkFor assembles into an SNP — and a `.cdd` that read
        //  its Snp alone once dropped every stability circle as the display opened.
        if (derived != DerivedParameters.None)
        {
            if (sources.NetworkFor(abs) is not { } network || network.IsEmpty)
                return Resolution.No($"'{sourceRef}' has no S-parameters to take "
                                   + $"{derived} from — a derived quantity is a function of the "
                                   + "network matrix, and this source has none.");

            var derivedTrace = new Trace(network, MatrixType.S, 0, 0, DependentVarFormat.Complex,
                                         secondaryAxis: false, Style(overlay, colorIndex))
            {
                Derived    = derived,
                SourcePath = abs,
                SourceRef  = sourceRef,
            };

            derivedTrace.Z0OverrideEnabled = overlay.Renormalize;
            derivedTrace.Z0                = new Complex(z0Chart, 0.0);

            Finish(derivedTrace, overlay);
            return Resolution.Ok(derivedTrace);
        }

        // ── ONE QUANTITY VOCABULARY ACROSS BOTH SOURCE KINDS ────────────────────────────────
        //
        //  `S11` means the same thing whether the data came out of a file or out of a run, so it is
        //  translated here into the bracket spec the cube path takes. 1-BASED PORT NUMBERS on both
        //  sides, which is the whole of the sparam-port-indexing rule: `S[:, 2, 1]` IS S21, and
        //  reading those digits as indices draws the wrong element in complete silence — invisible
        //  on a reciprocal part. Anything that is not that shape is passed through verbatim, so a
        //  hand-written slice of any other cube still works.
        if (LooksLikePortPair(spec)
            && sources.DataFor(abs) is { } ds
            && TryParseQuantity(spec, ports: int.MaxValue, DerivedParameters.None,
                                out var matrix, out int row, out int col, out _))
        {
            string bare      = matrix == MatrixType.S ? RfCore.Data.NetworkMetrics.SCubeName
                             : matrix == MatrixType.Z ? "Z" : "Y";
            string? sCubeRef = RfCore.Data.NetworkMetrics.FindSCubeSpec(ds);
            int     dot      = sCubeRef?.LastIndexOf('.') ?? -1;
            string  group    = dot > 0 ? sCubeRef![..(dot + 1)] : "";

            spec = $"{group}{bare}[:, {row + 1}, {col + 1}]";
        }

        var trace = new Trace(new SNP([1e9], 1), MatrixType.S, 0, 0, DependentVarFormat.Complex,
                              secondaryAxis: false, Style(overlay, colorIndex))
        {
            SourcePath = abs,
            SourceRef  = sourceRef,
            CubeName   = spec,
            Expression = spec,
            Transform  = CubeTransform.None,
        };

        trace.Z0OverrideEnabled = overlay.Renormalize;
        trace.Z0                = new Complex(z0Chart, 0.0);

        TraceResolve.ResolveCubeTrace(trace, sources, PlotType.Smith, FreqUnit.GHz);

        if (trace.ExpressionError is { Length: > 0 } err)
            return Resolution.No($"'{spec}' in '{sourceRef}' could not be resolved: {err}");

        Finish(trace, overlay);
        return Resolution.Ok(trace);
    }

    // ── shared ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The one thing every overlay carries whatever its source is: it is reference material, so it
    /// stays out of the chart's autoscale unless the row says otherwise (<c>R-smith8-4</c>).
    /// </summary>
    /// <remarks>
    /// <b>A stability circle can be enormous</b> — an unconditionally stable device's load circle
    /// routinely sits far outside the unit disc — and one unlucky overlay reframing the chart would
    /// squash the cascade the user is actually working on into a corner of it. That is why the
    /// default is OUT and the opt-in is per row.
    /// </remarks>
    private static void Finish(Trace trace, SmithOverlayRef overlay)
    {
        trace.ExcludeFromAutoscale = !overlay.IncludeInAutoscale;

        // Adding a Trace to a Plot does NOT build its path — the collection handler only re-fits the
        // axes — so an SNP-backed overlay put on the plot without this draws nothing at all, with no
        // error anywhere. The cube path is already built by ResolveCubeTrace; calling it twice is a
        // rebuild, not a fault.
        trace.BuildPath(PlotType.Smith, FreqUnit.GHz);
    }

    private static TraceProperties Style(SmithOverlayRef overlay, int colorIndex)
    {
        var props = new TraceProperties
        {
            LineColorIndex   = colorIndex,
            MarkerColorIndex = colorIndex,
            FillColorIndex   = colorIndex,
            LineEnabled      = true,
            LineType         = overlay.Dashed ? LineType.Dashed : LineType.Solid,
            LineWidth        = 1.0,
        };

        if (TryParseHex(overlay.ColorHex) is { } custom)
        {
            props.LineColorStorage   = custom;
            props.MarkerColorStorage = custom;
        }

        // LAST: every setter above raises Custom. An overlay whose row states a colour IS a custom
        // style and keeps the flag; one that does not reads as palette-default, exactly as the
        // trajectories do.
        props.Custom = overlay.ColorHex is { Length: > 0 };
        return props;
    }

    /// <summary><c>#rrggbb</c> or <c>#rrggbbaa</c>, or null. A colour that cannot be read is not a
    /// refusal — the row simply takes the palette's next colour, because a missing colour is a
    /// cosmetic answer and not a missing curve.</summary>
    private static SKColor? TryParseHex(string? hex)
    {
        if (hex is not { Length: > 0 }) return null;
        string s = hex.TrimStart('#');
        if (s.Length is not (6 or 8)) return null;
        if (!uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint v)) return null;

        return s.Length == 6
            ? new SKColor((byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : new SKColor((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v);
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
    internal static bool TryParseDerived(string? name, out DerivedParameters derived)
    {
        if (string.IsNullOrWhiteSpace(name)) { derived = DerivedParameters.None; return true; }
        return Enum.TryParse(name.Trim(), ignoreCase: true, out derived);
    }

    /// <summary>
    /// <c>S11</c>, <c>S21</c>, <c>Z11</c>, <c>Y21</c> — a matrix letter and an ordered pair of
    /// <b>1-based PORT NUMBERS</b>.
    /// </summary>
    /// <remarks>
    /// <b>1-based, and that is the trap the <c>plot</c> verb already paid for</b>
    /// (<c>src/Cli/RESOLVED.md</c>): reading the digits of <c>S21</c> as indices draws S₁₀ — or, on
    /// the verb's own axes, S₁₂ for <c>i=2,j=1</c> — in complete silence, which is invisible on a
    /// reciprocal part and wrong on everything else. <c>Trace.Row</c>/<c>Col</c> are 0-based, so the
    /// conversion happens exactly here and nowhere else.
    ///
    /// <para>An empty quantity is <c>S11</c>, which is what a one-port file has and what a two-port
    /// overlay on a matching chart is nearly always wanted for. A DERIVED row ignores it entirely:
    /// the port pair a stability circle needs is <c>InputPort</c>/<c>OutputPort</c>, which default
    /// to 1 and 2.</para>
    /// </remarks>
    internal static bool TryParseQuantity(
        string? quantity, int ports, DerivedParameters derived,
        out MatrixType matrix, out int row, out int col, out string? refusal)
    {
        matrix  = MatrixType.S;
        row     = 0;
        col     = 0;
        refusal = null;

        if (derived != DerivedParameters.None) return true;
        if (string.IsNullOrWhiteSpace(quantity)) return true;

        string q = quantity.Trim().Replace(" ", "").Replace("(", "").Replace(")", "").Replace(",", "");

        matrix = char.ToUpperInvariant(q[0]) switch
        {
            'S' => MatrixType.S,
            'Z' => MatrixType.Z,
            'Y' => MatrixType.Y,
            _   => MatrixType.S,
        };

        if (!"SZYszy".Contains(q[0]))
        {
            refusal = $"'{quantity}' is not a network quantity. Spell one as a matrix letter and a "
                    + "pair of port numbers — S11, S21, Z11, Y21 — or pick a derived quantity "
                    + "instead.";
            return false;
        }

        string digits = q[1..];
        if (digits.Length != 2 || !char.IsDigit(digits[0]) || !char.IsDigit(digits[1]))
        {
            refusal = $"'{quantity}' does not name a pair of ports. The spelling is a matrix letter "
                    + "and two 1-based port numbers, as in S11 or S21.";
            return false;
        }

        int i = digits[0] - '0';
        int j = digits[1] - '0';

        if (i < 1 || i > ports || j < 1 || j > ports)
        {
            refusal = $"'{quantity}' asks for port {i} and port {j} of a {ports}-port file.";
            return false;
        }

        row = i - 1;
        col = j - 1;
        return true;
    }

    /// <summary>What the trace is called in the legend and what a marker on it is stored against —
    /// the file's own name (or the cube's) and the quantity, which is the pair a reader needs to say
    /// which curve is which when two overlays come from the same part.</summary>
    internal static string Label(SmithOverlayRef overlay)
    {
        string head = overlay.SourceKind == SmithOverlaySource.TouchstoneFile
            ? Path.GetFileName(overlay.Source.Replace('\\', '/'))
            : overlay.Source;

        if (string.IsNullOrWhiteSpace(head)) head = "overlay";

        string tail = TryParseDerived(overlay.Derived, out var d) && d != DerivedParameters.None
            ? d.ToString()
            : (string.IsNullOrWhiteSpace(overlay.Quantity) ? "S11" : overlay.Quantity.Trim());

        return $"{head} {tail}";
    }
}
