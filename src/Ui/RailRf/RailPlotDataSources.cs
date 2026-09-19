// Where the |Z| plot's traces resolve their data from
// (owner, 2026-09-19 — "user cannot change the plot trace's units").
//
// ── WHY THIS EXISTS AT ALL ────────────────────────────────────────────────────────────────────
//
// A trace is re-resolved from its SOURCE on every edit the Plot Inspector makes, and until RND-4's
// seam was used here the source was always the data-source LIBRARY — files on disk. railRF has no
// library: it publishes a fresh DataSet on every solve, the way harmonicaRF does, because it is a
// read-out of a design being edited rather than a document over files.
//
// So the first touch of anything in the inspector emptied every curve — 425 points to 0, silently,
// with no error on the card and no message anywhere. Measured, not guessed.
//
// IPlotDataSources is the existing answer: `circuitrf render` already implements it over the files a
// caller named (src/Cli/CddSources.cs). This is the same thing over the sweep this window is
// currently showing, and it is the whole of what railRF needs to make the shared inspector work.
//
// ── THE "PATH" IS A MODEL KIND ────────────────────────────────────────────────────────────────
//
// A source is keyed by a string that is a file path everywhere else, so these are spelled like one
// and never touch the disk. There are exactly two, because §2.9 keeps two readings of one board and
// the plot can show both at once — and a trace has to say WHICH, or an Accuracy run would re-point
// the fast curve at the accurate numbers and the two would draw on top of each other.

using System;
using System.Collections.Generic;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Render.DataDisplay;
using RfCore;
using RfCore.Data;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// The sweep results this window is holding, seen as the questions a trace resolution asks.
/// </summary>
internal sealed class RailPlotDataSources(IReadOnlyDictionary<PdnModelKind, PdnSweepResult> byModel)
    : IPlotDataSources
{
    /// <summary>The source key for one reading. Spelled like a path; never opened.</summary>
    internal static string PathFor(PdnModelKind kind) =>
        kind == PdnModelKind.Fast ? "railrf://fast" : "railrf://accurate";

    private static PdnModelKind? KindOf(string? absPath) =>
        string.Equals(absPath, PathFor(PdnModelKind.Fast), StringComparison.OrdinalIgnoreCase)
            ? PdnModelKind.Fast
            : string.Equals(absPath, PathFor(PdnModelKind.Accurate), StringComparison.OrdinalIgnoreCase)
                ? PdnModelKind.Accurate
                : null;

    public string? ResolveAbs(string? sourceRef) => sourceRef;

    public bool Contains(string absPath) =>
        KindOf(absPath) is { } kind && byModel.ContainsKey(kind);

    /// <summary>
    /// None, and that is not a gap.
    /// </summary>
    /// <remarks>
    /// A PDN sweep produces an impedance CUBE over one observation-port pair set, not an S-parameter
    /// network — the same shape as any other cube-only run, which the resolution already handles
    /// ("a simulated cube-only run has none by design").
    /// </remarks>
    public SNP? NetworkFor(string absPath) => null;

    public DataSet? DataFor(string absPath) =>
        KindOf(absPath) is { } kind && byModel.TryGetValue(kind, out var sweep) ? sweep.Data : null;

    /// <summary>What the trace card writes beside a spec — the reading, which is the only thing that
    /// distinguishes the two sources here.</summary>
    public string? AliasFor(string absPath) =>
        KindOf(absPath) is { } kind ? (kind == PdnModelKind.Fast ? "Fast" : "Accuracy") : null;

    public string? DisplayNameFor(string absPath) => AliasFor(absPath);

    /// <summary>True once both readings are in hand, which is when a curve needs saying which it
    /// is — the same condition the status strip's "both models in hand" reads.</summary>
    public bool HasMultipleSources => byModel.Count > 1;

    /// <summary>
    /// Nothing. There is no "selected source" here — a selection is what an <c>Add trace</c> seeds
    /// from, and this plot's trace set is fixed (<c>Plot.IsFixedReadout</c>).
    /// </summary>
    public DataSet? SelectedData => null;
}
