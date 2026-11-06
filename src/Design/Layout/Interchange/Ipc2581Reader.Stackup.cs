// The IPC-2581 stackup: which entries are electrical, what order they stand in, and what values they state. Files
// differ here more than anywhere else. A conductor may be an entry, or a group named after its copper layer and
// holding nothing electrical. A dielectric may be a declared layer, a name with no declaration at all, or not listed
// at all between two conductors. Values may be in a Spec, in an exporter's attribute, or nowhere.
//
// Series rule S5 applies throughout: materialType and product names decide nothing and are never looked up.

using System.Globalization;
using System.Xml.Linq;
using Clipper2Lib;

namespace CircuitRF.Design.Layout.Interchange;

public sealed partial class Ipc2581Reader
{
    private const string DielectricType = "core";
    private const string ConductorType = "copper";
    private const string NonElectricalType = "non-electrical";

    private enum RowKind { Unknown, Conductor, Dielectric, Skipped }

    private sealed class StackupRow(string name, XElement? source, double thickness)
    {
        public string Name { get; } = name;
        public XElement? Source { get; } = source;
        public double Thickness { get; set; } = thickness;
        public RowKind Kind { get; set; }
    }

    private readonly Dictionary<string, XElement> _specs = new(StringComparer.Ordinal);
    private readonly List<XElement> _zones = [];
    private List<string> _stackupOrder = [];
    private List<string> _conductorOrder = [];

    private void ReadStackup()
    {
        ReportSpecsNotUsed();

        var stackup = ChooseStackup();
        if (stackup is null) return;

        double mmPerUnit = EcadScale() / (_dbuPerMicron * 1000.0);
        if (Number(Attr(stackup, "overallThickness")) is { } overall && overall > 0)
            _board.OverallThicknessMm = overall * mmPerUnit;

        var rows = WalkStackup(stackup);
        Classify(rows);
        _stackupOrder = [.. rows.Select(r => r.Name)];

        var conductors = rows.Where(r => r.Kind == RowKind.Conductor).ToList();
        switch (CheckOrder(conductors))
        {
            case OrderCheck.Reversed:
                rows.Reverse();
                _stackupOrder.Reverse();
                conductors.Reverse();
                _board.Diagnostics.Add(
                    "This file lists its stackup bottom first, by the side its copper layers state, so it was reversed.");
                break;
            case OrderCheck.Contradictory:
                _stackupOrder = [];
                _board.StackupRefusal =
                    $"its order disagrees with the side its copper layers state ('{conductors[0].Name}' first and " +
                    $"'{conductors[^1].Name}' last), so it could be upside down.";
                return;
            case OrderCheck.Unknowable:
                _board.Diagnostics.Add(
                    "This file's copper layers state no side, so the stackup's order could not be checked and was taken as written.");
                break;
        }

        _conductorOrder = [.. conductors.Select(c => c.Name)];

        var named = new HashSet<string>(_conductorOrder, StringComparer.Ordinal);
        var absent = _layers.Where(l => IsCopperFunction(l.Function) && !named.Contains(l.Name)).Select(l => l.Name).Distinct().ToList();
        if (absent.Count > 0 && conductors.Count > 0)
        {
            _conductorOrder = [];
            _board.StackupRefusal =
                $"it does not place the copper layer(s) {string.Join(", ", absent.Select(n => $"'{n}'"))}, so that copper has no height.";
            return;
        }

        CheckThickness(rows, mmPerUnit);

        var notes = new StackupNotes();
        var entries = new List<PcbStackupEntry>(rows.Count);
        StackupRow? previous = null;
        int added = 0;
        foreach (var row in rows)
        {
            if (row.Kind == RowKind.Skipped)
            {
                entries.Add(new PcbStackupEntry(row.Name, NonElectricalType, null, null, null));
                continue;
            }

            if (row.Kind == RowKind.Conductor && previous?.Kind == RowKind.Conductor)
            {
                added++;
                entries.Add(new PcbStackupEntry(UnusedDielectricName(rows, added), DielectricType, null, null, null));
            }

            double? mm = row.Thickness > 0 ? row.Thickness * mmPerUnit : null;
            entries.Add(row.Kind == RowKind.Conductor
                ? new PcbStackupEntry(row.Name, ConductorType, mm, null, null, ConductivityOf(row, notes))
                : DielectricEntry(row, mm, notes));
            previous = row;
        }

        if (added > 0)
            _board.Diagnostics.Add(
                $"This file's stackup lists {added} pair(s) of conductors with nothing between them, so a dielectric with no " +
                "stated values was added to each.");
        notes.Report(_board);

        _board.Stackup = entries;
        _board.CompleteStackupFromDefaults = true;
    }

    /// <summary>The copper layers, top first: the stackup's order when it has one and otherwise the side each states.</summary>
    private List<string> CopperTopToBottom()
    {
        if (_conductorOrder.Count > 0) return _conductorOrder;

        return [.. _layers.Where(l => IsCopperFunction(l.Function))
            .DistinctBy(l => l.Name)
            .OrderBy(l => l.Side == "TOP" ? 0 : l.Side == "BOTTOM" ? 2 : 1)
            .Select(l => l.Name)];
    }

    // ── Which stackup: D4 ──────────────────────────────────────────────────────────────────────

    /// <summary>One technology holds one stackup. With zones it is the largest zone's, and every zone is named.</summary>
    private XElement? ChooseStackup()
    {
        if (_stackups.Count == 0) return null;

        if (_zones.Count == 0)
        {
            if (_stackups.Count > 1)
                _board.Diagnostics.Add($"This file holds {_stackups.Count} stackups and no zones say where each applies. The first was used.");
            return _stackups[0];
        }

        var measured = _zones.Select(zone => (Zone: zone, Name: Attr(zone, "name") ?? "(unnamed)", Area: ZoneArea(zone))).ToList();
        var largest = measured.MaxBy(z => z.Area);
        string? reference = Attr(largest.Zone, "stackupRef")
            ?? (Child(largest.Zone, "StackupRef") is { } r ? Attr(r, "name") : null);
        var chosen = _stackups.FirstOrDefault(s => Attr(s, "name") == reference);

        _board.Diagnostics.Add(
            $"This file declares {_zones.Count} stackup zone(s) ({string.Join(", ", measured.Select(z => z.Name))}). One technology " +
            $"holds one stackup, so the stackup of the largest zone, '{largest.Name}', was used. All artwork was imported." +
            (chosen is null ? " That zone names no stackup in the file, so the file's first stackup was used instead." : ""));
        return chosen ?? _stackups[0];
    }

    private double ZoneArea(XElement zone)
    {
        var all = new Paths64();
        if (Child(zone, "Profile") is { } profile)
            foreach (var region in ProfileRegions(profile))
                foreach (var shape in Ipc2581Shapes.Place(region, Ipc2581Transform.Identity, _flattenTolDbu))
                    all.AddRange(LayoutClipper.ToClipperPaths(shape, LayoutFlattener.ResolveTolDbu(shape, null)));
        return Math.Abs(Clipper.Area(Clipper.Union(all, LayoutClipper.Rule)));
    }

    // ── The walk ───────────────────────────────────────────────────────────────────────────────

    private static string? GroupName(XElement group) => Attr(group, "name") ?? Attr(group, "stackupGroupName");

    /// <summary>Every entry top to bottom as written: by <c>sequence</c> when every entry at a level has one and by
    /// document order otherwise, descending into a group an entry refers to. A group named after a copper layer is
    /// that conductor, and it comes before what it holds.</summary>
    private List<StackupRow> WalkStackup(XElement stackup)
    {
        var groups = new Dictionary<string, XElement>(StringComparer.Ordinal);
        foreach (var group in stackup.Descendants().Where(e => e.Name.LocalName == "StackupGroup"))
            if (GroupName(group) is { Length: > 0 } name) groups.TryAdd(name, group);

        var referred = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in stackup.Descendants().Where(e => e.Name.LocalName == "StackupLayer"))
            if (Attr(entry, "layerOrGroupRef") is { Length: > 0 } r && groups.ContainsKey(r)) referred.Add(r);

        var copper = _layers.Where(l => IsCopperFunction(l.Function)).Select(l => l.Name).ToHashSet(StringComparer.Ordinal);
        var rows = new List<StackupRow>();
        var byName = new Dictionary<string, StackupRow>(StringComparer.Ordinal);

        void Add(string name, XElement source)
        {
            double thickness = Number(Attr(source, "thickness")) ?? 0;
            if (byName.TryGetValue(name, out var seen))
            {
                if (seen.Thickness <= 0) seen.Thickness = thickness;
                return;
            }
            var row = new StackupRow(name, source, thickness);
            rows.Add(row);
            byName[name] = row;
        }

        var visiting = new HashSet<XElement>();
        void WalkGroup(XElement group)
        {
            if (!visiting.Add(group)) return;
            if (GroupName(group) is { } name && copper.Contains(name)) Add(name, group);
            Walk(group);
        }

        void Walk(XElement container)
        {
            var entries = container.Elements()
                .Where(e => e.Name.LocalName is "StackupLayer" or "StackupGroup").ToList();
            if (entries.Count > 0 && entries.All(e => Number(Attr(e, "sequence")) is not null))
                entries = [.. entries.OrderBy(e => Number(Attr(e, "sequence"))!.Value)];

            foreach (var entry in entries)
            {
                if (entry.Name.LocalName == "StackupGroup")
                {
                    if (!(GroupName(entry) is { } name && referred.Contains(name))) WalkGroup(entry);
                    continue;
                }

                string reference = Attr(entry, "layerOrGroupRef") ?? "";
                if (groups.TryGetValue(reference, out var group)) WalkGroup(group);
                else if (reference.Length > 0) Add(reference, entry);
            }
        }

        Walk(stackup);
        return rows;
    }

    /// <summary>A referenced layer's function decides; with no layer, a stated type; with neither, an entry with a
    /// thickness between two copper layers can only be a dielectric. materialType decides nothing.</summary>
    private void Classify(List<StackupRow> rows)
    {
        var functions = _layers.GroupBy(l => l.Name, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Function, StringComparer.Ordinal);

        foreach (var row in rows)
        {
            if (functions.TryGetValue(row.Name, out var function)) row.Kind = KindOfFunction(function);
            else if (Attribute(row.Source, "LAYER_TYPE") is { } stated) row.Kind = KindOfFunction(stated.ToUpperInvariant());
        }

        int first = rows.FindIndex(r => r.Kind == RowKind.Conductor);
        int last = rows.FindLastIndex(r => r.Kind == RowKind.Conductor);
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Kind == RowKind.Unknown)
                rows[i].Kind = i > first && i < last && first >= 0 && rows[i].Thickness > 0 ? RowKind.Dielectric : RowKind.Skipped;
    }

    private static RowKind KindOfFunction(string function) =>
        IsCopperFunction(function) ? RowKind.Conductor
        : function.StartsWith("DIEL", StringComparison.Ordinal) ? RowKind.Dielectric
        : RowKind.Skipped;

    private enum OrderCheck { Fine, Reversed, Contradictory, Unknowable }

    /// <summary>A stackup read upside down simulates cleanly and answers the wrong question, so the sides the copper
    /// layers state are what its order is checked against.</summary>
    private OrderCheck CheckOrder(List<StackupRow> conductors)
    {
        if (conductors.Count < 2) return OrderCheck.Fine;

        string SideOf(StackupRow row) => _layers.FirstOrDefault(l => l.Name == row.Name)?.Side ?? "";
        string first = SideOf(conductors[0]), last = SideOf(conductors[^1]);
        bool firstTop = first == "TOP", firstBottom = first == "BOTTOM", lastTop = last == "TOP", lastBottom = last == "BOTTOM";

        if (!firstTop && !firstBottom && !lastTop && !lastBottom)
            return first is "" or "NONE" && last is "" or "NONE" ? OrderCheck.Unknowable : OrderCheck.Contradictory;
        if (!firstBottom && !lastTop) return OrderCheck.Fine;
        if (!firstTop && !lastBottom) return OrderCheck.Reversed;
        return OrderCheck.Contradictory;
    }

    /// <summary>Compared only where every electrical row states a thickness and no dielectric is unlisted: a sum missing
    /// any of those is short by construction and says nothing. Skipped rows count, since a stated overall thickness
    /// includes a mask.</summary>
    private void CheckThickness(List<StackupRow> rows, double mmPerUnit)
    {
        if (_board.OverallThicknessMm is not { } overall) return;
        var electrical = rows.Where(r => r.Kind != RowKind.Skipped).ToList();
        if (electrical.Exists(r => r.Thickness <= 0)) return;
        for (int i = 1; i < electrical.Count; i++)
            if (electrical[i].Kind == RowKind.Conductor && electrical[i - 1].Kind == RowKind.Conductor) return;

        double sum = rows.Sum(r => r.Thickness) * mmPerUnit;
        if (Math.Abs(sum - overall) > 0.05 * overall)
            _board.Diagnostics.Add(
                $"This file's stackup entries add up to {sum:0.###} mm and it states an overall thickness of {overall:0.###} mm, a " +
                "difference of more than 5%. Both were kept.");
    }

    private static string UnusedDielectricName(List<StackupRow> rows, int number)
    {
        for (int n = number; ; n++)
        {
            string name = $"Dielectric {n}";
            if (!rows.Exists(r => r.Name == name)) return name;
        }
    }

    // ── Values, in order of trust ──────────────────────────────────────────────────────────────

    private sealed class StackupNotes
    {
        public List<string> Tables { get; } = [];
        public List<string> FromAttribute { get; } = [];
        public List<string> Problems { get; } = [];
        public List<string> NoLossTangent { get; } = [];
        public int Roughness { get; set; }

        public void Report(PcbBoard board)
        {
            board.Diagnostics.AddRange(Tables);
            board.Diagnostics.AddRange(Problems);
            if (FromAttribute.Count > 0)
                board.Diagnostics.Add(
                    $"These stackup values came from an exporter's attribute and not from a Spec: {string.Join("; ", FromAttribute)}.");
            if (NoLossTangent.Count > 0)
                board.Diagnostics.Add(
                    $"Stated with a permittivity and no loss tangent, so simulated as lossless: {string.Join(", ", NoLossTangent)}.");
            if (Roughness > 0)
                board.Diagnostics.Add(
                    $"{Roughness} conductor(s) state a surface roughness that was not read: the file's roughness types do not state an RMS value.");
        }
    }

    private PcbStackupEntry DielectricEntry(StackupRow row, double? mm, StackupNotes notes)
    {
        double? er = TabulatedOf(row, "DIELECTRIC_CONSTANT", "relative permittivity", notes)
            ?? AttributeNumber(row, "LAYER_DIELECTRIC_CONSTANT", "relative permittivity", notes);
        double? tanD = TabulatedOf(row, "LOSS_TANGENT", "loss tangent", notes)
            ?? AttributeNumber(row, "LAYER_LOSS_TANGENT", "loss tangent", notes);

        if (er > 0 && tanD is null) notes.NoLossTangent.Add($"'{row.Name}'");
        return new PcbStackupEntry(row.Name, DielectricType, mm, er > 0 ? er : null, tanD);
    }

    private double? ConductivityOf(StackupRow row, StackupNotes notes)
    {
        foreach (var conductor in SpecChildren(row, "Conductor"))
        {
            string type = (Attr(conductor, "type") ?? "").ToUpperInvariant();
            if (type.StartsWith("SURFACE_ROUGHNESS", StringComparison.Ordinal)) notes.Roughness++;
        }

        foreach (var conductor in SpecChildren(row, "Conductor"))
        {
            if (!string.Equals(Attr(conductor, "type"), "CONDUCTIVITY", StringComparison.OrdinalIgnoreCase)) continue;
            var property = Child(conductor, "Property");
            if (property is null || Number(Attr(property, "value")) is not { } value) continue;
            if (ConductivitySm(value, Attr(property, "unit")) is { } sm && sm > 0) return sm;
            notes.Problems.Add(
                $"The conductivity on '{row.Name}' states the unit \"{Attr(property, "unit") ?? "none"}\", which was not understood, so it was treated as missing.");
            return null;
        }

        if (Attribute(row.Source, "LAYER_ELECTRICAL_CONDUCTIVITY") is not { } text) return null;

        string[] parts = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 2 && Number(parts[0]) is { } number && ConductivitySm(number, parts[1]) is { } converted && converted > 0)
        {
            notes.FromAttribute.Add($"conductivity on '{row.Name}'");
            return converted;
        }
        notes.Problems.Add(
            $"The conductivity attribute on '{row.Name}' reads \"{text}\", whose unit was not understood, so it was treated as missing.");
        return null;
    }

    /// <summary>S/m for a stated unit, and null for any other. An unstated or unknown unit is never assumed to be S/m.</summary>
    private static double? ConductivitySm(double value, string? unit) =>
        (unit ?? "").Replace(" ", "").ToUpperInvariant() switch
        {
            "MHO/CM" or "S/CM" or "SIEMENS/CM" => value * 100,
            "MHO/M" or "S/M" or "SIEMENS/M" => value,
            _ => null,
        };

    /// <summary>One value from a Spec's Dielectric of this type. D2: with a table against frequency, the lowest stated
    /// frequency's value, and the message lists every stated pair.</summary>
    private double? TabulatedOf(StackupRow row, string type, string label, StackupNotes notes)
    {
        foreach (var dielectric in SpecChildren(row, "Dielectric"))
        {
            if (!string.Equals(Attr(dielectric, "type"), type, StringComparison.OrdinalIgnoreCase)) continue;

            var pairs = new List<(double Value, double Hz)>();
            double? plain = null;
            foreach (var property in dielectric.Elements().Where(e => e.Name.LocalName == "Property"))
            {
                if (Number(Attr(property, "value")) is not { } value) continue;

                string? refValue = Attr(property, "refValue");
                if (refValue is null) { plain ??= value; continue; }

                if (Number(refValue) is { } reference && FrequencyHz(reference, Attr(property, "refUnit")) is { } hz)
                    pairs.Add((value, hz));
                else
                    notes.Problems.Add(
                        $"A {label} on '{row.Name}' is stated at {refValue} {Attr(property, "refUnit") ?? "(no unit)"}, which is not a frequency that could be converted, so that row was ignored.");
            }

            if (pairs.Count == 0)
            {
                if (plain is not null) return plain;
                continue;
            }

            var lowest = pairs.MinBy(p => p.Hz);
            notes.Tables.Add(
                $"'{row.Name}' tabulates {label}: {string.Join(", ", pairs.Select(p => $"{p.Value.ToString("0.####", CultureInfo.InvariantCulture)} at {Frequency(p.Hz)}"))}. " +
                $"The value at the lowest frequency, {lowest.Value.ToString("0.####", CultureInfo.InvariantCulture)}, was used.");
            return lowest.Value;
        }
        return null;
    }

    private static double? FrequencyHz(double value, string? unit) =>
        (unit ?? "").ToUpperInvariant() switch
        {
            "HZ" => value,
            "KHZ" => value * 1e3,
            "MHZ" => value * 1e6,
            "GHZ" => value * 1e9,
            _ => null,
        };

    private static string Frequency(double hz) =>
        hz >= 1e9 ? $"{(hz / 1e9).ToString("0.###", CultureInfo.InvariantCulture)} GHz"
        : hz >= 1e6 ? $"{(hz / 1e6).ToString("0.###", CultureInfo.InvariantCulture)} MHz"
        : $"{hz.ToString("0.###", CultureInfo.InvariantCulture)} Hz";

    private double? AttributeNumber(StackupRow row, string name, string label, StackupNotes notes)
    {
        if (Attribute(row.Source, name) is not { } text) return null;
        if (Number(text.Trim()) is { } value)
        {
            notes.FromAttribute.Add($"{label} on '{row.Name}'");
            return value;
        }
        notes.Problems.Add($"The {label} attribute on '{row.Name}' reads \"{text}\", which is not a number, so it was treated as missing.");
        return null;
    }

    private static string? Attribute(XElement? source, string name) =>
        source?.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "NonstandardAttribute" && Attr(e, "name") == name) is { } found
            ? Attr(found, "value")
            : null;

    private IEnumerable<XElement> SpecChildren(StackupRow row, string childName)
    {
        if (row.Source is null) yield break;
        foreach (var reference in row.Source.Elements().Where(e => e.Name.LocalName == "SpecRef"))
        {
            if (Attr(reference, "id") is not { } id || !_specs.TryGetValue(id, out var spec)) continue;
            foreach (var child in spec.Elements().Where(e => e.Name.LocalName == childName))
                yield return child;
        }
    }

    /// <summary>What a Spec can hold and this import does not use. Impedance targets would help the Impedance tool, and
    /// a backdrill removes a barrel stub that changes an RF answer, so each is said by count.</summary>
    private void ReportSpecsNotUsed()
    {
        int CountHolding(string child) => _specs.Values.Count(s => s.Elements().Any(e => e.Name.LocalName == child));

        int impedance = CountHolding("Impedance");
        if (impedance > 0)
            _board.Diagnostics.Add($"{impedance} impedance specification(s) in this file were not used.");

        int backdrill = CountHolding("Backdrill");
        if (backdrill > 0)
            _board.Diagnostics.Add(
                $"{backdrill} backdrill specification(s) in this file were not modelled, so a simulation keeps the barrel stubs they remove.");
    }
}
