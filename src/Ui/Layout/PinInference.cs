// Derives R3-grade pins — name, position, connecting WIDTH and OUTWARD DIRECTION — from artwork that
// only carries a box on a pin-purpose layer and, sometimes, a text label sitting inside it.
//
// This is the single riskiest inference in the whole PDK program, and the reason is worth stating
// plainly: **a wrong answer here renders perfectly.** Geometry is unchanged, the cell draws exactly as
// the process drew it, and only the connectivity is wrong — a pin facing the wrong way, or named after
// the device's model rather than its terminal. Nothing on screen says so. So every rule below is
// STRUCTURAL (it asks only about shapes and containment, never about what a name looks like), every
// inconclusive case is REPORTED rather than guessed at, and anything a kit knows that circuitRF cannot
// derive is supplied as run-time data beside the kit.
//
// Measured against a real process's own device artwork; the cases that shaped each rule are recorded
// at the rule.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Ui.Layout;

/// <summary>Where a pin's name came from — a name that was GUESSED must be distinguishable from one
/// the kit stated, because only one of the two is trustworthy for connectivity.</summary>
public enum PinNameSource
{
    /// <summary>No name could be established. The pin is still a usable connection point.</summary>
    None,
    /// <summary>Inferred from a label lying inside the pin, under the systematic-labelling rule.</summary>
    InferredFromLabel,
    /// <summary>Stated by the kit beside itself. Always wins.</summary>
    Declared,
}

/// <summary>Where a pin's outward direction came from.</summary>
public enum PinDirectionSource
{
    /// <summary>Derived from where the pin sits relative to the rest of the cell.</summary>
    Geometry,
    /// <summary>Geometry did not decide it; a deterministic fallback was used and reported.</summary>
    Ambiguous,
    /// <summary>Stated by the kit beside itself. Always wins.</summary>
    Declared,
}

/// <summary>
/// One pin recovered from artwork. Position is a point ON the connecting edge (its midpoint), width is
/// that edge's length, and outward direction is the way an arm leaves the cell — the three things
/// <c>Pin</c> requires and a pin-layer box does not carry.
/// </summary>
public sealed record InferredPin(
    string?            Name,
    long               XDbu,
    long               YDbu,
    long               WidthDbu,
    double             OutwardDeg,
    LayerKey           Layer,
    PinNameSource      NameSource,
    PinDirectionSource DirectionSource);

/// <summary>What an inference produced, and everything it could not settle.</summary>
public sealed record PinInferenceResult(
    IReadOnlyList<InferredPin> Pins,
    IReadOnlyList<string>      Notes);

// ── the kit's own declaration ─────────────────────────────────────────────────

/// <summary>
/// What a kit states about its own pins, in a file beside it. Everything here is something circuitRF
/// cannot derive from geometry; nothing kit-specific belongs in the product.
///
/// <para>Mirrors <c>device-provider.json</c>/<c>pcell-generators.json</c> in shape, so a kit author who
/// has met one recognises this.</para>
/// </summary>
public sealed class PinInferenceRules
{
    /// <summary>The name this file has beside a kit, so every caller looks in the same place.</summary>
    public const string FileName = "pin-rules.json";

    /// <summary>Layer purposes whose shapes are pins. Defaults to the near-universal "pin".</summary>
    public List<string> PinPurposes { get; set; } = ["pin"];

    /// <summary>
    /// Layer purposes whose labels may NAME a pin. Empty means "any label", which is the right default:
    /// measured on a real process, a cell's terminal labels sit on a general text layer rather than on
    /// the matching pin layer, so requiring them to agree would find none.
    /// </summary>
    public List<string> LabelPurposes { get; set; } = [];

    /// <summary>Per-cell statements, keyed by cell name.</summary>
    public Dictionary<string, CellPinDeclaration> Cells { get; set; } = new(StringComparer.Ordinal);

    public static PinInferenceRules Default => new();

    /// <summary>
    /// Reads a rules file. A missing file is not an error — it means the kit states nothing and pure
    /// geometry applies; that is the ordinary case and reporting it would be noise on every import.
    /// A file that is PRESENT and unreadable IS reported, because those two need different answers.
    /// </summary>
    public static PinInferenceRules Load(string path, out string? problem)
    {
        problem = null;
        if (!File.Exists(path)) return Default;

        try
        {
            var opts = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
            };
            return JsonSerializer.Deserialize<PinInferenceRules>(File.ReadAllText(path), opts) ?? Default;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            problem = $"The pin declaration beside the kit could not be read ({ex.Message}); " +
                      "pins were derived from geometry alone.";
            return Default;
        }
    }
}

/// <summary>What a kit states about one cell's pins.</summary>
public sealed class CellPinDeclaration
{
    /// <summary>
    /// Keyed by the pin's inferred name when there is one, or by its ordinal — <c>"#0"</c>, <c>"#1"</c>
    /// … in <see cref="PinInference"/>'s own deterministic order. The ordinal form exists because the
    /// name is often exactly what the kit is supplying, so keying only by name would be circular.
    /// </summary>
    public Dictionary<string, PinDeclaration> Pins { get; set; } = new(StringComparer.Ordinal);
}

/// <summary>One stated pin. Every field is optional; each one supplied replaces what geometry derived.</summary>
public sealed class PinDeclaration
{
    public string? Name       { get; set; }
    public double? OutwardDeg { get; set; }
    public long?   WidthDbu   { get; set; }
}

// ── the inference ─────────────────────────────────────────────────────────────

public static class PinInference
{
    /// <summary>
    /// How far a pin's centre must sit off the cell's centre, as a fraction of the cell's size, before
    /// that offset is taken to mean anything. Below it the pin is central and its outward direction is
    /// genuinely undetermined by position — a real case (a bipolar's emitter contact sits dead centre).
    /// </summary>
    private const double CentralFraction = 0.02;

    public static PinInferenceResult Infer(
        string                     cellName,
        IReadOnlyList<LayoutShape> shapes,
        Technology?                tech,
        PinInferenceRules?         rules = null)
    {
        rules ??= PinInferenceRules.Default;
        var notes = new List<string>();

        var pinPurposes   = new HashSet<string>(rules.PinPurposes,   StringComparer.OrdinalIgnoreCase);
        var labelPurposes = new HashSet<string>(rules.LabelPurposes, StringComparer.OrdinalIgnoreCase);

        string? PurposeOf(LayerKey k) => tech?.Layers.FirstOrDefault(l => l.Key == k)?.Purpose;

        // A pin is a non-label shape on a pin-purpose layer. Ordered deterministically, because the
        // kit's own declaration keys on that order.
        var pinShapes = shapes
            .Where(s => s is not LabelShape && PurposeOf(s.Layer) is { } p && pinPurposes.Contains(p))
            .Select(s => (Shape: s, Box: LayoutGeometry.BboxOf(s)))
            .OrderBy(t => t.Shape.Layer.Layer).ThenBy(t => t.Shape.Layer.Datatype)
            .ThenBy(t => t.Box.MinX).ThenBy(t => t.Box.MinY)
            .ToList();

        if (pinShapes.Count == 0)
            return new PinInferenceResult([], notes);

        // The cell's own extent, EXCLUDING labels: a label is an anchor point that may sit well outside
        // the drawn geometry, and letting one stretch the extent moves the centre every direction is
        // measured against.
        var geometry = shapes.Where(s => s is not LabelShape).ToList();
        var cellBox  = (geometry.Count > 0 ? geometry : shapes.ToList())
                       .Select(LayoutGeometry.BboxOf)
                       .Aggregate((a, b) => a.Union(b));

        var names = InferNames(pinShapes, shapes, labelPurposes, PurposeOf, notes);

        rules.Cells.TryGetValue(cellName, out var declared);

        var pins = new List<InferredPin>(pinShapes.Count);
        for (int i = 0; i < pinShapes.Count; i++)
        {
            var (shape, box) = pinShapes[i];

            var (deg, source) = Direction(box, cellBox, out bool reported);
            if (reported)
                notes.Add($"Pin #{i} on layer ({shape.Layer.Layer},{shape.Layer.Datatype}) sits centrally " +
                          "in the cell, so which way it faces is not determined by where it is; " +
                          $"{deg:0}° was used. State it beside the kit if it matters.");

            long width = PerpendicularExtent(box, deg);
            var (x, y) = EdgeMidpoint(box, deg);

            string?       name       = names.GetValueOrDefault(i);
            PinNameSource nameSource = name is null ? PinNameSource.None : PinNameSource.InferredFromLabel;

            // The kit's own statement always wins — that is the whole point of it existing.
            if (declared is not null &&
                (declared.Pins.TryGetValue($"#{i}", out var d) ||
                 (name is not null && declared.Pins.TryGetValue(name, out d))))
            {
                if (d.Name is { Length: > 0 })  { name = d.Name;  nameSource = PinNameSource.Declared; }
                if (d.OutwardDeg is { } od)     { deg  = od;      source     = PinDirectionSource.Declared;
                                                  width = PerpendicularExtent(box, deg);
                                                  (x, y) = EdgeMidpoint(box, deg); }
                if (d.WidthDbu is > 0)          { width = d.WidthDbu.Value; }
            }

            pins.Add(new InferredPin(name, x, y, width, deg, shape.Layer, nameSource, source));
        }

        return new PinInferenceResult(pins, notes);
    }

    // ── naming ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Names pins from labels lying inside them — but only when the cell labels its pins SYSTEMATICALLY.
    ///
    /// <para><b>Containment alone is not enough, and this is the rule the whole thing turns on.</b>
    /// Measured on a real process: a transistor cell carries one descriptive label at its centre, and
    /// that centre falls inside the gate's pin box — so plain containment names the gate after the
    /// device's model, confidently and invisibly. A capacitor's labels fall inside BOTH its pins. Only
    /// the bipolar, which genuinely labels its terminals, has one label per pin.</para>
    ///
    /// <para>So a label must lie inside exactly ONE pin, a pin must contain exactly ONE such label, and
    /// then the whole assignment is accepted only if it looks deliberate: **two or more pins named, or
    /// every pin named.** A cell either labels its terminals or it does not; one label landing in one
    /// pin out of three is an annotation that happens to overlap. All six devices measured come out
    /// right — the bipolar keeps E/B/C, and nothing else is named at all.</para>
    ///
    /// <para>This asks nothing about what the text SAYS. A rule that rejected names looking like model
    /// numbers would be knowledge about one supplier's naming habits living inside circuitRF, and would
    /// fail on the next kit.</para>
    /// </summary>
    private static Dictionary<int, string> InferNames(
        List<(LayoutShape Shape, Bbox Box)> pinShapes,
        IReadOnlyList<LayoutShape>          shapes,
        HashSet<string>                     labelPurposes,
        Func<LayerKey, string?>             purposeOf,
        List<string>                        notes)
    {
        var result = new Dictionary<int, string>();

        var labels = shapes.OfType<LabelShape>()
            .Where(l => l.Text is { Length: > 0 })
            .Where(l => labelPurposes.Count == 0 ||
                        (purposeOf(l.Layer) is { } p && labelPurposes.Contains(p)))
            .ToList();
        if (labels.Count == 0) return result;

        var perPin       = new Dictionary<int, List<string>>();
        int ambiguousLabels = 0;

        foreach (var label in labels)
        {
            var inside = new List<int>();
            for (int i = 0; i < pinShapes.Count; i++)
                if (Contains(pinShapes[i].Box, label.X, label.Y)) inside.Add(i);

            if (inside.Count == 0) continue;
            if (inside.Count > 1) { ambiguousLabels++; continue; }   // inside several pins: names none

            if (!perPin.TryGetValue(inside[0], out var list)) perPin[inside[0]] = list = [];
            list.Add(label.Text);
        }

        var unambiguous = perPin.Where(kv => kv.Value.Count == 1)
                                .ToDictionary(kv => kv.Key, kv => kv.Value[0]);

        bool systematic = unambiguous.Count >= 2 || unambiguous.Count == pinShapes.Count;

        if (!systematic)
        {
            if (unambiguous.Count > 0 || ambiguousLabels > 0)
                notes.Add($"{unambiguous.Count} of {pinShapes.Count} pin(s) had a label inside them, " +
                          "which reads as an annotation that happens to overlap rather than as terminal " +
                          "names — so no pin was named. State the names beside the kit if they matter.");
            return result;
        }

        foreach (var kv in unambiguous) result[kv.Key] = kv.Value;

        if (unambiguous.Count < pinShapes.Count)
            notes.Add($"{unambiguous.Count} of {pinShapes.Count} pin(s) were named from labels; the rest " +
                      "carry no name.");

        return result;
    }

    // ── geometry ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Which way the pin faces, from where it sits relative to the rest of the cell.
    ///
    /// <para>The dominant axis of (pin centre − cell centre) decides it. That is the rule that agrees
    /// with the real artwork measured: a transistor's source and drain sit left and right of centre and
    /// face outward along X; a bipolar's base and collector sit below and above and face along Y.</para>
    ///
    /// <para><b>A pin sitting centrally is not a failure of the rule — it is a real case</b> (a gate
    /// spanning the full cell height, an emitter contact in the middle), and position genuinely says
    /// nothing about it. Rather than let a rounding difference pick a side, that case is reported and
    /// falls back to the pin's own SHAPE: a box presents its short edges, so a tall thin pin faces up
    /// and a wide flat one faces right. Deterministic, and stated rather than silent.</para>
    /// </summary>
    private static (double Deg, PinDirectionSource Source) Direction(Bbox pin, Bbox cell, out bool reported)
    {
        double dx = (pin.MinX + pin.MaxX) / 2.0 - (cell.MinX + cell.MaxX) / 2.0;
        double dy = (pin.MinY + pin.MaxY) / 2.0 - (cell.MinY + cell.MaxY) / 2.0;

        double tolX = Math.Max(1.0, (cell.MaxX - cell.MinX) * CentralFraction);
        double tolY = Math.Max(1.0, (cell.MaxY - cell.MinY) * CentralFraction);

        bool offX = Math.Abs(dx) > tolX;
        bool offY = Math.Abs(dy) > tolY;

        if (offX || offY)
        {
            reported = false;
            // Compared as FRACTIONS of the cell's own size, not as raw distances: a cell far wider than
            // it is tall would otherwise read every pin as facing sideways.
            double fx = Math.Abs(dx) / Math.Max(1.0, cell.MaxX - cell.MinX);
            double fy = Math.Abs(dy) / Math.Max(1.0, cell.MaxY - cell.MinY);
            return fx >= fy
                ? (dx >= 0 ? 0.0 : 180.0, PinDirectionSource.Geometry)
                : (dy >= 0 ? 90.0 : 270.0, PinDirectionSource.Geometry);
        }

        reported = true;
        long w = pin.MaxX - pin.MinX;
        long h = pin.MaxY - pin.MinY;
        return (h >= w ? 90.0 : 0.0, PinDirectionSource.Ambiguous);
    }

    /// <summary>The pin box's extent across the direction it faces — the connecting edge's length.</summary>
    private static long PerpendicularExtent(Bbox box, double deg)
        => IsHorizontal(deg) ? box.MaxY - box.MinY : box.MaxX - box.MinX;

    /// <summary>The midpoint of the edge facing <paramref name="deg"/> — a connection is an edge, and
    /// this is the point on it.</summary>
    private static (long X, long Y) EdgeMidpoint(Bbox box, double deg)
    {
        long cx = (box.MinX + box.MaxX) / 2;
        long cy = (box.MinY + box.MaxY) / 2;
        return Normalize(deg) switch
        {
            0   => (box.MaxX, cy),
            180 => (box.MinX, cy),
            90  => (cx, box.MaxY),
            _   => (cx, box.MinY),
        };
    }

    private static bool IsHorizontal(double deg) => Normalize(deg) is 0 or 180;

    /// <summary>Snaps to the four axes. A pin-layer box is axis-aligned by construction, so a stated
    /// direction that is not is a declaration circuitRF cannot honour exactly; the nearest axis is used
    /// rather than refused.</summary>
    private static int Normalize(double deg)
    {
        double d = ((deg % 360) + 360) % 360;
        return (int)(Math.Round(d / 90.0) % 4) * 90;
    }

    private static bool Contains(Bbox b, long x, long y)
        => x >= b.MinX && x <= b.MaxX && y >= b.MinY && y <= b.MaxY;
}
