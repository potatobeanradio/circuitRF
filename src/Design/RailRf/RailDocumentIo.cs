// .crail file format — rev 1.
// Mirrors EmSetupPersistence exactly, which mirrors TechPersistence: System.Text.Json,
// WriteIndented, enum-as-string, WhenWritingNull, reject-on-newer-format-version, AtomicFile write,
// gzip sniff on load. A fourth spelling of the same thing would be a fourth thing to keep in step.
//
// NUMBERS ARE STORED IN BASE SI and carry their display scale nowhere near them. A frequency field
// is hertz, a current field is amps, a voltage field is volts, a length is DBU. That is the
// sweep-unit trap recorded in src/Engine/RESOLVED.md: a mark read without its scale once produced a
// run at 2 Hz that looked entirely normal. The two deliberate exceptions are the TARGETS, which are
// millivolts and milliohms — see RailTarget, where the reason is written down.

using System.Text.Json;
using System.Text.Json.Serialization;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;

namespace CircuitRF.Design.RailRf;

/// <summary>Reads and writes <c>.crail</c>. Framework-free (no Avalonia / Skia).</summary>
public static class RailDocumentIo
{
    public const string Extension = ".crail";
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
    /// <para><b>Validated on the way OUT as well as in</b>: a document that cannot be read back is a
    /// document that was never written, and the alternative is a file on the user's disk whose only
    /// symptom is that it refuses to open next week.</para>
    /// </summary>
    /// <exception cref="InvalidDataException">The document is not well formed — the sentence names
    /// the rail and the row.</exception>
    public static string Serialize(RailDocument doc)
    {
        if (doc.Refusal() is { } r) throw new InvalidDataException(r);
        return SerializeUnvalidated(doc);
    }

    /// <summary>
    /// The same JSON, with the well-formedness check SKIPPED — for a CLIPBOARD payload, and for
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// <b>A copy always writes</b> (railrf.md §11.7, brief-railrf-9-copy.md R-rail9-5): a copy that
    /// puts nothing on the system clipboard leaves the PREVIOUS copy sitting there, so the next paste
    /// produces something unrelated and nothing reports a failure. A half-built document — a rail with
    /// no net picked yet — is exactly the state someone copies from while they are still working, so
    /// the validation <see cref="Serialize"/> performs would turn that into the silent-wrong-paste
    /// case it exists to prevent.
    ///
    /// <para>A FILE is still validated on the way out, because a document that cannot be read back is
    /// a document that was never written and its only symptom is a file that refuses to open next
    /// week. A clipboard payload is not a file: it is re-read by
    /// <see cref="DeserializeUnvalidated"/> in this process within seconds, and a half-built document
    /// that arrives half-built is the honest result.</para>
    /// </remarks>
    public static string SerializeUnvalidated(RailDocument doc)
        => JsonSerializer.Serialize(ToFileModel(doc), JsonOpts);

    public static void SaveToFile(string path, RailDocument doc)
        => AtomicFile.WriteAllText(path, Serialize(doc));

    /// <exception cref="InvalidDataException">The file is empty, is from a newer circuitRF, or is not
    /// well formed.</exception>
    public static RailDocument Deserialize(string json)
    {
        var doc = DeserializeUnvalidated(json);
        if (doc.Refusal() is { } r) throw new InvalidDataException(r);
        return doc;
    }

    /// <summary>
    /// The reading half of <see cref="SerializeUnvalidated"/>, and it is unvalidated for that
    /// method's reason. The format-version refusal is NOT skipped: a document from a newer circuitRF
    /// is unreadable rather than half-built, and reading it anyway would silently drop whatever the
    /// newer version added.
    /// </summary>
    /// <exception cref="InvalidDataException">The JSON is empty, malformed, or from a newer
    /// circuitRF.</exception>
    public static RailDocument DeserializeUnvalidated(string json)
    {
        var file = JsonSerializer.Deserialize<CrailFile>(json, JsonOpts)
            ?? throw new InvalidDataException("Failed to deserialize .crail file.");

        if (file.FormatVersion > CurrentFormatVersion)
            throw new InvalidDataException(
                $".crail format_version {file.FormatVersion} is newer than expected " +
                $"{CurrentFormatVersion}. Update the application.");

        return FromFileModel(file);
    }

    public static RailDocument LoadFromFile(string path)
        => Deserialize(GzipTextFile.ReadAllTextAutoGzip(path));

    // ── convert: document → file ──────────────────────────────────────────────

    private static CrailFile ToFileModel(RailDocument d) => new()
    {
        FormatVersion  = CurrentFormatVersion,
        Name           = NullIfEmpty(d.Name),
        ArtworkCellRef = NullIfEmpty(d.ArtworkCellRef),
        TechnologyRef  = NullIfEmpty(d.TechnologyRef),
        PartLibraryRef = NullIfEmpty(d.PartLibraryRef),
        BoardNetlistRef = NullIfEmpty(d.BoardNetlistRef),
        PlacementRef    = NullIfEmpty(d.PlacementRef),
        ReferenceNet    = NullIfEmpty(d.ReferenceNet),
        Settings = new CrailSettings
        {
            CopperTemperatureCelsius      = d.Settings.CopperTemperatureCelsius,
            ViaPlatingThicknessMicrometres = d.Settings.ViaPlatingThicknessMicrometres,
            ViaTemperatureRiseCelsius      = d.Settings.ViaTemperatureRiseCelsius,
        },
        Rails = d.Rails.Count > 0 ? [.. d.Rails.Select(ToFile)] : null,
        // Deterministic order, so a document saved twice with no edit in between is the same bytes
        // and revision control has nothing to show.
        ClassOverrides = d.ClassOverrides.Count > 0
            ? [.. d.ClassOverrides
                   .OrderBy(kv => kv.Key.Layer.Layer).ThenBy(kv => kv.Key.Layer.Datatype)
                   .ThenBy(kv => kv.Key.Y).ThenBy(kv => kv.Key.X)
                   .Select(kv => new CrailClassOverride
                   {
                       Layer    = kv.Key.Layer.Layer,
                       Datatype = kv.Key.Layer.Datatype,
                       X        = kv.Key.X,
                       Y        = kv.Key.Y,
                       Class    = kv.Value,
                   })]
            : null,
    };

    private static CrailRail ToFile(RailSpec r) => new()
    {
        Name                   = r.Name,
        NetName                = NullIfEmpty(r.NetName),
        ReferenceLayer         = r.ReferenceLayer?.Layer,
        ReferenceLayerDatatype = r.ReferenceLayer?.Datatype,
        ReferenceExtent        = r.ReferenceExtent,
        BandStartHz            = r.Band.StartHz,
        BandStopHz             = r.Band.StopHz,
        BandPoints             = r.Band.Points,
        BandLogarithmic        = r.Band.Logarithmic,
        DropBudget             = ToFile(r.DropBudget),
        ImpedanceTarget        = ToFile(r.ImpedanceTarget),
        Sources                = r.Sources.Count    > 0 ? [.. r.Sources.Select(ToFile)]    : null,
        Loads                  = r.Loads.Count      > 0 ? [.. r.Loads.Select(ToFile)]      : null,
        Aggressors             = r.Aggressors.Count > 0 ? [.. r.Aggressors.Select(ToFile)] : null,
        Parts                  = r.Parts.Count      > 0 ? [.. r.Parts.Select(ToFile)]      : null,
    };

    private static CrailSource ToFile(RailSource s) => new()
    {
        Anchor                  = ToFile(s.Anchor),
        OpenCircuitVoltageV     = s.OpenCircuitVoltageV,
        SeriesResistanceOhms    = s.SeriesResistanceOhms,
        SeriesInductanceHenries = s.SeriesInductanceHenries,
        TouchstoneRef           = NullIfEmpty(s.TouchstoneRef),
    };

    private static CrailLoad ToFile(RailLoad l) => new()
    {
        Anchor                 = ToFile(l.Anchor),
        DcCurrentA             = l.DcCurrentA,
        PeakCurrentA           = l.PeakCurrentA,
        Mask                   = ToFile(l.Mask),
        RegulatorInputCurrentA = l.RegulatorInputCurrentA,
        MinimumInputVoltageV   = l.MinimumInputVoltageV,
    };

    private static CrailAnchor ToFile(RailPortAnchor a) => new()
    {
        Refdes = NullIfEmpty(a.Refdes),
        Pin    = NullIfEmpty(a.Pin),
        PointX = a.Point?.X,
        PointY = a.Point?.Y,
    };

    private static CrailAggressor ToFile(RailAggressor a) => new()
    {
        Name        = a.Name,
        FrequencyHz = a.FrequencyHz,
        Harmonics   = a.Harmonics,
        Origin      = a.Origin,
    };

    private static CrailPart ToFile(RailPart p) => new()
    {
        Refdes                    = p.Refdes,
        PartNumber                = NullIfEmpty(p.PartNumber),
        MountingInductanceHenries = p.MountingInductanceHenries,
        Origin                    = p.Origin,
    };

    private static CrailTarget? ToFile(RailTarget? t) => t is null ? null : new CrailTarget
    {
        Kind                 = t.Kind,
        DropBudgetMillivolts = t.DropBudgetMillivolts,
        FlatMilliohms        = t.FlatMilliohms,
        Mask                 = t.Mask is { Count: > 0 }
                                 ? [.. t.Mask.Select(p => new CrailMaskPoint
                                       { FrequencyHz = p.FrequencyHz, LimitOhms = p.LimitOhms })]
                                 : null,
        DeltaIAmps       = t.Transient?.DeltaIAmps,
        DeltaVVolts      = t.Transient?.DeltaVVolts,
        RiseTimeSeconds  = t.Transient?.RiseTimeSeconds,
    };

    // ── convert: file → document ──────────────────────────────────────────────

    private static RailDocument FromFileModel(CrailFile f)
    {
        var doc = new RailDocument
        {
            Name           = f.Name ?? "",
            ArtworkCellRef = f.ArtworkCellRef,
            TechnologyRef  = f.TechnologyRef,
            PartLibraryRef = f.PartLibraryRef,
            BoardNetlistRef = f.BoardNetlistRef,
            PlacementRef    = f.PlacementRef,
            ReferenceNet    = f.ReferenceNet,
            Settings = new RailSettings
            {
                CopperTemperatureCelsius      = f.Settings?.CopperTemperatureCelsius ?? 20.0,
                ViaPlatingThicknessMicrometres = f.Settings?.ViaPlatingThicknessMicrometres,
                ViaTemperatureRiseCelsius      = f.Settings?.ViaTemperatureRiseCelsius
                                              ?? PdnViaCurrentLimit.ReferenceRiseCelsius,
            },
        };

        foreach (var r in f.Rails ?? []) doc.Rails.Add(FromFile(r));

        foreach (var o in f.ClassOverrides ?? [])
            doc.ClassOverrides[new PdnRegionRef(new LayerKey(o.Layer, o.Datatype), o.X, o.Y)] = o.Class;

        return doc;
    }

    private static RailSpec FromFile(CrailRail r)
    {
        var spec = new RailSpec
        {
            Name            = r.Name ?? "",
            NetName         = r.NetName,
            ReferenceLayer  = r.ReferenceLayer is { } layer
                                ? new LayerKey(layer, r.ReferenceLayerDatatype ?? 0)
                                : null,
            ReferenceExtent = r.ReferenceExtent,
            Band            = new RailBand(
                                  r.BandStartHz     ?? RailBand.Default.StartHz,
                                  r.BandStopHz      ?? RailBand.Default.StopHz,
                                  r.BandPoints      ?? RailBand.Default.Points,
                                  r.BandLogarithmic ?? RailBand.Default.Logarithmic),
            DropBudget      = FromFile(r.DropBudget),
            ImpedanceTarget = FromFile(r.ImpedanceTarget),
        };

        foreach (var s in r.Sources    ?? []) spec.Sources.Add(FromFile(s));
        foreach (var l in r.Loads      ?? []) spec.Loads.Add(FromFile(l));
        foreach (var a in r.Aggressors ?? []) spec.Aggressors.Add(FromFile(a));
        foreach (var p in r.Parts      ?? []) spec.Parts.Add(FromFile(p));
        return spec;
    }

    private static RailSource FromFile(CrailSource s) => new()
    {
        Anchor                  = FromFile(s.Anchor),
        OpenCircuitVoltageV     = s.OpenCircuitVoltageV,
        SeriesResistanceOhms    = s.SeriesResistanceOhms,
        SeriesInductanceHenries = s.SeriesInductanceHenries,
        TouchstoneRef           = s.TouchstoneRef,
    };

    private static RailLoad FromFile(CrailLoad l) => new()
    {
        Anchor                 = FromFile(l.Anchor),
        DcCurrentA             = l.DcCurrentA,
        PeakCurrentA           = l.PeakCurrentA,
        Mask                   = FromFile(l.Mask),
        RegulatorInputCurrentA = l.RegulatorInputCurrentA,
        MinimumInputVoltageV   = l.MinimumInputVoltageV,
    };

    private static RailPortAnchor FromFile(CrailAnchor? a) => new()
    {
        Refdes = a?.Refdes,
        Pin    = a?.Pin,
        // Both or neither. One of the two alone is a coordinate with an axis missing, which would
        // otherwise read back as (x, 0) — a point on the board, and a plausible one.
        Point  = a is { PointX: { } x, PointY: { } y } ? (x, y) : null,
    };

    private static RailAggressor FromFile(CrailAggressor a) =>
        new(a.Name ?? "", a.FrequencyHz ?? 0, a.Harmonics ?? 1) { Origin = a.Origin };

    private static RailPart FromFile(CrailPart p) => new()
    {
        Refdes                    = p.Refdes ?? "",
        PartNumber                = p.PartNumber ?? "",
        MountingInductanceHenries = p.MountingInductanceHenries,
        Origin                    = p.Origin,
    };

    private static RailTarget? FromFile(CrailTarget? t) => t is null ? null : new RailTarget
    {
        Kind                 = t.Kind,
        DropBudgetMillivolts = t.DropBudgetMillivolts,
        FlatMilliohms        = t.FlatMilliohms,
        Mask                 = t.Mask is { Count: > 0 }
                                 ? [.. t.Mask.Select(p => new RailMaskPoint(p.FrequencyHz, p.LimitOhms))]
                                 : null,
        Transient            = t.DeltaIAmps is { } di && t.DeltaVVolts is { } dv
                                                      && t.RiseTimeSeconds is { } tr
                                 ? new RailTransientSpec(di, dv, tr)
                                 : null,
    };

    private static string? NullIfEmpty(string? s) => s is { Length: > 0 } ? s : null;

    // ── the serialised shape. Every field nullable, so absent takes the default, and an unknown
    //    key read by a newer circuitRF is ignored rather than losing the keys beside it (the
    //    .ctech precedent). ─────────────────────────────────────────────────────

    private sealed class CrailFile
    {
        public int              FormatVersion  { get; set; }
        public string?          Name           { get; set; }
        public string?          ArtworkCellRef { get; set; }
        public string?          TechnologyRef  { get; set; }
        public string?          PartLibraryRef { get; set; }

        /// <summary>The board netlist beside the artwork, and the placement — what makes a REFDES
        /// resolve to copper. Absent on a document imported before these were persisted, and on
        /// every assisted-Gerber board, which has neither.</summary>
        public string?          BoardNetlistRef { get; set; }
        public string?          PlacementRef    { get; set; }

        /// <summary>The reference return's net. Absent is not "GND" — see RailDocument.</summary>
        public string?          ReferenceNet    { get; set; }

        public CrailSettings?   Settings       { get; set; }
        public List<CrailRail>? Rails          { get; set; }

        /// <summary>R-rail4-3's overrides. Absent on every document nobody has corrected, which is
        /// most of them.</summary>
        public List<CrailClassOverride>? ClassOverrides { get; set; }
    }

    /// <summary>One forced region — the drawing layer and the vertex that identify it, and what it
    /// was forced to.</summary>
    private sealed class CrailClassOverride
    {
        public int            Layer    { get; set; }
        public int            Datatype { get; set; }
        public long           X        { get; set; }
        public long           Y        { get; set; }
        public PdnCopperClass Class    { get; set; }
    }

    private sealed class CrailSettings
    {
        public double? CopperTemperatureCelsius       { get; set; }
        public double? ViaPlatingThicknessMicrometres { get; set; }
        public double? ViaTemperatureRiseCelsius      { get; set; }
    }

    private sealed class CrailRail
    {
        public string?                Name                   { get; set; }
        public string?                NetName                { get; set; }
        public int?                   ReferenceLayer         { get; set; }
        public int?                   ReferenceLayerDatatype { get; set; }
        public RailReferenceExtent    ReferenceExtent        { get; set; } = RailReferenceExtent.AsImported;
        public double?                BandStartHz            { get; set; }
        public double?                BandStopHz             { get; set; }
        public int?                   BandPoints             { get; set; }
        public bool?                  BandLogarithmic        { get; set; }
        public CrailTarget?           DropBudget             { get; set; }
        public CrailTarget?           ImpedanceTarget        { get; set; }
        public List<CrailSource>?     Sources                { get; set; }
        public List<CrailLoad>?       Loads                  { get; set; }
        public List<CrailAggressor>?  Aggressors             { get; set; }
        public List<CrailPart>?       Parts                  { get; set; }
    }

    private sealed class CrailAnchor
    {
        public string? Refdes { get; set; }
        public string? Pin    { get; set; }
        public long?   PointX { get; set; }
        public long?   PointY { get; set; }
    }

    private sealed class CrailSource
    {
        public CrailAnchor? Anchor                  { get; set; }
        public double?      OpenCircuitVoltageV     { get; set; }
        public double?      SeriesResistanceOhms    { get; set; }
        public double?      SeriesInductanceHenries { get; set; }
        public string?      TouchstoneRef           { get; set; }
    }

    private sealed class CrailLoad
    {
        public CrailAnchor? Anchor                 { get; set; }
        public double?      DcCurrentA             { get; set; }
        public double?      PeakCurrentA           { get; set; }
        public CrailTarget? Mask                   { get; set; }
        public double?      RegulatorInputCurrentA { get; set; }
        public double?      MinimumInputVoltageV   { get; set; }
    }

    private sealed class CrailAggressor
    {
        public string?              Name        { get; set; }
        public double?              FrequencyHz { get; set; }
        public int?                 Harmonics   { get; set; }
        public RailAggressorOrigin  Origin      { get; set; } = RailAggressorOrigin.Typed;
    }

    /// <summary>One part on the rail. <see cref="CrailPart.MountingInductanceHenries"/> is the only
    /// electrical number here, and it is the one a BOM cannot carry — see <see cref="RailPart"/>.</summary>
    private sealed class CrailPart
    {
        public string?         Refdes                    { get; set; }
        public string?         PartNumber                { get; set; }
        public double?         MountingInductanceHenries { get; set; }
        public RailPartOrigin  Origin                    { get; set; } = RailPartOrigin.Typed;
    }

    /// <summary>One target of any of the four kinds. <see cref="Kind"/> says which, and exactly the
    /// payload it names is written — see <see cref="RailTarget.Refusal"/>, which is what holds
    /// that.</summary>
    private sealed class CrailTarget
    {
        public RailTargetKind        Kind                 { get; set; }
        public double?               DropBudgetMillivolts { get; set; }
        public double?               FlatMilliohms        { get; set; }
        public List<CrailMaskPoint>? Mask                 { get; set; }
        public double?               DeltaIAmps           { get; set; }
        public double?               DeltaVVolts          { get; set; }
        public double?               RiseTimeSeconds      { get; set; }
    }

    private sealed class CrailMaskPoint
    {
        public double FrequencyHz { get; set; }
        public double LimitOhms   { get; set; }
    }
}
