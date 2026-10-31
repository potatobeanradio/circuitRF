// The Model Composer document, a .cmc (brief-modelcomposer-3 R-mc3-1, R-mc3-2; overview D1, D2, D6, D11).
//
// A .cmc records WHAT DATA a model is built from and UNDER WHAT CONDITIONS. It never holds the data:
// sources are paths relative to the .cmc, and their parsed form lives in <name>.cmc-cache/, which is
// derived and may be deleted (CmcCache). The user's own decisions about the data — which points are
// excluded and why — are document state, because the cache must hold nothing a re-import cannot rebuild.
//
// NUMBERS ARE BASE SI: volts, amps, seconds, ohms, hertz. The one exception is temperature, in °C,
// because that is what a chuck is set to and what the elaborator's `temp` is.
//
// Pads, Network and Steps are carried as JSON and written back exactly, so a document saved by one build loses
// nothing a later one wrote. Pads and Network are read and validated through CmcExtrinsic (MC-4).

using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.Composed;

namespace CircuitRF.Design.Modeling;

/// <summary>A three-terminal FET model, or an N-port passive one (D2).</summary>
public enum CmcKind { Active, Passive }

/// <summary>What a data set holds (R-mc3-2).</summary>
public enum DataSetKind
{
    DcIv,
    PulsedIv,
    /// <summary>CW multi-bias S-parameters.</summary>
    SParams,
    PulsedSParams,
    /// <summary>I and Y (or S, or C) sampled on a bias grid, as another simulator writes them.</summary>
    SimulatedTables,
    /// <summary>Power sweeps, loadpull, two-tone — validation only.</summary>
    LargeSignal,
    /// <summary>One passive block, or a family over a geometry value.</summary>
    PassiveSParams,
}

/// <summary>Where a data set's bias voltages were measured (D6). Spelled lower-case in the file.</summary>
public enum ReferencePlane { Instrument, Probe, Device, Intrinsic }

/// <summary>How pulsed points map onto trap state (MC-10). Spelled lower-case in the file.</summary>
public enum TrapStateMode { Auto, Setting, Extreme }

/// <summary>The bias-path resistance per terminal of an <c>instrument</c>-plane data set, in ohms.</summary>
public sealed record BiasPathOhms(double Gate, double Drain, double Source = 0);

/// <summary>The quiescent bias a pulse starts from and returns to. Currents are optional.</summary>
public sealed record QuiescentBias(double Vgs, double Vds, double? Ig = null, double? Id = null);

/// <summary>
/// The pulse of a double-pulse measurement that sets the trap state before each measuring pulse
/// ([N22], [G19]). <see cref="Gap"/> is from its end to the measuring pulse; times in seconds.
/// </summary>
public sealed record PrePulse(double Vgs, double Vds, double Width, double Gap, double? Id = null);

/// <summary>What a <see cref="DataSetKind.LargeSignal"/> set measured (MC-11).</summary>
public enum LargeSignalMeasurement
{
    /// <summary>Output against input power at one source, load, bias and frequency; a CSV of pin, pout and what else.</summary>
    PowerSweep,
    /// <summary>Two equal tones about <see cref="LargeSignalConditions.FrequencyHz"/>; a CSV of pin, pout, im3 (and im5).</summary>
    TwoTone,
    /// <summary>A loadpull file (<c>.spl</c> or <c>.lpcwave</c>), read through RfCore's readers.</summary>
    Loadpull,
}

/// <summary>
/// The conditions a large-signal set was measured under (MC-11 R-mc11-4) — what its generated testbench reproduces.
/// Voltages are at the set's reference plane, impedances in ohms at the measurement plane; a harmonic not listed is
/// shorted. A loadpull file states its own loads, drive and (usually) its frequency.
/// </summary>
public sealed record LargeSignalConditions
{
    public required LargeSignalMeasurement Measurement { get; init; }
    /// <summary>The fundamental, Hz; for two tones, their centre.</summary>
    public double? FrequencyHz { get; init; }
    /// <summary>Two tones: their spacing, Hz.</summary>
    public double? SpacingHz { get; init; }
    public double? Vgs { get; init; }
    public double? Vds { get; init; }
    public System.Numerics.Complex? SourceZ { get; init; }
    public System.Numerics.Complex? LoadZ { get; init; }
    /// <summary>The source termination at 2f0, 3f0, …; a harmonic beyond the list is shorted.</summary>
    public IReadOnlyList<System.Numerics.Complex> SourceHarmonics { get; init; } = [];
    /// <summary>The load termination at 2f0, 3f0, …; a harmonic beyond the list is shorted.</summary>
    public IReadOnlyList<System.Numerics.Complex> LoadHarmonics { get; init; } = [];
}

/// <summary>An instrument's current limits, in amps — what the compliance flag reads (R-mc3-5).</summary>
public sealed record ComplianceLimits(double? Gate = null, double? Drain = null);

/// <summary>
/// The conditions a data set was measured under (R-mc3-2). Every one is optional here: a condition
/// a later step needs is reported by that step, not by the reader.
/// </summary>
public sealed record DataConditions
{
    public double? AmbientC { get; init; }
    public ReferencePlane? ReferencePlane { get; init; }
    public BiasPathOhms? BiasPathOhms { get; init; }
    public double? PulseWidth { get; init; }
    public double? Period { get; init; }
    public QuiescentBias? Quiescent { get; init; }
    /// <summary>From the pulse's start to the current or S sample, seconds.</summary>
    public double? SampleDelay { get; init; }
    public TrapStateMode? TrapState { get; init; }
    public PrePulse? PrePulse { get; init; }
    public double? SoakTime { get; init; }
    public ComplianceLimits? Compliance { get; init; }
    /// <summary>A <see cref="DataSetKind.LargeSignal"/> set's measurement (MC-11); required there and nowhere else.</summary>
    public LargeSignalConditions? LargeSignal { get; init; }
    public string? Note { get; init; }

    public static DataConditions None { get; } = new();
}

/// <summary>One CSV/TSV column: the column (its header, or its 1-based number without a header), the
/// quantity it holds, and the unit its numbers are in.</summary>
public sealed record ColumnMap(string Column, string Quantity, string? Unit = null);

/// <summary>
/// How a data set's sources were read — stored so a re-import is exact (R-mc3-3). Which fields apply
/// depends on <see cref="Format"/>.
/// </summary>
public sealed record ImportSettings
{
    public const string Touchstone = "touchstone", Csv = "csv", Npy = "npy", Mat = "mat";
    /// <summary>A NumPy <c>.npz</c> archive of named arrays (<c>numpy.savez</c>).</summary>
    public const string Npz = "npz";

    /// <summary>True for the formats whose sources are named arrays mapped by <see cref="Arrays"/>.</summary>
    public static bool IsArrayFormat(string format) => format is Npy or Mat or Npz;
    /// <summary>A loadpull file, <c>.spl</c> or <c>.lpcwave</c> (a <see cref="LargeSignalMeasurement.Loadpull"/> set, MC-11).</summary>
    public const string Loadpull = "loadpull";

    public required string Format { get; init; }

    /// <summary>Touchstone: the bias pattern, with fields <c>{vgs}</c>, <c>{vds}</c>, <c>{T}</c>,
    /// <c>{id}</c>, <c>{ig}</c> (each optionally <c>{id:mA}</c> for its unit).</summary>
    public string? Pattern { get; init; }

    /// <summary>Touchstone: <c>name</c> (the file name) or <c>comment</c> (its comment lines).</summary>
    public string? PatternSource { get; init; }

    /// <summary>CSV/TSV: the column map.</summary>
    public IReadOnlyList<ColumnMap>? Columns { get; init; }

    /// <summary>CSV/TSV: false when the first row is data. Default true.</summary>
    public bool? Header { get; init; }

    /// <summary>CSV/TSV: the delimiter; inferred from the first row when absent.</summary>
    public string? Delimiter { get; init; }

    /// <summary>.npy/.mat/.npz: quantity → array name.</summary>
    public IReadOnlyDictionary<string, string>? Arrays { get; init; }

    /// <summary>.npy/.mat/.npz: quantity → the unit its array's numbers are in, for a quantity not in SI (<c>freq</c> in
    /// <c>GHz</c>, <c>id</c> in <c>mA</c>). A quantity with no entry is in SI.</summary>
    public IReadOnlyDictionary<string, string>? ArrayUnits { get; init; }

    /// <summary>.npy/.mat: the frequency of a Y or S table that carries no frequency array, Hz.</summary>
    public double? FrequencyHz { get; init; }

    /// <summary>The reference impedance a table's Y is converted to S at, ohms (default 50).</summary>
    public double? Z0 { get; init; }
}

/// <summary>A point the user excluded, by its stable id, and why. The user decides; nothing excludes automatically.</summary>
public sealed record PointExclusion(string Point, string Reason);

/// <summary>One data set of a <c>.cmc</c> (R-mc3-2).</summary>
public sealed record CmcDataSet
{
    public required string Name { get; init; }
    public required DataSetKind Kind { get; init; }
    /// <summary>Paths relative to the <c>.cmc</c>, with '/' separators.</summary>
    public required IReadOnlyList<string> Sources { get; init; }
    public DataConditions Conditions { get; init; } = DataConditions.None;
    public required ImportSettings Import { get; init; }
    public IReadOnlyList<PointExclusion> Exclusions { get; init; } = [];
}

/// <summary>
/// Window view state. Absent from the file when every panel shows — railRF's rule: a document saved
/// with the default view carries no view block.
/// </summary>
public sealed record CmcPanels
{
    /// <summary>The panels a user switched off, by name, sorted when written.</summary>
    public IReadOnlyList<string> Hidden { get; init; } = [];

    public bool IsDefault => Hidden.Count == 0;
}

/// <summary>
/// The window's bias cursor (MC-14 R-mc14-2): the bias every plot is drawn at. Voltages in volts, the ambient in °C;
/// <see cref="Quiescent"/> picks a pulsed set's quiescent point (its currents are never written here).
/// </summary>
public sealed record CmcBiasCursor(double? Vgs = null, double? Vds = null, double? AmbientC = null, QuiescentBias? Quiescent = null);

/// <summary>
/// Where the window was left (MC-14 R-mc14-1): the selected step and the bias cursor, so a reopened document comes back
/// where it was. View state, written only when something is set, and never an undo step.
/// </summary>
public sealed record CmcView
{
    public string? Step { get; init; }
    public CmcBiasCursor? Cursor { get; init; }

    public bool IsDefault => Step is null && Cursor is null;
}

/// <summary>The <c>.cmc</c> document (R-mc3-1).</summary>
public sealed record CmcDocument
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public required CmcKind Kind { get; init; }
    /// <summary>The measured device's fingers and unit gate width (Active; D9).</summary>
    public BasePeriphery? BasePeriphery { get; init; }
    public IReadOnlyList<CmcDataSet> DataSets { get; init; } = [];

    /// <summary>Carried verbatim; <see cref="CmcExtrinsic"/> reads Pads and Network, later phases Steps.</summary>
    public JsonNode? Pads { get; init; }
    public JsonNode? Network { get; init; }
    public JsonNode? Steps { get; init; }

    public CmcPanels Panels { get; init; } = new();

    public CmcView View { get; init; } = new();
}
