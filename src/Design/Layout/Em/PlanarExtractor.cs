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

        // ── MIM-1 — drawn REGIONS on a via-bound layer, and the silence that used to swallow them ──
        //
        // A MIM capacitor's plate connection is a rectangle or polygon nearly as large as the plate,
        // not a point. Until MIM-1 the only via artwork this loop recognised was a ViaShape, so a
        // region on a via-bound layer missed `binding` (BuildStack skips every Via entry, so a via's
        // drawing layer is never in that map) and fell into `ignoredOther` — counted with everything
        // else and reported by a sentence about Paths and unbound layers, which is exactly the wrong
        // advice for artwork that IS bound. Nothing on a via-bound layer may land there any more:
        // a region becomes a footprint, and whatever still cannot (a Path centreline, a shape that
        // encloses no area) is counted separately and named.
        var regionViaShapes = new List<(LayoutShape Shape, StackupLayer Entry)>();
        int ignoredViaPath = 0;

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

            // A Path is a centreline on a via layer for the same reason it is one on a conductor
            // layer — it encloses no area — but on a via layer it now gets its OWN sentence.
            if (s is PathShape)
            {
                if (viaBinding.ContainsKey(s.Layer)) ignoredViaPath++;
                else                                 ignoredOther++;
                continue;
            }

            // The conductor binding is asked FIRST, so a layer bound to both a conductor entry and a
            // via entry keeps behaving exactly as it did before MIM-1.
            if (binding.TryGetValue(s.Layer, out var bands))
            {
                var signalBand = bands.FirstOrDefault(b =>
                    b.Layer.Kind == StackupKind.Conductor && !b.Layer.IsGroundReference);
                if (signalBand is not null) { conductorShapes.Add((s, signalBand)); continue; }

                if (bands.Any(b => b.Layer.Kind == StackupKind.Conductor && b.Layer.IsGroundReference))
                { ignoredGround++; continue; }
            }

            if (viaBinding.TryGetValue(s.Layer, out var regionEntry))
            { regionViaShapes.Add((s, regionEntry)); continue; }

            ignoredOther++;
        }

        if (conductorShapes.Count == 0)
            // "Bind the layer to a conductor entry" is exactly wrong when the layer IS bound and the
            // conductor it is bound to is a GROUND-designated one — the advice sends the user to
            // re-do something already done. That case is reachable on any stackup with more than one
            // plane (a 4-layer board where the only artwork so far is an inner pour), so it is named
            // separately. User-reported, 2026-08-30.
            return PlanarExtractionResult.No(
                ignoredGround > 0
                    ? $"This EM setup's geometry is entirely on ground-designated conductor layers of " +
                      $"technology '{tech.Name}', so there is no signal conductor to solve for. A " +
                      "ground plane is not meshed — it is the laterally infinite return the Green's " +
                      "function handles analytically — so a run needs at least one shape on a " +
                      "conductor that is NOT marked as a ground reference. Draw the trace, or untick " +
                      "\"Ground reference\" on the conductor this artwork belongs to."
                    : $"This EM setup is pointed at geometry with nothing on a layer bound to a signal " +
                      $"conductor entry in technology '{tech.Name}'. Draw the artwork on a conductor " +
                      "layer, or bind the layer it is on to a conductor entry in the technology " +
                      "editor's Stackup tab.",
                notes);

        // ── L9d/D5 — WHICH LEVELS, bottom-to-top, and it is N or a refusal ───────────────────
        //
        // Through L8 this block picked exactly ONE level and refused every other case, because the
        // Green's function had one. It now selects a SET: the names the .cem gives, else the single
        // name R-em-4b's older field gives, else every signal conductor that actually carries
        // artwork. Ordered bottom-to-top because R-via-5 and R-msh-2 both index by that order.
        var signalBands = conductorShapes.Select(c => c.Band).Distinct().OrderBy(b => b.SheetM).ToList();

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
            levels = [.. levels.OrderBy(b => b.SheetM)];
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

        // ── MIM-7 — a PATTERNED dielectric enters the medium only when its plate is analysed ──
        //
        // Everything above decided WHICH LEVELS; that is also the honest per-run answer to "does
        // this run contain capacitors?", so it is the first point at which a tie can be resolved.
        // Deactivating one changes z arithmetic (a material becomes air, and the conductor beneath
        // gives its sheet back to the bottom of its band), so the stack is REBUILT rather than
        // patched, and every band already in hand is re-resolved by its stackup index.
        var analysed = levels.Select(b => b.Layer.Name).ToHashSet(StringComparer.Ordinal);
        var effectiveStackup = PatternedDielectric.Deactivate(
            tech.Stackup, analysed.Contains, revertSheetSurface: true, notes);
        if (effectiveStackup is not null)
        {
            stack  = BuildStack(effectiveStackup);
            levels = [.. levels.Select(b => stack.First(x => x.Index == b.Index)).OrderBy(b => b.SheetM)];
            // `conductorShapes` keeps its OLD bands on purpose: everything downstream matches them
            // by `Band.Index`, which is the stackup position and is unchanged by the rebuild.
        }

        var signal = levels[0];       // the LOWEST level — the one the slab's top surface is

        // ── R-em-4: ground is the TOP SURFACE of the highest ground-designated conductor below ──
        //
        // MIM-6: this is R-em-4's own rule and is NOT `SheetAt`. A ground plane is not a meshed
        // level with a sheet to place — it is the boundary the Green's function terminates on, and
        // the boundary is the metal's top surface whatever a ground entry's `SheetAt` says. Do not
        // "unify" the two: reading `SheetAt` here would let a stray Bottom on a ground entry drop
        // the reference plane by a metal thickness on every technology that carries one.
        var groundBand = stack
            .Where(b => b.Layer.Kind == StackupKind.Conductor && b.Layer.IsGroundReference && b.TopM <= signal.SheetM)
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
                $"signal level at {signal.SheetM * 1e6:G4} µm. That plane is the negative " +
                "terminal of every port in this run and is not selectable per port; it is modelled " +
                "as laterally infinite. To return through a different conductor, designate that one " +
                "as the ground reference in the technology editor.");
        }
        else if (tech.Stackup.Bottom == BoundaryCondition.Ground)
        {
            groundTopM = stack[0].BottomM;

            // `groundBand` is null for TWO different reasons, and saying the wrong one is worse than
            // saying nothing: the query above is "the highest ground-designated conductor BELOW this
            // signal level", so it also comes back empty on a stackup that HAS a designated ground
            // sitting ABOVE the signal. On every 2-layer technology the two coincide (the only
            // candidate is the bottom conductor), which is why the message could say "none is marked"
            // unconditionally and be right — until the first technology with an INNER ground plane,
            // where a trace on a lower layer was told its technology designates no ground at all
            // while the Stackup tab plainly showed one ticked. User-reported, 2026-08-30.
            var above = stack
                .Where(b => b.Layer.Kind == StackupKind.Conductor && b.Layer.IsGroundReference)
                .OrderBy(b => b.TopM)
                .ToList();

            notes.Add(above.Count == 0
                ? $"No conductor layer in technology '{tech.Name}' is marked as a ground reference, so " +
                  "the ground plane was taken from Stackup.Bottom = Ground at the bottom of the stack. " +
                  "Mark the return-path conductor as a ground reference in the technology editor to " +
                  "place it exactly."
                : $"The signal level '{signal.Layer.Name}' is BELOW every ground-designated conductor " +
                  $"in technology '{tech.Name}' ({string.Join(", ", above.Select(b => $"'{b.Layer.Name}'"))}), " +
                  "so none of them can be its return path — a port returns through a plane BENEATH the " +
                  "conductor it feeds. The ground plane was taken from Stackup.Bottom = Ground at the " +
                  "bottom of the stack instead, which is further away than the technology's own plane " +
                  "and will read as a higher impedance. Either designate a conductor below this level " +
                  "as a ground reference, or run this level's structure against the plane it is " +
                  "actually referenced to.");
        }
        else
        {
            return PlanarExtractionResult.No(UngroundedRefusal(tech, signal, stack), notes);
        }

        // ── The slab: the dielectric between ground and the LOWEST level ──────────────────────
        double slabHeight = signal.SheetM - groundTopM;
        if (!(slabHeight > 0))
        {
            // "Check the stackup order" is the right advice only when the order really is wrong. The
            // commoner way to arrive here is a correctly-ordered board whose BOTTOM conductor is
            // being treated as the signal: it sits on the Stackup.Bottom = Ground boundary, so the
            // slab has zero height and nothing is misordered at all.
            bool onTheBottomBoundary = ReferenceEquals(signal, stack[0]);
            return PlanarExtractionResult.No(
                $"The signal conductor '{signal.Layer.Name}' sits at or below the ground plane, so " +
                "there is no dielectric slab between them to solve on. " +
                (onTheBottomBoundary
                    ? "It is the bottom conductor of the stackup, resting directly on the " +
                      "Stackup.Bottom = Ground boundary — there is no dielectric beneath it to be a " +
                      "slab. Artwork on the bottom conductor is a ground pour or a backside feature, " +
                      "not a signal level: either mark that conductor as a ground reference (its " +
                      "shapes are then ignored with a note rather than refused), or move the trace to " +
                      "a conductor that has a ground-designated plane beneath it."
                    : "Check the stackup order in the technology editor."), notes);
        }

        var slabBands = stack
            .Where(b => b.Layer.Kind == StackupKind.Dielectric &&
                        b.BottomM >= groundTopM - 1e-15 && b.TopM <= signal.SheetM + 1e-15)
            .ToList();
        if (slabBands.Count == 0)
            return PlanarExtractionResult.No(
                $"There is no dielectric stackup layer between the ground plane and the signal " +
                $"conductor '{signal.Layer.Name}', so the slab this kernel solves on has no material. " +
                "Add the substrate to the stackup in the technology editor.", notes);

        foreach (var b in slabBands)
            if (!(b.Layer.Epsr >= 1))
                return PlanarExtractionResult.No(
                    $"Stackup layer '{b.Layer.Name}' is under the lowest analysis level but has εr = " +
                    $"{b.Layer.Epsr:G4}. Relative permittivity is ≥ 1 — set it in the technology " +
                    "editor's Stackup tab (FR-4 is 4.4, GaAs 12.9).", notes);

        // ── MIM-4 — the STRATIFIED SUB-FEED REGION IS CARRIED, not refused ────────────────────
        //
        // The refusal that stood here said a stratified region under the feed "would renormalise
        // every published s-parameter by the wrong reference", because C_pul came from an image
        // series over ONE grounded slab, and told the user to "merge the layers under the feed".
        // That merge was a change to the physics dressed as a workaround. The medium built below
        // has always carried the real layers; what could not was the de-embedding, and MIM-4's
        // InteriorStaticImages closes it — PlanarSolve solves D7's electrostatics at the port
        // level's own z, in the real stack.
        //
        // The GroundedSlab below is still built, and is now purely a SIZING object: the calibration
        // standards' geometry, the branch-continuation β seed, the accelerated near-radius floor,
        // and the mesh. None of those is the published reference impedance any more. Where the
        // sub-feed region is stratified it is the SERIES-CAPACITANCE equivalent — h/ε_eff =
        // Σ d_i/ε_i, exactly the electrostatic equivalent of the layers in series, which is the
        // right average for every one of those uses and reduces to the single layer's own εᵣ, bit
        // for bit, when there is only one.
        var slabLayer = slabBands[0];
        EmMaterial slabMaterial;
        if (slabBands.Count == 1)
        {
            slabMaterial = new EmMaterial(slabLayer.Layer.Epsr, slabLayer.Layer.TanD,
                                          slabLayer.Layer.Mur <= 0 ? 1.0 : slabLayer.Layer.Mur);
        }
        else
        {
            double invEps = 0, thickness = 0, tanWeighted = 0, muWeighted = 0;
            foreach (var b in slabBands)
            {
                double d = b.TopM - b.BottomM;
                if (!(d > 0)) continue;
                invEps      += d / b.Layer.Epsr;
                tanWeighted += d * b.Layer.TanD;
                muWeighted  += d * (b.Layer.Mur <= 0 ? 1.0 : b.Layer.Mur);
                thickness   += d;
            }
            double epsEff = thickness > 0 && invEps > 0 ? thickness / invEps : slabLayer.Layer.Epsr;
            slabMaterial = new EmMaterial(epsEff,
                                          thickness > 0 ? tanWeighted / thickness : slabLayer.Layer.TanD,
                                          thickness > 0 ? muWeighted / thickness : 1.0);

            var names = string.Join(", ", slabBands.Select(b => $"'{b.Layer.Name}'"));
            notes.Add(
                $"There are {slabBands.Count} dielectric layers between the ground plane and the " +
                $"lowest analysis level '{signal.Layer.Name}' ({names}); all of them are carried into " +
                "the medium at their stated thicknesses, and the de-embedding's C_pul is solved in " +
                $"that medium at the level's own height. εᵣ = {epsEff:G4} — the series-capacitance " +
                "equivalent of those layers — is used only to SIZE the calibration standards, the " +
                "mesh and the phase seed, never as the published reference impedance.");
        }

        var slab = new GroundedSlab(slabHeight, slabMaterial);

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

        // ── MIM-1 — the same shape -> PlanarPolygon conversion, for via-bound regions ─────────
        //
        // Deliberately the conductor path's own conversion rather than a second one: outer ring plus
        // holes, the layout's own flatten tolerance, the same degenerate-ring floor. A via footprint
        // and a conductor footprint are the same kind of artwork resolved onto the same tensor grid,
        // and two conversions that could drift apart would show up as a via that meshes to a slightly
        // different set of cells than the metal it lands on.
        var regionViaPolys = new List<(PlanarPolygon Poly, StackupLayer Entry)>();
        int ignoredViaRegion = 0;
        foreach (var (shape, entry) in regionViaShapes)
        {
            long tol = LayoutFlattener.ResolveTolDbu(shape, tech);
            IReadOnlyList<long[]> rings;
            try { rings = LayoutFlattener.Flatten(shape, tol); }
            catch (ArgumentOutOfRangeException) { ignoredViaRegion++; continue; }

            if (rings.Count == 0 || rings[0].Length < 6) { ignoredViaRegion++; continue; }
            if (LayoutBooleans.IsCurved(shape)) flattenedCurves++;

            var viaOuter = ToPoints(rings[0], perDbu);
            var viaHoles = new List<IReadOnlyList<EmPoint>>();
            for (int i = 1; i < rings.Count; i++)
                if (rings[i].Length >= 6) viaHoles.Add(ToPoints(rings[i], perDbu));

            regionViaPolys.Add((new PlanarPolygon(viaOuter, viaHoles.Count == 0 ? null : viaHoles), entry));
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
        // MIM-1 — the two things still ignorable on a via-bound layer, each named rather than
        // folded into the sentence above, which would send the user to re-bind a layer that is
        // already bound.
        if (ignoredViaPath > 0)
            notes.Add($"{ignoredViaPath} Path shape(s) on a via-bound drawing layer were ignored. A " +
                      "Path is a centreline with no enclosed area, and a via footprint is a region — " +
                      "draw the connection as a rectangle or a polygon, or place a via primitive.");
        if (ignoredViaRegion > 0)
            notes.Add($"{ignoredViaRegion} shape(s) on a via-bound drawing layer enclose no area and " +
                      "were ignored. A via footprint is meshed by the cells it covers, so a shape " +
                      "with no interior covers nothing.");
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

        var vias = BuildVias(viaShapes, regionViaPolys, levels, perDbu, notes, groundBand);

        // MIM-4 — a STRATIFIED medium turns the general kernel on even at one level. Before this
        // brief that case could not arise: a stratified region under the lowest level was refused at
        // extraction, and with one level there is nothing above it in the stack, so a one-level
        // problem was always one dielectric. Now that the layers are carried, handing L8's one-slab
        // kernel a stack it does not describe would be exactly the plausible-wrong-answer failure
        // L9d's own D5 note guards against. A genuinely single-slab problem still yields
        // LayerCount == 1 here and stays on the shipped path, bit for bit.
        bool generalMedium = levels.Count > 1 || mediumStack.LayerCount > 1;

        var problem = new PlanarProblem(
            conductorLayers,
            slab,
            maxFrequencyHz,
            alternatives,
            generalMedium ? mediumStack : null,
            vias.Count > 0 ? vias : null);

        if (levels.Count > 1)
            // MIM-6 — the z is printed WITH the surface of the band it sits on. A level's z is
            // otherwise unreadable against the process data: 103 µm for a Metal1 whose band runs
            // 100 to 103 is either a mistake or a deliberate reference-surface choice, and the note
            // is the only place a user can tell which.
            notes.Add($"{levels.Count} conductor level(s) at z = " +
                      string.Join(", ", levels.Select((b, i) =>
                          $"{levelZ[i] * 1e6:G4} µm ({SurfaceOf(b.Layer).ToString().ToLowerInvariant()} " +
                          $"of '{b.Layer.Name}')")) +
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
            .Where(b => b.Layer.Kind == StackupKind.Dielectric && b.TopM <= lowest.SheetM + 1e-15)
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
        for (int i = 0; i < levels.Count; i++) levelZ[i] = levels[i].SheetM - groundTopM;

        double topOfInterest = levels[^1].SheetM;
        var dielectrics = bands
            .Where(b => b.Layer.Kind == StackupKind.Dielectric && b.TopM > groundTopM + 1e-15)
            .OrderBy(b => b.BottomM).ToList();

        // Every boundary the stack must carry: the ground plane, each level, and each material change
        // up to (and including) the dielectric that encloses the topmost level.
        var cuts = new SortedSet<double> { groundTopM };
        foreach (var b in levels) cuts.Add(b.SheetM);
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
            // region — it is absorbed into a NEIGHBOURING dielectric. The stackup does not say what
            // fills a metal band where no metal is drawn, and which neighbour takes it is not a free
            // choice: it is the one PAIRED with where that conductor's sheet sits, which is what
            // keeps the sheet on an interface of the medium by construction.
            //
            //   Bottom (the default, and every technology before MIM-6): sheet on the band's bottom,
            //   band absorbed into the dielectric ABOVE — metal deposited on the layer below and
            //   encapsulated by what comes next, which is what the validated cross-section problems
            //   model and what makes a microstrip's height come out as the substrate thickness.
            //
            //   Top (MIM-6): sheet on the band's top, band absorbed into the dielectric BELOW — what
            //   a capacitor's LOWER plate needs, so the gap between the plate sheets is the capacitor
            //   dielectric alone instead of that dielectric plus the plate's own metal.
            //
            // Getting either one backwards inserts a spurious region the thickness of the metal into
            // the medium — and on a plate pair that region IS the answer.
            var host = dielectrics.FirstOrDefault(d => d.BottomM <= mid && mid <= d.TopM);
            if (host is null)
            {
                var metal = bands.FirstOrDefault(b => b.Layer.Kind == StackupKind.Conductor &&
                                                      b.BottomM <= mid && mid <= b.TopM);
                host = metal is not null && SurfaceOf(metal.Layer) == ConductorSheetSurface.Top
                    ? dielectrics.FirstOrDefault(d => Math.Abs(d.TopM    - lo) <= 1e-15)
                    : dielectrics.FirstOrDefault(d => Math.Abs(d.BottomM - hi) <= 1e-15);
            }

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
    ///
    /// <para><b>MIM-1 — a via is also a drawn REGION, and that footprint is NOT squared.</b> The
    /// equal-area substitution above exists so a circle nobody drew does not staircase; a rectangle
    /// or polygon drawn on a via-bound layer already IS the footprint, and it reaches the mesher at
    /// the outline the user drew. A MIM capacitor's plate connection is exactly that: a region
    /// nearly as large as the plate itself. Both kinds share every rule below — the span and the
    /// conductivity come from the stackup entry, and the noSpan / unknownLevels / notAdjacent /
    /// toGround / wrongGround accounting is one accounting.</para>
    /// </summary>
    private static List<PlanarVia> BuildVias(
        List<(ViaShape Shape, StackupLayer Entry)> viaShapes,
        List<(PlanarPolygon Poly, StackupLayer Entry)> regionViaPolys,
        List<Band> levels, double perDbu, List<string> notes, Band? groundBand = null)
    {
        var vias = new List<PlanarVia>();
        if (viaShapes.Count == 0 && regionViaPolys.Count == 0) return vias;

        int noSpan = 0, unknownLevels = 0, notAdjacent = 0, toGround = 0, wrongGround = 0;
        int pointVias = 0, regionVias = 0, regionPolys = 0;
        var wrongGroundNames = new List<string>();

        // ── Which two terminals does this stackup entry name? ─────────────────────────────────
        //
        // Shared by both artwork kinds ON PURPOSE (MIM-1): the whole point of "the artwork says
        // WHERE, the stackup says WHICH TWO CONDUCTORS" is that the answer cannot depend on how the
        // via was drawn, and a second copy of this block is exactly how it would come to. `count` is
        // how many SHAPES to charge to whichever counter bites, since a region entry stands for
        // several drawn shapes and a point via for one.
        //
        // ── R-gv-6 — a via to the GROUND the kernel actually models ───────────────────────────
        //
        // A backside via names a conductor that is NOT an analysis level, so before L9's own phase
        // gate it fell into `unknownLevels` and was dropped with a note. That behaviour was correct
        // and reported; what was missing was the basis. It is now built — but ONLY when the named
        // conductor is the one the Green's function terminates on. The ground plane this kernel has
        // is the laterally infinite PEC at z = 0, which R-em-4 resolves to exactly one band; a via
        // to some OTHER ground-designated pour is a finite conductor the kernel does not mesh, and
        // turning it into an attachment would silently model a different structure. The refusal must
        // not simply disappear — that is the failure mode L9's own FINDING 2 is about.
        bool Terminals(StackupLayer entry, int count, out int lower, out int upper)
        {
            lower = upper = 0;

            string? from = entry.SpanFromLayer, to = entry.SpanToLayer;
            if (from is not { Length: > 0 } || to is not { Length: > 0 }) { noSpan += count; return false; }

            int a  = levels.FindIndex(b => string.Equals(b.Layer.Name, from, StringComparison.Ordinal));
            int b2 = levels.FindIndex(b => string.Equals(b.Layer.Name, to,   StringComparison.Ordinal));

            if (a < 0 || b2 < 0)
            {
                string missing = a < 0 ? from : to;
                int meshed     = a < 0 ? b2   : a;

                if (meshed < 0) { unknownLevels += count; return false; }

                if (groundBand is null ||
                    !string.Equals(groundBand.Layer.Name, missing, StringComparison.Ordinal))
                {
                    wrongGround += count;
                    if (!wrongGroundNames.Contains(missing)) wrongGroundNames.Add(missing);
                    return false;
                }

                lower = PlanarVia.GroundTerminal;
                upper = meshed;
                toGround += count;
                return true;
            }

            lower = Math.Min(a, b2);
            upper = Math.Max(a, b2);
            if (upper != lower + 1) { notAdjacent += count; return false; }
            return true;
        }

        foreach (var (shape, entry) in viaShapes)
        {
            if (!Terminals(entry, 1, out int lower, out int upper)) continue;

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
            pointVias++;
        }

        // ── MIM-1 — the drawn REGIONS, GROUPED BY THEIR STACKUP ENTRY ─────────────────────────
        //
        // One PlanarVia per via entry carrying every footprint drawn on it, rather than one per
        // shape. Two reasons, and the second is a correctness one:
        //
        //   • the span, the conductivity and the ground rule all come from the ENTRY, so every
        //     region on it resolves to the identical pair of terminals — splitting them would be
        //     one identical record per shape;
        //   • the mesher scans every grid cell against a via's polygon list and stops at the FIRST
        //     one that covers it, so two OVERLAPPING footprints in one PlanarVia contribute one
        //     vertical basis to a shared cell. As separate PlanarVias they would each contribute
        //     one, silently doubling the metal in the overlap. A plate connection drawn as several
        //     touching or overlapping rectangles is an ordinary thing to draw.
        //
        // The counters below stay in SHAPES, because that is what the user drew and can go and look
        // at; only the PlanarVia count is per entry.
        foreach (var group in regionViaPolys.GroupBy(r => r.Entry))
        {
            var entry = group.Key;
            int shapeCount = group.Count();

            if (!Terminals(entry, shapeCount, out int lower, out int upper)) continue;

            vias.Add(new PlanarVia(lower, upper, [.. group.Select(g => g.Poly)], entry.SigmaSm));
            regionVias++;
            regionPolys += shapeCount;
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

        if (pointVias > 0)
            notes.Add($"{pointVias} via(s) were extracted. Each round barrel is replaced by the " +
                      "EQUAL-AREA square centred on it (side = 0.886 × the drill diameter), which " +
                      "preserves the conducting cross-section the via's own impedance depends on. A " +
                      "staircased circle would contribute a hard gridline per facet to the shared " +
                      "tensor grid every level uses, multiplying the unknown count for no physics.");
        if (regionVias > 0)
            notes.Add($"{regionPolys} drawn region(s) became the footprints of {regionVias} via " +
                      "connection(s), at the outline you drew. A region via is meshed " +
                      "exactly as a point via is — one vertical basis per cell of the shared tensor " +
                      "grid the footprint covers that carries metal on both levels — so a plate " +
                      "connection nearly as large as its plate is an ordinary via, not a special " +
                      "case. Nothing is squared: the equal-area substitution applies to a round " +
                      "barrel, and a drawn outline already is the footprint.");
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

    /// <summary><see cref="SheetM"/> is where this band's zero-thickness ANALYSIS SHEET sits, which
    /// is not the same question as where the band is. Every level-z, slab-height and medium-cut
    /// decision below reads <see cref="SheetM"/>; <see cref="BottomM"/>/<see cref="TopM"/> stay the
    /// band's own extent and are what the absorption arithmetic and the conductor's reported
    /// thickness are written in (MIM-6).</summary>
    private sealed record Band(StackupLayer Layer, double BottomM, double TopM, int Index, double SheetM);

    /// <summary>MIM-6: which surface of its own band a conductor's sheet sits on. Null, and every
    /// non-conductor entry, is <see cref="ConductorSheetSurface.Bottom"/> — today's behaviour.</summary>
    private static ConductorSheetSurface SurfaceOf(StackupLayer l) =>
        l.Kind == StackupKind.Conductor ? l.SheetAt ?? ConductorSheetSurface.Bottom
                                        : ConductorSheetSurface.Bottom;

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
            double bottomM = y * perDbu, topM = (y + l.ThicknessDbu) * perDbu;
            bands.Add(new Band(l, bottomM, topM, i,
                SurfaceOf(l) == ConductorSheetSurface.Top ? topM : bottomM));
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
