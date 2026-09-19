// .csmith file format — rev 1.
// Mirrors RailDocumentIo exactly, which mirrors EmSetupPersistence, which mirrors TechPersistence:
// System.Text.Json, WriteIndented, enum-as-string, WhenWritingNull, reject-on-newer-format-version,
// AtomicFile write, gzip sniff on load. A FIFTH spelling of the same thing would be a fifth thing to
// keep in step.
//
// NUMBERS ARE STORED IN BASE SI — hertz, henries, farads, ohms — and carry their display scale
// nowhere near them. A picohenry is 1e-12, not 1. That is the sweep-unit trap recorded in
// src/Engine/RESOLVED.md, and this tool is the one most likely to get it wrong because its inputs
// are all picohenries and gigahertz. THE ONE DELIBERATE EXCEPTION is ElectricalLengthDeg, whose
// name carries its unit — see SmithDesign.cs' header for why radians would have been worse.
//
// EVERY FIELD IN THE FILE MODEL IS NULLABLE, so an absent key takes the model's own default and an
// unknown key written by a newer circuitRF is ignored rather than losing the keys beside it (the
// .ctech precedent).

using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;

namespace CircuitRF.Design.Smith;

/// <summary>Reads and writes <c>.csmith</c>. Framework-free (no Avalonia).</summary>
public static class SmithDesignIo
{
    public const string Extension            = ".csmith";
    public const int    CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented               = true,
        DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() },
    };

    // ── read / write ──────────────────────────────────────────────────────────

    /// <summary>
    /// The document as it goes on disk.
    ///
    /// <para><b>Validated on the way OUT as well as in</b> (§7): a document that cannot be read back
    /// is a document that was never written, and the alternative is a file on the user's disk whose
    /// only symptom is that it refuses to open next week.</para>
    /// </summary>
    /// <exception cref="InvalidDataException">The design is not well formed — the sentence names the
    /// element, the frequency or the setting.</exception>
    public static string Serialize(SmithDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        if (design.Refusal() is { } r) throw new InvalidDataException(r);
        return SerializeUnvalidated(design);
    }

    /// <summary>
    /// The same JSON with the well-formedness check SKIPPED — for a CLIPBOARD payload, and for
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// <b>A copy always writes</b> (R-smith1-6, on <c>RailDocumentIo.SerializeUnvalidated</c>'s own
    /// reasoning). A half-built design — a cascade with an element the user has not named yet, a
    /// generator table still being typed — is exactly the state someone copies from while they are
    /// still working, and a copy that wrote nothing would leave the PREVIOUS copy sitting on the
    /// clipboard for the next paste to find. A clipboard payload is not a file: it is re-read in
    /// this process within seconds, and a half-built design that arrives half-built is the honest
    /// result.
    /// </remarks>
    public static string SerializeUnvalidated(SmithDesign design)
    {
        ArgumentNullException.ThrowIfNull(design);
        return JsonSerializer.Serialize(ToFileModel(design), JsonOpts);
    }

    public static void SaveToFile(string path, SmithDesign design)
        => AtomicFile.WriteAllText(path, Serialize(design));

    /// <exception cref="InvalidDataException">The file is empty, is from a newer circuitRF, or is
    /// not well formed.</exception>
    public static SmithDesign Deserialize(string json)
    {
        var design = DeserializeUnvalidated(json);
        if (design.Refusal() is { } r) throw new InvalidDataException(r);
        return design;
    }

    /// <summary>
    /// The reading half of <see cref="SerializeUnvalidated"/>, unvalidated for that method's reason.
    ///
    /// <para><b>The format-version refusal is NOT skipped.</b> A document from a newer circuitRF is
    /// unreadable rather than half-built: reading it anyway would silently drop whatever the newer
    /// version added, and the user would find out by saving it back.</para>
    /// </summary>
    /// <exception cref="InvalidDataException">The JSON is empty, malformed, or from a newer
    /// circuitRF.</exception>
    public static SmithDesign DeserializeUnvalidated(string json)
    {
        var file = JsonSerializer.Deserialize<CsmithFile>(json, JsonOpts)
            ?? throw new InvalidDataException("Failed to deserialize .csmith file.");

        if (file.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(
                $".csmith format_version {file.FormatVersion} is newer than expected " +
                $"{CurrentFormatVersion}. Update the application.");

        return FromFileModel(file);
    }

    public static SmithDesign LoadFromFile(string path)
        => Deserialize(GzipTextFile.ReadAllTextAutoGzip(path));

    // ── convert: design → file ────────────────────────────────────────────────

    private static CsmithFile ToFileModel(SmithDesign d) => new()
    {
        FormatVersion = CurrentFormatVersion,
        Name          = NullIfEmpty(d.Name),
        Chart = new CsmithChart
        {
            Z0Ohm             = d.Chart.Z0Ohm,
            DesignFrequencyHz = d.Chart.DesignFrequencyHz,
            // Absent is "fit", which is what a document nobody has zoomed says and what every
            // document written before somebody did says too.
            Window = d.Chart.Window is { } w
                ? new CsmithWindow { MinX = w.MinX, MinY = w.MinY, MaxX = w.MaxX, MaxY = w.MaxY }
                : null,
            ShowGrippers = d.Chart.ShowGrippers,
            ShowTargets  = d.Chart.ShowTargets,
            ShowLabels   = d.Chart.ShowLabels,
        },
        Generator = new CsmithGenerator
        {
            SourcePath = NullIfEmpty(d.Generator.SourcePath),
            Rows = d.Generator.Rows.Count > 0
                ? [.. d.Generator.Rows.Select(r => new CsmithGeneratorRow
                      {
                          FrequencyHz   = r.FrequencyHz,
                          ResistanceOhm = r.ResistanceOhm,
                          ReactanceOhm  = r.ReactanceOhm,
                      })]
                : null,
        },
        Elements  = d.Elements.Count  > 0 ? [.. d.Elements.Select(ToFile)]  : null,
        Sweep     = IsDefault(d.Sweep)
                        ? null
                        : new CsmithSweep
                          {
                              Enabled = d.Sweep.Enabled,
                              StartHz = d.Sweep.StartHz,
                              StopHz  = d.Sweep.StopHz,
                              Points  = d.Sweep.Points,
                          },
        ConstantQ = IsDefault(d.ConstantQ)
                        ? null
                        : new CsmithConstantQ { Enabled = d.ConstantQ.Enabled, Q = d.ConstantQ.Q },
        Overlays  = d.Overlays.Count > 0 ? [.. d.Overlays.Select(ToFile)] : null,
        Markers   = d.Markers.Count  > 0 ? [.. d.Markers.Select(ToFile)]  : null,
        View = new CsmithView
        {
            SplitterMain   = d.View.SplitterMain,
            SplitterSide   = d.View.SplitterSide,
            NetworkScrollX = d.View.NetworkScrollX,
            NetworkScrollY = d.View.NetworkScrollY,
            NetworkZoom    = d.View.NetworkZoom,
            MirrorNetwork  = d.View.MirrorNetwork,
        },
    };

    private static CsmithElement ToFile(SmithElement e) => new()
    {
        Kind            = e.Kind,
        Placement       = e.Placement,
        Name            = e.Name,
        Enabled         = e.Enabled,
        ActiveParameter = e.ActiveParameter,
        Values = new CsmithValues
        {
            ROhm                 = e.Values.ROhm,
            LHenry               = e.Values.LHenry,
            CFarad               = e.Values.CFarad,
            Z0Ohm                = e.Values.Z0Ohm,
            ElectricalLengthDeg  = e.Values.ElectricalLengthDeg,
            ReferenceFrequencyHz = e.Values.ReferenceFrequencyHz,
            ImpedanceRealOhm     = e.Values.ImpedanceOhm.Real,
            ImpedanceImagOhm     = e.Values.ImpedanceOhm.Imaginary,
        },
        FileRef = NullIfEmpty(e.FileRef),
        // A LIST rather than a keyed object, and sorted: System.Text.Json spells a dictionary with
        // an enum key as a number unless told otherwise, and a document saved twice with no edit in
        // between has to be the same bytes or revision control has something to show every time.
        SliderRange = e.SliderRange.Count > 0
            ? [.. e.SliderRange
                   .OrderBy(kv => kv.Key)
                   .Select(kv => new CsmithSliderRange
                   {
                       Parameter = kv.Key,
                       Min       = kv.Value.Min,
                       Max       = kv.Value.Max,
                   })]
            : null,
    };

    private static CsmithOverlay ToFile(SmithOverlayRef o) => new()
    {
        SourceKind         = o.SourceKind,
        Source             = o.Source,
        Quantity           = NullIfEmpty(o.Quantity),
        Derived            = o.Derived,
        Renormalize        = o.Renormalize,
        Visible            = o.Visible,
        IncludeInAutoscale = o.IncludeInAutoscale,
        ColorHex           = NullIfEmpty(o.ColorHex),
        Dashed             = o.Dashed,
    };

    private static CsmithMarker ToFile(SmithMarker m) => new()
    {
        TraceName              = NullIfEmpty(m.TraceName),
        Name                   = m.Name,
        Index                  = m.Index,
        Freq                   = m.Freq,
        FreqUnits              = m.FreqUnits,
        MatrixFormat           = m.MatrixFormat,
        MatrixFormatImpedance  = m.MatrixFormatImpedance,
        Style                  = m.Style,
        UseNormalizedImpedance = m.UseNormalizedImpedance,
        MaximumFractionDigits  = m.MaximumFractionDigits,
        InfoBoxX               = m.InfoBoxX,
        InfoBoxY               = m.InfoBoxY,
        IsMulti                = m.IsMulti,
        IsDelta                = m.IsDelta,
        PositionStaticX        = m.PositionStaticX,
        PositionStaticY        = m.PositionStaticY,
        MarkerKind             = m.MarkerKind,
        ShowInfoBox            = m.ShowInfoBox,
        ContourSnapped         = m.ContourSnapped,
        VswrEnabled            = m.VswrEnabled,
        VswrValue              = m.VswrValue,
    };

    // ── convert: file → design ────────────────────────────────────────────────

    private static SmithDesign FromFileModel(CsmithFile f)
    {
        var d = new SmithDesign
        {
            Name = f.Name ?? "",
            Chart = new SmithChartSettings
            {
                Z0Ohm             = f.Chart?.Z0Ohm ?? 50.0,
                DesignFrequencyHz = f.Chart?.DesignFrequencyHz ?? 0.0,
                Window            = f.Chart?.Window is { } w
                                      ? new SmithWindow
                                        {
                                            MinX = w.MinX ?? -1.0, MinY = w.MinY ?? -1.0,
                                            MaxX = w.MaxX ??  1.0, MaxY = w.MaxY ??  1.0,
                                        }
                                      : null,
                ShowGrippers      = f.Chart?.ShowGrippers ?? true,
                ShowTargets       = f.Chart?.ShowTargets  ?? true,
                ShowLabels        = f.Chart?.ShowLabels   ?? true,
            },
            Sweep = new SmithSweep
            {
                Enabled = f.Sweep?.Enabled ?? false,
                StartHz = f.Sweep?.StartHz ?? 0.0,
                StopHz  = f.Sweep?.StopHz  ?? 0.0,
                Points  = f.Sweep?.Points  ?? 51,
            },
            ConstantQ = new SmithConstantQ
            {
                Enabled = f.ConstantQ?.Enabled ?? false,
                Q       = f.ConstantQ?.Q       ?? 1.0,
            },
            View = new SmithView
            {
                SplitterMain   = f.View?.SplitterMain   ?? 0.65,
                SplitterSide   = f.View?.SplitterSide   ?? 0.5,
                NetworkScrollX = f.View?.NetworkScrollX ?? 0.0,
                NetworkScrollY = f.View?.NetworkScrollY ?? 0.0,
                NetworkZoom    = f.View?.NetworkZoom    ?? 1.0,
                MirrorNetwork  = f.View?.MirrorNetwork  ?? false,
            },
        };

        d.Generator.SourcePath = f.Generator?.SourcePath;
        foreach (var r in f.Generator?.Rows ?? [])
            d.Generator.Rows.Add(new SmithGeneratorRow(
                r.FrequencyHz ?? 0.0, r.ResistanceOhm ?? 0.0, r.ReactanceOhm ?? 0.0));

        foreach (var e in f.Elements ?? []) d.Elements.Add(FromFile(e));
        foreach (var o in f.Overlays ?? []) d.Overlays.Add(FromFile(o));
        foreach (var m in f.Markers  ?? []) d.Markers.Add(FromFile(m));

        return d;
    }

    private static SmithElement FromFile(CsmithElement e)
    {
        var el = new SmithElement
        {
            Kind            = e.Kind,
            Placement       = e.Placement,
            Name            = e.Name ?? "",
            Enabled         = e.Enabled ?? true,
            ActiveParameter = e.ActiveParameter ?? SmithComponentMap.DefaultParameter(e.Kind),
            FileRef         = e.FileRef,
            Values = new SmithElementValues
            {
                ROhm                 = e.Values?.ROhm                 ?? 0.0,
                LHenry               = e.Values?.LHenry               ?? 0.0,
                CFarad               = e.Values?.CFarad               ?? 0.0,
                Z0Ohm                = e.Values?.Z0Ohm                ?? 50.0,
                ElectricalLengthDeg  = e.Values?.ElectricalLengthDeg  ?? 0.0,
                ReferenceFrequencyHz = e.Values?.ReferenceFrequencyHz ?? 0.0,
                ImpedanceOhm         = new System.Numerics.Complex(
                                           e.Values?.ImpedanceRealOhm ?? 0.0,
                                           e.Values?.ImpedanceImagOhm ?? 0.0),
            },
        };

        foreach (var s in e.SliderRange ?? [])
            el.SliderRange[s.Parameter] = new SmithSliderRange(s.Min ?? 0.0, s.Max ?? 0.0);

        return el;
    }

    private static SmithOverlayRef FromFile(CsmithOverlay o) => new()
    {
        SourceKind         = o.SourceKind,
        Source             = o.Source ?? "",
        Quantity           = o.Quantity ?? "",
        Derived            = o.Derived ?? "None",
        Renormalize        = o.Renormalize        ?? true,
        Visible            = o.Visible            ?? true,
        IncludeInAutoscale = o.IncludeInAutoscale ?? false,
        ColorHex           = o.ColorHex,
        Dashed             = o.Dashed             ?? false,
    };

    private static SmithMarker FromFile(CsmithMarker m) => new()
    {
        TraceName              = m.TraceName ?? "",
        Name                   = m.Name ?? "m0",
        Index                  = m.Index ?? 0,
        Freq                   = m.Freq  ?? 0.0,
        FreqUnits              = m.FreqUnits             ?? "GHz",
        MatrixFormat           = m.MatrixFormat          ?? "MA",
        MatrixFormatImpedance  = m.MatrixFormatImpedance ?? "RI",
        Style                  = m.Style                 ?? "Medium",
        UseNormalizedImpedance = m.UseNormalizedImpedance ?? true,
        MaximumFractionDigits  = m.MaximumFractionDigits  ?? 4,
        InfoBoxX               = m.InfoBoxX ?? 0.0,
        InfoBoxY               = m.InfoBoxY ?? 0.0,
        IsMulti                = m.IsMulti ?? false,
        IsDelta                = m.IsDelta ?? false,
        PositionStaticX        = m.PositionStaticX ?? 0f,
        PositionStaticY        = m.PositionStaticY ?? 0f,
        MarkerKind             = m.MarkerKind  ?? "Polyline",
        ShowInfoBox            = m.ShowInfoBox ?? true,
        ContourSnapped         = m.ContourSnapped ?? false,
        VswrEnabled            = m.VswrEnabled ?? false,
        VswrValue              = m.VswrValue   ?? 2.0,
    };

    private static string? NullIfEmpty(string? s) => s is { Length: > 0 } ? s : null;

    // ── the serialised shape ──────────────────────────────────────────────────

    /// <summary>
    /// True when nothing about the band has been touched — <b>which is the only state that goes
    /// unwritten</b>.
    /// </summary>
    /// <remarks>
    /// <b>The rule is "absent means untouched", not "absent means off".</b> Writing the block only
    /// while the feature was ENABLED lost a disabled band's start, stop and point count — and,
    /// because the undo stack is a serialize/deserialize round trip of this same writer, it lost them
    /// WITHIN the session too: unchecking the box and checking it again handed back the defaults,
    /// silently, with the user's own numbers gone and nothing said. A document nobody has touched
    /// still writes nothing, so no existing file changes.
    /// </remarks>
    private static bool IsDefault(SmithSweep s)
        => !s.Enabled && s.StartHz == 0.0 && s.StopHz == 0.0 && s.Points == 51;

    /// <inheritdoc cref="IsDefault(SmithSweep)"/>
    private static bool IsDefault(SmithConstantQ q) => !q.Enabled && q.Q == 1.0;

    private sealed class CsmithFile
    {
        public int                    FormatVersion { get; set; }
        public string?                Name          { get; set; }
        public CsmithChart?           Chart         { get; set; }
        public CsmithGenerator?       Generator     { get; set; }
        public List<CsmithElement>?   Elements      { get; set; }

        /// <summary>Written only when the band is not at its DEFAULTS — see
        /// <see cref="IsDefault(SmithSweep)"/> for why that is not the same as "when it is on".
        /// Absent is off with defaults, which is every document nobody has touched it in and every
        /// one written before it existed.</summary>
        public CsmithSweep?           Sweep         { get; set; }

        /// <summary>Same rule as <see cref="Sweep"/>.</summary>
        public CsmithConstantQ?       ConstantQ     { get; set; }

        public List<CsmithOverlay>?   Overlays      { get; set; }
        public List<CsmithMarker>?    Markers       { get; set; }
        public CsmithView?            View          { get; set; }
    }

    private sealed class CsmithChart
    {
        public double?       Z0Ohm             { get; set; }
        public double?       DesignFrequencyHz { get; set; }
        public CsmithWindow? Window            { get; set; }
        public bool?         ShowGrippers      { get; set; }
        public bool?         ShowTargets       { get; set; }
        public bool?         ShowLabels        { get; set; }
    }

    /// <summary>Γ extents — dimensionless, so there is no unit to get wrong here.</summary>
    private sealed class CsmithWindow
    {
        public double? MinX { get; set; }
        public double? MinY { get; set; }
        public double? MaxX { get; set; }
        public double? MaxY { get; set; }
    }

    private sealed class CsmithGenerator
    {
        public string?                   SourcePath { get; set; }
        public List<CsmithGeneratorRow>? Rows       { get; set; }
    }

    private sealed class CsmithGeneratorRow
    {
        public double? FrequencyHz   { get; set; }
        public double? ResistanceOhm { get; set; }
        public double? ReactanceOhm  { get; set; }
    }

    private sealed class CsmithElement
    {
        public SmithElementKind   Kind            { get; set; }
        public SmithPlacement     Placement       { get; set; }
        public string?            Name            { get; set; }
        public bool?              Enabled         { get; set; }

        /// <summary>Absent takes <see cref="SmithComponentMap.DefaultParameter"/>, which is §3.3's
        /// own column — so a hand-written `.csmith` that omits it gets the gripper the window would
        /// have given it rather than none.</summary>
        public SmithParameter?    ActiveParameter { get; set; }

        public CsmithValues?      Values          { get; set; }
        public string?            FileRef         { get; set; }
        public List<CsmithSliderRange>? SliderRange { get; set; }
    }

    /// <summary>Base SI throughout, with the one named exception. See this file's header.</summary>
    private sealed class CsmithValues
    {
        public double? ROhm                 { get; set; }
        public double? LHenry               { get; set; }
        public double? CFarad               { get; set; }
        public double? Z0Ohm                { get; set; }
        public double? ElectricalLengthDeg  { get; set; }
        public double? ReferenceFrequencyHz { get; set; }
        public double? ImpedanceRealOhm     { get; set; }
        public double? ImpedanceImagOhm     { get; set; }
    }

    private sealed class CsmithSliderRange
    {
        public SmithParameter Parameter { get; set; }
        public double?        Min       { get; set; }
        public double?        Max       { get; set; }
    }

    private sealed class CsmithSweep
    {
        public bool?   Enabled { get; set; }
        public double? StartHz { get; set; }
        public double? StopHz  { get; set; }
        public int?    Points  { get; set; }
    }

    private sealed class CsmithConstantQ
    {
        public bool?   Enabled { get; set; }
        public double? Q       { get; set; }
    }

    private sealed class CsmithOverlay
    {
        public SmithOverlaySource SourceKind         { get; set; }
        public string?            Source             { get; set; }
        public string?            Quantity           { get; set; }
        public string?            Derived            { get; set; }
        public bool?              Renormalize        { get; set; }
        public bool?              Visible            { get; set; }
        public bool?              IncludeInAutoscale { get; set; }
        public string?            ColorHex           { get; set; }
        public bool?              Dashed             { get; set; }
    }

    /// <summary>The Data Display's <c>MarkerConfig</c>, field for field — the four enums as their
    /// own member names, which is what <c>JsonStringEnumConverter</c> writes for them in a
    /// `.cdd`.</summary>
    private sealed class CsmithMarker
    {
        /// <summary>Which curve the marker is a reading ON — the trace's label. The one field
        /// <c>MarkerConfig</c> does not have, because a `.cdd` nests its markers under their trace
        /// and a `.csmith` has no trace list to nest them in. Absent reads as the first curve.</summary>
        public string? TraceName              { get; set; }

        public string? Name                   { get; set; }
        public int?    Index                  { get; set; }
        public double? Freq                   { get; set; }
        public string? FreqUnits              { get; set; }
        public string? MatrixFormat           { get; set; }
        public string? MatrixFormatImpedance  { get; set; }
        public string? Style                  { get; set; }
        public bool?   UseNormalizedImpedance { get; set; }
        public int?    MaximumFractionDigits  { get; set; }
        public double? InfoBoxX               { get; set; }
        public double? InfoBoxY               { get; set; }
        public bool?   IsMulti                { get; set; }
        public bool?   IsDelta                { get; set; }
        public float?  PositionStaticX        { get; set; }
        public float?  PositionStaticY        { get; set; }
        public string? MarkerKind             { get; set; }
        public bool?   ShowInfoBox            { get; set; }
        public bool?   ContourSnapped         { get; set; }
        public bool?   VswrEnabled            { get; set; }
        public double? VswrValue              { get; set; }
    }

    private sealed class CsmithView
    {
        public double? SplitterMain   { get; set; }
        public double? SplitterSide   { get; set; }
        public double? NetworkScrollX { get; set; }
        public double? NetworkScrollY { get; set; }
        public double? NetworkZoom    { get; set; }
        public bool?   MirrorNetwork  { get; set; }
    }
}
