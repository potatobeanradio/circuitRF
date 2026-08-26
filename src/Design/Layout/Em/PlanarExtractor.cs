// L8b — drawn layout geometry + a Technology -> the neutral PlanarProblem the surface mesher
// consumes (R-mom-1, unchanged: the engine sees metres, never DBU or LayerKey).
//
// R-em-1 — framework-free, and unit-testable without a document, a canvas or a workspace. Do not
// reach for a LayoutDocument, a LayoutEditorViewModel, Avalonia or SkiaSharp from this file.
//
// **This is deliberately NOT bolted onto CrossSectionExtractor, and the reason is worth stating.**
// That file is 939 lines and almost all of it is the hard part of §10.3.3 — detecting that geometry
// REDUCES to straight, mutually parallel, constant-width conductors, and refusing specifically when
// it does not (a bend, a curved edge, a taper, non-parallel conductors). A planar extractor needs
// none of that, because ACCEPTING geometry that does not reduce is the entire point of a full-wave
// kernel. Merging the two would put the refusal logic of one on the acceptance path of the other:
// every bend refusal would have to grow an "unless planar" branch, and the first one forgotten would
// be a silent capability loss. They are a different, much simpler function that happens to read the
// same inputs.
//
// What IS shared is the stackup reading, and it is shared by restating the two rules rather than by
// calling into the other file: the two-DBU-scales rule (shape coordinates use the layout's own
// DbuPerMicron; stackup thicknesses use LayoutUnits.DefaultDbuPerMicron, ALWAYS) and R-em-4's ground
// rule (the ground plane is the TOP SURFACE of the highest ground-designated conductor below the
// signal). Both are load-bearing and both are 2%-scale traps; see src/Ui/Layout/Em/CLAUDE.md.

using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em;

/// <summary>Either a <see cref="PlanarProblem"/>, or a refusal that names what is missing and where
/// the capability arrives — the same R-mom-17 shape every other refusal in this area uses.</summary>
public sealed record PlanarExtractionResult(
    PlanarProblem?        Problem,
    string?               Refusal,
    IReadOnlyList<string> Notes)
{
    public bool Ok => Problem is not null && Refusal is null;

    public static PlanarExtractionResult No(string refusal, IEnumerable<string>? notes = null)
        => new(null, refusal, notes is null ? [] : [.. notes]);

    public static PlanarExtractionResult Yes(PlanarProblem p, IEnumerable<string>? notes = null)
        => new(p, null, notes is null ? [] : [.. notes]);
}

public static class PlanarExtractor
{
    /// <summary>See <c>CrossSectionExtractor.StackupDbuPerMicron</c>: a technology's stackup
    /// thicknesses are DBU at the DEFAULT resolution, never at the layout's own, because neither
    /// <c>Technology</c> nor the <c>.ctech</c> file carries a resolution for them to be relative
    /// to. Conflating the two rescales every substrate height by that ratio — a plausible-looking
    /// answer, wrong by 10× on a layout drawn at 100 DBU/µm.</summary>
    private const int StackupDbuPerMicron = LayoutUnits.DefaultDbuPerMicron;

    /// <summary>
    /// R-msh-8a's mapping, in one place: which built-in PCell generators also have a validated
    /// closed-form model, and — <b>as reworded on the owner's instruction, 2026-08-14</b> — what the
    /// full-wave run ADDS over it.
    ///
    /// <para><b>The direction of this note is the whole design, and it used to point the wrong way.</b>
    /// Each `Reason` read as an argument for the cheap model: "a slowly-varying quasi-TEM structure…
    /// effectively free". But the only person who ever sees it has opened an EM setup, pointed it at
    /// this part, and pressed Simulate — they know the analytic model exists, and being told so reads
    /// as being told they are wasting their time. The note is worth having for the opposite reason:
    /// full-wave genuinely does buy something on these parts (radiation and surface-wave loss, the end
    /// discontinuities, coupling to neighbouring metal), and it genuinely does NOT move the quantity
    /// the analytic model already gets right — so a user who knows both halves can tell a confirming
    /// result from a wasted one. **Never reword these back into a recommendation.**</para>
    ///
    /// <para>A mitred bend is deliberately ABSENT: <c>MicrostripBendModel</c> exists, but a bend is
    /// exactly the discontinuity kernel B is for, and R-pc-18 records that mitred and unmitred are
    /// DISTINCT discontinuities — which is the whole reason a bend is interesting to a full-wave
    /// kernel at all.</para>
    /// </summary>
    public static PlanarAnalyticAlternative? AnalyticAlternativeFor(string generatorId, string? instanceName = null)
    {
        string subject = instanceName is { Length: > 0 } n ? $"'{n}'" : generatorId;
        return generatorId.ToUpperInvariant() switch
        {
            "MKLOPF" => new PlanarAnalyticAlternative(subject, "MicrostripKlopfModel",
                "a Klopfenstein taper. What full-wave adds over a cascade of uniform sections is what " +
                "the cascade cannot see: radiation and surface-wave loss along the flare, the " +
                "discontinuity at each end, and coupling to whatever else is on the board. What it " +
                "will not move much is the in-band match, which the taper profile already sets and " +
                "which MicrostripKlopfModel integrates directly (Klopfenstein 1956 plus Kajfez & " +
                "Prewitt's endpoint correction) — so a result close to that one is the two methods " +
                "agreeing, not the solve being wasted."),
            "MTAPER" => new PlanarAnalyticAlternative(subject, "MicrostripTaperModel",
                "a linear taper. What full-wave adds over the uniform-section cascade is radiation " +
                "and surface-wave loss along the flare, the end discontinuities, and coupling to " +
                "nearby metal; the in-band transformation itself is what MicrostripTaperModel already " +
                "computes, so close agreement there is confirmation rather than a wasted run."),
            "MLIN" => new PlanarAnalyticAlternative(subject, "MicrostripLineModel",
                "a uniform line. Its impedance, dispersion and loss are what MicrostripLineModel " +
                "computes (Hammerstad-Jensen plus Kirschning-Jansen); what full-wave adds is the end " +
                "discontinuities and any coupling to neighbouring metal, which a closed form has no " +
                "way to express."),
            _ => null,
        };
    }

    /// <summary>
    /// Builds the planar problem from drawn artwork.
    /// </summary>
    /// <param name="shapes">The layout's own shapes. Labels and bitmaps are annotation, never artwork,
    /// and are ignored rather than allowed to make a whole extraction refuse.</param>
    /// <param name="tech">The resolved technology — supplies the stackup and the layer bindings.</param>
    /// <param name="dbuPerMicron">The LAYOUT's own resolution, for shape coordinates only.</param>
    /// <param name="maxFrequencyHz">The highest frequency of the sweep (D4). Zero means no cap.</param>
    /// <param name="settings">Reuses the cross-section extractor's settings record — the only field
    /// this extractor reads is <c>SignalStackupLayerName</c>, and reusing the record rather than
    /// growing a second one keeps the <c>.cem</c> panel from having two "which layer is the signal"
    /// controls that could disagree.</param>
    /// <param name="generatorIds">Optional PCell generator ids present in the analysed geometry, for
    /// R-msh-8a. The extractor cannot see instances (it is handed flattened shapes), so the caller —
    /// which resolved them — supplies this.</param>
    public static PlanarExtractionResult Extract(
        IReadOnlyList<LayoutShape> shapes,
        Technology                 tech,
        int                        dbuPerMicron,
        double                     maxFrequencyHz     = 0,
        EmExtractionSettings?      settings           = null,
        IReadOnlyList<string>?     generatorIds       = null)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(tech);
        settings ??= EmExtractionSettings.Default;

        var notes = new List<string>();

        if (dbuPerMicron <= 0)
            return PlanarExtractionResult.No(
                $"The layout's resolution is {dbuPerMicron} DBU per micron, which is not a usable " +
                "scale. Set a positive DbuPerMicron on the layout.");

        var stack = BuildStack(tech.Stackup);
        if (stack.Count == 0)
            return PlanarExtractionResult.No(
                $"Technology '{tech.Name}' has no stackup layers, so there is nothing to say how thick " +
                "the metal is, what is under it, or where the ground plane sits. Add a stackup in the " +
                "technology editor.");

        // ── Classify shapes against the stackup's DrawingLayers bindings ──────────────────────
        var binding = BuildLayerBinding(stack);
        var viaBinding = BuildViaBinding(tech.Stackup);
        var conductorShapes = new List<(LayoutShape Shape, Band Band)>();
        int ignoredAnnotation = 0, ignoredGround = 0, ignoredOther = 0;

        var viaShapes = new List<(ViaShape Shape, StackupLayer Entry)>();

        foreach (var s in shapes)
        {
            if (s is LabelShape or BitmapShape) { ignoredAnnotation++; continue; }

            // L9d/D5 — a ViaShape on a layer bound to a StackupKind.Via entry is now ARTWORK, not
            // annotation. Before L9d it was skipped with everything else that is not a filled region
            // on a conductor layer, which was correct while there was only one level for it to join.
            if (s is ViaShape vs)
            {
                if (viaBinding.TryGetValue(vs.Layer, out var viaEntry))
                    viaShapes.Add((vs, viaEntry));
                else ignoredOther++;
                continue;
            }

            if (s is PathShape)                 { ignoredOther++;      continue; }

            if (!binding.TryGetValue(s.Layer, out var bands)) { ignoredOther++; continue; }

            var signalBand = bands.FirstOrDefault(b =>
                b.Layer.Kind == StackupKind.Conductor && !b.Layer.IsGroundReference);
            if (signalBand is not null) { conductorShapes.Add((s, signalBand)); continue; }

            if (bands.Any(b => b.Layer.Kind == StackupKind.Conductor && b.Layer.IsGroundReference))
            { ignoredGround++; continue; }

            ignoredOther++;
        }

        if (conductorShapes.Count == 0)
            return PlanarExtractionResult.No(
                $"This EM setup is pointed at geometry with nothing on a layer bound to a signal " +
                $"conductor entry in technology '{tech.Name}'. Draw the artwork on a conductor layer, " +
                "or bind the layer it is on to a conductor entry in the technology editor's Stackup tab.",
                notes);

        // ── L9d/D5 — WHICH LEVELS, bottom-to-top, and it is N or a refusal ───────────────────
        //
        // Through L8 this block picked exactly ONE level and refused every other case, because the
        // Green's function had one. It now selects a SET: the names the .cem gives, else the single
        // name R-em-4b's older field gives, else every signal conductor that actually carries
        // artwork. Ordered bottom-to-top because R-via-5 and R-msh-2 both index by that order.
        var signalBands = conductorShapes.Select(c => c.Band).Distinct().OrderBy(b => b.BottomM).ToList();

        List<Band> levels;
        if (settings.AnalysisLevelNames is { Length: > 0 } wantedLevels)
        {
            levels = [];
            foreach (string name in wantedLevels)
            {
                var band = stack.FirstOrDefault(b =>
                    b.Layer.Kind == StackupKind.Conductor &&
                    string.Equals(b.Layer.Name, name, StringComparison.Ordinal));
                if (band is null)
                    return PlanarExtractionResult.No(
                        $"This EM setup names '{name}' as one of its analysis levels, but technology " +
                        $"'{tech.Name}' has no conductor stackup layer with that name.", notes);
                if (band.Layer.IsGroundReference)
                    return PlanarExtractionResult.No(
                        $"This EM setup names '{name}' as an analysis level, but that conductor is " +
                        "marked as a GROUND REFERENCE in the technology. The ground plane is the " +
                        "laterally infinite plane the Green's function handles analytically — it is " +
                        "not meshed, and a finite ground pour is not built. Name a signal conductor.",
                        notes);
                if (!levels.Any(b => b.Index == band.Index)) levels.Add(band);
            }
            levels = [.. levels.OrderBy(b => b.BottomM)];
        }
        else if (settings.SignalStackupLayerName is { Length: > 0 } wanted)
        {
            var named = stack.FirstOrDefault(b =>
                b.Layer.Kind == StackupKind.Conductor &&
                string.Equals(b.Layer.Name, wanted, StringComparison.Ordinal));
            if (named is null)
                return PlanarExtractionResult.No(
                    $"This EM setup names '{wanted}' as its signal conductor, but technology " +
                    $"'{tech.Name}' has no conductor stackup layer with that name.", notes);
            levels = [named];
        }
        else
        {
            levels = signalBands;
        }

        // Artwork on a signal layer the analysis does not include is DROPPED, and said so — a shape
        // that silently vanishes from a full-wave solve is the failure this note exists to prevent.
        var levelIndices = levels.Select(b => b.Index).ToHashSet();
        int droppedLevels = signalBands.Count(b => !levelIndices.Contains(b.Index));
        if (droppedLevels > 0)
            notes.Add($"{droppedLevels} signal conductor layer(s) carry artwork but are NOT in this " +
                      $"EM setup's analysis levels ({string.Join(", ", levels.Select(b => $"'{b.Layer.Name}'"))}). " +
                      "Their shapes are not meshed and contribute nothing to the answer. Add them to " +
                      "the setup's level list if they are part of the structure.");

        var signal = levels[0];       // the LOWEST level — the one the slab's top surface is

        // ── R-em-4: ground is the TOP SURFACE of the highest ground-designated conductor below ──
        var groundBand = stack
            .Where(b => b.Layer.Kind == StackupKind.Conductor && b.Layer.IsGroundReference && b.TopM <= signal.BottomM)
            .OrderByDescending(b => b.TopM)
            .FirstOrDefault();

        double groundTopM;
        if (groundBand is not null)
        {
            groundTopM = groundBand.TopM;

            // ── SAY WHICH CONDUCTOR THE RETURN PATH IS ──────────────────────────────────────────
            //
            // R-em-4's rule is a 2%-scale trap (this file's own header records it costing the Tier A
            // oracle when the boundary condition was taken literally instead of the designated
            // conductor), and on a stackup with several metal layers "the highest ground-designated
            // conductor BELOW the signal" is not something a user can read off the panel — every
            // port's negative terminal is this one plane and there is no per-port control for it.
            //
            // It was reported only in the FALLBACK case below, which is the case where the answer is
            // least likely to be what anyone wanted. The normal case said nothing at all: the
            // panel's own "Ground reference" row is bound to the CROSS-SECTION readback, which a
            // full-wave run does not produce.
            notes.Add(
                $"Every port returns through '{groundBand.Layer.Name}', the ground-designated " +
                $"conductor at {groundBand.TopM * 1e6:G4} µm — the highest one below the " +
                $"signal level at {signal.BottomM * 1e6:G4} µm. That plane is the negative " +
                "terminal of every port in this run and is not selectable per port; it is modelled " +
                "as laterally infinite. To return through a different conductor, designate that one " +
                "as the ground reference in the technology editor.");
        }
        else if (tech.Stackup.Bottom == BoundaryCondition.Ground)
        {
            groundTopM = stack[0].BottomM;
            notes.Add(
                $"No conductor layer in technology '{tech.Name}' is marked as a ground reference, so " +
                "the ground plane was taken from Stackup.Bottom = Ground at the bottom of the stack. " +
                "Mark the return-path conductor as a ground reference in the technology editor to " +
                "place it exactly.");
        }
        else
        {
            return PlanarExtractionResult.No(UngroundedRefusal(tech, signal, stack), notes);
        }

        // ── The slab: the dielectric between ground and the LOWEST level ──────────────────────
        double slabHeight = signal.BottomM - groundTopM;
        if (!(slabHeight > 0))
            return PlanarExtractionResult.No(
                $"The signal conductor '{signal.Layer.Name}' sits at or below the ground plane, so " +
                "there is no dielectric slab between them to solve on. Check the stackup order in the " +
                "technology editor.", notes);

        var slabBands = stack
            .Where(b => b.Layer.Kind == StackupKind.Dielectric &&
                        b.BottomM >= groundTopM - 1e-15 && b.TopM <= signal.BottomM + 1e-15)
            .ToList();
        if (slabBands.Count == 0)
            return PlanarExtractionResult.No(
                $"There is no dielectric stackup layer between the ground plane and the signal " +
                $"conductor '{signal.Layer.Name}', so the slab this kernel solves on has no material. " +
                "Add the substrate to the stackup in the technology editor.", notes);

        if (slabBands.Count > 1)
        {
            // L9d narrows what this is ABOUT rather than deleting it: the general stack now carries
            // as many dielectrics as it likes ABOVE the lowest level, but the de-embedding's Z_c is
            // still γ/(jωC_pul) with C_pul from an electrostatic IMAGE SERIES over one grounded slab,
            // and a stratified region between ground and the lowest metal is not that problem.
            var names = string.Join(", ", slabBands.Select(b => $"'{b.Layer.Name}'"));
            return PlanarExtractionResult.No(
                $"There are {slabBands.Count} dielectric layers between the ground plane and the " +
                $"lowest analysis level '{signal.Layer.Name}' ({names}). L9's Green's function handles " +
                "a stratified medium happily — what does not is the de-embedding: Z_c is γ/(jωC_pul) " +
                "and C_pul comes from an electrostatic image series over ONE grounded slab, so a " +
                "stratified region UNDER the feed would renormalise every published s-parameter by " +
                "the wrong reference. Dielectrics ABOVE the lowest level are fine and are carried. " +
                "Merge the layers under the feed into one substrate entry, or wait for a static " +
                "Green's function at interior heights (L9c's own un-run Tier 4).", notes);
        }

        var slabLayer = slabBands[0];
        if (!(slabLayer.Layer.Epsr >= 1))
            return PlanarExtractionResult.No(
                $"Stackup layer '{slabLayer.Layer.Name}' is the substrate but has εr = " +
                $"{slabLayer.Layer.Epsr:G4}. Relative permittivity is ≥ 1 — set it in the technology " +
                "editor's Stackup tab (FR-4 is 4.4, GaAs 12.9).", notes);

        var slab = new GroundedSlab(slabHeight,
            new EmMaterial(slabLayer.Layer.Epsr, slabLayer.Layer.TanD,
                           slabLayer.Layer.Mur <= 0 ? 1.0 : slabLayer.Layer.Mur));

        // The metal must be ON the slab's top surface — L8a's own refusal, asked here rather than
        // re-derived, so the two cannot drift.
        var host = GroundedSlab.CanHost(1, slabHeight, slabHeight);
        if (!host.Ok)
            return PlanarExtractionResult.No(host.Reason ?? "This stackup is not one the L8 kernel supports.", notes);

        // ── Geometry: DBU -> metres, with NO translation ───────────────────────────────────────
        // The plan-view overlay maps back with a single scalar precisely because nothing is centred
        // here (unlike the cross-section extractor, which centres so truncation is symmetric — a
        // requirement that has no analogue for a bounded piece of artwork). Do not add one.
        double perDbu = 1.0 / (dbuPerMicron * 1e6);

        // ── L9d: one polygon list PER LEVEL, in the level order the stack is built in ─────────
        var polysByLevel = new List<PlanarPolygon>[levels.Count];
        for (int i = 0; i < levels.Count; i++) polysByLevel[i] = [];

        int flattenedCurves = 0;
        foreach (var (shape, band) in conductorShapes)
        {
            int li = levels.FindIndex(b => b.Index == band.Index);
            if (li < 0) continue;                                   // a level the setup left out

            long tol = LayoutFlattener.ResolveTolDbu(shape, tech);
            IReadOnlyList<long[]> rings;
            try { rings = LayoutFlattener.Flatten(shape, tol); }
            catch (ArgumentOutOfRangeException) { ignoredOther++; continue; }

            if (rings.Count == 0 || rings[0].Length < 6) continue;
            if (LayoutBooleans.IsCurved(shape)) flattenedCurves++;

            var outer = ToPoints(rings[0], perDbu);
            var holes = new List<IReadOnlyList<EmPoint>>();
            for (int i = 1; i < rings.Count; i++)
                if (rings[i].Length >= 6) holes.Add(ToPoints(rings[i], perDbu));

            polysByLevel[li].Add(new PlanarPolygon(outer, holes.Count == 0 ? null : holes));
        }

        int totalPolys = polysByLevel.Sum(l => l.Count);
        if (totalPolys == 0)
            return PlanarExtractionResult.No(
                "None of the geometry on the analysis levels encloses an area to mesh — a planar " +
                "solver needs filled regions, not centrelines or markers.", notes);

        if (ignoredAnnotation > 0)
            notes.Add($"{ignoredAnnotation} label/bitmap shape(s) ignored — annotation is not artwork.");
        if (ignoredGround > 0)
            notes.Add($"{ignoredGround} shape(s) on the ground-designated conductor layer were ignored. " +
                      "The ground plane is the laterally infinite plane the Green's function handles " +
                      "analytically; a finite ground pour is not meshed, and modelling one is not " +
                      "part of L9.");
        if (ignoredOther > 0)
            notes.Add($"{ignoredOther} shape(s) were ignored — a Path is a centreline, and anything " +
                      "not bound to a stackup conductor or via entry is not metal as far as this " +
                      "technology is concerned.");
        if (flattenedCurves > 0)
            notes.Add($"{flattenedCurves} curved shape(s) were flattened to polygons at the layout's " +
                      "own flatten tolerance before meshing. That tolerance is an ARTWORK decision and " +
                      "the mesh does not inherit it — cell boundaries come from the analysis, never " +
                      "from a drawing's vertex count.");

        // ── R-msh-8a ──────────────────────────────────────────────────────────────────────────
        var alternatives = new List<PlanarAnalyticAlternative>();
        if (generatorIds is not null)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in generatorIds)
                if (seen.Add(id) && AnalyticAlternativeFor(id) is { } alt)
                    alternatives.Add(alt);
        }

        // ── L9d/D5 — the general MEDIUM, and each level's own z on one of its interfaces ───────
        var (mediumStack, levelZ, stackNote) = BuildMediumStack(stack, levels, groundTopM);
        if (stackNote is not null) notes.Add(stackNote);

        var conductorLayers = new PlanarConductorLayer[levels.Count];
        for (int i = 0; i < levels.Count; i++)
            conductorLayers[i] = new PlanarConductorLayer(
                levels[i].Layer.Name, polysByLevel[i], levels[i].Layer.SigmaSm,
                levels[i].TopM - levels[i].BottomM,
                // A one-level problem keeps ZM UNSET, so it stays on L8's shipped path bit-for-bit
                // (PlanarProblem.RequiresGeneralKernel). Naming the height is what turns the general
                // kernel on, and it must only happen when there is something general to say.
                levels.Count > 1 ? levelZ[i] : double.NaN);

        var vias = BuildVias(viaShapes, levels, tech, perDbu, notes, groundBand);

        var problem = new PlanarProblem(
            conductorLayers,
            slab,
            maxFrequencyHz,
            alternatives,
            levels.Count > 1 ? mediumStack : null,
            vias.Count > 0 ? vias : null);

        if (levels.Count > 1)
            notes.Add($"{levels.Count} conductor level(s) at z = " +
                      string.Join(", ", levelZ.Select(z => $"{z * 1e6:G4} µm")) +
                      $" above the ground plane, in the medium {mediumStack}. " +
                      (vias.Count > 0
                          ? $"{vias.Count} via(s) carry z-directed current between them."
                          : "No via joins them, so the levels couple only through the medium."));

        return PlanarExtractionResult.Yes(problem, notes);
    }

    private static EmPoint[] ToPoints(long[] xy, double perDbu)
    {
        var pts = new EmPoint[xy.Length / 2];
        for (int i = 0; i < pts.Length; i++)
            pts[i] = new EmPoint(xy[2 * i] * perDbu, xy[2 * i + 1] * perDbu);
        return pts;
    }

    /// <summary>
    /// <b>L9d/D5 — the ungrounded refusal, NARROWED to what L9b actually measured.</b>
    ///
    /// <para>Through L8 this said "an ungrounded stack needs the general layered stack, which arrives
    /// at L9". L9 has arrived, so the pointer has to be replaced by a reason — and L9b measured that
    /// there are TWO reasons, not one, which is the narrowing:</para>
    ///
    /// <list type="bullet">
    ///   <item><b>A DENSER half-space below than above is refused permanently, on a structural
    ///   obstruction rather than an accuracy budget.</b> It puts a SECOND branch point of the
    ///   spectrum inside the half-plane DCIM's sampling path runs into, and DCIM fits a sum of
    ///   exponentials, which is entire and cannot carry a cut. Measured on a 4 µm oxide over silicon:
    ///   59× the free-space kernel on G_q and 2.3e+4× on G_A. <c>Dcim.CanFit</c> is where that
    ///   refusal lives and this one quotes it rather than restating it.</item>
    ///   <item><b>An equal-or-lighter half-space below is FITTABLE</b> — <c>Dcim.CanFit</c> accepts
    ///   it, and L9b's measurement says so. What still blocks it is not the Green's function: it is
    ///   the DE-EMBEDDING. Z_c is γ/(jωC_pul), C_pul comes from an electrostatic image series over a
    ///   GROUNDED slab, and the calibration standard's end run is measured in substrate heights —
    ///   none of which exists without a ground plane.</item>
    /// </list>
    ///
    /// <para><b>The accepted set did not widen</b>, and saying that plainly is the point: what
    /// narrowed is the CLAIM, from one phase-number pointer to two measured, separately-addressable
    /// reasons with the remaining work named.</para>
    /// </summary>
    private static string UngroundedRefusal(Technology tech, Band lowest, List<Band> stack)
    {
        double belowEps = 1.0;
        var underneath = stack
            .Where(b => b.Layer.Kind == StackupKind.Dielectric && b.TopM <= lowest.BottomM + 1e-15)
            .OrderByDescending(b => b.TopM).FirstOrDefault();
        if (underneath is not null) belowEps = underneath.Layer.Epsr;

        return
            $"Technology '{tech.Name}' has no ground plane below the lowest analysis level " +
            $"'{lowest.Layer.Name}' — no conductor is marked as a ground reference and Stackup.Bottom " +
            "is Open. Two separate things are missing, and only the first is about the Green's " +
            "function:\n" +
            $"(1) The SPECTRUM. An open bottom half-space DENSER than the one above puts a second " +
            $"branch point inside the half-plane DCIM's sampling path runs into, and DCIM fits a sum " +
            $"of exponentials, which cannot carry a cut — measured at 59× the free-space kernel on " +
            $"G_q and 2.3e+4× on G_A. The dielectric under this level reads εᵣ = {belowEps:G4}; that " +
            $"is refused whenever it exceeds the medium above. An equal-or-lighter half-space is " +
            $"fittable and is NOT what blocks this.\n" +
            "(2) The DE-EMBEDDING, which is what actually blocks it here. The published " +
            "s-parameters are referenced to the line's own Z_c = γ/(jωC_pul); C_pul is differenced " +
            "from an electrostatic IMAGE SERIES over a grounded slab, and the calibration standard's " +
            "end run is measured in substrate heights. Neither quantity exists without a ground " +
            "plane. Mark the return-path conductor as a ground reference in the technology editor, " +
            "or set Stackup.Bottom = Ground.";
    }

    /// <summary>
    /// <b>L9d/D5 — the stackup's dielectric bands as a <see cref="LayerStack"/>, split so that every
    /// analysis level lands exactly on an interface.</b>
    ///
    /// <para>That last clause is the whole job. <c>PlanarProblem.CanSolve</c> refuses a level that is
    /// not on an interface of its own medium (L9c's first earned refusal), and a conductor band sits
    /// BETWEEN dielectric bands rather than inside one — so the layer list is built by walking z
    /// upward from the ground plane and cutting at every level position as well as at every material
    /// change. A conductor band contributes no thickness: a level is a sheet at one z, exactly as
    /// R-em-4a already established for the cross-section extractor.</para>
    /// </summary>
    private static (LayerStack Stack, double[] LevelZ, string? Note) BuildMediumStack(
        List<Band> bands, List<Band> levels, double groundTopM)
    {
        var levelZ = new double[levels.Count];
        for (int i = 0; i < levels.Count; i++) levelZ[i] = levels[i].BottomM - groundTopM;

        double topOfInterest = levels[^1].BottomM;
        var dielectrics = bands
            .Where(b => b.Layer.Kind == StackupKind.Dielectric && b.TopM > groundTopM + 1e-15)
            .OrderBy(b => b.BottomM).ToList();

        // Every boundary the stack must carry: the ground plane, each level, and each material change
        // up to (and including) the dielectric that encloses the topmost level.
        var cuts = new SortedSet<double> { groundTopM };
        foreach (var b in levels) cuts.Add(b.BottomM);
        foreach (var d in dielectrics)
        {
            if (d.BottomM > groundTopM + 1e-15 && d.BottomM <= topOfInterest + 1e-15) cuts.Add(d.BottomM);
            if (d.TopM    >  groundTopM + 1e-15 && d.TopM    <= topOfInterest + 1e-15) cuts.Add(d.TopM);
        }
        cuts.Add(topOfInterest);

        var ordered = cuts.Where(z => z <= topOfInterest + 1e-15).OrderBy(z => z).ToList();
        var built = new List<(double Lo, double Hi, EmMaterial M)>();
        string? note = null;

        for (int i = 0; i + 1 < ordered.Count; i++)
        {
            double lo = ordered[i], hi = ordered[i + 1];
            if (!(hi - lo > 0)) continue;
            double mid = 0.5 * (lo + hi);

            // R-em-4a, restated for the plan view: a CONDUCTOR's own z band is not a dielectric
            // region — it is absorbed into the dielectric ABOVE it. The stackup does not say what
            // fills a metal band where no metal is drawn, and "whatever is above it" is what matches
            // the validated cross-section problems (metal is deposited on the layer below and
            // encapsulated by what comes next). Getting this wrong inserts a spurious air gap the
            // thickness of the metal into the medium.
            var host = dielectrics.FirstOrDefault(d => d.BottomM <= mid && mid <= d.TopM)
                    ?? dielectrics.FirstOrDefault(d => Math.Abs(d.BottomM - hi) <= 1e-15);

            if (host is null)
            {
                note = "Part of the space between the analysis levels is not covered by a dielectric " +
                       "stackup entry and was taken as free space. Add the encapsulation or spacer to " +
                       "the stackup so the medium is stated rather than assumed.";
                built.Add((lo, hi, EmMaterial.Air));
                continue;
            }
            built.Add((lo, hi, new EmMaterial(host.Layer.Epsr, host.Layer.TanD,
                                              host.Layer.Mur <= 0 ? 1.0 : host.Layer.Mur)));
        }

        // Merge adjacent same-material slabs — the same rule the cross-section extractor uses, and
        // for the same reason: a boundary between two identical materials is not an interface, and
        // carrying it costs a cascade section per frequency for nothing. NEVER merge across a
        // boundary a LEVEL sits on: PlanarProblem.CanSolve requires every level to be on an interface.
        var levelSet = levelZ.Select(z => z + groundTopM).ToList();
        bool IsLevelBoundary(double z) => levelSet.Any(l => Math.Abs(l - z) <= 1e-15);

        var layers = new List<MediumLayer>();
        for (int i = 0; i < built.Count; i++)
        {
            double lo = built[i].Lo, hi = built[i].Hi;
            var m = built[i].M;
            while (i + 1 < built.Count && !IsLevelBoundary(hi) && SameMaterial(m, built[i + 1].M))
            {
                hi = built[i + 1].Hi;
                i++;
            }
            layers.Add(new MediumLayer(hi - lo, m));
        }

        return (new LayerStack(Termination.Pec, layers, Termination.Air), levelZ, note);
    }

    private static bool SameMaterial(EmMaterial a, EmMaterial b) =>
        Math.Abs(a.EpsR - b.EpsR) <= 1e-12 &&
        Math.Abs(a.TanD - b.TanD) <= 1e-12 &&
        Math.Abs(a.MuR  - b.MuR)  <= 1e-12;


    /// <summary>
    /// <b>L9d/D5 — <see cref="ViaShape"/>s become <see cref="PlanarVia"/>s, and the span comes from
    /// the technology rather than from the artwork.</b>
    ///
    /// <para><c>StackupLayer.SpanFromLayer</c>/<c>SpanToLayer</c> have existed since the via-primitive
    /// brief with the note "unread until L6/L9". This is L9. The artwork says WHERE a via is; the
    /// stackup says WHICH TWO CONDUCTORS it joins — which is the right split, because a board plates
    /// every via of a given kind between the same two layers whatever the drawing says.</para>
    ///
    /// <para><b>The footprint is squared, deliberately, and reported.</b> L9c's own mesher findings
    /// are that a via footprint must contribute hard GRIDLINES or the via silently vanishes, and that
    /// it must NOT get the edge grading a conductor rim gets. A round barrel staircased onto the
    /// shared tensor grid would contribute a gridline per facet and multiply the unknown count for no
    /// physics — so the barrel is replaced by the EQUAL-AREA square, which preserves the conducting
    /// cross-section (the quantity a via's own impedance depends on) and costs two gridlines per
    /// axis.</para>
    /// </summary>
    private static List<PlanarVia> BuildVias(
        List<(ViaShape Shape, StackupLayer Entry)> viaShapes, List<Band> levels, Technology tech,
        double perDbu, List<string> notes, Band? groundBand = null)
    {
        var vias = new List<PlanarVia>();
        if (viaShapes.Count == 0) return vias;

        int noSpan = 0, unknownLevels = 0, notAdjacent = 0, toGround = 0, wrongGround = 0;
        var wrongGroundNames = new List<string>();

        foreach (var (shape, entry) in viaShapes)
        {
            string? from = entry.SpanFromLayer, to = entry.SpanToLayer;
            if (from is not { Length: > 0 } || to is not { Length: > 0 }) { noSpan++; continue; }

            int a = levels.FindIndex(b => string.Equals(b.Layer.Name, from, StringComparison.Ordinal));
            int b2 = levels.FindIndex(b => string.Equals(b.Layer.Name, to, StringComparison.Ordinal));

            // ── R-gv-6 — a via to the GROUND the kernel actually models ───────────────────────
            //
            // A backside via names a conductor that is NOT an analysis level, so before L9's own
            // phase gate it fell into `unknownLevels` and was dropped with a note. That behaviour
            // was correct and reported; what was missing was the basis. It is now built — but ONLY
            // when the named conductor is the one the Green's function terminates on. The ground
            // plane this kernel has is the laterally infinite PEC at z = 0, which R-em-4 resolves
            // to exactly one band; a via to some OTHER ground-designated pour is a finite conductor
            // the kernel does not mesh, and turning it into an attachment would silently model a
            // different structure. The refusal must not simply disappear — that is the failure
            // mode L9's own FINDING 2 is about.
            int lower, upper;
            if (a < 0 || b2 < 0)
            {
                string missing = a < 0 ? from : to;
                int meshed     = a < 0 ? b2   : a;

                if (meshed < 0) { unknownLevels++; continue; }

                if (groundBand is null ||
                    !string.Equals(groundBand.Layer.Name, missing, StringComparison.Ordinal))
                {
                    wrongGround++;
                    if (!wrongGroundNames.Contains(missing)) wrongGroundNames.Add(missing);
                    continue;
                }

                lower = PlanarVia.GroundTerminal;
                upper = meshed;
                toGround++;
            }
            else
            {
                lower = Math.Min(a, b2);
                upper = Math.Max(a, b2);
                if (upper != lower + 1) { notAdjacent++; continue; }
            }

            // The equal-area square, centred on the via: side = d·√π/2.
            double d = shape.DrillSize * perDbu;
            if (!(d > 0)) d = shape.PadSize * perDbu;
            if (!(d > 0)) continue;
            double half = 0.5 * d * Math.Sqrt(Math.PI) / 2.0;
            double cx = shape.X * perDbu, cy = shape.Y * perDbu;

            vias.Add(new PlanarVia(lower, upper,
                [new PlanarPolygon([new EmPoint(cx - half, cy - half), new EmPoint(cx + half, cy - half),
                                    new EmPoint(cx + half, cy + half), new EmPoint(cx - half, cy + half)])],
                entry.SigmaSm));
        }

        if (toGround > 0)
            notes.Add($"{toGround} of them are BACKSIDE vias, running from a signal level down to " +
                      $"the ground plane '{groundBand!.Layer.Name}'. That plane is the laterally " +
                      "infinite conductor the Green's function handles analytically rather than a " +
                      "meshed level, so each is a half (attachment) basis whose return charge is the " +
                      "plane's own image.");
        if (wrongGround > 0)
            notes.Add($"{wrongGround} via shape(s) span a conductor ({string.Join(", ", wrongGroundNames)}) " +
                      "that is neither an analysis level nor the ground plane this kernel models, and " +
                      "were ignored. The only non-meshed conductor a via may terminate on is the " +
                      "ground reference R-em-4 resolves — a different ground pour is a finite " +
                      "conductor this kernel does not mesh, and treating it as the infinite plane " +
                      "would solve a structure you did not draw.");

        if (vias.Count > 0)
            notes.Add($"{vias.Count} via(s) were extracted. Each round barrel is replaced by the " +
                      "EQUAL-AREA square centred on it (side = 0.886 × the drill diameter), which " +
                      "preserves the conducting cross-section the via's own impedance depends on. A " +
                      "staircased circle would contribute a hard gridline per facet to the shared " +
                      "tensor grid every level uses, multiplying the unknown count for no physics.");
        if (noSpan > 0)
            notes.Add($"{noSpan} via shape(s) were ignored because their stackup via entry names no " +
                      "SpanFrom/SpanTo conductors. Which two levels a via joins is a property of the " +
                      "process, not of the drawing — set the span in the technology editor's Stackup " +
                      "tab. Ignoring it is safer than guessing: a via joining the wrong pair of " +
                      "levels renders perfectly and solves to a wrong answer.");
        if (unknownLevels > 0)
            notes.Add($"{unknownLevels} via shape(s) span conductors that are not among this EM " +
                      "setup's analysis levels, and were ignored.");
        if (notAdjacent > 0)
            notes.Add($"{notAdjacent} via shape(s) span two levels that are not ADJACENT in the " +
                      "analysis. A vertical basis pairs a cell with the cell directly above it, so a " +
                      "stacked via is a chain of vias — give one via entry per gap, or include the " +
                      "intervening level in the analysis.");

        return vias;
    }

    // ── Stackup -> z bands (restated from CrossSectionExtractor, not shared — see the header) ──

    private sealed record Band(StackupLayer Layer, double BottomM, double TopM, int Index);

    /// <summary>R-em-3: <c>Stackup.Layers</c> is ordered TOP to BOTTOM, so the stack accumulates
    /// thickness UPWARD from the bottom — walk the list in reverse. A Via entry contributes no
    /// thickness. Returned bottom-to-top.</summary>
    private static List<Band> BuildStack(Stackup stackup)
    {
        const double perDbu = 1.0 / (StackupDbuPerMicron * 1e6);
        var bands = new List<Band>();
        long y = 0;
        for (int i = stackup.Layers.Count - 1; i >= 0; i--)
        {
            var l = stackup.Layers[i];
            if (l.Kind == StackupKind.Via) continue;
            bands.Add(new Band(l, y * perDbu, (y + l.ThicknessDbu) * perDbu, i));
            y += l.ThicknessDbu;
        }
        return bands;
    }

    /// <summary>
    /// <b>Via entries are bound SEPARATELY from the z bands, and the reason is a bug L9's own phase
    /// gate found.</b> <see cref="BuildStack"/> skips every <c>StackupKind.Via</c> entry — correctly,
    /// since a via contributes no thickness and has no z band of its own — so a via's drawing layer
    /// never appeared in <see cref="BuildLayerBinding"/>'s map, and the <c>ViaShape</c> branch that
    /// looks for it could never match. <b>Every drawn via was silently ignored and
    /// <see cref="BuildVias"/> was unreachable</b>, which is exactly why it had no test: nothing could
    /// reach it. The two bindings answer different questions (where a layer sits in z, versus which
    /// two conductors a via joins) and are kept apart rather than merged, so the z arithmetic above
    /// stays untouched by anything a via does.
    /// </summary>
    private static Dictionary<LayerKey, StackupLayer> BuildViaBinding(Stackup stackup)
    {
        var map = new Dictionary<LayerKey, StackupLayer>();
        foreach (var l in stackup.Layers)
        {
            if (l.Kind != StackupKind.Via) continue;
            foreach (var key in l.DrawingLayers) map.TryAdd(key, l);
        }
        return map;
    }

    private static Dictionary<LayerKey, List<Band>> BuildLayerBinding(List<Band> stack)
    {
        var map = new Dictionary<LayerKey, List<Band>>();
        foreach (var b in stack)
            foreach (var key in b.Layer.DrawingLayers)
            {
                if (!map.TryGetValue(key, out var list)) map[key] = list = [];
                list.Add(b);
            }
        return map;
    }
}
