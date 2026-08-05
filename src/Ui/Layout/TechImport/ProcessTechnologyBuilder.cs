// Joins a process's stack description and its layer table into a circuitRF Technology.
//
// This is where the judgement lives, and it is kept out of the two readers on purpose: reading those
// files is mechanical, while turning them into circuitRF's own stack model requires a real
// conversion (see the embedded-conductor rule below) that has to be able to report when it could not
// be made cleanly.
//
// Nothing here names a process, a supplier or a tool. Every name in the result comes out of the files
// it was handed.

using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Layout.TechImport;

/// <summary>What a technology import produced, and everything it had to decide on the way.</summary>
public sealed record TechnologyImportResult(
    Technology            Technology,
    IReadOnlyList<string> Notes);

/// <summary>Builds a <see cref="Technology"/> from the two process files. Framework-free.</summary>
public static class ProcessTechnologyBuilder
{
    /// <summary>
    /// Database units per micrometre. A <c>.ctech</c> carries no resolution field of its own — the
    /// figure lives on the layout (<c>LayoutView.DbuPerMicron</c>) and defaults to this, which is what
    /// every shipped technology's stackup thicknesses are already expressed in. Getting it wrong here
    /// would scale a whole process by a factor of a thousand while every number still looked plausible.
    /// </summary>
    public const double DbuPerMicron = 1000.0;

    /// <summary>Thicknesses at or below this (in µm) are treated as nothing at all.</summary>
    private const double NegligibleUm = 1e-9;

    public static TechnologyImportResult Build(
        ProcessStackDescription stack,
        ProcessLayerTable?      layerTable,
        string                  fallbackName)
    {
        var notes  = new List<string>();
        notes.AddRange(stack.Notes);
        if (layerTable is not null) notes.AddRange(layerTable.Notes);

        var layers  = BuildLayers(layerTable, notes);
        var byName  = IndexByBaseName(layerTable);
        var slabs   = BuildSlabs(stack, notes);
        var zByName = ComputeConductorSpans(slabs);

        AttachDrawingLayers(slabs, byName, notes);

        var vias = BuildVias(stack, zByName, byName, notes);

        var stackup = new Stackup
        {
            Top    = BoundaryCondition.Open,
            Bottom = BoundaryCondition.Ground,
            Layers = [.. slabs, .. vias],
        };

        double minFeatureUm = MinimumFeatureUm(stack);
        long   snapDbu      = ChooseSnapDbu(minFeatureUm);

        var tech = new Technology
        {
            Name                  = stack.TechnologyName is { Length: > 0 } n ? n : fallbackName,
            DefaultDisplayUnit    = ChooseDisplayUnit(slabs),
            DefaultSnapDbu        = snapDbu,
            DefaultFlattenTolDbu  = snapDbu,
            DefaultLabelHeightDbu = Math.Max(1, ToDbu(minFeatureUm * 10)),
            DefaultViaPadDbu      = ChooseViaSizeDbu(stack),
            DefaultViaDrillDbu    = ChooseViaSizeDbu(stack),
            Layers                = layers,
            Stackup               = stackup,
            DrcRules              = BuildDrcRules(stack, byName),
        };

        // A process states no ground plane — every conductor in a back-end stack is a signal layer
        // until a design says otherwise — so the choice is the user's and has to be made before a
        // microstrip component can resolve a substrate. Said here, at import, rather than left for
        // each component to fail independently much later.
        if (stackup.Layers.Any(l => l.Kind == StackupKind.Conductor))
            notes.Add("No conductor is marked as the ground reference — a process file does not state " +
                      "one. Pick the layer this design uses as its return path in the Technology " +
                      "Editor's Stackup tab before placing microstrip components.");

        return new TechnologyImportResult(tech, notes);
    }

    // ── the layer table ───────────────────────────────────────────────────────

    private static List<LayerDef> BuildLayers(ProcessLayerTable? table, List<string> notes)
    {
        var layers = new List<LayerDef>();
        if (table is null) return layers;

        int generated = 0;
        foreach (var e in table.Entries)
        {
            var key = new LayerKey(e.Layer, e.Datatype);

            // A row that states no usable colour still has to be drawable and distinguishable, so it
            // gets one from the same generator an undefined layer already renders with rather than a
            // shared default that would make several layers indistinguishable.
            var colour = e.Color;
            if (colour is null) { colour = FallbackPalette.For(key).Color; generated++; }

            layers.Add(new LayerDef
            {
                Key         = key,
                // The full spelling, so two purposes of one layer are distinguishable by name alone —
                // which is what every name-first match in circuitRF (paste reconciliation, technology
                // retargeting) keys on. The purpose is carried separately as well.
                Name        = e.FullName,
                Color       = colour.Value,
                FillOpacity = 0.35,
                ZOrder      = e.Order,
                Visible     = e.Visible,
                Selectable  = true,
                Purpose     = e.Purpose,
            });
        }

        if (generated > 0)
            notes.Add($"{generated} layer(s) stated no display colour; each was given a generated one.");

        return layers;
    }

    /// <summary>
    /// Layer-table rows indexed by the layer's own name, so a stack entry can find the row it draws
    /// on. A drawing purpose wins over every other, because that is the geometry; a row with no
    /// purpose at all is the fallback.
    /// </summary>
    private static Dictionary<string, ProcessLayerEntry> IndexByBaseName(ProcessLayerTable? table)
    {
        var index = new Dictionary<string, ProcessLayerEntry>(StringComparer.OrdinalIgnoreCase);
        if (table is null) return index;

        foreach (var e in table.Entries)
        {
            bool isDrawing = string.Equals(e.Purpose, "drawing", StringComparison.OrdinalIgnoreCase);
            bool isPlain   = e.Purpose is null or { Length: 0 };

            if (!index.TryGetValue(e.BaseName, out var already))
            {
                index[e.BaseName] = e;
                continue;
            }

            bool alreadyDrawing = string.Equals(already.Purpose, "drawing", StringComparison.OrdinalIgnoreCase);
            bool alreadyPlain   = already.Purpose is null or { Length: 0 };

            if (isDrawing && !alreadyDrawing) index[e.BaseName] = e;
            else if (isPlain && !alreadyDrawing && !alreadyPlain) index[e.BaseName] = e;
        }

        return index;
    }

    // ── the stack ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Converts the file's EMBEDDED-CONDUCTOR convention into circuitRF's ALTERNATING-SLAB one, and
    /// this is the load-bearing part of the whole import.
    ///
    /// <para>A process file states a conductor's thickness and, separately, the thickness of the
    /// dielectric the conductor sits INSIDE — so the two overlap. circuitRF's stackup is a plain
    /// ordered pile of slabs whose thicknesses add up, so carrying the numbers across verbatim
    /// inflates the whole stack by the sum of every metal thickness. On a real back-end stack that is
    /// over half the total height, and nothing downstream would say so: the layer table, the layer
    /// colours and every drawn shape would be exactly right while every substrate height an
    /// electrical model resolves was wrong.</para>
    ///
    /// <para>So a conductor's own thickness is taken OUT of the dielectric run directly above it,
    /// nearest slab first. Nearest-first matters when a file splits that run in two (a process
    /// routinely does, to model a liner separately): taking it all from the last slab drives that one
    /// negative while the run as a whole is thick enough.</para>
    /// </summary>
    private static List<StackupLayer> BuildSlabs(ProcessStackDescription stack, List<string> notes)
    {
        var output  = new List<StackupLayer>();
        var pending = new List<(string Name, double ThicknessUm, double Epsr)>();

        void FlushPending()
        {
            foreach (var d in pending)
                if (d.ThicknessUm > NegligibleUm)
                    output.Add(new StackupLayer
                    {
                        Kind         = StackupKind.Dielectric,
                        Name         = d.Name,
                        ThicknessDbu = ToDbu(d.ThicknessUm),
                        Epsr         = d.Epsr,
                        TanD         = 0,
                        Mur          = 1,
                    });
            pending.Clear();
        }

        foreach (var e in stack.Entries)
        {
            switch (e.Kind)
            {
                case ProcessStackEntryKind.Dielectric:
                    pending.Add((e.Name, e.ThicknessUm, e.RelativePermittivity));
                    break;

                case ProcessStackEntryKind.Conductor:
                {
                    double remaining = e.ThicknessUm;
                    for (int i = pending.Count - 1; i >= 0 && remaining > NegligibleUm; i--)
                    {
                        double take = Math.Min(pending[i].ThicknessUm, remaining);
                        pending[i]  = pending[i] with { ThicknessUm = pending[i].ThicknessUm - take };
                        remaining  -= take;
                    }

                    if (remaining > NegligibleUm)
                        notes.Add($"Conductor \"{e.Name}\" is {e.ThicknessUm:0.###} µm thick but the " +
                                  $"insulation above it accounts for {e.ThicknessUm - remaining:0.###} µm; " +
                                  $"the remaining {remaining:0.###} µm was taken as zero separation. The " +
                                  "layer is placed at full thickness.");

                    FlushPending();

                    double sigma = Conductivity(e.SheetResistanceOhmPerSquare, e.ThicknessUm);
                    if (sigma <= 0)
                        notes.Add($"Conductor \"{e.Name}\" states no sheet resistance, so its " +
                                  "conductivity could not be derived. Set it in the Technology Editor " +
                                  "before running anything that depends on loss.");

                    output.Add(new StackupLayer
                    {
                        Kind         = StackupKind.Conductor,
                        Name         = e.Name,
                        ThicknessDbu = ToDbu(e.ThicknessUm),
                        SigmaSm      = sigma,
                        Epsr         = 1,
                        Mur          = 1,
                    });
                    break;
                }

                case ProcessStackEntryKind.Via:
                    break;   // vias are not slabs; they are built once the spans are known
            }
        }

        FlushPending();   // whatever lies below the bottom conductor — the substrate
        return output;
    }

    /// <summary>
    /// Where each conductor sits, measured downward from the top of the stack in µm, so a via can be
    /// told how far it actually reaches. Returned as (top, bottom) depths, top smaller.
    /// </summary>
    private static Dictionary<string, (double Top, double Bottom)> ComputeConductorSpans(
        List<StackupLayer> slabs)
    {
        var spans = new Dictionary<string, (double, double)>(StringComparer.OrdinalIgnoreCase);
        double depth = 0;

        foreach (var s in slabs)
        {
            double thickness = s.ThicknessDbu / DbuPerMicron;
            if (s.Kind == StackupKind.Conductor) spans[s.Name] = (depth, depth + thickness);
            depth += thickness;
        }

        return spans;
    }

    private static void AttachDrawingLayers(
        List<StackupLayer>                       slabs,
        Dictionary<string, ProcessLayerEntry>    byName,
        List<string>                             notes)
    {
        var unmapped = new List<string>();

        foreach (var s in slabs)
        {
            if (byName.TryGetValue(s.Name, out var row))
                s.DrawingLayers = [new LayerKey(row.Layer, row.Datatype)];
            else if (s.Kind == StackupKind.Conductor)
                unmapped.Add(s.Name);
        }

        // Only conductors are reported. A dielectric is very often not drawn at all — it is the
        // material between the drawn layers — so naming each one would bury the conductors that
        // genuinely could not be matched.
        if (unmapped.Count > 0)
            notes.Add("No layer-table row matches these conductors, so nothing drawn is bound to " +
                      $"them: {string.Join(", ", unmapped)}.");
    }

    private static List<StackupLayer> BuildVias(
        ProcessStackDescription                   stack,
        Dictionary<string, (double Top, double Bottom)> spans,
        Dictionary<string, ProcessLayerEntry>     byName,
        List<string>                              notes)
    {
        var vias     = new List<StackupLayer>();
        var dangling = new List<string>();
        var undrawn  = new List<string>();

        foreach (var e in stack.Entries)
        {
            if (e.Kind != ProcessStackEntryKind.Via) continue;

            bool haveFrom = e.SpanFrom is { Length: > 0 } && spans.ContainsKey(e.SpanFrom);
            bool haveTo   = e.SpanTo   is { Length: > 0 } && spans.ContainsKey(e.SpanTo);

            double reachUm = 0;
            if (haveFrom && haveTo)
            {
                var a = spans[e.SpanFrom!];
                var b = spans[e.SpanTo!];
                // The clear distance between the two conductors' facing surfaces.
                reachUm = a.Top < b.Top ? b.Top - a.Bottom : a.Top - b.Bottom;
            }
            else
            {
                dangling.Add(e.Name);
            }

            var via = new StackupLayer
            {
                Kind          = StackupKind.Via,
                Name          = e.Name,
                ThicknessDbu  = 0,                    // a via has no thickness of its own (R-via-3)
                SigmaSm       = ViaConductivity(e.ResistanceOhms, e.CrossSectionUm2, reachUm),
                Epsr          = 1,
                Mur           = 1,
                // A process via is a filled plug, not a plated barrel — there is no hole in it, so
                // there is no wall thickness to state either.
                Fill          = ViaFillKind.Solid,
                SpanFromLayer = e.SpanFrom,
                SpanToLayer   = e.SpanTo,
            };

            if (byName.TryGetValue(e.Name, out var row))
                via.DrawingLayers = [new LayerKey(row.Layer, row.Datatype)];
            else
                undrawn.Add(e.Name);

            vias.Add(via);
        }

        if (dangling.Count > 0)
            notes.Add("These vias name a conductor the stack does not contain, so their conductivity " +
                      $"could not be derived: {string.Join(", ", dangling)}.");

        // Several via TYPES routinely share one drawn layer — a process distinguishes a contact to one
        // material from a contact to another while drawing both the same way — so this is normal and
        // is reported rather than treated as a fault. It still has to be said: nothing drawn is bound
        // to these entries, so the Via tool cannot place one.
        if (undrawn.Count > 0)
            notes.Add("No layer-table row matches these vias, so nothing drawn is bound to them: " +
                      $"{string.Join(", ", undrawn)}. Where two via types share one drawn layer this is " +
                      "expected; set the drawing layer in the Technology Editor if you need to place them.");

        return vias;
    }

    // ── derived quantities ────────────────────────────────────────────────────

    /// <summary>
    /// Bulk conductivity from sheet resistance and thickness: a square of sheet of thickness t has
    /// resistance 1/(σ·t), so σ = 1/(Rs·t).
    /// </summary>
    private static double Conductivity(double sheetResistanceOhmPerSquare, double thicknessUm)
        => sheetResistanceOhmPerSquare > 0 && thicknessUm > 0
            ? 1.0 / (sheetResistanceOhmPerSquare * thicknessUm * 1e-6)
            : 0;

    /// <summary>
    /// Effective conductivity of one via plug, from the resistance and cross-section the process
    /// states and the distance the via actually has to reach: R = length / (σ·A), so σ = length/(R·A).
    ///
    /// <para>This is an EFFECTIVE figure, not the plug material's own — a stated per-via resistance
    /// includes the contact resistance at both ends, which is most of it on a small via. That is the
    /// right number to carry, because it is what the process guarantees the connection will do.</para>
    /// </summary>
    private static double ViaConductivity(double resistanceOhms, double crossSectionUm2, double reachUm)
        => resistanceOhms > 0 && crossSectionUm2 > 0 && reachUm > 0
            ? reachUm * 1e-6 / (resistanceOhms * crossSectionUm2 * 1e-12)
            : 0;

    private static double MinimumFeatureUm(ProcessStackDescription stack)
    {
        double min = double.MaxValue;
        foreach (var e in stack.Entries)
        {
            if (e.MinWidthUm   > 0) min = Math.Min(min, e.MinWidthUm);
            if (e.MinSpacingUm > 0) min = Math.Min(min, e.MinSpacingUm);
        }
        return min == double.MaxValue ? 1.0 : min;
    }

    /// <summary>
    /// A snap step a tenth of the process's finest feature, rounded DOWN to a 1/2/5 step so it reads
    /// as a round number in the toolbar. Down rather than to-nearest: a snap coarser than a tenth of
    /// the minimum feature quietly stops the finest geometry from landing where it was drawn.
    /// </summary>
    private static long ChooseSnapDbu(double minFeatureUm)
    {
        double target = minFeatureUm * DbuPerMicron / 10.0;
        if (target < 1) return 1;

        double decade = Math.Pow(10, Math.Floor(Math.Log10(target)));
        foreach (double m in new[] { 5.0, 2.0, 1.0 })
            if (decade * m <= target) return (long)Math.Round(decade * m);

        return 1;
    }

    /// <summary>The side of a square of the smallest via's cross-section — a sensible default size.</summary>
    private static long ChooseViaSizeDbu(ProcessStackDescription stack)
    {
        double smallest = double.MaxValue;
        foreach (var e in stack.Entries)
            if (e.Kind == ProcessStackEntryKind.Via && e.CrossSectionUm2 > 0)
                smallest = Math.Min(smallest, e.CrossSectionUm2);

        return smallest == double.MaxValue ? 0 : Math.Max(1, ToDbu(Math.Sqrt(smallest)));
    }

    private static LayoutUnit ChooseDisplayUnit(List<StackupLayer> slabs)
    {
        double totalUm = slabs.Sum(s => s.ThicknessDbu) / DbuPerMicron;
        return totalUm >= 1000 ? LayoutUnit.Mm : LayoutUnit.Um;
    }

    /// <summary>
    /// Minimum width and spacing, where the process states them. These come from the STACK
    /// description, which carries them alongside the material properties — they are not a translation
    /// of a rule deck, which is a script for another tool and is deliberately not attempted.
    /// </summary>
    private static List<DrcRule> BuildDrcRules(
        ProcessStackDescription               stack,
        Dictionary<string, ProcessLayerEntry> byName)
    {
        var rules = new List<DrcRule>();

        foreach (var e in stack.Entries)
        {
            if (e.Kind != ProcessStackEntryKind.Conductor) continue;
            if (!byName.TryGetValue(e.Name, out var row)) continue;

            var key = new LayerKey(row.Layer, row.Datatype);

            if (e.MinWidthUm > 0)
                rules.Add(new DrcRule
                {
                    Name     = $"{e.Name} minimum width",
                    Kind     = DrcRuleKind.MinWidth,
                    Layer    = key,
                    ValueDbu = ToDbu(e.MinWidthUm),
                    Severity = DrcSeverity.Error,
                });

            if (e.MinSpacingUm > 0)
                rules.Add(new DrcRule
                {
                    Name     = $"{e.Name} minimum spacing",
                    Kind     = DrcRuleKind.MinSpacing,
                    Layer    = key,
                    ValueDbu = ToDbu(e.MinSpacingUm),
                    Severity = DrcSeverity.Error,
                });
        }

        return rules;
    }

    private static long ToDbu(double um) => (long)Math.Round(um * DbuPerMicron, MidpointRounding.AwayFromZero);
}
