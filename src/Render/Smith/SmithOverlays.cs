// ================================================================
//  SmithOverlays.cs  —  a `.csmith`'s reference material, as the
//  Data Display's own trace configs
//
//  brief-smith-12-overlays-via-the-inspector.md (R-smith12-4,
//  R-smith12-5, R-smith12-5c). docs/design/smith-chart.md §5.7, §7.
//
//  WHAT THIS FILE IS NOT: a trace resolver, a trace writer, or a second
//  overlay format. `PlotConfigLoader.LoadTrace` builds the Trace and
//  `DataDisplayViewModel.BuildTraceConfig` writes the config; this is the
//  three things in between — the JSON a `.csmith` stores them as, the
//  name a marker on one is stored against, and the migration of the
//  brief-8 rows that came before them.
// ================================================================

using System.Text.Json;
using CircuitRF.Design.Smith;
using CircuitRF.Render.DataDisplay;

namespace CircuitRF.Render.Smith;

public static class SmithOverlays
{
    /// <summary>What a Smith Chart overlay is drawn as — the chart's own plot type and unit.</summary>
    public const PlotType ChartPlotType = PlotType.Smith;

    /// <summary>The frequency unit the chart's traces are built in.</summary>
    public const FreqUnit ChartFreqUnit = FreqUnit.GHz;

    // ── the document's own JSON ──────────────────────────────────────────────

    /// <summary>
    /// One stored overlay as a <see cref="TraceConfig"/>, or null when the JSON is not one.
    /// </summary>
    /// <remarks>
    /// <b>The `.cdd`'s own options</b> (<see cref="DataDisplayJson.Options"/>), which is the whole
    /// of <c>R-smith12-4b</c>'s "one spelling": every <c>MatrixType</c>, <c>DerivedParameters</c>,
    /// <c>LineType</c> and <c>CubeTransform</c> in the block is written as a NAME, and a reader
    /// without the converter binds none of them and falls back to their defaults — a document that
    /// opens, draws, and is wrong.
    /// </remarks>
    public static TraceConfig? Read(JsonElement stored)
    {
        if (stored.ValueKind != JsonValueKind.Object) return null;
        try { return stored.Deserialize<TraceConfig>(DataDisplayJson.Options); }
        catch (JsonException) { return null; }
    }

    /// <summary>The inverse — a config as the JSON the `.csmith` stores.</summary>
    public static JsonElement Write(TraceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return JsonSerializer.SerializeToElement(config, DataDisplayJson.Options);
    }

    // ── the trace ────────────────────────────────────────────────────────────

    /// <summary>
    /// One stored overlay as a live <c>Trace</c>, resolved against <paramref name="sources"/>.
    /// </summary>
    /// <remarks>
    /// <b>The `.cdd`'s own loader, at the chart's plot type</b> (<c>R-smith12-5</c>). Nothing here
    /// reads a Touchstone file, parses a quantity or computes a renormalization: a trace config says
    /// all three and <see cref="PlotConfigLoader.LoadTrace"/> has honoured them since the Data
    /// Display's first release.
    /// </remarks>
    public static Trace? Load(TraceConfig config, IPlotDataSources sources)
        => PlotConfigLoader.LoadTrace(config, ChartPlotType, ChartFreqUnit, sources);

    /// <summary>
    /// A stable, whitespace-independent fingerprint of a whole overlay list.
    /// </summary>
    /// <remarks>
    /// <b>It answers exactly one question: is the document's overlay list still the one the live
    /// traces were built from?</b> The traces are carried across a rebuild rather than re-resolved
    /// (<c>R-smith12-5a</c>), and an ordinary committed edit — a component value, a generator row —
    /// replaces the whole design through a snapshot, so without this every keystroke elsewhere in
    /// the window would throw the user's own traces away and build new ones.
    ///
    /// <para><b>Compact, not raw.</b> A `.csmith` writes indented and nests the block two levels in,
    /// so the same content comes back with different whitespace; comparing <c>GetRawText</c> would
    /// report a change on every save and defeat the whole thing.</para>
    /// </remarks>
    public static string Signature(IEnumerable<JsonElement> overlays)
    {
        ArgumentNullException.ThrowIfNull(overlays);
        return string.Join("\u0000", overlays.Select(o => JsonSerializer.Serialize(o, Compact)));
    }

    private static readonly JsonSerializerOptions Compact = new() { WriteIndented = false };

    // ── the marker key (R-smith12-5c) ────────────────────────────────────────

    /// <summary>
    /// What an overlay's curve is CALLED — its legend name, and the string a marker taken on it is
    /// stored against.
    /// </summary>
    /// <remarks>
    /// <b>Derived from the config alone, which is what makes it stable</b> (<c>R-smith12-5c</c>).
    /// Every trace on this chart is rebuilt on every edit and the markers on them go with it, so a
    /// marker is stored against a NAME; a name derived from the overlay's position in the list would
    /// move on a reorder, and a marker taken on one overlay would land on another — the visible
    /// failure brief 8 chose deliberately, and not what anyone wants here.
    ///
    /// <para>The pair is the source and the quantity, which is what a reader needs to say which
    /// curve is which when two overlays come from the same part.</para>
    /// </remarks>
    public static string Key(TraceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        string head = config.SourcePath is { Length: > 0 } src
            ? Path.GetFileName(src.Replace('\\', '/'))
            : "overlay";
        if (string.IsNullOrWhiteSpace(head)) head = "overlay";

        string tail =
            config.Derived != DerivedParameters.None ? config.Derived.ToString()
          : config.Expression is { Length: > 0 } expr ? expr
          : config.CubeName is { Length: > 0 } cube   ? cube
          : $"{config.MatrixType}{config.Row + 1}{config.Col + 1}";

        return $"{head} {tail}";
    }
}
