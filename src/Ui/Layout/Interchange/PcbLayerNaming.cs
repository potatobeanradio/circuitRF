// circuitRF LayerKey -> the board format's own canonical layer name and ordinal, for export.
//
// This is the export half of the aliasing L4d's R-L4d-4 added for import, and it needs one thing the
// import side did not: an ORDINAL, because a written file must declare its own (layers …) table and
// every entity references a row of it. The ordinals here are the 20221018-epoch scheme, transcribed
// from a real file of that epoch rather than remembered — F.Cu 0, B.Cu 31, inner copper 1..30, and the
// technical layers 32..49. They are NOT stable across epochs (B.Cu moved to 2 at 20260206), which is
// exactly why PcbWriter targets ONE epoch and says so, instead of trying to be version-agnostic the way
// PcbReader must be.
//
// How a layer gets its name, in order:
//   1. an explicit InterchangeMapping.PcbLayerName alias — the technology author's own statement;
//   2. otherwise, a layer bound to a StackupKind.Conductor entry becomes copper by STACK POSITION
//      (topmost F.Cu, bottom-most B.Cu, the rest In1.Cu…), so the shipped PCB starter technology
//      exports its copper correctly with zero authoring;
//   3. otherwise Dwgs.User, reported by name with a count — never silently dropped and never silently
//      given a technical layer's meaning it does not have.

namespace CircuitRF.Ui.Layout.Interchange;

/// <summary>One row of the (layers …) table a written file declares.</summary>
public sealed record PcbLayerRow(int Ordinal, string Name, string Type)
{
    public bool IsCopper => Type == "signal";
}

public static class PcbLayerNaming
{
    /// <summary>The format epoch this writer emits. Chosen deliberately: it is late enough to be free
    /// of design rules and net classes (those left the board file at 20211014 — measured across four
    /// real epochs, see src/Ui/RESOLVED.md) and early enough that every subsequent release still opens
    /// it, whereas emitting the newest stamp would exclude every older reader.</summary>
    public const string TargetVersion = "20221018";

    /// <summary>Canonical name → ordinal, at <see cref="TargetVersion"/>. Copper is <c>signal</c>;
    /// everything else is <c>user</c>, which is the type word the format itself uses.</summary>
    private static readonly (string Name, int Ordinal)[] Technical =
    [
        ("B.Adhes", 32), ("F.Adhes", 33), ("B.Paste", 34), ("F.Paste", 35),
        ("B.SilkS", 36), ("F.SilkS", 37), ("B.Mask", 38), ("F.Mask", 39),
        ("Dwgs.User", 40), ("Cmts.User", 41), ("Eco1.User", 42), ("Eco2.User", 43),
        ("Edge.Cuts", 44), ("Margin", 45), ("B.CrtYd", 46), ("F.CrtYd", 47),
        ("B.Fab", 48), ("F.Fab", 49),
    ];

    /// <summary>Where an unmapped layer goes. A general-purpose drawing layer: it carries no
    /// fabrication meaning, so putting artwork there cannot be mistaken for silkscreen, a board
    /// outline, or copper.</summary>
    public const string FallbackName = "Dwgs.User";

    public sealed record Result(
        IReadOnlyDictionary<LayerKey, PcbLayerRow> RowByKey,
        IReadOnlyList<PcbLayerRow> Table,
        /// <summary>Technology layer names that landed on <see cref="FallbackName"/> because nothing
        /// said where they belong — reported, never silent.</summary>
        IReadOnlyList<string> UnmappedLayerNames);

    /// <summary>
    /// Assigns every layer of <paramref name="tech"/> a row of the written file's layer table.
    /// </summary>
    public static Result Assign(Technology? tech)
    {
        var rowByKey = new Dictionary<LayerKey, PcbLayerRow>();
        var table = new List<PcbLayerRow>();
        var unmapped = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);

        void Declare(LayerKey key, PcbLayerRow row)
        {
            rowByKey[key] = row;
            if (used.Add(row.Name)) table.Add(row);
        }

        if (tech is null)
        {
            // No technology at all: everything is drawing. The file still opens; nothing pretends to be
            // copper it was never said to be.
            var fallback = new PcbLayerRow(40, FallbackName, "user");
            table.Add(fallback);
            return new Result(rowByKey, table, []);
        }

        // ── 1. Copper, from the stackup's own conductor order ───────────────────────────────────
        var conductors = tech.Stackup.Layers.Where(l => l.Kind == StackupKind.Conductor).ToList();
        var copperNameByKey = new Dictionary<LayerKey, (string Name, int Ordinal)>();
        for (int i = 0; i < conductors.Count; i++)
        {
            string name;
            int ordinal;
            if (i == 0) { name = "F.Cu"; ordinal = 0; }
            else if (i == conductors.Count - 1) { name = "B.Cu"; ordinal = 31; }
            else { name = $"In{i}.Cu"; ordinal = i; }

            foreach (var key in conductors[i].DrawingLayers)
                copperNameByKey[key] = (name, ordinal);
        }

        // Copper rows are declared even when no artwork uses them — a board with a bottom copper layer
        // declared but empty is ordinary; one whose stackup and layer table disagree is not.
        foreach (var (name, ordinal) in copperNameByKey.Values.Distinct())
            if (used.Add(name)) table.Add(new PcbLayerRow(ordinal, name, "signal"));

        // ── 2. Explicit aliases win, including over the stackup derivation ──────────────────────
        foreach (var layer in tech.Layers)
        {
            if (layer.Interchange?.PcbLayerName is not { Length: > 0 } alias) continue;
            var row = RowFor(alias);
            if (row is null) { continue; }        // an alias naming nothing this epoch declares
            Declare(layer.Key, row);
        }

        // ── 3. Everything else: stackup copper, then the fallback ───────────────────────────────
        foreach (var layer in tech.Layers)
        {
            if (rowByKey.ContainsKey(layer.Key)) continue;

            if (copperNameByKey.TryGetValue(layer.Key, out var copper))
            {
                Declare(layer.Key, new PcbLayerRow(copper.Ordinal, copper.Name, "signal"));
                continue;
            }

            // A layer whose own NAME is already a board layer name needs no alias: "F.SilkS" means
            // F.SilkS. This is not a vendor-name heuristic, it is the identity case — and it is what
            // makes a technology hand-authored against this format work with zero extra fields.
            if (RowFor(layer.Name) is { } byName)
            {
                Declare(layer.Key, byName);
                continue;
            }

            Declare(layer.Key, new PcbLayerRow(40, FallbackName, "user"));
            unmapped.Add(layer.Name);
        }

        table.Sort((a, b) => a.Ordinal.CompareTo(b.Ordinal));
        return new Result(rowByKey, table, unmapped);
    }

    /// <summary>The row a canonical name denotes at <see cref="TargetVersion"/>, or null when this
    /// epoch declares no such layer.</summary>
    public static PcbLayerRow? RowFor(string canonicalName)
    {
        foreach (var (name, ordinal) in Technical)
            if (string.Equals(name, canonicalName, StringComparison.OrdinalIgnoreCase))
                return new PcbLayerRow(ordinal, name, "user");

        if (string.Equals(canonicalName, "F.Cu", StringComparison.OrdinalIgnoreCase))
            return new PcbLayerRow(0, "F.Cu", "signal");
        if (string.Equals(canonicalName, "B.Cu", StringComparison.OrdinalIgnoreCase))
            return new PcbLayerRow(31, "B.Cu", "signal");

        if (canonicalName.StartsWith("In", StringComparison.OrdinalIgnoreCase) &&
            canonicalName.EndsWith(".Cu", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(canonicalName[2..^3], out int inner) && inner is >= 1 and <= 30)
            return new PcbLayerRow(inner, $"In{inner}.Cu", "signal");

        return null;
    }

    /// <summary>The back-side counterpart of a front-side technical layer, for baking an instance's
    /// mirror into a written footprint (measured in L4d: the format stores a flipped footprint's child
    /// LAYERS already rewritten, not merely its coordinates).</summary>
    public static string FlipSide(string name) => name switch
    {
        "F.Cu" => "B.Cu",
        "B.Cu" => "F.Cu",
        "F.SilkS" => "B.SilkS",
        "B.SilkS" => "F.SilkS",
        "F.Mask" => "B.Mask",
        "B.Mask" => "F.Mask",
        "F.Paste" => "B.Paste",
        "B.Paste" => "F.Paste",
        "F.Adhes" => "B.Adhes",
        "B.Adhes" => "F.Adhes",
        "F.CrtYd" => "B.CrtYd",
        "B.CrtYd" => "F.CrtYd",
        "F.Fab" => "B.Fab",
        "B.Fab" => "F.Fab",
        _ => name,
    };
}
