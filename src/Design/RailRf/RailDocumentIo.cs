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
using CircuitRF.Design.Layout.Interchange;
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

        // OMITTED ENTIRELY WHERE NOTHING WAS STATED, which is every document written before this
        // existed and every board whose placement file declares its own origin and units. A group
        // of three nulls in the JSON would be three questions a reader has to work out are not
        // being answered.
        Placement       = d.Placement.IsEmpty ? null : new CrailPlacement
        {
            Origin  = d.Placement.Origin?.ToString(),
            Units   = d.Placement.Units?.ToString(),
            Columns = d.Placement.Columns is { Count: > 0 } c ? [.. c] : null,
        },

        ReferenceNet    = NullIfEmpty(d.ReferenceNet),
        Settings = new CrailSettings
        {
            CopperTemperatureCelsius      = d.Settings.CopperTemperatureCelsius,
            ViaPlatingThicknessMicrometres = d.Settings.ViaPlatingThicknessMicrometres,
            ViaTemperatureRiseCelsius      = d.Settings.ViaTemperatureRiseCelsius,
        },
        // WRITTEN ONLY WHEN SOMETHING IS HIDDEN. Absent is the all-shown state, so a document
        // nobody has collapsed a panel in is the same bytes it was before panels existed — and a
        // `.crail` from an older circuitRF opens with all four, which is the same rule read from
        // the other end.
        Panels = d.Panels.AllShown ? null : new CrailPanels
        {
            ShowSpecification = d.Panels.ShowSpecification,
            ShowBoard         = d.Panels.ShowBoard,
            ShowParts         = d.Panels.ShowParts,
            ShowResults       = d.Panels.ShowResults,
            ShowResultText    = d.Panels.ShowResultText,
        },
        // R-rail20-1b: the WINDOW's own hidden layers, never the technology's. Written only when
        // something is hidden, for Panels' reason — and sorted, so a document saved twice with no
        // edit between is the same bytes.
        HiddenLayers = d.HiddenLayers.Count > 0
            ? [.. d.HiddenLayers
                   .OrderBy(k => k.Layer).ThenBy(k => k.Datatype)
                   .Select(k => new CrailLayerKey { Layer = k.Layer, Datatype = k.Datatype })]
            : null,
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
        Markers                = r.Markers.Count    > 0 ? [.. r.Markers.Select(ToFile)]    : null,
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
        Layer         = a.Layer?.Layer,
        LayerDatatype = a.Layer?.Datatype,
    };

    private static CrailMarker ToFile(RailMarker m) => new()
    {
        Model       = m.Model,
        Port        = m.Port,
        FrequencyHz = m.FrequencyHz,
        CurveIndex  = m.CurveIndex,
        Index       = m.Index,
        Name        = NullIfEmpty(m.Name),
        // NEVER a NaN. System.Text.Json refuses to write one, and a marker nobody has dragged has
        // no box position at all — absent is what this format says for that, and the renderer then
        // places it exactly where it would place a new one.
        InfoBoxX    = double.IsFinite(m.InfoBoxX ?? double.NaN) ? m.InfoBoxX : null,
        InfoBoxY    = double.IsFinite(m.InfoBoxY ?? double.NaN) ? m.InfoBoxY : null,
        ShowInfoBox = m.ShowInfoBox,
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

        // R-rail23-1a: ABSENT MEANS MOUNTED. Written only when the part is unmounted, which is
        // what makes every .crail written before this flag existed read exactly as it did — and
        // what keeps the ordinary document free of a line saying nothing.
        Mounted                   = p.Mounted ? null : false,

        // brief 25, and the SAME rule: absent means SHUNT, and absent means DOWNSTREAM. Every
        // .crail written before a series element existed reads exactly as it did, and an ordinary
        // decoupling row is not given two lines saying what it already is.
        Connection                = p.Connection == RailPartConnection.Shunt ? null : p.Connection,
        TerminalA                 = p.TerminalA is { } ta ? ToFile(ta) : null,
        TerminalB                 = p.TerminalB is { } tb ? ToFile(tb) : null,
        DcResistanceOhms          = p.DcResistanceOhms,
        SeriesResistanceOhms      = p.SeriesResistanceOhms,
        SeriesInductanceHenries   = p.SeriesInductanceHenries,
        TouchstoneRef             = NullIfEmpty(p.TouchstoneRef),
        Side                      = p.Side == RailSection.Downstream ? null : p.Side,
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
            Placement       = new RailPlacementReading
            {
                // PARSED LENIENTLY AND DROPPED WHERE IT DOES NOT PARSE. A token this build does not
                // know is a reading it cannot honour, and falling back to "let the file say" is the
                // state the document was in before anyone answered — which is recoverable. Throwing
                // would make one unknown token cost the whole document.
                Origin  = Enum.TryParse<PlacementOrigin>(f.Placement?.Origin, ignoreCase: true, out var placementOrigin)
                              ? placementOrigin : null,
                Units   = Enum.TryParse<LayoutUnit>(f.Placement?.Units, ignoreCase: true, out var placementUnits)
                              ? placementUnits : null,
                Columns = f.Placement?.Columns is { Length: > 0 } c ? [.. c] : null,
            },
            ReferenceNet    = f.ReferenceNet,
            Settings = new RailSettings
            {
                CopperTemperatureCelsius      = f.Settings?.CopperTemperatureCelsius ?? 20.0,
                ViaPlatingThicknessMicrometres = f.Settings?.ViaPlatingThicknessMicrometres,
                ViaTemperatureRiseCelsius      = f.Settings?.ViaTemperatureRiseCelsius
                                              ?? PdnViaCurrentLimit.ReferenceRiseCelsius,
            },
            Panels = new RailPanels
            {
                ShowSpecification = f.Panels?.ShowSpecification ?? true,
                ShowBoard         = f.Panels?.ShowBoard         ?? true,
                ShowParts         = f.Panels?.ShowParts         ?? true,
                ShowResults       = f.Panels?.ShowResults       ?? true,
                ShowResultText    = f.Panels?.ShowResultText    ?? true,
            },
        };

        // A hand-edited file can say every panel is hidden; the window cannot, and a window with no
        // content in it is not a state anything should have to be rescued from. See RailPanels.
        if (!doc.Panels.AnyShown) doc.Panels = new RailPanels();

        foreach (var k in f.HiddenLayers ?? []) doc.HiddenLayers.Add(new LayerKey(k.Layer, k.Datatype));

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
        foreach (var m in r.Markers    ?? []) spec.Markers.Add(FromFile(m));
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
        // The datatype defaults to 0, as ReferenceLayerDatatype's does.
        Layer  = a?.Layer is { } layer ? new LayerKey(layer, a.LayerDatatype ?? 0) : null,
    };

    private static RailMarker FromFile(CrailMarker m) =>
        new(m.Model, m.Port, m.FrequencyHz ?? 0)
        {
            CurveIndex  = m.CurveIndex ?? 0,
            Index       = m.Index ?? 1,
            Name        = m.Name ?? "",
            InfoBoxX    = m.InfoBoxX,
            InfoBoxY    = m.InfoBoxY,
            ShowInfoBox = m.ShowInfoBox ?? true,
        };

    private static RailAggressor FromFile(CrailAggressor a) =>
        new(a.Name ?? "", a.FrequencyHz ?? 0, a.Harmonics ?? 1) { Origin = a.Origin };

    private static RailPart FromFile(CrailPart p) => new()
    {
        Refdes                    = p.Refdes ?? "",
        PartNumber                = p.PartNumber ?? "",
        MountingInductanceHenries = p.MountingInductanceHenries,
        Origin                    = p.Origin,
        Mounted                   = p.Mounted ?? true,

        // brief 25. ABSENT READS AS SHUNT and ABSENT READS AS DOWNSTREAM, which is what makes every
        // .crail written before this unchanged. The terminals read back through the ONE anchor
        // reader, so a series terminal and a load port agree about what "both or neither" means.
        Connection                = p.Connection ?? RailPartConnection.Shunt,
        TerminalA                 = p.TerminalA is null ? null : FromFile(p.TerminalA),
        TerminalB                 = p.TerminalB is null ? null : FromFile(p.TerminalB),
        DcResistanceOhms          = p.DcResistanceOhms,
        SeriesResistanceOhms      = p.SeriesResistanceOhms,
        SeriesInductanceHenries   = p.SeriesInductanceHenries,
        TouchstoneRef             = p.TouchstoneRef,
        Side                      = p.Side ?? RailSection.Downstream,
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

        /// <summary>How to READ that file — see <see cref="RailPlacementReading"/>. Absent on every
        /// document written before it existed and on every board whose placement file declares its
        /// own origin and units, which is what makes leaving it out the right default.</summary>
        public CrailPlacement?  Placement       { get; set; }

        /// <summary>The reference return's net. Absent is not "GND" — see RailDocument.</summary>
        public string?          ReferenceNet    { get; set; }

        public CrailSettings?   Settings       { get; set; }

        /// <summary>Which of the window's four panels are on screen. <b>Absent means all four</b>,
        /// which is every document written before panels existed and every one nobody has
        /// collapsed a panel in.</summary>
        public CrailPanels?     Panels         { get; set; }

        /// <summary>The drawing layers the window is not drawing. <b>Absent means none</b>, which
        /// is every document written before railRF had a layer list of its own.</summary>
        public List<CrailLayerKey>? HiddenLayers { get; set; }

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

    /// <summary>One drawing layer, as the file spells one.</summary>
    private sealed class CrailLayerKey
    {
        public int Layer    { get; set; }
        public int Datatype { get; set; }
    }

    /// <summary>Which panels are on screen. Each nullable for the format's own reason — an absent
    /// key takes the default, and the default here is shown.</summary>
    private sealed class CrailPanels
    {
        public bool? ShowSpecification { get; set; }
        public bool? ShowBoard         { get; set; }
        public bool? ShowParts         { get; set; }
        public bool? ShowResults       { get; set; }
        public bool? ShowResultText    { get; set; }
    }

    /// <summary>
    /// The three answers a placement file cannot be read without and does not always carry.
    /// </summary>
    /// <remarks>
    /// <b>Strings rather than the enums</b>, which is this file's rule everywhere else it stores
    /// one: a token a future build does not know is a reading it cannot honour, and a lenient parse
    /// that drops it leaves the document in the state it was in before anyone answered. A strict
    /// enum converter would make one unknown token cost the whole document.
    /// </remarks>
    private sealed class CrailPlacement
    {
        public string?   Origin  { get; set; }
        public string?   Units   { get; set; }
        public string[]? Columns { get; set; }
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
        public List<CrailMarker>?     Markers                { get; set; }
    }

    /// <summary>One marker on this rail's |Z| curve. <see cref="CrailMarker.InfoBoxX"/> and
    /// <see cref="CrailMarker.InfoBoxY"/> are absent for a marker nobody has dragged — see
    /// <see cref="RailMarker"/> for why that is not written as a NaN.</summary>
    private sealed class CrailMarker
    {
        public PdnModelKind Model       { get; set; } = PdnModelKind.Fast;
        public int          Port        { get; set; }
        public double?      FrequencyHz { get; set; }
        public int?         CurveIndex  { get; set; }
        public int?         Index       { get; set; }
        public string?      Name        { get; set; }
        public double?      InfoBoxX    { get; set; }
        public double?      InfoBoxY    { get; set; }
        public bool?        ShowInfoBox { get; set; }
    }

    private sealed class CrailAnchor
    {
        public string? Refdes { get; set; }
        public string? Pin    { get; set; }
        public long?   PointX { get; set; }
        public long?   PointY { get; set; }

        // R-rail34-2: which copper a coordinate means. Absent reads as it always did.
        public int?    Layer         { get; set; }
        public int?    LayerDatatype { get; set; }
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

        /// <summary>Null — the ordinary case — means MOUNTED. See <see cref="RailPart.Mounted"/>.</summary>
        public bool?           Mounted                   { get; set; }

        /// <summary>Null — the ordinary case — means SHUNT. See <see cref="RailPart.Connection"/>.</summary>
        public RailPartConnection? Connection              { get; set; }

        /// <summary>The series element's two rail-side terminals. Null on a shunt part.</summary>
        public CrailAnchor?    TerminalA                 { get; set; }

        /// <summary>The other one.</summary>
        public CrailAnchor?    TerminalB                 { get; set; }

        /// <summary>OHMS. Null is UNSTATED, never zero — see <see cref="RailPart.DcResistanceOhms"/>.</summary>
        public double?         DcResistanceOhms          { get; set; }

        /// <summary>OHMS, of the R-L model over frequency — a different number from the DCR.</summary>
        public double?         SeriesResistanceOhms      { get; set; }

        /// <summary>HENRIES, of the same model.</summary>
        public double?         SeriesInductanceHenries   { get; set; }

        /// <summary>The element's own measured impedance, relative to the <c>.crail</c>.</summary>
        public string?         TouchstoneRef             { get; set; }

        /// <summary>Null — the ordinary case — means DOWNSTREAM. See <see cref="RailPart.Side"/>.</summary>
        public RailSection?    Side                      { get; set; }
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
