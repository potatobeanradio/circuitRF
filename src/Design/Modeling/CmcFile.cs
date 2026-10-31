// .cmc reader, validator and writer (brief-modelcomposer-3 R-mc3-1) — CmodelFile's shape: a JsonDocument
// walk in which every miss names its field, and a Utf8JsonWriter in a fixed order with \n line ends, so the
// same document writes the same bytes and a document can be diffed and committed.
//
// STRICT ON NAMES. An unknown key anywhere is a refusal naming it: a misspelt condition that was silently
// dropped would leave a data set measured under conditions nobody stated, and the step that needed it would
// report it missing with the typo still sitting in the file. Pads and Network are kept as JSON and carried
// verbatim, but validated through CmcExtrinsic (MC-4), their one reader; Steps is opaque except Steps.Current (MC-7),
// Steps.Charge (MC-8), Steps.Thermal (MC-9), Steps.Traps (MC-10), MC-11's Export, Validate and Report, and
// Steps.Passive (MC-12).

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Core.Expressions;
using CircuitRF.Design.Cells;

namespace CircuitRF.Design.Modeling;

/// <summary>
/// A <c>.cmc</c> the reader or validator refuses. <see cref="Exception.Message"/> begins with the FIELD
/// it is about (<c>DataSets[1].Conditions.Quiescent.Vgs: …</c>), which is what <c>check</c> prints.
/// </summary>
public sealed class CmcFormatException(string field, string sentence) : Exception($"{field}: {sentence}")
{
    public string Field { get; } = field;
}

public static class CmcFile
{
    public const string Extension = ".cmc";

    /// <summary>The quantities a data set's rows carry (R-mc3-2), as an importer's map names them.</summary>
    public static readonly IReadOnlyList<string> PointQuantities = ["vgs", "vds", "ig", "id", "T", "vgsi", "vdsi"];

    /// <summary>A 2-port table as its four elements, one array each shaped <c>[point, freq]</c>: <c>S11</c>…<c>S22</c> or
    /// <c>Y11</c>…<c>Y22</c>, all four together.</summary>
    public static readonly IReadOnlyList<string> ElementQuantities = ["S11", "S12", "S21", "S22", "Y11", "Y12", "Y21", "Y22"];

    /// <summary>The quantities only an array import maps: a frequency axis, the 2-port tables, and the capacitances as the
    /// circuit set (MC-20) — per point <c>cgs</c>, <c>cgd</c>, <c>cds</c> and optionally <c>cm</c>, in farads.</summary>
    public static readonly IReadOnlyList<string> TableQuantities =
        ["freq", "S", "Y", "C", "cgs", "cgd", "cds", "cm", .. ElementQuantities];

    /// <summary>The circuit set's quantities, the first three of which come together (R-mc20-1).</summary>
    public static readonly IReadOnlyList<string> CircuitSetQuantities = ["cgs", "cgd", "cds", "cm"];

    /// <summary>Why a capacitance table is refused away from the intrinsic plane (R-mc20-2).</summary>
    public const string CapacitancePlaneSentence =
        "a capacitance table is the core's own and is read at the intrinsic plane only: capacitances cannot be de-embedded " +
        "through an extrinsic network without their frequency response, so export the core's capacitances, or Y.";

    /// <summary>The fields a Touchstone bias pattern may name.</summary>
    /// <summary>The fields a bias pattern may name; <c>{geometry}</c> is a passive block's geometry value (MC-12).</summary>
    public static readonly IReadOnlyList<string> PatternFields = ["vgs", "vds", "T", "id", "ig", "geometry"];

    /// <summary>A pre-pulse dissipating at least this (W) is warned about — its heating is not modelled.</summary>
    public const double DissipatingPrePulseW = 1e-3;

    // ── Reading ───────────────────────────────────────────────────────────────

    public static CmcDocument Read(string path) => Parse(File.ReadAllText(path));

    /// <summary>Reads and validates <paramref name="json"/>.</summary>
    public static CmcDocument Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new CmcFormatException("document", $"it is not JSON ({ex.Message})."); }
        CmcDocument cmc;
        using (doc) cmc = ParseRoot(doc.RootElement);
        Validate(cmc);
        return cmc;
    }

    /// <summary>
    /// One data set, read from its JSON as the document holds it under <c>DataSets</c> — how an import that is not a whole
    /// document (the window's importer, the <c>model import</c> verb) gets the reader's own field checks. Whether it fits
    /// the rest of a document is <see cref="Validate"/>'s question.
    /// </summary>
    public static CmcDataSet ParseDataSet(string json, string field = "DataSet")
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new CmcFormatException(field, $"it is not JSON ({ex.Message})."); }
        using (doc) return ParseDataSet(doc.RootElement, field);
    }

    private static readonly string[] RootKeys =
        ["FormatVersion", "Kind", "BasePeriphery", "DataSets", "Pads", "Network", "Steps", "Panels", "View"];

    private static CmcDocument ParseRoot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new CmcFormatException("document", "the top level is not a JSON object.");

        int version = Int(root, "FormatVersion", "FormatVersion");
        if (version > CmcDocument.CurrentFormatVersion)
            throw new CmcFormatException("FormatVersion",
                $"this file is format {version}, written by a newer circuitRF; this one reads up to format {CmcDocument.CurrentFormatVersion}.");
        if (version < 1) throw new CmcFormatException("FormatVersion", $"{version} is not a format version.");
        Only(root, "", RootKeys);

        var kind = Enum<CmcKind>(root, "Kind", "Kind");

        BasePeriphery? bp = null;
        if (Has(root, "BasePeriphery", out var bpe))
        {
            Only(bpe, "BasePeriphery", ["Fingers", "UnitWidth"]);
            bp = new BasePeriphery(Int(bpe, "Fingers", "BasePeriphery.Fingers"), Str(bpe, "UnitWidth", "BasePeriphery.UnitWidth"));
        }

        var sets = new List<CmcDataSet>();
        if (Has(root, "DataSets", out var dse))
        {
            if (dse.ValueKind != JsonValueKind.Array) throw new CmcFormatException("DataSets", "it is not a list.");
            int i = 0;
            foreach (var e in dse.EnumerateArray()) sets.Add(ParseDataSet(e, $"DataSets[{i++}]"));
        }

        var panels = new CmcPanels();
        if (Has(root, "Panels", out var pe))
        {
            Only(pe, "Panels", ["Hidden"]);
            panels = new CmcPanels { Hidden = Has(pe, "Hidden", out _) ? Strings(pe, "Hidden", "Panels.Hidden") : [] };
        }

        var view = new CmcView();
        if (Has(root, "View", out var ve))
        {
            Only(ve, "View", ["Step", "Cursor"]);
            CmcBiasCursor? cursor = null;
            if (Has(ve, "Cursor", out var ce))
            {
                Only(ce, "View.Cursor", ["Vgs", "Vds", "AmbientC", "Quiescent"]);
                QuiescentBias? q = null;
                if (Has(ce, "Quiescent", out var qe))
                {
                    Only(qe, "View.Cursor.Quiescent", ["Vgs", "Vds"]);
                    q = new QuiescentBias(Num(qe, "Vgs", "View.Cursor.Quiescent.Vgs"), Num(qe, "Vds", "View.Cursor.Quiescent.Vds"));
                }
                cursor = new CmcBiasCursor(OptNum(ce, "Vgs", "View.Cursor.Vgs"), OptNum(ce, "Vds", "View.Cursor.Vds"),
                                           OptNum(ce, "AmbientC", "View.Cursor.AmbientC"), q);
            }
            view = new CmcView { Step = Has(ve, "Step", out _) ? Str(ve, "Step", "View.Step") : null, Cursor = cursor };
        }

        return new CmcDocument
        {
            FormatVersion = version,
            Kind          = kind,
            BasePeriphery = bp,
            DataSets      = sets,
            Pads          = Opaque(root, "Pads"),
            Network       = Opaque(root, "Network"),
            Steps         = Opaque(root, "Steps"),
            Panels        = panels,
            View          = view,
        };
    }

    private static JsonNode? Opaque(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null ? JsonNode.Parse(v.GetRawText()) : null;

    private static CmcDataSet ParseDataSet(JsonElement e, string f)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new CmcFormatException(f, "it is not an object.");
        Only(e, f, ["Name", "Kind", "Sources", "Conditions", "Import", "Exclusions"]);

        var conditions = DataConditions.None;
        if (Has(e, "Conditions", out var c)) conditions = ParseConditions(c, $"{f}.Conditions");

        var exclusions = new List<PointExclusion>();
        if (Has(e, "Exclusions", out var xe))
        {
            if (xe.ValueKind != JsonValueKind.Array) throw new CmcFormatException($"{f}.Exclusions", "it is not a list.");
            int k = 0;
            foreach (var x in xe.EnumerateArray())
            {
                string xf = $"{f}.Exclusions[{k++}]";
                if (x.ValueKind != JsonValueKind.Object) throw new CmcFormatException(xf, "it is not an object.");
                Only(x, xf, ["Point", "Reason"]);
                exclusions.Add(new PointExclusion(Str(x, "Point", $"{xf}.Point"), Has(x, "Reason", out _) ? Str(x, "Reason", $"{xf}.Reason") : ""));
            }
        }

        return new CmcDataSet
        {
            Name       = Str(e, "Name", $"{f}.Name"),
            Kind       = Enum<DataSetKind>(e, "Kind", $"{f}.Kind"),
            Sources    = Strings(e, "Sources", $"{f}.Sources"),
            Conditions = conditions,
            Import     = ParseImport(Obj(e, "Import", $"{f}.Import"), $"{f}.Import"),
            Exclusions = exclusions,
        };
    }

    private static DataConditions ParseConditions(JsonElement c, string f)
    {
        if (c.ValueKind != JsonValueKind.Object) throw new CmcFormatException(f, "it is not an object.");
        Only(c, f, ["AmbientC", "ReferencePlane", "BiasPathOhms", "PulseWidth", "Period", "Quiescent", "SampleDelay",
                    "TrapState", "PrePulse", "SoakTime", "Compliance", "LargeSignal", "Note"]);

        BiasPathOhms? bias = null;
        if (Has(c, "BiasPathOhms", out var b))
        {
            string bf = $"{f}.BiasPathOhms";
            Only(b, bf, ["Gate", "Drain", "Source"]);
            bias = new BiasPathOhms(Num(b, "Gate", $"{bf}.Gate"), Num(b, "Drain", $"{bf}.Drain"), OptNum(b, "Source", $"{bf}.Source") ?? 0);
        }
        QuiescentBias? q = null;
        if (Has(c, "Quiescent", out var qe))
        {
            string qf = $"{f}.Quiescent";
            Only(qe, qf, ["Vgs", "Vds", "Ig", "Id"]);
            q = new QuiescentBias(Num(qe, "Vgs", $"{qf}.Vgs"), Num(qe, "Vds", $"{qf}.Vds"), OptNum(qe, "Ig", $"{qf}.Ig"), OptNum(qe, "Id", $"{qf}.Id"));
        }
        PrePulse? pp = null;
        if (Has(c, "PrePulse", out var pe))
        {
            string pf = $"{f}.PrePulse";
            Only(pe, pf, ["Vgs", "Vds", "Width", "Gap", "Id"]);
            pp = new PrePulse(Num(pe, "Vgs", $"{pf}.Vgs"), Num(pe, "Vds", $"{pf}.Vds"), Num(pe, "Width", $"{pf}.Width"),
                              Num(pe, "Gap", $"{pf}.Gap"), OptNum(pe, "Id", $"{pf}.Id"));
        }
        ComplianceLimits? limits = null;
        if (Has(c, "Compliance", out var le))
        {
            string lf = $"{f}.Compliance";
            Only(le, lf, ["Gate", "Drain"]);
            limits = new ComplianceLimits(OptNum(le, "Gate", $"{lf}.Gate"), OptNum(le, "Drain", $"{lf}.Drain"));
        }

        LargeSignalConditions? large = null;
        if (Has(c, "LargeSignal", out var lse)) large = ParseLargeSignal(lse, $"{f}.LargeSignal");

        return new DataConditions
        {
            LargeSignal    = large,
            AmbientC       = OptNum(c, "AmbientC", $"{f}.AmbientC"),
            ReferencePlane = Has(c, "ReferencePlane", out _) ? Enum<ReferencePlane>(c, "ReferencePlane", $"{f}.ReferencePlane") : null,
            BiasPathOhms   = bias,
            PulseWidth     = OptNum(c, "PulseWidth", $"{f}.PulseWidth"),
            Period         = OptNum(c, "Period", $"{f}.Period"),
            Quiescent      = q,
            SampleDelay    = OptNum(c, "SampleDelay", $"{f}.SampleDelay"),
            TrapState      = Has(c, "TrapState", out _) ? Enum<TrapStateMode>(c, "TrapState", $"{f}.TrapState") : null,
            PrePulse       = pp,
            SoakTime       = OptNum(c, "SoakTime", $"{f}.SoakTime"),
            Compliance     = limits,
            Note           = Has(c, "Note", out _) ? Str(c, "Note", $"{f}.Note") : null,
        };
    }

    private static LargeSignalConditions ParseLargeSignal(JsonElement e, string f)
    {
        if (e.ValueKind != JsonValueKind.Object) throw new CmcFormatException(f, "it is not an object.");
        Only(e, f, ["Measurement", "FrequencyHz", "SpacingHz", "Vgs", "Vds", "SourceZ", "LoadZ", "SourceHarmonics", "LoadHarmonics"]);
        Complex? Z(string name) => Has(e, name, out var z) ? ComplexOf(z, $"{f}.{name}") : null;
        IReadOnlyList<Complex> Zs(string name)
        {
            if (!Has(e, name, out var a)) return [];
            if (a.ValueKind != JsonValueKind.Array) throw new CmcFormatException($"{f}.{name}", "it is not a list of [re, im] pairs.");
            return [.. a.EnumerateArray().Select((x, i) => ComplexOf(x, $"{f}.{name}[{i}]"))];
        }
        return new LargeSignalConditions
        {
            Measurement     = Enum<LargeSignalMeasurement>(e, "Measurement", $"{f}.Measurement"),
            FrequencyHz     = OptNum(e, "FrequencyHz", $"{f}.FrequencyHz"),
            SpacingHz       = OptNum(e, "SpacingHz", $"{f}.SpacingHz"),
            Vgs             = OptNum(e, "Vgs", $"{f}.Vgs"),
            Vds             = OptNum(e, "Vds", $"{f}.Vds"),
            SourceZ         = Z("SourceZ"),
            LoadZ           = Z("LoadZ"),
            SourceHarmonics = Zs("SourceHarmonics"),
            LoadHarmonics   = Zs("LoadHarmonics"),
        };
    }

    private static Complex ComplexOf(JsonElement z, string f)
    {
        if (z.ValueKind != JsonValueKind.Array || z.GetArrayLength() != 2 || z.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Number))
            throw new CmcFormatException(f, "it is not an impedance written [re, im], in ohms.");
        return new Complex(z[0].GetDouble(), z[1].GetDouble());
    }

    private static ImportSettings ParseImport(JsonElement e, string f)
    {
        Only(e, f, ["Format", "Pattern", "PatternSource", "Columns", "Header", "Delimiter", "Arrays", "ArrayUnits", "FrequencyHz", "Z0"]);

        List<ColumnMap>? columns = null;
        if (Has(e, "Columns", out var ce))
        {
            if (ce.ValueKind != JsonValueKind.Array) throw new CmcFormatException($"{f}.Columns", "it is not a list.");
            columns = [];
            int k = 0;
            foreach (var col in ce.EnumerateArray())
            {
                string cf = $"{f}.Columns[{k++}]";
                if (col.ValueKind != JsonValueKind.Object) throw new CmcFormatException(cf, "it is not an object.");
                Only(col, cf, ["Column", "Quantity", "Unit"]);
                columns.Add(new ColumnMap(Str(col, "Column", $"{cf}.Column"), Str(col, "Quantity", $"{cf}.Quantity"),
                                          Has(col, "Unit", out _) ? Str(col, "Unit", $"{cf}.Unit") : null));
            }
        }
        SortedDictionary<string, string>? arrays = null;
        if (Has(e, "Arrays", out var ae))
        {
            if (ae.ValueKind != JsonValueKind.Object) throw new CmcFormatException($"{f}.Arrays", "it is not an object of quantity → array name.");
            arrays = new(StringComparer.Ordinal);
            foreach (var p in ae.EnumerateObject())
                arrays[p.Name] = Str(ae, p.Name, $"{f}.Arrays.{p.Name}");
        }
        SortedDictionary<string, string>? arrayUnits = null;
        if (Has(e, "ArrayUnits", out var ue))
        {
            if (ue.ValueKind != JsonValueKind.Object) throw new CmcFormatException($"{f}.ArrayUnits", "it is not an object of quantity → unit.");
            arrayUnits = new(StringComparer.Ordinal);
            foreach (var p in ue.EnumerateObject())
                arrayUnits[p.Name] = Str(ue, p.Name, $"{f}.ArrayUnits.{p.Name}");
        }
        bool? header = null;
        if (Has(e, "Header", out var he))
        {
            if (he.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new CmcFormatException($"{f}.Header", "it is not true or false.");
            header = he.GetBoolean();
        }

        return new ImportSettings
        {
            Format        = Str(e, "Format", $"{f}.Format"),
            Pattern       = Has(e, "Pattern", out _) ? Str(e, "Pattern", $"{f}.Pattern") : null,
            PatternSource = Has(e, "PatternSource", out _) ? Str(e, "PatternSource", $"{f}.PatternSource") : null,
            Columns       = columns,
            Header        = header,
            Delimiter     = Has(e, "Delimiter", out _) ? Str(e, "Delimiter", $"{f}.Delimiter") : null,
            Arrays        = arrays,
            ArrayUnits    = arrayUnits,
            FrequencyHz   = OptNum(e, "FrequencyHz", $"{f}.FrequencyHz"),
            Z0            = OptNum(e, "Z0", $"{f}.Z0"),
        };
    }

    // ── Validation ────────────────────────────────────────────────────────────

    /// <summary>Refuses a document that is not well formed, naming the field. Called by the reader and the writer.</summary>
    public static void Validate(CmcDocument d)
    {
        if (d.BasePeriphery is { } bp)
        {
            if (d.Kind == CmcKind.Passive)
                throw new CmcFormatException("BasePeriphery", "a Passive document has no base periphery.");
            if (bp.Fingers < 1)
                throw new CmcFormatException("BasePeriphery.Fingers", $"{bp.Fingers} is not a finger count of 1 or more.");
            if (CmodelFile.UnitWidthMetres(bp.UnitWidth) is not > 0)
                throw new CmcFormatException("BasePeriphery.UnitWidth", $"'{bp.UnitWidth}' is not a positive width with its unit (\"125 um\").");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < d.DataSets.Count; i++)
        {
            var s = d.DataSets[i];
            string f = $"DataSets[{i}]";
            if (string.IsNullOrWhiteSpace(s.Name)) throw new CmcFormatException($"{f}.Name", "it is empty.");
            if (!names.Add(s.Name)) throw new CmcFormatException($"{f}.Name", $"'{s.Name}' names an earlier data set too.");
            if (d.Kind == CmcKind.Passive && s.Kind != DataSetKind.PassiveSParams)
                throw new CmcFormatException($"{f}.Kind", $"a Passive document takes PassiveSParams data sets, not {s.Kind}.");
            if (d.Kind == CmcKind.Active && s.Kind == DataSetKind.PassiveSParams)
                throw new CmcFormatException($"{f}.Kind", "an Active document does not take PassiveSParams data sets.");
            if (s.Sources.Count == 0) throw new CmcFormatException($"{f}.Sources", "it names no source.");
            for (int k = 0; k < s.Sources.Count; k++)
            {
                string src = s.Sources[k];
                if (string.IsNullOrWhiteSpace(src)) throw new CmcFormatException($"{f}.Sources[{k}]", "it is empty.");
                if (Path.IsPathRooted(src) || src.StartsWith('/') || src.StartsWith('\\'))
                    throw new CmcFormatException($"{f}.Sources[{k}]", $"'{src}' is not relative to the .cmc.");
            }
            ValidateConditions(s, $"{f}.Conditions");
            ValidateImport(s, $"{f}.Import");
            ValidateCapacitances(s, f);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int k = 0; k < s.Exclusions.Count; k++)
            {
                string p = s.Exclusions[k].Point;
                if (string.IsNullOrWhiteSpace(p)) throw new CmcFormatException($"{f}.Exclusions[{k}].Point", "it is empty.");
                if (!seen.Add(p)) throw new CmcFormatException($"{f}.Exclusions[{k}].Point", $"'{p}' is excluded twice.");
            }
        }

        // MC-4's blocks: CmcExtrinsic is their one reader, and a parse is their validation.
        CmcExtrinsic.Pads(d);
        CmcExtrinsic.Network(d);
        // MC-7's step: its settings and record are parsed, so a misspelt key is refused here too.
        CurrentFit.Validate(d);
        ChargeFit.Validate(d);
        ThermalFit.Validate(d);
        TrapFit.Validate(d);
        CmodelExport.Validate(d);
        ModelValidation.Validate(d);
        ModelValidationReport.Validate(d);
        Passive.PassiveStep.Validate(d);
    }

    private static bool IsPulsed(DataSetKind k) => k is DataSetKind.PulsedIv or DataSetKind.PulsedSParams;

    private static void ValidateConditions(CmcDataSet s, string f)
    {
        var c = s.Conditions;
        if (c.BiasPathOhms is { } b)
        {
            if (c.ReferencePlane != ReferencePlane.Instrument)
                throw new CmcFormatException($"{f}.BiasPathOhms", "it applies only to the instrument reference plane.");
            if (b.Gate < 0 || b.Drain < 0 || b.Source < 0)
                throw new CmcFormatException($"{f}.BiasPathOhms", "a bias-path resistance is negative.");
        }
        if (c.LargeSignal is not null && s.Kind != DataSetKind.LargeSignal)
            throw new CmcFormatException($"{f}.LargeSignal", $"it applies only to a LargeSignal data set, and this one is {s.Kind}.");
        if (s.Kind == DataSetKind.LargeSignal) ValidateLargeSignal(s, $"{f}.LargeSignal");
        if (!IsPulsed(s.Kind))
        {
            foreach (var (set, name) in new[] { (c.PulseWidth is not null, "PulseWidth"), (c.Period is not null, "Period"),
                                                (c.Quiescent is not null, "Quiescent"), (c.SampleDelay is not null, "SampleDelay"),
                                                (c.TrapState is not null, "TrapState"), (c.PrePulse is not null, "PrePulse") })
                if (set) throw new CmcFormatException($"{f}.{name}", $"it applies only to a pulsed data set, and this one is {s.Kind}.");
        }
        Positive(c.PulseWidth, $"{f}.PulseWidth");
        Positive(c.Period, $"{f}.Period");
        Positive(c.SoakTime, $"{f}.SoakTime");
        if (c.SampleDelay is < 0) throw new CmcFormatException($"{f}.SampleDelay", "it is negative.");
        if (c.PulseWidth is { } w && c.Period is { } t && t <= w)
            throw new CmcFormatException($"{f}.Period", $"{G(t)} s is not longer than the pulse width {G(w)} s.");
        if (c.PulseWidth is { } w2 && c.SampleDelay is { } sd && sd > w2)
            throw new CmcFormatException($"{f}.SampleDelay", $"{G(sd)} s is after the pulse ends at {G(w2)} s.");
        if (c.PrePulse is { } pp)
        {
            Positive(pp.Width, $"{f}.PrePulse.Width");
            if (pp.Gap < 0) throw new CmcFormatException($"{f}.PrePulse.Gap", "it is negative.");
        }
        if (c.Compliance is { } l)
        {
            Positive(l.Gate, $"{f}.Compliance.Gate");
            Positive(l.Drain, $"{f}.Compliance.Drain");
        }
    }

    /// <summary>
    /// A set's capacitances (R-mc20-1, R-mc20-2): the matrix <c>C</c> or the circuit set, never both; <c>cgs</c>, <c>cgd</c> and
    /// <c>cds</c> together; a SimulatedTables set's; and at the intrinsic plane only.
    /// </summary>
    private static void ValidateCapacitances(CmcDataSet s, string f)
    {
        if (s.Import.Arrays is not { } map) return;
        bool matrix = map.ContainsKey("C");
        var circuit = CircuitSetQuantities.Where(map.ContainsKey).ToList();
        if (!matrix && circuit.Count == 0) return;
        if (matrix && circuit.Count > 0)
            throw new CmcFormatException($"{f}.Import.Arrays", "map the capacitance matrix C or the circuit set (cgs, cgd, cds), not both.");
        var missing = CircuitSetQuantities.Take(3).Where(q => !map.ContainsKey(q)).ToList();
        if (circuit.Count > 0 && missing.Count > 0)
            throw new CmcFormatException($"{f}.Import.Arrays", $"cgs, cgd and cds come together; {string.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} not mapped.");
        if (s.Kind != DataSetKind.SimulatedTables)
            throw new CmcFormatException($"{f}.Import.Arrays", $"capacitances belong to a SimulatedTables data set, and this one is {s.Kind}.");
        if (s.Conditions.ReferencePlane != ReferencePlane.Intrinsic)
            throw new CmcFormatException($"{f}.Conditions.ReferencePlane", CapacitancePlaneSentence);
    }

    /// <summary>The quantities a large-signal CSV maps (MC-11): powers in dBm, gain in dB, efficiencies in %, currents in A.</summary>
    public static readonly IReadOnlyList<string> LargeSignalQuantities = ["pin", "pout", "gain", "pae", "de", "id", "ig", "im3", "im5"];

    /// <summary>The one unit a large-signal quantity is read in (dBm, dB or %), or null for a current, which takes any.</summary>
    public static string? LargeSignalUnit(string quantity) => quantity switch
    {
        "pin" or "pout" or "im3" or "im5" => "dBm",
        "gain" => "dB",
        "pae" or "de" => "%",
        _ => null,
    };

    private static void ValidateLargeSignal(CmcDataSet s, string f)
    {
        if (s.Conditions.LargeSignal is not { } ls)
            throw new CmcFormatException(f, "a LargeSignal data set states its measurement (PowerSweep, TwoTone or Loadpull) here.");
        bool file = ls.Measurement == LargeSignalMeasurement.Loadpull;
        if (file != (s.Import.Format == ImportSettings.Loadpull))
            throw new CmcFormatException($"{f}.Measurement",
                file ? "a Loadpull set is read from a loadpull file; its import format is loadpull."
                     : $"a {ls.Measurement} set is read from a CSV/TSV file of its columns.");
        if (!file)
        {
            if (ls.FrequencyHz is null) throw new CmcFormatException($"{f}.FrequencyHz", "it is missing; a power sweep is at one frequency.");
            if (ls.Vgs is null || ls.Vds is null) throw new CmcFormatException(f, "the bias, Vgs and Vds, is missing.");
        }
        if (ls.Measurement == LargeSignalMeasurement.TwoTone && ls.SpacingHz is null)
            throw new CmcFormatException($"{f}.SpacingHz", "two tones need their spacing.");
        Positive(ls.FrequencyHz, $"{f}.FrequencyHz");
        Positive(ls.SpacingHz, $"{f}.SpacingHz");
        foreach (var (z, name) in new[] { (ls.SourceZ, "SourceZ"), (ls.LoadZ, "LoadZ") })
            if (z is { Real: < 0 }) throw new CmcFormatException($"{f}.{name}", "its resistance is negative.");
    }

    private static void ValidateImport(CmcDataSet s, string f)
    {
        var im = s.Import;
        if (s.Kind == DataSetKind.LargeSignal)
        {
            if (im.Format == ImportSettings.Loadpull) return;
            if (im.Format != ImportSettings.Csv) throw new CmcFormatException($"{f}.Format", "a LargeSignal set is read from csv or a loadpull file.");
            if (im.Columns is not { Count: > 0 }) throw new CmcFormatException($"{f}.Columns", "a CSV/TSV import needs a column map.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int k = 0; k < im.Columns.Count; k++)
            {
                var col = im.Columns[k];
                if (!LargeSignalQuantities.Contains(col.Quantity))
                    throw new CmcFormatException($"{f}.Columns[{k}].Quantity", $"'{col.Quantity}' is not one of {string.Join(", ", LargeSignalQuantities)}.");
                if (!seen.Add(col.Quantity)) throw new CmcFormatException($"{f}.Columns[{k}].Quantity", $"'{col.Quantity}' is mapped twice.");
                if (col.Unit is { } u)
                {
                    string? fixedUnit = LargeSignalUnit(col.Quantity);
                    if (fixedUnit is not null ? u != fixedUnit : UnitScale(u) is null)
                        throw new CmcFormatException($"{f}.Columns[{k}].Unit", fixedUnit is not null
                            ? $"{col.Quantity} is read in {fixedUnit}; '{u}' is not that." : $"'{u}' is not a unit circuitRF knows.");
                }
            }
            if (!seen.Contains("pin")) throw new CmcFormatException($"{f}.Columns", "the map needs a pin column.");
            return;
        }
        switch (im.Format)
        {
            case ImportSettings.Touchstone:
                if (s.Kind is DataSetKind.DcIv or DataSetKind.PulsedIv)
                    throw new CmcFormatException($"{f}.Format", $"a {s.Kind} data set is not read from Touchstone files.");
                // A passive block has no bias; its pattern is optional, and gives a family's {geometry} (MC-12).
                if (s.Kind == DataSetKind.PassiveSParams && im.Pattern is null) break;
                if (im.PatternSource is not ("name" or "comment"))
                    throw new CmcFormatException($"{f}.PatternSource", $"'{im.PatternSource}' is not name or comment.");
                if (string.IsNullOrEmpty(im.Pattern)) throw new CmcFormatException($"{f}.Pattern", "a Touchstone set needs a bias pattern.");
                var pattern = BiasPattern.Parse(im.Pattern, $"{f}.Pattern");
                if (s.Kind != DataSetKind.PassiveSParams && !(pattern.Scales.ContainsKey("vgs") && pattern.Scales.ContainsKey("vds")))
                    throw new CmcFormatException($"{f}.Pattern", "the pattern needs {vgs} and {vds}.");
                if (s.Kind != DataSetKind.PassiveSParams && pattern.Scales.ContainsKey("geometry"))
                    throw new CmcFormatException($"{f}.Pattern", "{geometry} belongs to a passive data set.");
                break;
            case ImportSettings.Csv:
                if (im.Columns is not { Count: > 0 }) throw new CmcFormatException($"{f}.Columns", "a CSV/TSV import needs a column map.");
                var q = new HashSet<string>(StringComparer.Ordinal);
                for (int k = 0; k < im.Columns.Count; k++)
                {
                    var col = im.Columns[k];
                    string cf = $"{f}.Columns[{k}]";
                    if (!PointQuantities.Contains(col.Quantity))
                        throw new CmcFormatException($"{cf}.Quantity", $"'{col.Quantity}' is not one of {string.Join(", ", PointQuantities)}.");
                    if (!q.Add(col.Quantity)) throw new CmcFormatException($"{cf}.Quantity", $"'{col.Quantity}' is mapped twice.");
                    if (col.Unit is { } u && UnitScale(u) is null)
                        throw new CmcFormatException($"{cf}.Unit", $"'{u}' is not a unit circuitRF knows.");
                }
                if (!q.Contains("vgs") || !q.Contains("vds"))
                    throw new CmcFormatException($"{f}.Columns", "the map needs a vgs and a vds column.");
                break;
            case ImportSettings.Npy:
            case ImportSettings.Mat:
            case ImportSettings.Npz:
                if (im.Arrays is not { Count: > 0 }) throw new CmcFormatException($"{f}.Arrays", "an array import needs its quantity → array map.");
                foreach (var key in im.Arrays.Keys)
                    if (!PointQuantities.Contains(key) && !TableQuantities.Contains(key))
                        throw new CmcFormatException($"{f}.Arrays.{key}", $"'{key}' is not one of {string.Join(", ", PointQuantities.Concat(TableQuantities))}.");
                if (!im.Arrays.ContainsKey("vgs") || !im.Arrays.ContainsKey("vds"))
                    throw new CmcFormatException($"{f}.Arrays", "the map needs a vgs and a vds array.");
                var tables = new[] { "S", "Y" }.Where(t => im.Arrays.ContainsKey(t) || im.Arrays.Keys.Any(k => k.Length == 3 && k[0] == t[0] && ElementQuantities.Contains(k))).ToList();
                if (tables.Count > 1) throw new CmcFormatException($"{f}.Arrays", "map S or Y, not both.");
                foreach (string t in tables)
                {
                    string[] elements = [$"{t}11", $"{t}12", $"{t}21", $"{t}22"];
                    int given = elements.Count(im.Arrays.ContainsKey);
                    if (given > 0 && im.Arrays.ContainsKey(t))
                        throw new CmcFormatException($"{f}.Arrays", $"map the {t} table as one array or as {string.Join(", ", elements)}, not both.");
                    if (given is > 0 and < 4)
                        throw new CmcFormatException($"{f}.Arrays", $"{string.Join(", ", elements.Where(x => !im.Arrays.ContainsKey(x)))} missing: the elements come as all four.");
                }
                foreach (var (key, unit) in im.ArrayUnits ?? new Dictionary<string, string>())
                {
                    if (!im.Arrays.ContainsKey(key))
                        throw new CmcFormatException($"{f}.ArrayUnits.{key}", $"'{key}' has a unit but no array.");
                    if (key == "S" || key.StartsWith('S') && ElementQuantities.Contains(key))
                        throw new CmcFormatException($"{f}.ArrayUnits.{key}", "S has no unit.");
                    if (key == "T" ? unit.Trim() is not ("C" or "°C" or "degC" or "K") : UnitScale(unit) is null)
                        throw new CmcFormatException($"{f}.ArrayUnits.{key}", key == "T"
                            ? $"T is in °C (C, degC) or K; '{unit}' is neither." : $"'{unit}' is not a unit circuitRF knows.");
                }
                break;
            case ImportSettings.Loadpull:
                throw new CmcFormatException($"{f}.Format", $"a loadpull file holds large-signal data; a {s.Kind} set is not read from one.");
            default:
                throw new CmcFormatException($"{f}.Format", $"'{im.Format}' is not touchstone, csv, npy, mat, npz or loadpull.");
        }
        Positive(im.FrequencyHz, $"{f}.FrequencyHz");
        Positive(im.Z0, $"{f}.Z0");
    }

    /// <summary>
    /// What a well-formed document still deserves a word about — one line each, naming the field. Today: a
    /// pre-pulse whose stated current dissipates, because its heating is not modelled (R-mc3-2).
    /// </summary>
    public static IReadOnlyList<string> Warnings(CmcDocument d)
    {
        var list = new List<string>();
        for (int i = 0; i < d.DataSets.Count; i++)
            if (d.DataSets[i].Conditions.PrePulse is { Id: { } id } pp && Math.Abs(id * pp.Vds) >= DissipatingPrePulseW)
                list.Add($"DataSets[{i}].Conditions.PrePulse.Id: the pre-pulse dissipates {G(Math.Abs(id * pp.Vds))} W; its heating is not modelled.");
        return list;
    }

    /// <summary>The scale of a unit as a CSV map or a pattern states it (<c>mA</c>, <c>µA</c>, <c>V</c>), or null.</summary>
    public static double? UnitScale(string unit)
    {
        string u = UnitNormalizer.ToEngineUnit(unit.Trim());
        if (u is "C" or "°C" or "degC") return 1;
        return Units.IsRecognizedUnit(u) ? Units.SuffixScale(u) : null;
    }

    private static void Positive(double? v, string f)
    {
        if (v is { } x && !(x > 0)) throw new CmcFormatException(f, $"{G(x)} is not positive.");
    }

    private static string G(double v) => v.ToString("G6", CultureInfo.InvariantCulture);

    // ── Writing ───────────────────────────────────────────────────────────────

    /// <summary>Validates and writes <paramref name="d"/> atomically.</summary>
    public static void Write(string path, CmcDocument d) => AtomicFile.WriteAllText(path, Encoding.UTF8.GetString(ToBytes(d)));

    /// <summary>The file's bytes, in the fixed order. Validated on the way out: a document that cannot be read back was never written.</summary>
    public static byte[] ToBytes(CmcDocument d)
    {
        Validate(d);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, NewLine = "\n" }))
        {
            w.WriteStartObject();
            w.WriteNumber("FormatVersion", d.FormatVersion);
            w.WriteString("Kind", d.Kind.ToString());
            if (d.BasePeriphery is { } bp)
            {
                w.WriteStartObject("BasePeriphery");
                w.WriteNumber("Fingers", bp.Fingers);
                w.WriteString("UnitWidth", bp.UnitWidth);
                w.WriteEndObject();
            }
            w.WriteStartArray("DataSets");
            foreach (var s in d.DataSets) WriteDataSet(w, s);
            w.WriteEndArray();
            WriteOpaque(w, "Pads", d.Pads);
            WriteOpaque(w, "Network", d.Network);
            WriteOpaque(w, "Steps", d.Steps);
            if (!d.Panels.IsDefault)
            {
                w.WriteStartObject("Panels");
                w.WriteStartArray("Hidden");
                foreach (string h in d.Panels.Hidden.Order(StringComparer.Ordinal)) w.WriteStringValue(h);
                w.WriteEndArray();
                w.WriteEndObject();
            }
            if (!d.View.IsDefault)
            {
                w.WriteStartObject("View");
                if (d.View.Step is { } step) w.WriteString("Step", step);
                if (d.View.Cursor is { } c)
                {
                    w.WriteStartObject("Cursor");
                    Opt(w, "Vgs", c.Vgs);
                    Opt(w, "Vds", c.Vds);
                    Opt(w, "AmbientC", c.AmbientC);
                    if (c.Quiescent is { } q)
                    {
                        w.WriteStartObject("Quiescent");
                        w.WriteNumber("Vgs", q.Vgs);
                        w.WriteNumber("Vds", q.Vds);
                        w.WriteEndObject();
                    }
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    private static void WriteOpaque(Utf8JsonWriter w, string name, JsonNode? node)
    {
        if (node is null) return;
        w.WritePropertyName(name);
        node.WriteTo(w);
    }

    private static void WriteDataSet(Utf8JsonWriter w, CmcDataSet s)
    {
        w.WriteStartObject();
        w.WriteString("Name", s.Name);
        w.WriteString("Kind", s.Kind.ToString());
        w.WriteStartArray("Sources");
        foreach (string src in s.Sources) w.WriteStringValue(src);
        w.WriteEndArray();

        var c = s.Conditions;
        if (c != DataConditions.None)
        {
            w.WriteStartObject("Conditions");
            Opt(w, "AmbientC", c.AmbientC);
            if (c.ReferencePlane is { } rp) w.WriteString("ReferencePlane", rp.ToString().ToLowerInvariant());
            if (c.BiasPathOhms is { } b)
            {
                w.WriteStartObject("BiasPathOhms");
                w.WriteNumber("Gate", b.Gate);
                w.WriteNumber("Drain", b.Drain);
                if (b.Source != 0) w.WriteNumber("Source", b.Source);
                w.WriteEndObject();
            }
            Opt(w, "PulseWidth", c.PulseWidth);
            Opt(w, "Period", c.Period);
            if (c.Quiescent is { } q)
            {
                w.WriteStartObject("Quiescent");
                w.WriteNumber("Vgs", q.Vgs);
                w.WriteNumber("Vds", q.Vds);
                Opt(w, "Ig", q.Ig);
                Opt(w, "Id", q.Id);
                w.WriteEndObject();
            }
            Opt(w, "SampleDelay", c.SampleDelay);
            if (c.TrapState is { } ts) w.WriteString("TrapState", ts.ToString().ToLowerInvariant());
            if (c.PrePulse is { } pp)
            {
                w.WriteStartObject("PrePulse");
                w.WriteNumber("Vgs", pp.Vgs);
                w.WriteNumber("Vds", pp.Vds);
                w.WriteNumber("Width", pp.Width);
                w.WriteNumber("Gap", pp.Gap);
                Opt(w, "Id", pp.Id);
                w.WriteEndObject();
            }
            Opt(w, "SoakTime", c.SoakTime);
            if (c.Compliance is { } l)
            {
                w.WriteStartObject("Compliance");
                Opt(w, "Gate", l.Gate);
                Opt(w, "Drain", l.Drain);
                w.WriteEndObject();
            }
            if (c.LargeSignal is { } ls)
            {
                w.WriteStartObject("LargeSignal");
                w.WriteString("Measurement", ls.Measurement.ToString());
                Opt(w, "FrequencyHz", ls.FrequencyHz);
                Opt(w, "SpacingHz", ls.SpacingHz);
                Opt(w, "Vgs", ls.Vgs);
                Opt(w, "Vds", ls.Vds);
                if (ls.SourceZ is { } zs) WriteComplex(w, "SourceZ", zs);
                if (ls.LoadZ is { } zl) WriteComplex(w, "LoadZ", zl);
                foreach (var (list, name) in new[] { (ls.SourceHarmonics, "SourceHarmonics"), (ls.LoadHarmonics, "LoadHarmonics") })
                {
                    if (list.Count == 0) continue;
                    w.WriteStartArray(name);
                    foreach (var z in list) { w.WriteStartArray(); w.WriteNumberValue(z.Real); w.WriteNumberValue(z.Imaginary); w.WriteEndArray(); }
                    w.WriteEndArray();
                }
                w.WriteEndObject();
            }
            if (c.Note is { } note) w.WriteString("Note", note);
            w.WriteEndObject();
        }

        var im = s.Import;
        w.WriteStartObject("Import");
        w.WriteString("Format", im.Format);
        if (im.Pattern is { } pat) w.WriteString("Pattern", pat);
        if (im.PatternSource is { } ps) w.WriteString("PatternSource", ps);
        if (im.Columns is { } cols)
        {
            w.WriteStartArray("Columns");
            foreach (var col in cols)
            {
                w.WriteStartObject();
                w.WriteString("Column", col.Column);
                w.WriteString("Quantity", col.Quantity);
                if (col.Unit is { } u) w.WriteString("Unit", u);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        if (im.Header is { } h) w.WriteBoolean("Header", h);
        if (im.Delimiter is { } dl) w.WriteString("Delimiter", dl);
        if (im.Arrays is { } arrays)
        {
            w.WriteStartObject("Arrays");
            foreach (var (k, v) in arrays.OrderBy(p => p.Key, StringComparer.Ordinal)) w.WriteString(k, v);
            w.WriteEndObject();
        }
        if (im.ArrayUnits is { Count: > 0 } units)
        {
            w.WriteStartObject("ArrayUnits");
            foreach (var (k, v) in units.OrderBy(p => p.Key, StringComparer.Ordinal)) w.WriteString(k, v);
            w.WriteEndObject();
        }
        Opt(w, "FrequencyHz", im.FrequencyHz);
        Opt(w, "Z0", im.Z0);
        w.WriteEndObject();

        if (s.Exclusions.Count > 0)
        {
            w.WriteStartArray("Exclusions");
            foreach (var x in s.Exclusions)
            {
                w.WriteStartObject();
                w.WriteString("Point", x.Point);
                w.WriteString("Reason", x.Reason);
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    private static void Opt(Utf8JsonWriter w, string name, double? v)
    {
        if (v is { } x) w.WriteNumber(name, x);
    }

    // ── JSON field helpers: every miss names its field ───────────────────────

    private static void Only(JsonElement e, string f, string[] keys)
    {
        foreach (var p in e.EnumerateObject())
            if (Array.IndexOf(keys, p.Name) < 0)
                throw new CmcFormatException(f.Length == 0 ? p.Name : $"{f}.{p.Name}", "it is not a field this circuitRF knows.");
    }

    private static void WriteComplex(Utf8JsonWriter w, string name, Complex z)
    {
        w.WriteStartArray(name);
        w.WriteNumberValue(z.Real);
        w.WriteNumberValue(z.Imaginary);
        w.WriteEndArray();
    }

    private static bool Has(JsonElement e, string name, out JsonElement v) =>
        e.TryGetProperty(name, out v) && v.ValueKind != JsonValueKind.Null;

    private static JsonElement Obj(JsonElement e, string name, string f)
    {
        if (!Has(e, name, out var v)) throw new CmcFormatException(f, "it is missing.");
        if (v.ValueKind != JsonValueKind.Object) throw new CmcFormatException(f, "it is not an object.");
        return v;
    }

    private static string Str(JsonElement e, string name, string f)
    {
        if (!Has(e, name, out var v)) throw new CmcFormatException(f, "it is missing.");
        if (v.ValueKind != JsonValueKind.String) throw new CmcFormatException(f, "it is not a string.");
        return v.GetString()!;
    }

    private static double Num(JsonElement e, string name, string f)
    {
        if (!Has(e, name, out var v)) throw new CmcFormatException(f, "it is missing.");
        if (v.ValueKind != JsonValueKind.Number) throw new CmcFormatException(f, "it is not a number.");
        return v.GetDouble();
    }

    private static double? OptNum(JsonElement e, string name, string f) => Has(e, name, out _) ? Num(e, name, f) : null;

    private static int Int(JsonElement e, string name, string f)
    {
        double d = Num(e, name, f);
        if (d != Math.Floor(d) || d > int.MaxValue || d < int.MinValue)
            throw new CmcFormatException(f, $"{G(d)} is not a whole number.");
        return (int)d;
    }

    private static T Enum<T>(JsonElement e, string name, string f) where T : struct, Enum
    {
        string s = Str(e, name, f);
        foreach (var v in System.Enum.GetValues<T>())
            if (string.Equals(v.ToString(), s, StringComparison.OrdinalIgnoreCase)) return v;
        throw new CmcFormatException(f, $"'{s}' is not one of {string.Join(", ", System.Enum.GetNames<T>())}.");
    }

    private static IReadOnlyList<string> Strings(JsonElement e, string name, string f)
    {
        if (!Has(e, name, out var v)) throw new CmcFormatException(f, "it is missing.");
        if (v.ValueKind != JsonValueKind.Array) throw new CmcFormatException(f, "it is not a list.");
        var list = new List<string>();
        foreach (var x in v.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.String) throw new CmcFormatException(f, "an entry is not a string.");
            list.Add(x.GetString()!);
        }
        return list;
    }
}

/// <summary>
/// A Touchstone bias pattern (R-mc3-3): literal text with named fields — <c>Vgs{vgs}_Vds{vds}</c>. A field
/// may state its unit, <c>{id:mA}</c>; otherwise volts, amps and °C. Whitespace matches any run of
/// whitespace and <c>*</c> matches anything. Against a file name the pattern must match the whole name
/// (without its extension); against comments it is searched in the comment lines joined by spaces.
/// </summary>
public sealed class BiasPattern
{
    private static readonly Regex FieldRx = new(@"\{(\w+)(?::([^}]+))?\}", RegexOptions.Compiled);
    private const string NumberRx = @"[+-]?(?:\d+\.?\d*|\.\d+)(?:[eE][+-]?\d+)?";

    public Regex Regex { get; }
    public IReadOnlyDictionary<string, double> Scales { get; }

    private BiasPattern(Regex rx, IReadOnlyDictionary<string, double> scales) { Regex = rx; Scales = scales; }

    public static BiasPattern Parse(string pattern, string field = "Pattern")
    {
        var sb = new StringBuilder();
        var scales = new Dictionary<string, double>(StringComparer.Ordinal);
        int at = 0;
        foreach (Match m in FieldRx.Matches(pattern))
        {
            sb.Append(Literal(pattern[at..m.Index]));
            string name = m.Groups[1].Value;
            if (!CmcFile.PatternFields.Contains(name))
                throw new CmcFormatException(field, $"{{{name}}} is not a field; the fields are {string.Join(", ", CmcFile.PatternFields.Select(p => "{" + p + "}"))}.");
            if (scales.ContainsKey(name)) throw new CmcFormatException(field, $"{{{name}}} appears twice.");
            double scale = 1;
            if (m.Groups[2].Success)
                scale = CmcFile.UnitScale(m.Groups[2].Value)
                     ?? throw new CmcFormatException(field, $"'{m.Groups[2].Value}' in {{{name}:…}} is not a unit circuitRF knows.");
            scales[name] = scale;
            sb.Append($"(?<{name}>{NumberRx})");
            at = m.Index + m.Length;
        }
        sb.Append(Literal(pattern[at..]));
        return new BiasPattern(new Regex(sb.ToString(), RegexOptions.CultureInvariant), scales);
    }

    private static string Literal(string text)
    {
        var sb = new StringBuilder();
        bool inSpace = false;
        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch)) { if (!inSpace) sb.Append(@"\s+"); inSpace = true; continue; }
            inSpace = false;
            sb.Append(ch == '*' ? ".*?" : Regex.Escape(ch.ToString()));
        }
        return sb.ToString();
    }

    /// <summary>The fields found in <paramref name="text"/>, in base units, or null when the pattern does not match.</summary>
    public IReadOnlyDictionary<string, double>? Match(string text, bool whole)
    {
        var m = whole ? new Regex($"^(?:{Regex})$", RegexOptions.CultureInvariant).Match(text) : Regex.Match(text);
        if (!m.Success) return null;
        var values = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (name, scale) in Scales)
            values[name] = double.Parse(m.Groups[name].Value, NumberStyles.Float, CultureInfo.InvariantCulture) * scale;
        return values;
    }
}
