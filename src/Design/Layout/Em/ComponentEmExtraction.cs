using CircuitRF.Core.Design;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em;

/// <summary>
/// One built-in component's drawn part, as an EM problem: its PCell artwork on a technology, a port
/// on every pin, and a planar setup over a frequency sweep — the "extract this spiral" half of
/// brief-agent-authoring-overview.md AA-1's hybrid model (owner decision: a closed-form estimate,
/// flagged as one, PLUS a command that writes an SnP from planar EM).
///
/// <para><b>It owns no EM logic and draws nothing of its own.</b> The artwork is the generator's own
/// output (the same cell a schematic generates into a layout), the ports are ordinary port labels on
/// the generator's own pins, and the run is <see cref="EmRunService.Run"/> — so what this extracts is
/// exactly what Simulate would extract from that part drawn in a layout with a port on each pin. The
/// headless spelling is <c>circuitrf em --component "SPIRAL N=3 W=10um …"</c>.</para>
///
/// <para>Port <i>n</i> is the component's terminal <i>n</i>, in the order its instance line writes
/// them, so the SnP drops into a schematic in place of the part with no renumbering.</para>
/// </summary>
public static class ComponentEmExtraction
{
    /// <summary>What <see cref="Prepare"/> built: the setup and the in-memory layout to hand to
    /// <see cref="EmRunService.Run"/>, and what the generator said while drawing.</summary>
    public sealed record Prepared(EmSetup Setup, EmLayoutSource Source, IReadOnlyList<string> Notes);

    /// <summary>
    /// The mesh an extraction runs on: two cells across the narrowest metal and no graded edge fan.
    ///
    /// <para><b>A setting traded for reach, stated rather than hidden.</b> At the default (four cells
    /// across plus the edge fan) the shipped 2.5-turn, 10 µm spiral meshed to 20,637 unknowns, four
    /// times past the planar kernel's 5,000 ceiling, and was refused — a coil is the narrowest metal
    /// spread over the widest area, which is exactly the shape a tensor grid pays most for. Two across
    /// with no fan brings parts of that size inside it; what that costs in accuracy is measured in
    /// src/Design/RESOLVED.md (AA-1).</para>
    /// </summary>
    public static readonly PlanarMeshSettings ExtractionMesh = new(Auto: false, EdgeMesh: false, MinCellsAcrossConductor: 2);

    /// <summary>
    /// Builds the problem, or returns null with <paramref name="refusal"/> saying why. Only a BUILT-IN
    /// generator is accepted: a kit's script would need the kit's workspace and permission to run, and
    /// a component with no artwork has nothing to extract.
    /// </summary>
    /// <param name="token">The component's token — <c>SPIRAL</c>, <c>MIMCAP</c>, <c>MLIN</c> — which is
    /// also its generator id.</param>
    /// <param name="parameters">SI values, as a PCell receives them (a length in metres).</param>
    /// <param name="layoutPath">Where the layout would live — only its NAME and directory are used, for
    /// the setup's reference and the result's file name. Nothing is written there.</param>
    public static Prepared? Prepare(string token, IReadOnlyDictionary<string, PCellValue> parameters,
                                    Technology technology, FrequencySpec frequency, string layoutPath,
                                    out string? refusal)
    {
        refusal = null;
        if (!PCellRegistry.KnownGeneratorIds.Contains(token, StringComparer.OrdinalIgnoreCase)
            || !PCellRegistry.TryGet(token, out var generator))
        {
            refusal = $"'{token}' has no built-in layout generator, so there is no drawn part to extract. Components " +
                      $"with one: {string.Join(", ", PCellRegistry.KnownGeneratorIds.Order(StringComparer.Ordinal))}.";
            return null;
        }

        var art = generator(parameters, technology, PCellLayerSelection.Default);
        if (art.Pins.Count == 0)
        {
            refusal = $"the {token} artwork has no pins, so no port can be placed on it.";
            return null;
        }

        // A layer the stackup does not carry is invisible to the solve. A capacitor mask is the one
        // exception — it is how a patterned film enters a run (PresentWithLayer) — so it is not one.
        // Anything else would extract the part WITHOUT that piece. A resistive film is carried: it is an
        // entry on its level, which the extractor meshes as a sheet between the contacts.
        var bound = new HashSet<LayerKey>(technology.Stackup.Layers.SelectMany(l => l.DrawingLayers));
        foreach (var (_, film) in ResistiveFilms.All(technology)) bound.Add(film.Layer);
        var masks = new HashSet<string>(technology.Stackup.Layers.Select(l => l.PresentWithLayer).OfType<string>(),
                                        StringComparer.OrdinalIgnoreCase);
        var unseen = art.Shapes.Select(s => s.Layer).Distinct()
            .Where(k => !bound.Contains(k))
            .Select(k => technology.Layers.FirstOrDefault(d => d.Key == k))
            .Where(d => d is null || !masks.Contains(d.Name))
            .Select(d => d?.Name ?? "an undefined layer")
            .ToList();
        if (unseen.Count > 0)
        {
            refusal = $"the {token} artwork draws on {string.Join(", ", unseen.Select(n => $"'{n}'"))}, which the " +
                      "technology's stackup does not carry, so a planar EM solve would not see that part of it. " +
                      (token.Equals("TFR", StringComparison.OrdinalIgnoreCase)
                          ? "Define the resistor film on the conductor it is deposited on (Technology ▸ Stackup) first."
                          : "Bind the layer to a stackup entry first.");
            return null;
        }

        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
        var view = new LayoutView { DbuPerMicron = dbuPerMicron };
        foreach (var shape in art.Shapes) view.Shapes.Add(shape);
        for (int i = 0; i < art.Pins.Count; i++)
        {
            var pin = art.Pins[i];
            view.Shapes.Add(new LabelShape
            {
                Layer = pin.Layer, X = pin.X, Y = pin.Y, Text = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                Height = Math.Max(pin.WidthDbu / 2, dbuPerMicron), IsPort = true,
            });
        }

        string name = Path.GetFileNameWithoutExtension(layoutPath);
        var setup = new EmSetup
        {
            Name = name,
            LayoutRef = Path.GetFileName(layoutPath),
            AnalysisKind = EmAnalysisKind.Planar,
            Frequency = frequency,
            PlanarMesh = ExtractionMesh,
        };
        return new Prepared(setup, new EmLayoutSource(Path.GetFullPath(layoutPath), view, technology, dbuPerMicron),
                            art.Diagnostics ?? []);
    }
}
