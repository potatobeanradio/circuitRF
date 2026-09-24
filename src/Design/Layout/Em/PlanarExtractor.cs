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

/// <summary>
/// <b>RP-1 — which conductor this run terminates on, and who decided.</b> The <c>PlanarProblem</c>
/// itself cannot say: it is the neutral engine type and knows only a height, so a caller wanting to
/// REPORT the return plane (the panel, <c>circuitrf explain</c>) would otherwise have to re-derive
/// R-em-4 from the technology — a second spelling of the rule, which is the one thing this area
/// keeps refusing to grow.
/// </summary>
/// <param name="ConductorName">The stackup entry, or null when the plane came from
/// <c>Stackup.Bottom = Ground</c> rather than from a conductor.</param>
/// <param name="TopM">The z the medium terminates at, in metres — a conductor's TOP surface.</param>
/// <param name="Overridden">True when the <c>.cem</c> named it, false when R-em-4 inferred it.</param>
/// <param name="Flipped">RP-3: true when this run was solved with the stackup MIRRORED, because the
/// plane lies above the analysis levels in the technology's own orientation. <see cref="TopM"/> is
/// then measured in the flipped frame — downward from the top surface of the stackup — so a caller
/// reporting it must say so rather than print it against the Stackup tab.</param>
public sealed record PlanarReturnPlane(string? ConductorName, double TopM, bool Overridden,
                                       bool Flipped = false);

/// <summary>Either a <see cref="PlanarProblem"/>, or a refusal that names what is missing and where
/// the capability arrives — the same R-mom-17 shape every other refusal in this area uses.</summary>
public sealed record PlanarExtractionResult(
    PlanarProblem?           Problem,
    string?                  Refusal,
    /// <summary>EM-SEV R-emsev-1: every sentence this extraction produced, each carrying the one
    /// thing the reader needs before the words — whether it changed what was solved. This is the
    /// STORED list; <see cref="Notes"/> is a view of it.</summary>
    IReadOnlyList<EmFinding> Findings,
    PlanarReturnPlane?       ReturnPlane = null)
{
    public bool Ok => Problem is not null && Refusal is null;

    /// <summary>Every finding's text, class discarded — the spelling this result carried before
    /// EM-SEV, and the one the panel and the gates read. <b>Derived, never stored</b>: a second
    /// stored list is a list that drifts.</summary>
    public IReadOnlyList<string> Notes => EmFindings.Texts(Findings);

    /// <summary>Just the ones that say the answer is not what was drawn.</summary>
    public IReadOnlyList<string> Warnings => EmFindings.WarningTexts(Findings);

    public static PlanarExtractionResult No(string refusal, IEnumerable<EmFinding>? findings = null)
        => new(null, refusal, findings is null ? [] : [.. findings]);

    public static PlanarExtractionResult Yes(PlanarProblem p, IEnumerable<EmFinding>? findings = null,
                                             PlanarReturnPlane? returnPlane = null)
        => new(p, null, findings is null ? [] : [.. findings], returnPlane);
}

/// <summary>What <see cref="PlanarExtractor.SurveyArtwork"/> found in a layout, by stackup entry
/// NAME — the identity a <c>.cem</c>'s analysis-level list is written in, so the two can be compared
/// without resolving anything.</summary>
/// <param name="ConductorsWithArtwork">Conductor entries this layout draws filled artwork on.</param>
/// <param name="ConductorsDrawnViasSpan">Conductor entries a drawn via's stackup entry names as one
/// of its two ends. A via landing on a level the run excludes is a connection the designer STATED
/// and the answer does not contain.</param>
public sealed record EmArtworkSurvey(
    IReadOnlySet<string> ConductorsWithArtwork,
    IReadOnlySet<string> ConductorsDrawnViasSpan);

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

        var notes = new List<EmFinding>();

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

        // GI2 R-gi2-4 — the SKELETON refusal, and it has to come before everything below it.
        //
        // A stackup whose every conductor and dielectric is zero thick puts every band at z = 0, so
        // the first thing that noticed was the slab-height check, which then said "the signal sits at
        // or below the ground plane — mark a conductor as a ground reference or check the stackup
        // order". Every word of that is a wrong diagnosis of a stack whose order is fine and whose
        // thicknesses were simply never entered — which is exactly the document a Gerber import with
        // no job file now produces. Answer the real question here, once, before any geometry
        // reasoning can reach a misleading conclusion about it.
        //
        // Narrow on purpose: EVERY non-via entry zero, not any of them. A partly filled-in stackup is
        // a different state with its own per-layer refusals, and this must not widen into them.
        if (stack.All(b => b.Layer.ThicknessDbu == 0))
            return PlanarExtractionResult.No(
                $"Every conductor and dielectric in technology '{tech.Name}' has zero thickness, so the " +
                "stackup states the layers and their order but nothing about the substrate — there is " +
                "no height for a slab, no separation between levels and no material to solve in. Enter " +
                "a thickness for each layer, and a relative permittivity and loss tangent for each " +
                "dielectric, on the technology editor's Stackup tab. (A Gerber file set with no job " +
                "file carries none of those values, so an import creates this shape deliberately rather " +
                "than inventing a substrate.)");

        // ── Classify shapes against the stackup's DrawingLayers bindings ──────────────────────
        var binding = BuildLayerBinding(stack);
        var viaBinding = BuildViaBinding(tech.Stackup, out int nonPlatedViaEntries);
        if (nonPlatedViaEntries > 0)
            // GI1 R-gi1-2. Reported rather than silent: the shapes on those layers are still in the
            // layout and still drawn, so "my vias disappeared" needs an answer at the point the
            // decision was made.
            notes.Add($"{nonPlatedViaEntries} via stackup entr(y/ies) are marked NON-PLATED and were " +
                      "not extracted as conductors — a non-plated hole is a hole, not metal. Their " +
                      "artwork is unchanged; only the vertical conductor is absent.");
        var conductorShapes = new List<(LayoutShape Shape, Band Band)>();
        int ignoredAnnotation = 0, ignoredOther = 0;

        // ANT-11 §2 — artwork on a ground-designated conductor, kept with every ground band its layer
        // binds to so R-fg-2's selection can be made once the return plane is known.
        var groundShapes = new List<(LayoutShape Shape, List<Band> Bands)>();

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

        // ── MIM-11 — THE MASK THAT DEFINES A PATTERNED FILM IS ARTWORK, NOT CLUTTER ───────────
        //
        // A `PresentWithLayer` tie may name a DRAWING layer rather than a conductor entry, and on
        // this process that is the true statement: the nitride is streamed out as its own mask and
        // the plate is deposited on what it left. Such a layer is bound to no stackup entry, so
        // before MIM-11 its shapes reached `ignoredOther` and were reported as "not metal as far as
        // this technology is concerned" — which is correct about the metal and wrong about the run,
        // because those shapes are exactly what decides whether the film is in the medium at all.
        //
        // Collected here rather than in a second walk so the layout is read ONCE, and recorded
        // NON-EXCLUSIVELY (no `continue`): a mask layer that is also bound to a conductor or a via
        // keeps every classification it had, and only the final catch-all below changes.
        var maskLayers = PatternedDielectric.MaskLayers(tech);
        var maskShapes = new List<(LayoutShape Shape, string Mask)>();

        foreach (var s in shapes)
        {
            if (s is LabelShape or BitmapShape) { ignoredAnnotation++; continue; }

            bool onAMask = maskLayers.TryGetValue(s.Layer, out string? maskName);
            if (onAMask) maskShapes.Add((s, maskName!));

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

            // ── ANT-1 — A WIDTH-BEARING PATH IS METAL ────────────────────────────────────────
            //
            // Every Path used to be discarded here, on the stated premise that a Path "encloses no
            // area". That is true of a ZERO-width one and false of every other: a stroke carries a
            // Width, an end style and optional arcs, and the Gerber and board readers keep an
            // imported track as exactly that, primitive for primitive. So an imported board was
            // EM-simulated with its traces missing, and said nothing — one owner-supplied patch
            // board solved as a pure 1.6 pF capacitor (0.70 - j56.5 Ohm, 2.3 % of the port's power
            // leaving it) whose Zin did not move a digit when the mesh was doubled and conformal
            // cells switched on, which is what excludes mesh coarseness and leaves only "the
            // conductor is not in the model".
            //
            // A stroke therefore falls THROUGH to the ordinary dispatch below and is classified by
            // its layer like any other artwork — conductor binding first, exactly as before — and
            // is outlined into regions where the geometry is built (search ANT-1 there). Doing the
            // width test here rather than ahead of the dispatch is what keeps that order intact.
            //
            // A zero-width Path still IS a centreline: it is what the DXF reader produces for an
            // open polyline, it genuinely encloses no area, and meshing it as a hairline would
            // invent copper. It keeps the branch, and both its sentences now say ZERO-WIDTH.
            if (s is PathShape { Width: <= 0 })
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

                // ── ANT-11 §2 — THE GROUND POUR IS READ NOW, NOT ONLY COUNTED ─────────────
                //
                // This branch used to increment a counter and drop the geometry, and the note it
                // produced said so in a way that read as final: "a finite ground pour is not meshed,
                // and modelling one is not part of L9". The first half is still true and is R-fg-1 —
                // nothing below meshes this. The second half was the whole of it, and it threw away
                // the one thing that lets a run say how big the plane actually is in wavelengths,
                // which is the number that decides whether the infinite-plane assumption is
                // defensible at all. The shapes are kept WITH their bands, because R-fg-2 needs the
                // return plane's OWN pour and not the artwork on any ground layer — which band that
                // is is not known until R-em-4 has run, below.
                var groundBands = bands.Where(b =>
                    b.Layer.Kind == StackupKind.Conductor && b.Layer.IsGroundReference).ToList();
                if (groundBands.Count > 0) { groundShapes.Add((s, groundBands)); continue; }
            }

            if (viaBinding.TryGetValue(s.Layer, out var regionEntry))
            { regionViaShapes.Add((s, regionEntry)); continue; }

            // MIM-11 — read, not ignored. It bound to no conductor and no via because a mask is
            // neither; it still decided whether a layer of the medium is present.
            if (onAMask) continue;

            ignoredOther++;
        }

        if (conductorShapes.Count == 0)
            // "Bind the layer to a conductor entry" is exactly wrong when the layer IS bound and the
            // conductor it is bound to is a GROUND-designated one — the advice sends the user to
            // re-do something already done. That case is reachable on any stackup with more than one
            // plane (a 4-layer board where the only artwork so far is an inner pour), so it is named
            // separately. User-reported, 2026-08-30.
            return PlanarExtractionResult.No(
                groundShapes.Count > 0
                    ? $"This EM setup's geometry is entirely on ground-designated conductor layers of " +
                      $"technology '{tech.Name}' ({groundShapes.Count} shape(s)), so there is no " +
                      "signal conductor to solve for. A " +
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
        // artwork — less, in that last case, whatever the incidental-metal check below discards.
        // Ordered bottom-to-top because R-via-5 and R-msh-2 both index by that order.
        var signalBands = conductorShapes.Select(c => c.Band).Distinct().OrderBy(b => b.SheetM).ToList();

        // ── WHICH LEVELS THE PORTS CAN REACH — not the level set, a REACHABILITY oracle ──────
        //
        // A level is reachable when a port sits on it, or when a DRAWN via joins it to one that is.
        // This is deliberately NOT used as the level set, and the reason is worth stating because it
        // is the obvious design and it is wrong: a MIM capacitor's bottom plate is coupled to its top
        // plate by the capacitor dielectric and by nothing else — no port, no via — so seeding the
        // levels from the ports would silently drop half of every capacitor in the acceptance
        // fixtures. A parasitic stacked patch, a broadside-coupled pair and a floating shield are the
        // same shape. Field coupling is exactly what a full-wave kernel is FOR; a rule that can only
        // follow metal cannot be the one that decides what gets meshed.
        //
        // What it IS used for is one narrow question, below: whether the lowest level — the one that
        // sets the return plane for the entire run — is part of the structure or incidental metal.
        var portReach = LevelsThePortsReach(
            shapes, binding, viaShapes.Select(v => v.Entry).Concat(regionViaShapes.Select(v => v.Entry)),
            signalBands, out int portsSeen, out int portsUnresolved);

        List<Band> levels;
        var incidental = new List<Band>();
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

            // ── INCIDENTAL METAL MUST NOT SET THE RETURN PLANE ────────────────────────────────
            //
            // R-em-4 asks for the highest designated ground below the LOWEST level, so the lowest
            // level alone decides the reference plane for every port in the run. A Gerber import of
            // a 4-layer board brings in the inner-layer annular pads of every through-hole, which is
            // artwork on a signal conductor and therefore a level — and, being the lowest, the one
            // that answers that question. Two 0.485 mm rings nobody drew on purpose then pushed the
            // return plane past the real ground plane to the bottom of the board: a patch antenna
            // was solved over 1.67 mm of FR-4 instead of 0.3 mm, with its actual ground plane
            // absorbed into the dielectric as substrate. Nothing said so. User-reported, 2026-09-10.
            //
            // The test is BOTH halves and neither alone would do. A level no port reaches may be
            // perfectly real (that MIM plate), so unreachability cannot condemn it; and a level that
            // moves the return plane may be exactly what the user drew, so movement cannot either.
            // Only the conjunction — unreachable AND it drags the reference plane below a nearer
            // designated ground — describes metal that is in the way rather than in the structure.
            //
            // Iterative, because an import that left one such layer commonly left two.
            if (portReach.Count > 0)
            {
                while (levels.Count > 1 && !portReach.Contains(levels[0].Index))
                {
                    var asIs    = HighestGroundBelow(stack, levels[0].SheetM);
                    var without = HighestGroundBelow(stack, levels[1].SheetM);
                    if (without is null || (asIs is not null && without.TopM <= asIs.TopM)) break;

                    incidental.Add(levels[0]);
                    levels = [.. levels.Skip(1)];
                }
            }

            // Ports exist but none of them named a signal conductor, so the check above could not
            // run at all. Saying which is the difference between "nothing was wrong" and "your ports
            // could not be read" — a port label whose own drawing layer is not bound to a conductor
            // entry is the shape this takes in practice.
            else if (portsSeen > 0 && portsUnresolved == portsSeen)
                notes.Add($"{portsSeen} port label(s) are present but none of them resolves to a " +
                          "signal conductor layer, so this run could not check whether the conductor " +
                          "setting its return plane is one you are feeding. Place each port on the " +
                          "conductor it feeds, or list the analysis levels explicitly.");
        }

        if (incidental.Count > 0)
            notes.Add(
                $"{string.Join(", ", incidental.Select(b => $"'{b.Layer.Name}'"))} " +
                $"{(incidental.Count == 1 ? "carries artwork but no port reaches it, and it sits" : "carry artwork but no port reaches them, and they sit")} " +
                $"BELOW everything that is fed — so including " +
                $"{(incidental.Count == 1 ? "it" : "them")} would have put the return plane on a " +
                $"conductor further down the stackup than " +
                $"'{HighestGroundBelow(stack, levels[0].SheetM)?.Layer.Name ?? "the stack's own boundary"}', " +
                $"which is the plane under the metal being fed. " +
                $"{(incidental.Count == 1 ? "It was" : "They were")} left out of this run and " +
                $"{(incidental.Count == 1 ? "its shapes are" : "their shapes are")} not meshed. " +
                "That is what is wanted for the inner-layer pads a board import brings in with every " +
                "through-hole; it is NOT what is wanted for a real conductor on a lower level, which " +
                "you can put back by ticking it in this EM setup's analysis levels.");

        // Artwork on a signal layer the analysis does not include is DROPPED, and said so — a shape
        // that silently vanishes from a full-wave solve is the failure this note exists to prevent.
        //
        // The SENTENCE depends on who chose, and that distinction is the whole point of the note. "…
        // are NOT in this EM setup's analysis levels … add them to the setup's level list" is true
        // and useful when the user typed the list; said of a set the EXTRACTOR derived from the
        // ports, it blames the user for a decision they never made and sends them looking for a
        // setting that is empty. Name the rule that dropped them instead, and the one thing that
        // overrides it.
        var levelIndices = levels.Select(b => b.Index).ToHashSet();
        var incidentalIdx = incidental.Select(b => b.Index).ToHashSet();
        var dropped      = signalBands
            .Where(b => !levelIndices.Contains(b.Index) && !incidentalIdx.Contains(b.Index))
            .ToList();
        string levelList = string.Join(", ", levels.Select(b => $"'{b.Layer.Name}'"));
        //
        // EM-SEV R-emsev-1: a WARNING. Artwork that is drawn and not solved is the definition of
        // "the answer is not what was drawn", and it is the single line that would have told the
        // designer of the MIM capacitor what had happened to it.
        if (dropped.Count > 0)
            notes.Add(EmFinding.Warn(
                      $"{dropped.Count} signal conductor layer(s) carry artwork but are NOT in this " +
                      $"EM setup's analysis levels ({levelList}): " +
                      $"{string.Join(", ", dropped.Select(b => $"'{b.Layer.Name}'"))}. " +
                      "Their shapes are not meshed and contribute nothing to the answer. Add them to " +
                      "the setup's level list if they are part of the structure."));

        // ── MIM-7 — a PATTERNED dielectric enters the medium only when its plate is analysed ──
        //
        // Everything above decided WHICH LEVELS; that is also the honest per-run answer to "does
        // this run contain capacitors?", so it is the first point at which a tie can be resolved.
        // Deactivating one changes z arithmetic (a material becomes air, and the conductor beneath
        // gives its sheet back to the bottom of its band), so the stack is REBUILT rather than
        // patched, and every band already in hand is re-resolved by its stackup index.
        var analysed = levels.Select(b => b.Layer.Name).ToHashSet(StringComparer.Ordinal);
        // EM-SEV R-emsev-3 — the extractor knows BOTH halves, so the finding can be split on the
        // one that matters: `analysed` is what this run solves, `signalBands` is what the layout
        // actually draws on. A plate with no artwork is MIM-7's own case and stays a note; a plate
        // that is drawn and excluded is a capacitor missing from the answer.
        var drawnOn = signalBands.Select(b => b.Layer.Name).ToHashSet(StringComparer.Ordinal);
        // MIM-11 — the second namespace, and the films that came out PRESENT. A mask tie asks a
        // question about DRAWN ARTWORK rather than about this run's level list, which is why it is
        // answered from `maskShapes` and not from `analysed`.
        var maskDrawn = maskShapes.Select(m => m.Mask).ToHashSet(StringComparer.Ordinal);
        var carriedFilms = new List<CarriedFilm>();
        var effectiveStackup = PatternedDielectric.Deactivate(
            tech.Stackup, analysed.Contains, revertSheetSurface: true, notes, drawnOn.Contains,
            new PatternedFilmMask(tech.Layers, maskDrawn.Contains), carriedFilms);
        if (effectiveStackup is not null)
        {
            stack  = BuildStack(effectiveStackup);
            levels = [.. levels.Select(b => stack.First(x => x.Index == b.Index)).OrderBy(b => b.SheetM)];
            // `conductorShapes` keeps its OLD bands on purpose: everything downstream matches them
            // by `Band.Index`, which is the stackup position and is unchanged by the rebuild.
        }

        // ── RP-3 — A RETURN PLANE ABOVE THE LEVELS: SOLVE THE STACK UPSIDE DOWN ──────────────
        //
        // R-em-4 asks for a ground-designated conductor BELOW the lowest level, and that is not a
        // preference — the layered Green's function terminates on ONE laterally infinite plane and
        // every meshed level lives above it. A trace on the BOTTOM conductor of a board whose ground
        // plane is an inner layer breaks that rule while being an entirely ordinary structure, and
        // until this block the only answers were a fallback to `Stackup.Bottom = Ground` (a plane
        // further away than the real one, reported as a note and wrong by the ratio of the two
        // heights) or a refusal when the level sat on that boundary and the slab came out zero.
        //
        // Neither is necessary. Reflecting a structure in a horizontal plane is an EXACT symmetry of
        // an isotropic medium — the artwork's x and y are untouched, every material and every
        // thickness is what it was, and the s-parameters of the mirrored structure are the
        // s-parameters of the original. Flipping the stack puts the plane where the kernel needs it
        // and solves the problem the user actually drew. User-reported, 2026-09-12: the alternative
        // was hand-authoring a second `.ctech` whose stackup is typed in backwards, which is a copy
        // of the process data that nothing keeps in step with the original.
        //
        // WHAT IS FLIPPED, AND WHAT IS NOT. Only the z ARITHMETIC: each band keeps its stackup
        // `Index`, so every downstream match (`conductorShapes`, `groundShapes`, the via spans, which
        // are resolved by NAME) is untouched, and `Stackup.Layers` itself is never rewritten. The
        // one thing that is deliberately NOT a pure geometric reflection is the analysis SHEET: it
        // stays on the bottom of its own band IN THE FLIPPED FRAME, which is the surface facing the
        // plane, so the modelled height comes out as the substrate thickness — exactly the rule
        // `ConductorSheetSurface`'s own documentation states, applied in the frame being solved.
        //
        // WHEN IT FIRES. Only when the flipped frame resolves a usable plane and the stackup's own
        // orientation does not — or resolves only the `Stackup.Bottom` boundary where the flip finds
        // a conductor the technology actually designates. An ordinary microstrip run reaches none of
        // this and is bit-identical. A plane sitting BETWEEN the levels is not helped by a flip and
        // is not one problem: through an unbroken plane the two halves are decoupled, and the note
        // further down says so.
        bool flipped = false;
        double flipDatumM = stack[^1].TopM;
        var bottomBoundary = tech.Stackup.Bottom;

        bool directOk = ResolvesAUsablePlane(
            stack, levels, bottomBoundary, settings, out bool directDesignated);
        if (!directOk || !directDesignated)
        {
            var mirroredStack  = MirrorStack(stack, flipDatumM);
            var mirroredLevels = (List<Band>)
                [.. levels.Select(b => mirroredStack.First(x => x.Index == b.Index)).OrderBy(b => b.SheetM)];

            if (ResolvesAUsablePlane(mirroredStack, mirroredLevels, tech.Stackup.Top, settings,
                                     out bool mirroredDesignated)
                && (!directOk || mirroredDesignated))
            {
                // MIM-6/MIM-7 are the one thing a flip cannot carry, and a silently wrong capacitor
                // is worse than the fallback this block replaces. `SheetAt` names a surface of a
                // band and `PresentWithLayer` ties a film to the plate ABOVE it; both are written in
                // the stackup's own orientation, and reflecting them is a modelling decision this
                // has no measurement to make. Say so rather than flip, and rather than stay silent.
                var blockers = stack
                    .Where(b => b.Layer.SheetAt is not null || b.Layer.PresentWithLayer is { Length: > 0 })
                    .Select(b => $"'{b.Layer.Name}'")
                    .ToList();

                if (blockers.Count > 0)
                    notes.Add(EmFinding.Warn(
                        $"The analysis levels ({levelList}) sit BELOW every plane they could " +
                        "return through, which this run would normally solve by mirroring the whole " +
                        $"stack — but {string.Join(", ", blockers)} " +
                        $"{(blockers.Count == 1 ? "carries a reference-surface or patterned-film tie" : "carry reference-surface or patterned-film ties")} " +
                        "written in the stackup's own orientation, and reflecting those is a " +
                        "modelling decision rather than an arithmetic one. The stack was left as " +
                        "authored, so the note below applies and the plane it names is not the one " +
                        "this structure is referenced to."));
                else
                {
                    string wouldHave = directOk
                        ? "fallen back to the Stackup.Bottom = Ground boundary at the bottom of the " +
                          "stack, which is further away than the plane this structure is actually " +
                          "referenced to and reads as a higher impedance"
                        : "been refused for having no usable plane beneath its levels";

                    stack  = mirroredStack;
                    levels = mirroredLevels;
                    bottomBoundary = tech.Stackup.Top;
                    flipped = true;

                    notes.Insert(0,
                        "THIS RUN IS SOLVED WITH THE STACKUP FLIPPED. The analysis levels " +
                        $"({levelList}) sit BELOW the conductor they return through in technology " +
                        $"'{tech.Name}', and the layered Green's function terminates on one laterally " +
                        "infinite plane BENEATH every meshed level — so in the stackup's own " +
                        $"orientation the run would have {wouldHave}. Reflecting the whole stack in a " +
                        "horizontal plane is an EXACT symmetry of an isotropic medium: the same " +
                        "structure, the same s-parameters, with the plane where the kernel needs it. " +
                        "Nothing was written and nothing else moved — the technology, the layout and " +
                        "the artwork's x/y are untouched, and this applies to this run alone. " +
                        "EVERY HEIGHT BELOW IS MEASURED IN THE FLIPPED STACK: downward from the TOP " +
                        $"surface of the stackup as the technology lists it, over a stack " +
                        $"{flipDatumM * 1e6:G4} µm thick. Subtract a height from that total to read it " +
                        "against the Stackup tab.");
                }
            }
        }

        var signal = levels[0];       // the LOWEST level — the one the slab's top surface is

        // ── R-em-4: ground is the TOP SURFACE of the highest ground-designated conductor below ──
        //
        // MIM-6: this is R-em-4's own rule and is NOT `SheetAt`. A ground plane is not a meshed
        // level with a sheet to place — it is the boundary the Green's function terminates on, and
        // the boundary is the metal's top surface whatever a ground entry's `SheetAt` says. Do not
        // "unify" the two: reading `SheetAt` here would let a stray Bottom on a ground entry drop
        // the reference plane by a metal thickness on every technology that carries one.
        var inferredGround = HighestGroundBelow(stack, signal.SheetM);
        var groundBand     = inferredGround;
        bool overridden    = false;

        // ── RP-1: THE .cem MAY NAME THE RETURN PLANE, AND THEN IT MUST SAY SO ─────────────────
        //
        // R-em-4 above is the right rule and stays the default — every document written before this
        // field, and every one that leaves it empty, takes the inferred path bit for bit. What it
        // cannot express is a per-RUN answer, and there are two ordinary situations that need one:
        // a board with two designated planes below the structure whose trace is genuinely
        // referenced to the LOWER of them (an intentionally-voided inner plane), and the routine
        // "what if" of comparing one structure against two references. The only other way to say
        // either is to un-tick a plane's "Ground reference" in the TECHNOLOGY — which is shared by
        // every design that uses it, and which also turns that plane into meshed signal metal
        // everywhere.
        //
        // **This is not the remedy for a bad level set** and must never be used as one. The
        // 2026-09-10 report that inner-layer via pads dragged the return plane to the bottom of a
        // board was a LEVEL-selection defect and is fixed in the incidental trim above; an override
        // that papered over it would leave the levels just as wrong and hide the evidence.
        //
        // The refusals below are the substance: an override that quietly produces a
        // different-looking answer is exactly the failure this whole area is written against.
        if (settings.GroundStackupLayerName is { Length: > 0 } wantedGround)
        {
            var named = stack.FirstOrDefault(b =>
                b.Layer.Kind == StackupKind.Conductor &&
                string.Equals(b.Layer.Name, wantedGround, StringComparison.Ordinal));

            // R-rp1-2 — the same shape SignalStackupLayerName's own refusal uses: name the setting,
            // name the technology. A missing name is a typo or a technology that has moved on, and
            // silently falling back to R-em-4 would answer a question nobody asked.
            if (named is null)
                return PlanarExtractionResult.No(
                    $"This EM setup names '{wantedGround}' as its return plane, but technology " +
                    $"'{tech.Name}' has no conductor stackup layer with that name. Name one of " +
                    $"{ConductorList(stack)}, or clear the setting to let the run resolve the " +
                    "return plane from the technology's own ground designations.", notes);

            // R-rp1-4, the half that is a REFUSAL rather than a note. A conductor cannot be both the
            // meshed metal and the laterally infinite plane that metal returns to — one is unknowns
            // in the matrix, the other is the boundary the Green's function terminates on, and there
            // is no reading of "both" that the kernel could act on.
            if (levels.Any(b => b.Index == named.Index))
                return PlanarExtractionResult.No(
                    $"This EM setup names '{named.Layer.Name}' as its return plane, but that " +
                    "conductor is also one of its analysis levels. It cannot be both: an analysis " +
                    "level is meshed metal carrying unknowns, and the return plane is the laterally " +
                    "infinite boundary that metal returns to. Either untick it in this setup's " +
                    "analysis levels, or name a different conductor as the return plane.", notes);

            // R-rp1-3 — R-em-4's own physics, not a limitation of the override: a port returns
            // through a plane BENEATH the conductor it feeds, so a plane at or above the lowest
            // level leaves a slab of zero or negative height. Say which level and BOTH heights: the
            // user is looking at a stackup table and cannot see the analysis levels from there.
            if (named.TopM >= signal.SheetM - 1e-15)
                return PlanarExtractionResult.No(
                    $"This EM setup names '{named.Layer.Name}' as its return plane, but its top " +
                    $"surface is at {named.TopM * 1e6:G4} µm, which is NOT below the lowest analysis " +
                    $"level '{signal.Layer.Name}' at {signal.SheetM * 1e6:G4} µm. A " +
                    "return plane must lie BENEATH the conductor it feeds, or there is no dielectric " +
                    $"slab between them to solve on. Name a conductor below {signal.SheetM * 1e6:G4} " +
                    $"µm, or restrict this setup's analysis levels to conductors above " +
                    $"'{named.Layer.Name}'.", notes);

            groundBand = named;
            overridden = true;

            // R-rp1-4, the note. Accepting a conductor the technology does not designate is the
            // POINT of the field — but the `.ctech` and the run then disagree with nothing on screen
            // to say which won, and the run wins. Say so, here, where the answer is stated.
            if (!named.Layer.IsGroundReference)
                notes.Add(
                    $"'{named.Layer.Name}' is NOT marked as a ground reference in technology " +
                    $"'{tech.Name}', but this EM setup names it as the return plane, so this run " +
                    "overrides the technology and terminates on it anyway. It is modelled as a " +
                    "laterally infinite plane and its own artwork, if any, is not meshed — its own " +
                    "metal is carried, and the note below says with what. That override applies to " +
                    "this run only; the technology is unchanged.");
        }

        // CL7 — the medium's floor, decided HERE because this is where the return plane is settled
        // and because the note below has to say what it is. Both medium-building sites read it.
        var floor = FloorFor(groundBand);

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
            //
            // R-rp1-5: and it must READ DIFFERENTLY when the `.cem` chose. This note is the only
            // place the return plane is visible at all, so a run whose plane came from the document
            // rather than from R-em-4 has to say so here or the two are indistinguishable — which
            // would make the override precisely the silent-different-answer this note prevents.
            // Both spellings keep naming the HEIGHT, which is the number the 2%-scale trap is in.
            // ── RP-2b/R-rp2b-8 — "EVERY PORT" IS NO LONGER TRUE, AND THE NOTE MAY NOT SAY IT IS ──
            //
            // Both spellings below used to finish "That plane is the negative terminal of every port
            // in this run and is not selectable per port." RP-2a made that sentence false: a port
            // may name a DRAWN conductor as its return, and is then two cuts driven against each
            // other with the plane nowhere in its loop.
            //
            // The plane is still named once, with its height, because it is still the medium's own
            // boundary condition and still the return for every port that did not say otherwise —
            // the 2%-scale trap this note exists for is unchanged. What changes is only the clause
            // that claimed exclusivity, and it changes ONLY when a port actually says otherwise: a
            // run where every port is ground-referenced reads character for character as it did.
            //
            // The differing ports are named from their own LABELS rather than by port number on
            // purpose. Numbering is EmPortExtraction's (a label whose text names no number is
            // auto-numbered in document order), and re-deriving it here would be a second copy of
            // that ordering, free to drift from the one the s-matrix is indexed by. The label text
            // is what the user sees on the canvas, and the CONDUCTOR each return landed on is named
            // by that port's own note, which is the only place it is actually known.
            var referenced = shapes
                .OfType<LabelShape>()
                .Where(l => l is { IsPort: true, PortReference: not null })
                .Select(l => l.Text is { Length: > 0 } t ? $"'{t}'" : "an unnamed port")
                .ToList();

            // The OPENER is scoped too. Leaving "Every port returns through 'X'" standing and
            // correcting it two sentences later would be a note whose first sentence is false, which
            // is the same defect one clause down.
            string opener = referenced.Count == 0
                ? "Every port returns through"
                : "Every port that does not name its own return conductor returns through";

            string planeClause = referenced.Count == 0
                ? "That plane is the negative terminal of every port in this run and is not " +
                  "selectable per port; it is modelled as laterally infinite."
                : "That plane is modelled as laterally infinite. " +
                  $"{string.Join(", ", referenced)} " +
                  $"{(referenced.Count == 1 ? "names its own return conductor" : "name their own return conductors")} " +
                  $"instead, so {(referenced.Count == 1 ? "it returns" : "they return")} through " +
                  "drawn, meshed metal rather than through this plane — each port's own note below " +
                  "says which conductor its return terminal landed on.";

            notes.Add(overridden
                ? $"{opener} '{groundBand.Layer.Name}' at " +
                  $"{groundBand.TopM * 1e6:G4} µm, because THIS EM SETUP names it as the return " +
                  $"plane — {InferredWouldHaveBeen(inferredGround, bottomBoundary, stack)}. The signal level " +
                  $"sits at {signal.SheetM * 1e6:G4} µm. " + planeClause + " Clear this setup's " +
                  "return plane to go back to the automatic choice."
                : $"{opener} '{groundBand.Layer.Name}', the ground-designated " +
                  $"conductor at {groundBand.TopM * 1e6:G4} µm — the highest one below the " +
                  $"signal level at {signal.SheetM * 1e6:G4} µm. " + planeClause + " To return " +
                  "through a different conductor, designate that one as the ground reference in " +
                  "the technology editor, or name it as this EM setup's own return plane to " +
                  "override the choice for this run alone.");

            // ── CL7 — SAY WHAT THE PLANE IS MADE OF, BECAUSE IT IS NOW IN THE ANSWER ─────────
            //
            // Until CL7 the plane was a perfect conductor and there was nothing to report: its metal
            // was not in the physics, and `src/Engine/Mom/CLAUDE.md` carried the omission as a stated
            // limit with its measured size. It IS in the physics now — worth 21.1% of the conductor
            // term on FR-4, ~11% on the MMIC starter and 25.0% on a low-loss laminate — and the two
            // states are not distinguishable from any published number, so this note is the only
            // place a run says which it is in. A layer with no σ is a legitimate thing to have (it is
            // what every technology written before conductivity mattered has) and gets the PERFECT
            // plane, bit for bit; what it must not do is get it silently.
            notes.Add(floor.Kind == TerminationKind.SurfaceImpedance
                ? $"The return plane's own metal is in this solve: '{groundBand.Layer.Name}' is a " +
                  $"laterally infinite conductor of σ = {groundBand.Layer.SigmaSm:G4} S/m and " +
                  $"{(groundBand.TopM - groundBand.BottomM) * 1e6:G4} µm thickness, entered as a " +
                  "surface impedance on the boundary the Green's function terminates on. It is still " +
                  "not meshed and adds no unknowns. On an ordinary microstrip the plane is of order a " +
                  "fifth to a quarter of the total conductor loss, so a run with it and a run without " +
                  "it differ by a real amount in α and in the published |S₂₁|."
                : $"'{groundBand.Layer.Name}' has no conductivity set in technology '{tech.Name}', so " +
                  "the return plane is a PERFECT conductor in this solve and contributes no loss of " +
                  "its own. The signal metal's loss is unaffected. On an ordinary microstrip the " +
                  "plane is worth of order a fifth to a quarter of the total conductor loss, so this " +
                  "run's α is that much low; set that layer's σ in the technology editor's Stackup " +
                  "tab to carry it.");

            // ── A GROUND PLANE SKIPPED OVER IS NOT A GROUND PLANE — IT IS SUBSTRATE ───────────
            //
            // R-em-4 asks for the highest designated ground BELOW THE LOWEST LEVEL, so a designated
            // plane sitting between the levels cannot be chosen and is not otherwise mentioned. What
            // then happens to it is worse than being ignored: BuildMediumStack absorbs any conductor
            // band that is not a level into a NEIGHBOURING DIELECTRIC, so the plane leaves the
            // physics entirely — 18 µm of copper becomes 18 µm of FR-4 — and the run reports a
            // perfectly ordinary result for a structure referenced to a plane four times further
            // away than the real one. Every part of that is silent today.
            //
            // A note rather than a refusal, and deliberately: with levels seeded from the ports this
            // is reachable only when someone listed the levels themselves, and a plane with a large
            // enough opening under the structure is a thing a user may legitimately mean to skip.
            // It states what happens to the plane, not merely that it was not used.
            var skipped = stack
                .Where(b => b.Layer.Kind == StackupKind.Conductor && b.Layer.IsGroundReference &&
                            b.TopM > groundBand.TopM + 1e-15 && b.BottomM < levels[^1].SheetM - 1e-15)
                .OrderBy(b => b.TopM)
                .ToList();
            if (skipped.Count > 0)
                notes.Add(EmFinding.Warn(
                    $"{string.Join(", ", skipped.Select(b => $"'{b.Layer.Name}'"))} " +
                    $"{(skipped.Count == 1 ? "is a ground-designated conductor" : "are ground-designated conductors")} " +
                    $"lying BETWEEN the analysis levels and the return plane chosen above. A return " +
                    "plane must sit beneath the conductor it feeds, and this one does not, so it was " +
                    "passed over — and a conductor that is neither a level nor the return plane is " +
                    "absorbed into the surrounding dielectric, which means it is not in this solve at " +
                    "ALL: its metal is modelled as substrate. If the structure is referenced to it, " +
                    "the answer will be wrong by the ratio of the two heights and will not look it. " +
                    "Restrict this EM setup's analysis levels to the conductors above that plane, or " +
                    "untick its \"Ground reference\" in the technology editor so it is meshed as " +
                    "ordinary metal."));
        }
        else if (bottomBoundary == BoundaryCondition.Ground)
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

            // ── RP-3 — SAY WHY THE FLIP DID NOT RESCUE THIS ONE ─────────────────────────────
            //
            // Reaching this note at all now means the mirrored frame could not resolve a plane
            // either, and there is one shape that accounts for nearly every instance of it: the
            // designated plane lies BETWEEN the analysis levels. No orientation of the stack puts a
            // mid-stack plane beneath all of them, and the reason is not a limitation of the
            // kernel — through an unbroken plane the metal above and the metal below are two
            // decoupled structures. The remedy is therefore about the LEVEL SET and not about the
            // technology, and "designate a conductor below this level as a ground reference" (the
            // advice that stood here alone) sends the user to edit a stackup that is already right.
            //
            // A Gerber import is how this arrives in practice: it brings in the artwork of every
            // copper layer, so both outer layers become levels with the plane sandwiched between
            // them, and neither the ports nor the structure asked for that.
            var between = above.Where(b => b.BottomM < levels[^1].SheetM - 1e-15).ToList();
            string betweenLevels = between.Count > 0
                ? $"{string.Join(", ", between.Select(b => $"'{b.Layer.Name}'"))} " +
                  $"{(between.Count == 1 ? "lies" : "lie")} BETWEEN this run's analysis levels " +
                  $"({levelList}), so no orientation of the stack puts " +
                  $"{(between.Count == 1 ? "it" : "them")} beneath all of them — and that is not a " +
                  "limitation of the kernel: through an unbroken plane the metal above and the metal " +
                  "below are two decoupled structures, not one problem. Restrict this EM setup's " +
                  "analysis levels to the conductors on ONE side of it. With only the levels ABOVE " +
                  "it, it becomes this run's return plane; with only the levels BELOW it, the run is " +
                  "solved with the stack mirrored and it becomes the return plane that way."
                : "Either designate a conductor below this level as a ground reference, or run this " +
                  "level's structure against the plane it is actually referenced to.";

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
                  "and will read as a higher impedance. " + betweenLevels);
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

        // CL7 — the floor, on BOTH spellings of the medium. CL6 put it on GroundedSlab so that a
        // single-level single-dielectric design keeps Dcim.ValidatedRhoOverLambda's own ≤6e-3 tier
        // instead of being re-based onto the general path's ≤1.6e-2 to gain the term.
        var slab = new GroundedSlab(slabHeight, slabMaterial) { Floor = floor };

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

        // ── Overlapping copper on one level is ONE conductor (round-7 field report, 2026-09-24) ──
        //
        // A placed part's footprint pad lying over an imported board's own pad reached the kernel as
        // two polygons. The mesh fills their union, so the port resolved onto the merged metal's
        // true end face — but the feed-lead decision reads ONE polygon's end face and its uniform run
        // (PlanarFeedExtension.TryEndFace/UniformRun), saw the board pad alone as a uniform feed, and
        // grew no lead. The calibration then described a line that is not there and the published
        // S11 was an open circuit. Merged here, once, every per-polygon question downstream asks
        // about the copper. Only shapes that genuinely OVERLAP another on the same level are merged;
        // everything else passes through untouched, so artwork with no overlaps is bit-identical.
        conductorShapes = MergeOverlappingCopper(conductorShapes, tech, out int mergedShapes, out int mergedInto);
        if (mergedShapes > 0)
            notes.Add($"{mergedShapes} overlapping conductor shape(s) were merged into {mergedInto} before meshing — " +
                      "copper that overlaps on one level is one conductor (a placed part's pad over a drawn or " +
                      "imported pad is the usual case), and a port's feed is measured on the merged outline.");

        // ── L9d: one polygon list PER LEVEL, in the level order the stack is built in ─────────
        var polysByLevel = new List<PlanarPolygon>[levels.Count];
        for (int i = 0; i < levels.Count; i++) polysByLevel[i] = [];

        int flattenedCurves = 0;
        int strokesOutlined = 0;
        double strokeAreaM2 = 0.0;
        foreach (var (shape, band) in conductorShapes)
        {
            int li = levels.FindIndex(b => b.Index == band.Index);
            if (li < 0) continue;                                   // a level the setup left out

            long tol = LayoutFlattener.ResolveTolDbu(shape, tech);
            bool isStroke = shape is PathShape;
            bool anyRegion = false;

            foreach (var region in RegionsToMesh(shape, tech))
            {
                IReadOnlyList<long[]> rings;
                try { rings = LayoutFlattener.Flatten(region, tol); }
                catch (ArgumentOutOfRangeException) { ignoredOther++; continue; }

                if (rings.Count == 0 || rings[0].Length < 6) continue;
                anyRegion = true;

                var outer = ToPoints(rings[0], perDbu);
                var holes = new List<IReadOnlyList<EmPoint>>();
                for (int i = 1; i < rings.Count; i++)
                    if (rings[i].Length >= 6) holes.Add(ToPoints(rings[i], perDbu));

                var poly = new PlanarPolygon(outer, holes.Count == 0 ? null : holes);
                polysByLevel[li].Add(poly);
                if (isStroke) strokeAreaM2 += NetArea(poly);
            }

            // Counted ONCE per shape, as before — an outlined stroke may yield several regions (a
            // self-crossing track resolves to one union, a track that doubles back may not) and the
            // flattening note is about the SHAPE whose curves were approximated, not the pieces.
            if (!anyRegion) continue;
            if (LayoutBooleans.IsCurved(shape)) flattenedCurves++;
            if (isStroke) strokesOutlined++;
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
        int viaStrokesOutlined = 0;
        foreach (var (shape, entry) in regionViaShapes)
        {
            long tol = LayoutFlattener.ResolveTolDbu(shape, tech);
            bool isStroke = shape is PathShape;
            bool anyRegion = false;

            // ANT-1 — a width-bearing Path on a VIA-bound layer is outlined by the same rule. A
            // routed, plated slot is drawn as a stroke, and the alternative is that one shape means
            // copper on a conductor layer and nothing one layer down.
            foreach (var region in RegionsToMesh(shape, tech))
            {
                IReadOnlyList<long[]> rings;
                try { rings = LayoutFlattener.Flatten(region, tol); }
                catch (ArgumentOutOfRangeException) { continue; }

                if (rings.Count == 0 || rings[0].Length < 6) continue;
                anyRegion = true;

                var viaOuter = ToPoints(rings[0], perDbu);
                var viaHoles = new List<IReadOnlyList<EmPoint>>();
                for (int i = 1; i < rings.Count; i++)
                    if (rings[i].Length >= 6) viaHoles.Add(ToPoints(rings[i], perDbu));

                regionViaPolys.Add((new PlanarPolygon(viaOuter, viaHoles.Count == 0 ? null : viaHoles), entry));
            }

            if (!anyRegion) { ignoredViaRegion++; continue; }
            if (LayoutBooleans.IsCurved(shape)) flattenedCurves++;
            if (isStroke) viaStrokesOutlined++;
        }

        // ── MIM-11 — the MASK's own polygons, through the same conversion, for one number ─────
        //
        // Not meshed, not stamped, in no matrix: a mask is not metal and nothing here pretends it
        // is. It is converted only so the run can say how much of the layout the film it switched on
        // actually covers — the one quantity that turns "modelled across the whole plane" from a
        // property of the formulation into a size the reader can weigh. Same
        // `RegionsToMesh` -> `Flatten` -> `ToPoints` chain as the conductors, for the reason MIM-1
        // gives above: a second conversion could drift, and a coverage figure that disagreed with the
        // artwork by a few per cent would be invisible.
        //
        // UNIONED FIRST. Two overlapping nitride rectangles are one opening in the mask, and summing
        // their areas would report more film than exists — on a cell array, considerably more.
        var maskPolysByName = new Dictionary<string, List<PlanarPolygon>>(StringComparer.Ordinal);
        foreach (var group in maskShapes.GroupBy(m => m.Mask, StringComparer.Ordinal))
        {
            var operands = group.Select(m => m.Shape).ToList();
            IReadOnlyList<LayoutShape> merged;
            try   { merged = LayoutBooleans.Union(operands, tech).Shapes; }
            catch (Exception) { merged = operands; }     // a degenerate operand is not worth a refusal

            var polys = new List<PlanarPolygon>();
            foreach (var shape in merged)
            {
                long mtol = LayoutFlattener.ResolveTolDbu(shape, tech);
                foreach (var region in RegionsToMesh(shape, tech))
                {
                    IReadOnlyList<long[]> rings;
                    try { rings = LayoutFlattener.Flatten(region, mtol); }
                    catch (ArgumentOutOfRangeException) { continue; }
                    if (rings.Count == 0 || rings[0].Length < 6) continue;

                    var mOuter = ToPoints(rings[0], perDbu);
                    var mHoles = new List<IReadOnlyList<EmPoint>>();
                    for (int i = 1; i < rings.Count; i++)
                        if (rings[i].Length >= 6) mHoles.Add(ToPoints(rings[i], perDbu));
                    polys.Add(new PlanarPolygon(mOuter, mHoles.Count == 0 ? null : mHoles));
                }
            }
            maskPolysByName[group.Key] = polys;
        }

        // ── ANT-11 §2 — the ground pour, through the CONDUCTOR PATH'S OWN conversion ─────────
        //
        // Deliberately the same `RegionsToMesh` -> `LayoutFlattener.Flatten` -> `ToPoints` chain the
        // conductor levels take, and for MIM-1's reason restated: a second conversion could drift,
        // and a pour whose measured area disagreed with the artwork by a few per cent would be
        // invisible. R-fg-2's selection is made HERE and not in the shape loop, because which band is
        // the return plane is R-em-4's answer and is not known until it has run.
        //
        // A width-bearing Path is outlined like any other stroke (ANT-1) — a plane is routinely
        // stitched with thick tracks — and a zero-width one never reached this list at all.
        var groundOutlinePolys = new List<PlanarPolygon>();
        int groundOutlineShapes = 0;
        if (groundBand is not null)
            foreach (var (shape, gbands) in groundShapes)
            {
                if (!gbands.Any(b => b.Index == groundBand.Index)) continue;

                long gtol = LayoutFlattener.ResolveTolDbu(shape, tech);
                bool anyGroundRegion = false;
                foreach (var region in RegionsToMesh(shape, tech))
                {
                    IReadOnlyList<long[]> rings;
                    try { rings = LayoutFlattener.Flatten(region, gtol); }
                    catch (ArgumentOutOfRangeException) { continue; }
                    if (rings.Count == 0 || rings[0].Length < 6) continue;

                    var gOuter = ToPoints(rings[0], perDbu);
                    var gHoles = new List<IReadOnlyList<EmPoint>>();
                    for (int i = 1; i < rings.Count; i++)
                        if (rings[i].Length >= 6) gHoles.Add(ToPoints(rings[i], perDbu));

                    groundOutlinePolys.Add(new PlanarPolygon(gOuter, gHoles.Count == 0 ? null : gHoles));
                    anyGroundRegion = true;
                }
                if (anyGroundRegion) groundOutlineShapes++;
            }

        int totalPolys = polysByLevel.Sum(l => l.Count);
        if (totalPolys == 0)
            return PlanarExtractionResult.No(
                "None of the geometry on the analysis levels encloses an area to mesh — a planar " +
                "solver needs filled regions, not centrelines or markers.", notes);

        // ── ANT-1 — SAY WHAT WAS CONVERTED, not only what was ignored ────────────────────────
        //
        // The reason strokes went missing for as long as they did is that the only sentence about
        // them was an ignored-count among twenty, saying nothing about what had been lost. A user
        // who imports a board and reads this note can now see that the tracks are in, and how much
        // metal they are.
        if (strokesOutlined > 0)
            notes.Add($"{strokesOutlined} width-bearing Path shape(s) were outlined into conductor " +
                      $"artwork, contributing {strokeAreaM2 * 1e6:G4} mm² of metal to the mesh. A " +
                      "stroke with a non-zero width IS copper — it is how an imported track is " +
                      "drawn — and it is outlined by the same routine DRC and the Gerber/DXF writers " +
                      "use, at its own end style and at the layout's flatten tolerance.");
        if (viaStrokesOutlined > 0)
            notes.Add($"{viaStrokesOutlined} width-bearing Path shape(s) on a via-bound drawing layer " +
                      "were outlined into via footprints by the same rule — a routed, plated slot is " +
                      "drawn as a stroke, and one shape cannot mean copper on a conductor layer and " +
                      "nothing one layer down.");

        if (ignoredAnnotation > 0)
            notes.Add($"{ignoredAnnotation} label/bitmap shape(s) ignored — annotation is not artwork.");
        // ── ANT-11 §2 — SAY WHAT WAS READ, AND FROM WHICH PLANE ──────────────────────────────
        //
        // The old sentence here counted the shapes and ended "modelling one is not part of L9",
        // which was accurate about the mesh and wrong about the outline: the geometry is now carried
        // as a DESCRIBED BOUNDARY (R-fg-1 — still not meshed, still not stamped, still changes no
        // matrix entry) so the run can state the plane's size in wavelengths. Two counts, because
        // they mean different things: the pour on the RETURN plane is the one measured (R-fg-2), and
        // artwork on any OTHER ground-designated conductor is metal that is in the way rather than in
        // the structure — reporting them together would put a bottom-layer pour's size on a plane the
        // fields never see.
        if (groundShapes.Count > 0)
        {
            string measured = groundOutlinePolys.Count > 0
                ? $"{groundOutlineShapes} of them are on '{groundBand?.Layer.Name}', THIS run's return " +
                  $"plane, and their outline is read and carried so the run can report how large the " +
                  $"real plane is in wavelengths — see the ground-plane note beside the results. " +
                  $"It is still NOT MESHED: the plane in the analysis is the laterally infinite " +
                  $"boundary the Green's function terminates on, carrying that conductor's own metal, " +
                  $"and reading the outline changed no matrix entry and no published number."
                : "None of them is on this run's own return plane, so there is no outline to measure — " +
                  "a plane's size can only be reported for the conductor the fields actually return " +
                  "through.";
            notes.Add($"{groundShapes.Count} shape(s) are on a ground-designated conductor layer and " +
                      $"none of them is meshed. {measured}" +
                      (groundShapes.Count > groundOutlineShapes
                          ? $" The other {groundShapes.Count - groundOutlineShapes} are on a different " +
                            "ground-designated conductor — metal that is in the way rather than in the " +
                            "structure, and its extent says nothing about this run's return plane."
                          : ""));
        }
        // ANT-1 — this sentence may only ever be about a ZERO-WIDTH Path now. It used to say "a
        // Path is a centreline", full stop, which is the false premise that discarded every
        // imported track; leaving a refusal standing on a reason that has been shown wrong is worse
        // than the count being silent.
        if (ignoredOther > 0)
            notes.Add($"{ignoredOther} shape(s) were ignored — a ZERO-WIDTH Path is a centreline " +
                      "with no area to mesh, and anything not bound to a stackup conductor or via " +
                      "entry is not metal as far as this technology is concerned. (A Path that " +
                      "carries a width is outlined and meshed.)");
        // MIM-1 — the two things still ignorable on a via-bound layer, each named rather than
        // folded into the sentence above, which would send the user to re-bind a layer that is
        // already bound.
        if (ignoredViaPath > 0)
            notes.Add($"{ignoredViaPath} zero-width Path shape(s) on a via-bound drawing layer were " +
                      "ignored. A Path with no width is a centreline enclosing no area, and a via " +
                      "footprint is meshed by the cells it covers — give the path a width, draw the " +
                      "connection as a rectangle or a polygon, or place a via primitive.");
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
        var (mediumStack, levelZ, stackNote) = BuildMediumStack(stack, levels, groundTopM, floor);
        if (stackNote is not null) notes.Add(stackNote);

        // ── ANT-12 §1a — A COVER LAYER OVER THE TOP METAL IS NOT IN THE SOLVE, AND IT WAS SILENT ──
        //
        // BuildMediumStack stops at `topOfInterest` = the topmost analysis level's own sheet, and the
        // stack is then terminated in AIR at exactly that height. Anything the technology declares
        // above it — a radome, a conformal coating, a gain-raising superstrate — is discarded, and
        // until this note nothing said so. Measured on the shipped 5.8 GHz patch example: adding a
        // 0.5 mm, eps_r = 3.0 cover over the top copper produced s-parameters BIT-IDENTICAL to the
        // uncovered run, at every one of 11 frequencies, with the pattern and the power budget
        // identical too. A real cover moves a patch's resonance by per cent and changes its
        // surface-wave launch, so "no difference at all" is the one answer that cannot be right.
        //
        // A WARNING rather than a refusal, on the same terms as the skipped-ground-plane warning
        // above: the layer is genuinely representable in the medium this kernel solves (a uniform
        // superstrate is a layered medium), so the limit is in the EXTRACTION and not in the physics,
        // and refusing would stop runs that are otherwise correct about everything below the metal.
        var covers = stack
            .Where(b => b.Layer.Kind == StackupKind.Dielectric &&
                        b.BottomM >= levels[^1].SheetM - 1e-15 && b.TopM > b.BottomM)
            .OrderBy(b => b.BottomM)
            .ToList();
        if (covers.Count > 0)
            notes.Add(EmFinding.Warn(
                $"{string.Join(", ", covers.Select(b => $"'{b.Layer.Name}'"))} " +
                $"{(covers.Count == 1 ? "is a dielectric layer" : "are dielectric layers")} lying " +
                $"ABOVE '{levels[^1].Layer.Name}', the topmost analysis level, and " +
                $"{(covers.Count == 1 ? "it is NOT in this solve" : "they are NOT in this solve")}. " +
                "The medium is built from the ground plane up to the topmost level and then terminated " +
                "in an open air half-space at exactly that height, so a radome, a conformal coating or " +
                "a superstrate over the metal is discarded rather than modelled — the answer is the " +
                "answer for an UNCOVERED structure, and it will not look any different. On a patch " +
                "antenna a real cover moves the resonance by per cent and changes the surface-wave " +
                "launch. Remove the layer from the stackup if you did not mean it, and read the " +
                "published resonance as the uncovered one if you did."));

        // GVIA-1 — the plane's own artwork, classified once, is what tells a ground stitch from a
        // signal via passing through. It is built from the SAME polygons R-fg-2 reads above, so the
        // copper a via is tested against is the copper the run reports the size of.
        var planeMetal = new PlaneMetal(groundOutlinePolys);

        // GVIA-2 — the artwork on a conductor that is NOT in this analysis, flattened ON DEMAND.
        //
        // `conductorShapes` already holds every shape on every bound conductor layer, including the
        // bands the level loop above skipped, so nothing new is read from the layout — this is the
        // same RegionsToMesh -> Flatten -> ToPoints chain a level takes, which is the part that must
        // not drift. It runs only when a via actually bypassed the plane, and only for the band that
        // via's own span names, so an ordinary run never enters it.
        var farMetalCache = new Dictionary<int, IReadOnlyList<PlanarPolygon>>();
        IReadOnlyList<PlanarPolygon> FarMetal(int bandIndex)
        {
            if (farMetalCache.TryGetValue(bandIndex, out var cached)) return cached;

            var built = new List<PlanarPolygon>();
            foreach (var (shape, band) in conductorShapes)
            {
                if (band.Index != bandIndex) continue;
                long ftol = LayoutFlattener.ResolveTolDbu(shape, tech);
                foreach (var region in RegionsToMesh(shape, tech))
                {
                    IReadOnlyList<long[]> rings;
                    try { rings = LayoutFlattener.Flatten(region, ftol); }
                    catch (ArgumentOutOfRangeException) { continue; }
                    if (rings.Count == 0 || rings[0].Length < 6) continue;

                    var fOuter = ToPoints(rings[0], perDbu);
                    var fHoles = new List<IReadOnlyList<EmPoint>>();
                    for (int i = 1; i < rings.Count; i++)
                        if (rings[i].Length >= 6) fHoles.Add(ToPoints(rings[i], perDbu));
                    built.Add(new PlanarPolygon(fOuter, fHoles.Count == 0 ? null : fHoles));
                }
            }

            farMetalCache[bandIndex] = built;
            return built;
        }

        var vias = BuildVias(viaShapes, regionViaPolys, levels, perDbu, notes, groundBand,
                             stack, planeMetal, FarMetal, polysByLevel);

        // The conductor levels are built AFTER the vias, because a via that spans a level builds
        // that level's metal: see BuildVias' chain-through block, which appends the barrel's own
        // cross-section to `polysByLevel` for each level it passes. Nothing else here reorders — a
        // run with no spanning via produces the identical array either way.
        var conductorLayers = new PlanarConductorLayer[levels.Count];
        for (int i = 0; i < levels.Count; i++)
            conductorLayers[i] = new PlanarConductorLayer(
                levels[i].Layer.Name, polysByLevel[i], levels[i].Layer.SigmaSm,
                levels[i].TopM - levels[i].BottomM,
                // A one-level problem keeps ZM UNSET, so it stays on L8's shipped path bit-for-bit
                // (PlanarProblem.RequiresGeneralKernel). Naming the height is what turns the general
                // kernel on, and it must only happen when there is something general to say.
                levels.Count > 1 ? levelZ[i] : double.NaN);

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
            vias.Count > 0 ? vias : null,
            groundOutlinePolys.Count > 0
                ? new PlanarGroundOutline(groundBand?.Layer.Name, groundOutlinePolys)
                : null);

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

        // ── MIM-11 — A CARRIED FILM IS LATERALLY INFINITE, AND NOTHING SAID SO ────────────────
        //
        // The medium string above is a correct list of bands — "0.103 mm εᵣ=12.9 | 0.0002 mm εᵣ=6.8
        // | 0.0028 mm εᵣ=1" — and a reader who knows the stackup sees exactly what they expect and
        // no approximation at all. But the 0.2 µm εᵣ = 6.8 band is present across the WHOLE PLANE,
        // over every turn of a spiral and everywhere else, because the layered Green's function is
        // built on laterally infinite strata. That is a property of the formulation and not a
        // setting; MIM-11 explicitly does not try to fix it, and says its size instead.
        //
        // WHY WITH A COVERAGE FRACTION. "This is approximate" is not usable. "The film is modelled
        // everywhere and the artwork that defines it covers 4 % of the layout" tells the reader both
        // that the approximation is crude and that what it is crude ABOUT is small. MIM-11 measured
        // the error on a Metal1 line at +0.108° of S₁₁ phase and −2.68 % of a gap capacitance, which
        // is second order — the sentence says so rather than leaving the reader to fear the worst,
        // because the thing that DOES make a capacitor read wrong is elsewhere.
        //
        // HEIGHTS ARE IN THE STACKUP'S OWN FRAME HERE. A surviving tie is one of RP-3's flip
        // blockers, so a run that carries a film is never a flipped one and these z values may be
        // read straight against the Stackup tab.
        if (carriedFilms.Count > 0)
        {
            double extentM2 = ExtentArea(polysByLevel, regionViaPolys.Select(v => v.Poly),
                                         groundOutlinePolys, maskPolysByName.Values);

            foreach (var carried in carriedFilms)
            {
                // The artwork that DEFINES the film: the mask's own polygons where the tie names a
                // mask, the plate level's where it names a conductor. Both are the honest answer to
                // "how much of this layout has the film on it" in the namespace the tie was written
                // in, and reporting one in place of the other would be a number about a different
                // layer.
                IEnumerable<PlanarPolygon> defining =
                    carried.TieIsMask
                        ? maskPolysByName.TryGetValue(carried.Tie, out var mp) ? mp : []
                        : levels.FindIndex(b => string.Equals(b.Layer.Name, carried.Tie,
                                                              StringComparison.Ordinal)) is var pi && pi >= 0
                            ? polysByLevel[pi] : [];

                // ── AND ONLY IF IT IS ACTUALLY IN THE MEDIUM ────────────────────────────────
                //
                // `BuildMediumStack` stops at the topmost analysis level and terminates in air
                // there, so a film ABOVE that level is discarded from the solve — which is exactly
                // ANT-12 §1a's case, and its warning already names the band. Saying "this run
                // CARRIES the film" beside "that film is NOT in this solve" would be two findings
                // contradicting each other about one band, and the cover warning is the one that
                // matters. MIM-11 found this while measuring: two runs differing only in the film's
                // εᵣ came back BIT-IDENTICAL on a Metal1-only run, and the engine was right.
                //
                // The SHEET note below is deliberately outside this guard. The sheet moved whether
                // or not the film reached the medium — the substrate under this level really is
                // 103 µm in such a run — so that sentence is if anything more useful there, where
                // nothing else explains the height.
                var filmBand = stack.FirstOrDefault(
                    b => string.Equals(b.Layer.Name, carried.Film.Name, StringComparison.Ordinal));
                bool inMedium = filmBand is not null && filmBand.BottomM < levels[^1].SheetM - 1e-15;

                double coveredM2 = defining.Sum(NetArea);
                string coverage = extentM2 > 0
                    ? $"'{carried.Tie}' artwork covers " +
                      FormatPercent(100.0 * Math.Min(coveredM2 / extentM2, 1.0)) +
                      " of this layout's extent."
                    : $"'{carried.Tie}' artwork is what defines it.";

                // Deliberately NOT opening "'X' is a patterned thin film …", which is how the
                // DEACTIVATION sentence opens. Two findings that say opposite things about the same
                // band must not share an opening clause: a reader skimming, and every consumer
                // matching on the phrase, would take one for the other.
                if (inMedium) notes.Add(
                    $"This run CARRIES the patterned thin film '{carried.Film.Name}' " +
                    $"({carried.Film.ThicknessDbu / (double)StackupDbuPerMicron:G4} µm, " +
                    $"εᵣ = {carried.Film.Epsr:G4}). The 2.5D medium has no way " +
                    "to make a dielectric laterally finite, so it is modelled ACROSS THE WHOLE PLANE " +
                    $"— including under metal that has none of it. {coverage} " +
                    "On ordinary interconnect the error this introduces is of order a tenth of a " +
                    "degree of phase and a few per cent of a fringing capacitance; it is second " +
                    "order, and it is not the reason a capacitor would read wrong. To model the " +
                    "interconnect without it, run the interconnect and the capacitor as separate " +
                    "EM setups.");

                if (carried.SheetRaisedConductor is { Length: > 0 } raised &&
                    levels.FindIndex(b => string.Equals(b.Layer.Name, raised,
                                                        StringComparison.Ordinal)) is var ri && ri >= 0)
                    notes.Add(
                        $"'{raised}'s analysis sheet is on the TOP of its band because a patterned " +
                        $"film sits above it, so this run's '{raised}' is at z = " +
                        $"{levelZ[ri] * 1e6:G4} µm rather than " +
                        $"{(levelZ[ri] - (levels[ri].TopM - levels[ri].BottomM)) * 1e6:G4} µm. That is " +
                        "what makes a plate gap read as the film alone rather than the film plus the " +
                        "plate's own metal — and it means putting a capacitor anywhere in a layout " +
                        "changes the modelled height of every conductor on that level. A run with no " +
                        "film in it puts the sheet back on the bottom of the band, and is the run to " +
                        "compare against.");
            }
        }

        return PlanarExtractionResult.Yes(problem, notes,
            new PlanarReturnPlane(groundBand?.Layer.Name, groundTopM, overridden, flipped));
    }

    /// <summary>
    /// MIM-11 — the bounding-box area, in m², of every piece of artwork this run read: the meshed
    /// levels, the via footprints, the return plane's own pour and any patterned-film mask.
    ///
    /// <para><b>A bounding box and not a union</b>, because the question the coverage fraction
    /// answers is "how much of the thing you are looking at has this film on it" — the extent of the
    /// drawing, not the area of its copper. A union would put a sparse spiral's coverage near 100 %
    /// and say nothing at all.</para>
    /// </summary>
    private static double ExtentArea(
        IReadOnlyList<List<PlanarPolygon>> byLevel,
        IEnumerable<PlanarPolygon> viaPolys,
        IEnumerable<PlanarPolygon> groundPolys,
        IEnumerable<List<PlanarPolygon>> maskPolys)
    {
        double minX = double.PositiveInfinity, minY = double.PositiveInfinity;
        double maxX = double.NegativeInfinity, maxY = double.NegativeInfinity;

        void Grow(PlanarPolygon p)
        {
            foreach (var pt in p.Outer)
            {
                if (pt.X < minX) minX = pt.X;
                if (pt.X > maxX) maxX = pt.X;
                if (pt.Y < minY) minY = pt.Y;
                if (pt.Y > maxY) maxY = pt.Y;
            }
        }

        foreach (var level in byLevel) foreach (var p in level) Grow(p);
        foreach (var p in viaPolys)    Grow(p);
        foreach (var p in groundPolys) Grow(p);
        foreach (var m in maskPolys) foreach (var p in m) Grow(p);

        return maxX > minX && maxY > minY ? (maxX - minX) * (maxY - minY) : 0.0;
    }

    /// <summary>MIM-11 — a percentage a reader can act on. "0 %" for a film that covers a thousandth
    /// of the layout is a rounding that argues the opposite of the truth, so anything below the
    /// first decimal is reported as a bound rather than as zero.</summary>
    private static string FormatPercent(double pct) => pct switch
    {
        <= 0  => "0 %",
        < 0.1 => "under 0.1 %",
        _     => $"{pct:0.#} %",
    };

    /// <summary>
    /// <b>ANT-1 — the meshable REGIONS of one shape.</b> Everything except a <c>PathShape</c> is its
    /// own region and is returned unchanged, so every existing conversion is bit-for-bit what it was.
    /// A width-bearing stroke is outlined into filled regions here, and only here.
    ///
    /// <para><b>This writes no offsetting of its own.</b> <see cref="LayoutBooleans.Repair"/> is the
    /// one route — <c>LayoutClipper.ToClipperPaths</c> dispatches a Path to <c>InflatePaths</c> on
    /// the flattened centreline at <c>Width/2</c> with the cap taken from its <c>End</c> style (all
    /// four, <c>Extended</c> included), then a NonZero union resolves a self-crossing track into
    /// clean outer rings and holes. DRC, the Gerber writer and the DXF writer all reach the same
    /// routine, which is what makes "what the solver meshes" and "what the fab sees" the same
    /// copper. A second offsetter here would disagree at a mitre and nobody could localise it.</para>
    ///
    /// <para>Only reachable with <c>Width &gt; 0</c>: the classification loop keeps a zero-width Path
    /// out of both shape lists.</para>
    /// </summary>
    private static IReadOnlyList<LayoutShape> RegionsToMesh(LayoutShape shape, Technology tech)
        => shape is PathShape ? LayoutBooleans.Repair(shape, tech).Shapes : [shape];

    /// <summary>ANT-1 — one polygon's own area in m², holes subtracted, for the conversion note. The
    /// polygons are already in metres by the time this is asked, so the note's number is the metal
    /// the mesh actually receives rather than a DBU count converted a second way.</summary>
    private static double NetArea(PlanarPolygon poly)
    {
        double a = Polygon2D.Area(poly.Outer);
        if (poly.Holes is { Count: > 0 } holes)
            foreach (var h in holes) a -= Polygon2D.Area(h);
        return Math.Max(a, 0.0);
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
    /// <summary>
    /// <b>CL7 — the ground plane's own metal, as the medium's floor.</b> CL4 built the termination
    /// and CL6 gave the one-slab kernel one; this is the line that makes a run get one, and it is
    /// the whole of the flip. Worth 21.1% of the conductor term on FR-4, ~11% on the MMIC starter
    /// and 25.0% on a low-loss laminate (series overview §2), and on the MMIC starter the conductor
    /// term is itself 92-99% of the line's loss.
    ///
    /// <para><b>Both σ and t come off the band that was CHOSEN as the return plane and off nothing
    /// else.</b> A ground-designated conductor that was skipped over, or a conductor band that is
    /// neither a level nor the return plane, is absorbed into a neighbouring dielectric
    /// (<c>BuildMediumStack</c>, and the warning above says so) — those must not acquire a floor by
    /// accident, which is why this reads one band rather than searching for metal.</para>
    ///
    /// <para><b>A PERFECT floor is still what a stackup with no metal under it gets</b>, and it is
    /// returned as <see cref="Termination.Pec"/> rather than as a perfect SPELLING of a conducting
    /// plane. The two are bit-identical in the kernel (CL4 §1, CL6 §2) and are NOT identical to
    /// <see cref="EmSnpProvenance"/> or to record equality, so spelling "no metal" as
    /// <c>LossyGround(0, t)</c> would invalidate every cached <c>.snp</c> in every workspace to
    /// record a σ nobody supplied.</para>
    ///
    /// <para><b>There is no <c>.cem</c> key and no UI control for this</b>, per the series
    /// overview §5: whether Maxwell's equations include Ohm's law is not a decision to put in front
    /// of a user. The σ is the technology's, exactly as the signal metal's is.</para>
    /// </summary>
    private static Termination FloorFor(Band? groundBand)
    {
        if (groundBand is null) return Termination.Pec;
        double sigma = groundBand.Layer.SigmaSm;
        double t     = groundBand.TopM - groundBand.BottomM;
        return sigma > 0 && !double.IsPositiveInfinity(sigma) && t > 0
            ? Termination.LossyGround(sigma, t)
            : Termination.Pec;
    }

    private static (LayerStack Stack, double[] LevelZ, string? Note) BuildMediumStack(
        List<Band> bands, List<Band> levels, double groundTopM, Termination floor)
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

        return (new LayerStack(floor, layers, Termination.Air), levelZ, note);
    }

    /// <summary>Is (x, y) on any of these polygons? Box-filtered, then the polygon's own even-odd
    /// test — the same rule everywhere metal is tested for coverage.</summary>
    private static bool Covers(IReadOnlyList<PlanarPolygon> polys, double x, double y)
    {
        foreach (var p in polys)
        {
            var b = p.Bounds();
            if (x < b.MinX || x > b.MaxX || y < b.MinY || y > b.MaxY) continue;
            if (p.Contains(x, y)) return true;
        }
        return false;
    }

    /// <summary>The vertex average of a footprint's outline — a representative point for asking what
    /// is above and below a DRAWN via, which is a small compact shape by construction.</summary>
    private static (double X, double Y) Centroid(PlanarPolygon poly)
    {
        double cx = 0, cy = 0;
        foreach (var p in poly.Outer) { cx += p.X; cy += p.Y; }
        int n = Math.Max(poly.Outer.Count, 1);
        return (cx / n, cy / n);
    }

    private static bool SameMaterial(EmMaterial a, EmMaterial b) =>
        Math.Abs(a.EpsR - b.EpsR) <= 1e-12 &&
        Math.Abs(a.TanD - b.TanD) <= 1e-12 &&
        Math.Abs(a.MuR  - b.MuR)  <= 1e-12;


    /// <summary>What a via entry's declared span resolved to (GVIA-1). Three answers rather than
    /// two because a barrel that crosses the return plane has no per-ENTRY answer at all — the
    /// terminals are known, and whether any given via actually reaches them is a per-SHAPE question
    /// the artwork answers.</summary>
    private enum ViaSpan
    {
        /// <summary>Ignored, and one of BuildVias' counters says why.</summary>
        Rejected,

        /// <summary>Both terminals resolved from the technology, for every via on the entry.</summary>
        Fixed,

        /// <summary>The barrel crosses the return plane: the terminals are the analysis level and
        /// the plane, but only for the footprints that actually land on plane copper.</summary>
        CrossesPlane,
    }


    /// <summary>
    /// <b>GVIA-1 — WHICH COPPER DRAWN ON THE RETURN PLANE IS ACTUALLY THE PLANE.</b>
    ///
    /// <para>A board with an inner ground plane draws that plane as a pour with VOIDS in it, and
    /// every signal via that passes through the plane sits in one — usually with its own annular
    /// pad drawn as a separate island INSIDE the void, because that pad is copper on that layer too.
    /// So "is this via on plane copper?" and "is this via connected to the plane?" are different
    /// questions, and only the second one is the physics. Measured on the reported board: a
    /// containment test against the plane layer's artwork grounds all 327 vias, and 61 of them are
    /// sitting on isolated 0.11 mm² pads inside antipads.</para>
    ///
    /// <para><b>The rule is enclosure, not area.</b> A polygon whose outline lies inside a VOID of
    /// another polygon on the same layer is separated from that polygon by construction — that is
    /// what a void is — so it is an island and not the plane. Everything else is the plane. The
    /// obvious alternative, "the biggest pour wins", is rejected: a board with two genuine ground
    /// pours has two planes and picking one of them by area would silently drop the stitching on the
    /// other. Nesting deeper than one level (an island inside an island's own void) stays an island,
    /// which is the conservative direction — an unclassified via is dropped and reported, exactly as
    /// it is today.</para>
    ///
    /// <para>Not used for R-fg-2's published plane SIZE, deliberately: that number is the outline
    /// this run reads and changing what it measures is a separate question from which vias reach
    /// ground.</para>
    /// </summary>
    private sealed class PlaneMetal
    {
        private readonly record struct Box(double MinX, double MinY, double MaxX, double MaxY)
        {
            public bool Holds(double x, double y) => x >= MinX && x <= MaxX && y >= MinY && y <= MaxY;
            public bool Holds(in Box b) => b.MinX >= MinX && b.MaxX <= MaxX &&
                                           b.MinY >= MinY && b.MaxY <= MaxY;
        }

        private static Box BoundsOf(IReadOnlyList<EmPoint> ring)
        {
            double x0 = double.PositiveInfinity, y0 = double.PositiveInfinity;
            double x1 = double.NegativeInfinity, y1 = double.NegativeInfinity;
            foreach (var p in ring)
            {
                if (p.X < x0) x0 = p.X;
                if (p.Y < y0) y0 = p.Y;
                if (p.X > x1) x1 = p.X;
                if (p.Y > y1) y1 = p.Y;
            }
            return new Box(x0, y0, x1, y1);
        }

        private readonly (PlanarPolygon Poly, Box Outer, Box[] Holes)[] _plane;

        /// <summary>How many of the plane layer's polygons are isolated islands rather than plane.</summary>
        public int Islands { get; }

        /// <summary>True when there is plane copper to test against at all.</summary>
        public bool Any => _plane.Length > 0;

        public PlaneMetal(IReadOnlyList<PlanarPolygon> groundPolys)
        {
            int n = groundPolys.Count;
            var outer = new Box[n];
            var holes = new Box[n][];
            for (int i = 0; i < n; i++)
            {
                outer[i] = BoundsOf(groundPolys[i].Outer);
                var hr = groundPolys[i].HoleRings;
                holes[i] = new Box[hr.Count];
                for (int h = 0; h < hr.Count; h++) holes[i][h] = BoundsOf(hr[h]);
            }

            var keep = new List<(PlanarPolygon, Box, Box[])>(n);
            for (int i = 0; i < n; i++)
            {
                if (groundPolys[i].Outer.Count < 3) continue;
                if (Enclosed(groundPolys, outer, holes, i)) { Islands++; continue; }
                keep.Add((groundPolys[i], outer[i], holes[i]));
            }
            _plane = [.. keep];
        }

        /// <summary>Is polygon <paramref name="i"/>'s outline inside a VOID of some other polygon on
        /// this layer? Tested on one vertex of its outline, which is enough because plane artwork
        /// arrives as non-intersecting rings — a shape is wholly inside a void or wholly outside
        /// it.</summary>
        private static bool Enclosed(
            IReadOnlyList<PlanarPolygon> polys, Box[] outer, Box[][] holes, int i)
        {
            var probe = polys[i].Outer[0];
            for (int q = 0; q < polys.Count; q++)
            {
                if (q == i) continue;
                if (!outer[q].Holds(outer[i])) continue;
                var hr = polys[q].HoleRings;
                for (int h = 0; h < hr.Count; h++)
                {
                    if (!holes[q][h].Holds(outer[i])) continue;
                    if (PlanarPolygon.RingContains(hr[h], probe.X, probe.Y)) return true;
                }
            }
            return false;
        }

        /// <summary>True when (x, y) lands on copper that IS the plane.
        ///
        /// <para>Deliberately not <see cref="PlanarPolygon.Contains"/>, and only for the reason a
        /// bound gives: a plane pour carries a void per through-hole, so Contains walks every hole
        /// ring of a ~15,000-vertex polygon for every via on the board. Each ring is skipped by its
        /// own box here instead, and the ring test itself is the SAME even-odd function Contains
        /// calls — there is no second containment rule, only a cheaper way to reach it. Measured on
        /// the reported board: 200 ms to 24 ms over 327 vias.</para></summary>
        public bool Carries(double x, double y)
        {
            foreach (var (poly, box, holes) in _plane)
            {
                if (!box.Holds(x, y)) continue;
                if (!PlanarPolygon.RingContains(poly.Outer, x, y)) continue;

                bool inVoid = false;
                var hr = poly.HoleRings;
                for (int h = 0; h < hr.Count && !inVoid; h++)
                    inVoid = holes[h].Holds(x, y) && PlanarPolygon.RingContains(hr[h], x, y);

                if (!inVoid) return true;
            }
            return false;
        }

        /// <summary>True when any vertex of <paramref name="poly"/>, or its own centroid, lands on
        /// the plane. A drawn footprint is not a point and its centroid can fall in a void the
        /// footprint straddles, so a touch anywhere counts — a via that reaches plane copper is
        /// connected to it.</summary>
        public bool Touches(PlanarPolygon poly)
        {
            double cx = 0, cy = 0;
            foreach (var p in poly.Outer) { cx += p.X; cy += p.Y; }
            if (poly.Outer.Count > 0 && Carries(cx / poly.Outer.Count, cy / poly.Outer.Count)) return true;
            foreach (var p in poly.Outer)
                if (Carries(p.X, p.Y)) return true;
            return false;
        }
    }


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
    ///
    /// <para><b>GVIA-1 — a THROUGH via is the one case where the stackup cannot answer alone, and
    /// the artwork decides PER SHAPE.</b> Everything above rests on "a board plates every via of a
    /// given kind between the same two layers", which is true of the BARREL and says nothing about
    /// what the barrel touches on the way. A plated through-hole on a board with an inner ground
    /// plane spans top copper to bottom copper whatever it is for; whether it is a ground stitch or
    /// a signal via is decided by the plane's own artwork — pour copper at that point, or a void.
    /// Both are ordinary and both are in every such board.</para>
    ///
    /// <para>So when an entry's span CROSSES this run's return plane — one named conductor is an
    /// analysis level, the other is below the plane, and the plane lies between them — each drawn
    /// footprint is classified on its own: landing on plane copper makes it a ground attachment,
    /// and landing in a void (or on an isolated island in one) leaves it a via to a conductor this
    /// analysis does not have, which is dropped and counted exactly as before. That is the physics
    /// as well as the request: an unbroken plane decouples the two halves of the board, so the top
    /// half's model is its own metal plus the stitching that reaches the plane, and a via that
    /// passes through a void ends in mid-dielectric where this kernel has no basis to put it.</para>
    ///
    /// <para><b>This fires only where the previous behaviour dropped EVERY via with the wrongGround
    /// note</b> — it cannot change a span that resolves today, and it needs no technology edit: the
    /// `.ctech` already says the barrel goes top to bottom, which is true.</para>
    /// </summary>
    private static List<PlanarVia> BuildVias(
        List<(ViaShape Shape, StackupLayer Entry)> viaShapes,
        List<(PlanarPolygon Poly, StackupLayer Entry)> regionViaPolys,
        List<Band> levels, double perDbu, List<EmFinding> notes, Band? groundBand = null,
        List<Band>? stack = null, PlaneMetal? planeMetal = null,
        Func<int, IReadOnlyList<PlanarPolygon>>? farMetal = null,
        List<PlanarPolygon>[]? levelPolys = null)
    {
        var vias = new List<PlanarVia>();
        if (viaShapes.Count == 0 && regionViaPolys.Count == 0) return vias;

        // `toGround` counts only the vias whose ENTRY names the plane. A via that got there by
        // crossing is counted by `stitched` and reported by the crossing note, deliberately: both
        // notes firing said "266 vias go to the plane" twice in a row, which is exactly the noise
        // that makes a run's notes unread (owner, 2026-09-12).
        int noSpan = 0, unknownLevels = 0, notAdjacent = 0, toGround = 0, wrongGround = 0;
        int chained = 0, chainedLevels = 0;
        int pointVias = 0, regionVias = 0, regionPolys = 0;
        int stitched = 0, passedThrough = 0, carriedAway = 0;
        var wrongGroundNames = new List<string>();
        string? crossedName = null;
        int crossedFarIndex = -1, crossedLevel = -1;

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
        ViaSpan Terminals(StackupLayer entry, int count, out int lower, out int upper)
        {
            lower = upper = 0;

            string? from = entry.SpanFromLayer, to = entry.SpanToLayer;
            if (from is not { Length: > 0 } || to is not { Length: > 0 })
            { noSpan += count; return ViaSpan.Rejected; }

            int a  = levels.FindIndex(b => string.Equals(b.Layer.Name, from, StringComparison.Ordinal));
            int b2 = levels.FindIndex(b => string.Equals(b.Layer.Name, to,   StringComparison.Ordinal));

            if (a < 0 || b2 < 0)
            {
                string missing = a < 0 ? from : to;
                int meshed     = a < 0 ? b2   : a;

                if (meshed < 0) { unknownLevels += count; return ViaSpan.Rejected; }

                if (groundBand is not null &&
                    string.Equals(groundBand.Layer.Name, missing, StringComparison.Ordinal))
                {
                    lower = PlanarVia.GroundTerminal;
                    upper = meshed;
                    toGround += count;
                    return ViaSpan.Fixed;
                }

                // ── GVIA-1 — the barrel CROSSES the return plane, so the artwork decides ────────
                //
                // Narrow on purpose, and every clause earns its place. The far conductor must be a
                // real conductor in this stackup (a name that matches nothing is a technology
                // error, not a through via); the plane must sit wholly BETWEEN the two, which is
                // what makes "it passes through the plane" a statement about this stack rather
                // than about the two names; and there must be plane artwork to read, because with
                // none of it every via would classify the same way and the classification would be
                // a guess wearing a measurement's clothes. Any of those missing and this is the
                // wrongGround refusal it has always been.
                if (groundBand is not null && stack is not null && planeMetal is { Any: true })
                {
                    var far = stack.FirstOrDefault(b =>
                        b.Layer.Kind == StackupKind.Conductor &&
                        string.Equals(b.Layer.Name, missing, StringComparison.Ordinal));

                    if (far is not null &&
                        far.TopM <= groundBand.BottomM + 1e-15 &&
                        groundBand.TopM <= levels[meshed].SheetM + 1e-15)
                    {
                        lower = PlanarVia.GroundTerminal;
                        upper = meshed;
                        crossedName ??= missing;
                        crossedFarIndex = far.Index;
                        crossedLevel    = meshed;
                        return ViaSpan.CrossesPlane;
                    }
                }

                wrongGround += count;
                if (!wrongGroundNames.Contains(missing)) wrongGroundNames.Add(missing);
                return ViaSpan.Rejected;
            }

            lower = Math.Min(a, b2);
            upper = Math.Max(a, b2);

            // ── A VIA THAT SPANS A LEVEL IS A CHAIN OF VIAS, AND THE EXTRACTOR BUILDS IT ───────
            //
            // The vertical basis pairs a cell with the cell DIRECTLY above it, so a barrel from
            // level i to level i+2 has no single basis to live on. Until now it was dropped, and on
            // the shipped MMIC technology that is not an edge case: ticking 'MIM Metal' to model a
            // capacitor is exactly what puts a level between Metal1 and Metal2, and every
            // Metal1-Metal2 post in the design — a spiral's underpass, the MIM capacitor's own
            // output via — went with it. There was no setting that solved both halves at once.
            //
            // THE INTERVENING LEVEL'S METAL IS THE BARREL ITSELF. A post passing through z is
            // conducting metal at z, of exactly the barrel's cross-section, and `AddChain` below
            // puts that footprint on each level it passes before emitting one via per gap. This
            // invents nothing: it states the cross-section the drawn via already has at a height
            // the analysis happens to sample. If the intervening level carries its own metal there,
            // the post genuinely shorts to it — which is what the artwork says and what a real
            // process would build.
            //
            // It cannot change a span that resolves today: `upper == lower + 1` takes the same
            // single-via path it always has, and every existing gate is on that path.
            if (upper != lower + 1)
            {
                if (levelPolys is null) { notAdjacent += count; return ViaSpan.Rejected; }
                chained += count;
                chainedLevels = Math.Max(chainedLevels, upper - lower - 1);
            }
            return ViaSpan.Fixed;
        }

        // One PlanarVia per GAP, and the barrel's own cross-section on every level it passes
        // through. `lower` may be PlanarVia.GroundTerminal, which is not a level index and never
        // spans — the ground paths above return before the adjacency test, so `upper == lower + 1`
        // holds for them and this degenerates to the single Add it replaced.
        void AddSpan(int lo, int hi, List<PlanarPolygon> footprints, double sigmaSm)
        {
            if (lo == PlanarVia.GroundTerminal || hi == lo + 1)
            {
                vias.Add(new PlanarVia(lo, hi, footprints, sigmaSm));
                return;
            }
            for (int i = lo + 1; i < hi; i++) levelPolys![i].AddRange(footprints);
            for (int i = lo; i < hi; i++)
                vias.Add(new PlanarVia(i, i + 1, footprints, sigmaSm));
        }

        // ── GVIA-2 — a via that bypasses the plane: does it CARRY THE STRUCTURE AWAY? ─────────
        //
        // "Landed in a void" and "joins the two outer conductors" are different sets, and only the
        // second one damages the answer. A drill with nothing on the far side is a hole; a via with
        // metal on the analysis level AND on the conductor beyond the plane is a SIGNAL via, and the
        // structure this run solves continues through it into metal the run does not contain — the
        // s-parameters simply stop there. Measured on the reported board: 61 bypass the plane and 60
        // of them are that second kind, so reporting the first number alone would have described
        // the smaller fact.
        //
        // Both halves are tested, never assumed from the span: the stackup says where the barrel
        // LANDS, and whether there is copper there is a question about the artwork.
        void CountBypass(int level, double x, double y)
        {
            passedThrough++;
            if (farMetal is null || crossedFarIndex < 0 || levelPolys is null) return;
            if (!Covers(levelPolys[level], x, y)) return;
            if (!Covers(farMetal(crossedFarIndex), x, y)) return;
            carriedAway++;
        }

        foreach (var (shape, entry) in viaShapes)
        {
            var span = Terminals(entry, 1, out int lower, out int upper);
            if (span == ViaSpan.Rejected) continue;

            // The equal-area square, centred on the via: side = d·√π/2.
            double d = shape.DrillSize * perDbu;
            if (!(d > 0)) d = shape.PadSize * perDbu;
            if (!(d > 0)) continue;
            double half = 0.5 * d * Math.Sqrt(Math.PI) / 2.0;
            double cx = shape.X * perDbu, cy = shape.Y * perDbu;

            // GVIA-1 — a round barrel is tested at its CENTRE, which is the whole of the question
            // for it: an antipad is drawn larger than the drill it clears, so a via either sits in
            // the void or sits on the pour, and no part of the barrel is on the other side of that
            // answer. The drawn footprints below are not points and are tested as areas.
            if (span == ViaSpan.CrossesPlane)
            {
                if (!planeMetal!.Carries(cx, cy)) { CountBypass(upper, cx, cy); continue; }
                stitched++;
            }

            AddSpan(lower, upper,
                [new PlanarPolygon([new EmPoint(cx - half, cy - half), new EmPoint(cx + half, cy - half),
                                    new EmPoint(cx + half, cy + half), new EmPoint(cx - half, cy + half)])],
                entry.SigmaSm);
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

            var span = Terminals(entry, shapeCount, out int lower, out int upper);
            if (span == ViaSpan.Rejected) continue;

            // GVIA-1 — the group still becomes ONE PlanarVia, for the overlap reason above, but it
            // carries only the footprints that reach the plane. Splitting the survivors into one
            // record each would re-introduce exactly the double-counted metal the grouping exists
            // to prevent; dropping the group because some of it passed through would lose the part
            // that stitches.
            List<PlanarPolygon> footprints;
            if (span == ViaSpan.CrossesPlane)
            {
                // ONE pass: each drawn footprint is either kept or counted, never both and never
                // matched back by value — two identical rectangles drawn on top of each other are
                // equal as records and must still be two shapes in the accounting.
                footprints = [];
                foreach (var (poly, _) in group)
                {
                    if (planeMetal!.Touches(poly)) { footprints.Add(poly); continue; }
                    var c = Centroid(poly);
                    CountBypass(upper, c.X, c.Y);
                }

                stitched += footprints.Count;
                if (footprints.Count == 0) continue;
                shapeCount = footprints.Count;
            }
            else footprints = [.. group.Select(g => g.Poly)];

            AddSpan(lower, upper, footprints, entry.SigmaSm);
            regionVias++;
            regionPolys += shapeCount;
        }

        // SHORT, for the warning's own reason (owner, 2026-09-12): a run whose notes are paragraphs
        // is a run whose notes are skipped, and this one fires on every board of this shape. What
        // survives is the two counts, what decided them, and that the technology is fine — the last
        // because "my `.ctech` must be wrong" is the conclusion a user otherwise reaches. Everything
        // cut was either reference material (what an attachment basis IS — the BACKSIDE note's job)
        // or already in the WARNING beside it (what a pass-through costs, and solving the other
        // side). The island clause stays and is worth its length: "the via IS on my gnd layer, why
        // is it not grounded" is the one question this note exists to pre-empt.
        if (stitched > 0 || passedThrough > 0)
            notes.Add(
                $"{stitched} via(s) on this entry stitch to '{groundBand!.Layer.Name}'; " +
                $"{passedThrough} pass through a void in it and are ignored. The entry spans " +
                $"'{crossedName}' on the far side of the plane, so each via was classified from the " +
                "plane's own artwork — copper under it, or a clearance — not from the technology, " +
                "which needs no change." +
                (planeMetal!.Islands > 0
                    ? $" ({planeMetal.Islands} shape(s) on '{groundBand.Layer.Name}' are isolated " +
                      "islands inside a void rather than the plane, so a via landing on one is not " +
                      "grounded.)"
                    : string.Empty));

        // ── GVIA-2 — the one of those two counts that is a WARNING ───────────────────────────
        //
        // Separate note, deliberately, and it is not folded into the sentence above. That one
        // reports a decision the run made correctly; this one reports that the structure does not
        // END where this analysis does, which is the user's problem rather than the extractor's.
        //
        // THREE SENTENCES, and the brevity is the requirement rather than a style preference
        // (owner, 2026-09-12): a designer does not read a paragraph, and a warning nobody reads
        // warns nobody. So it carries only what cannot be worked out from anywhere else — the
        // count, the two conductors, the plane, that they are NOT modelled, and what to do. The
        // reasoning behind it is in this file and in RESOLVED.md, which is where reasoning belongs;
        // resist restating it here, because every clause added costs the sentence that matters.
        if (carriedAway > 0)
            notes.Add(EmFinding.Warn(
                $"{carriedAway} via(s) join '{levels[crossedLevel].Layer.Name}' to " +
                $"'{crossedName}' without touching '{groundBand!.Layer.Name}'. These are signal " +
                "vias, not stitches: they are NOT modelled, so the structure continues where these " +
                "s-parameters stop. Solve the other side as its own run and join them there, or " +
                "analyse a region these vias do not leave."));

        if (toGround > 0)
            notes.Add($"{toGround} of them are BACKSIDE vias, running from a signal level down to " +
                      $"the ground plane '{groundBand!.Layer.Name}'. That plane is the laterally " +
                      "infinite conductor the Green's function handles analytically rather than a " +
                      "meshed level, so each is a half (attachment) basis whose return charge is the " +
                      "plane's own image.");
        // ── EM-SEV R-emsev-2 — A DISCARDED VIA IS A WARNING, and this is the least ambiguous
        //    finding in the whole area. A designer who DREW a via stated a connection; dropping it
        //    severs that connection, and the answer published is for a structure in more pieces than
        //    the one on screen. On the design that prompted EM-SEV this single line was the
        //    difference between an inductor in series with a capacitor and two disconnected islands
        //    of metal — reported, correctly worded, in the same breath and at the same weight as the
        //    core count.
        //
        //    All three of these say the same thing with different reasons for it, so all three move
        //    together. The legitimate ground-pour case above (`stitched`/`toGround`) stays a note:
        //    there the run made a decision and made it right.
        if (wrongGround > 0)
            notes.Add(EmFinding.Warn(
                      $"{wrongGround} via shape(s) span a conductor ({string.Join(", ", wrongGroundNames)}) " +
                      "that is neither an analysis level nor the ground plane this kernel models, and " +
                      "were ignored — so a connection you drew is NOT in this answer. The only " +
                      "non-meshed conductor a via may terminate on is the " +
                      "ground reference R-em-4 resolves — a different ground pour is a finite " +
                      "conductor this kernel does not mesh, and treating it as the infinite plane " +
                      "would solve a structure you did not draw. Add that conductor to this EM " +
                      "setup's analysis levels to model the connection."));

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
            notes.Add(EmFinding.Warn(
                      $"{noSpan} via shape(s) were ignored because their stackup via entry names no " +
                      "SpanFrom/SpanTo conductors. Which two levels a via joins is a property of the " +
                      "process, not of the drawing — set the span in the technology editor's Stackup " +
                      "tab. Ignoring it is safer than guessing: a via joining the wrong pair of " +
                      "levels renders perfectly and solves to a wrong answer."));
        if (unknownLevels > 0)
            notes.Add(EmFinding.Warn(
                      $"{unknownLevels} via shape(s) span conductors that are not among this EM " +
                      "setup's analysis levels, and were ignored — the connection they draw is not " +
                      "in this answer. Add those conductors to the analysis levels."));
        // EM-SEV R-emsev-2, the fourth member its own text did not enumerate. It makes the
        // IDENTICAL claim to the three above — a connection the designer drew is not in the answer —
        // and it is the one that fires on the shipped MMIC technology, because adding 'MIM Metal' to
        // the level list is exactly what puts a level between Metal1 and Metal2 and drops every
        // Metal1-Metal2 post in the structure. A spiral's underpass and the shipped MIM capacitor's
        // own output via are both posts, so on that technology the user is told that half their
        // circuit was deleted at the weight of the core count, in the very run they added the plate
        // level to FIX the other half. Warning, for the reason the other three are.
        if (chained > 0)
            notes.Add($"{chained} via shape(s) span {(chainedLevels == 1 ? "a level" : "levels")} " +
                      "between their two terminals and were built as a CHAIN — one vertical basis " +
                      "per gap, with the barrel's own cross-section carried onto each level it " +
                      "passes through. A vertical basis pairs a cell with the cell directly above " +
                      "it, so a post from Metal1 to Metal2 across an intervening plate level is " +
                      "several vias, not one. The connection you drew IS in this answer.");
        if (notAdjacent > 0)
            notes.Add(EmFinding.Warn(
                      $"{notAdjacent} via shape(s) span two levels that are not ADJACENT in the " +
                      "analysis and were ignored — so a connection you drew is NOT in this answer. " +
                      "A vertical basis pairs a cell with the cell directly above it, so a " +
                      "stacked via is a chain of vias — give one via entry per gap, or REMOVE the " +
                      "intervening level from the analysis. Note that removing it is not always " +
                      "available: a level carrying artwork of its own cannot be dropped without " +
                      "deleting that artwork from the answer too."));

        return vias;
    }

    // ── Stackup -> z bands (restated from CrossSectionExtractor, not shared — see the header) ──

    /// <summary>
    /// <b>The analysis levels the PORTS imply: the levels they sit on, plus everything a drawn via
    /// joins to those, intersected with the levels that actually carry artwork.</b>
    ///
    /// <para>Resolution is by LAYER KEY, not by geometry, and that is the whole of it: a port's
    /// <see cref="LabelShape.PortLayer"/> is the conductor it committed to at placement, and its own
    /// drawing layer is what every port placed before that field existed has. Point-in-polygon would
    /// be more general and is deliberately not done here — it would need the flattened artwork, which
    /// does not exist until the levels are known, and the fallback below is already the safe
    /// direction. <c>EmPortExtraction</c> resolves a port geometrically against the finished mesh and
    /// is unaffected; this is only which levels get built.</para>
    ///
    /// <para><b>Empty means "no opinion", never "no levels".</b> A layout with no port labels — which
    /// is what nearly every extraction test hands <see cref="Extract"/> — returns empty here and
    /// takes the artwork rule unchanged.</para>
    /// </summary>
    private static HashSet<int> LevelsThePortsReach(
        IReadOnlyList<LayoutShape>       shapes,
        Dictionary<LayerKey, List<Band>> binding,
        IEnumerable<StackupLayer>        drawnViaEntries,
        List<Band>                       signalBands,
        out int                          portsSeen,
        out int                          portsUnresolved)
    {
        portsSeen = portsUnresolved = 0;

        var seeded = new HashSet<int>();
        foreach (var s in shapes)
        {
            if (s is not LabelShape { IsPort: true } label) continue;
            portsSeen++;

            var band = SignalBandFor(label.PortLayer, binding) ?? SignalBandFor(label.Layer, binding);
            if (band is null) { portsUnresolved++; continue; }
            seeded.Add(band.Index);
        }

        if (seeded.Count == 0) return [];

        // ── Carry the seed across DRAWN vias, to a fixpoint ──────────────────────────────────
        //
        // The artwork says a via is there; the STACKUP says which two conductors it joins
        // (BuildVias' own rule, restated rather than re-derived). Only entries that some shape
        // actually landed on are considered — a via kind declared in the technology and never drawn
        // joins nothing on this board, and letting it pull in a level would put the technology back
        // in charge of what is being solved.
        //
        // A fixpoint rather than one pass because via kinds chain: L1-L2 then L2-L3 reaches L3 from a
        // port on L1, and the loop cost is bounded by the number of levels.
        var spans = drawnViaEntries
            .Where(e => e.SpanFromLayer is { Length: > 0 } && e.SpanToLayer is { Length: > 0 })
            .Select(e => (From: e.SpanFromLayer!, To: e.SpanToLayer!))
            .Distinct()
            .ToList();

        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var (from, to) in spans)
            {
                var a = signalBands.FirstOrDefault(b => string.Equals(b.Layer.Name, from, StringComparison.Ordinal));
                var b2 = signalBands.FirstOrDefault(b => string.Equals(b.Layer.Name, to, StringComparison.Ordinal));
                if (a is null || b2 is null) continue;          // an end that is ground, or carries nothing
                if (seeded.Contains(a.Index) && seeded.Add(b2.Index)) grew = true;
                if (seeded.Contains(b2.Index) && seeded.Add(a.Index)) grew = true;
            }
        }

        seeded.IntersectWith(signalBands.Select(b => b.Index));
        return seeded;
    }

    /// <summary>RP-1: the conductor stackup entries a return-plane override may legally name, for the
    /// "no layer by that name" refusal. Every conductor, ground-designated or not — R-rp1-4 permits a
    /// non-designated one, and a list that hid the legal choices would teach the wrong rule in the
    /// one message a user reads when they have already got the name wrong.</summary>
    private static string ConductorList(List<Band> stack)
    {
        var names = stack
            .Where(b => b.Layer.Kind == StackupKind.Conductor)
            .OrderByDescending(b => b.TopM)
            .Select(b => $"'{b.Layer.Name}'")
            .ToList();
        return names.Count == 0 ? "(this technology has no conductor stackup layers)"
                                : string.Join(", ", names);
    }

    /// <summary>RP-1: what R-em-4 WOULD have resolved, for the overridden note. Reported because the
    /// interesting thing about an override is the difference it made, and a user comparing two
    /// references cannot otherwise see which one they moved away from. All three outcomes of the
    /// inferred rule are spelled out, the third included: an override can be the only reason a run
    /// happened at all.</summary>
    private static string InferredWouldHaveBeen(Band? inferred, BoundaryCondition bottom,
                                                List<Band> stack)
        => inferred is not null
            ? $"R-em-4 would otherwise have chosen '{inferred.Layer.Name}' at {inferred.TopM * 1e6:G4} µm"
            : bottom == BoundaryCondition.Ground
                ? "no conductor below that level is designated as a ground reference, so the run " +
                  $"would otherwise have taken Stackup.Bottom = Ground at {stack[0].BottomM * 1e6:G4} µm"
                : "no conductor below that level is designated as a ground reference and " +
                  "Stackup.Bottom is not Ground, so without this setting the run would have been " +
                  "refused for having no ground plane at all";

    /// <summary><b>R-em-4's own query, in one place.</b> The highest ground-designated conductor whose
    /// TOP SURFACE is at or below <paramref name="sheetM"/> — the plane a conductor at that height
    /// returns through. Factored out because the incidental-level check above has to ask it of a
    /// level it is considering DISCARDING, and a second spelling of this query would be a second
    /// chance to get R-em-4 wrong.</summary>
    private static Band? HighestGroundBelow(List<Band> stack, double sheetM) => stack
        .Where(b => b.Layer.Kind == StackupKind.Conductor && b.Layer.IsGroundReference && b.TopM <= sheetM)
        .OrderByDescending(b => b.TopM)
        .FirstOrDefault();

    /// <summary>
    /// <b>RP-3 — does the return-plane resolution below reach a USABLE plane in this frame?</b>
    /// Usable means a positive slab: a plane at or above the lowest level leaves nothing to solve
    /// on, which is the zero-height refusal further down rather than an answer.
    ///
    /// <para>It restates the resolution order deliberately and narrowly — the <c>.cem</c>'s own
    /// name first, then R-em-4, then the stackup boundary — because the flip decision has to be
    /// made BEFORE that block runs and asked of a frame that does not exist yet. It answers only
    /// "is there a plane", never which one: every refusal, every note and the plane itself still
    /// come from the one block below, so a disagreement here can cost a flip that was available
    /// but can never produce a plane the run did not resolve.</para>
    ///
    /// <para><paramref name="designated"/> separates a plane the TECHNOLOGY states (a
    /// ground-designated conductor, or one this setup named) from the <c>Stackup.Bottom</c>
    /// boundary fallback — which is what lets a flip that finds a real conductor win over an
    /// orientation that merely finds the bottom of the stack.</para>
    /// </summary>
    private static bool ResolvesAUsablePlane(
        List<Band> stack, List<Band> levels, BoundaryCondition bottom,
        EmExtractionSettings settings, out bool designated)
    {
        designated = false;
        var lowest = levels[0];

        if (settings.GroundStackupLayerName is { Length: > 0 } wanted)
        {
            var named = stack.FirstOrDefault(b =>
                b.Layer.Kind == StackupKind.Conductor &&
                string.Equals(b.Layer.Name, wanted, StringComparison.Ordinal));

            // A name the technology does not have, and a name that is also an analysis level, are
            // both REFUSALS below and neither is helped by a flip — report no plane in either
            // frame so the refusal is the one the user reads.
            if (named is null || levels.Any(b => b.Index == named.Index)) return false;

            designated = true;
            return named.TopM < lowest.SheetM - 1e-15;
        }

        if (HighestGroundBelow(stack, lowest.SheetM) is { } ground)
        {
            designated = true;
            return ground.TopM < lowest.SheetM - 1e-15;
        }

        return bottom == BoundaryCondition.Ground && stack[0].BottomM < lowest.SheetM - 1e-15;
    }

    /// <summary>
    /// <b>RP-3 — the same stack, reflected in a horizontal plane.</b> Every band's z is measured
    /// from <paramref name="datumM"/> downward instead of from the bottom up, so the stackup's top
    /// surface becomes the new zero and the order reverses.
    ///
    /// <para><b>Two things are deliberately NOT mirrored.</b> <see cref="Band.Index"/> is the
    /// stackup position and is carried through untouched — every downstream match (the classified
    /// shapes, the ground pour, a via's terminals) is made on it, and renumbering would silently
    /// re-point all of them. And the analysis SHEET stays on the same NAMED surface of its own band
    /// — bottom stays bottom — which in the flipped frame is the surface facing the plane, so the
    /// modelled height reads as the substrate thickness exactly as
    /// <see cref="ConductorSheetSurface"/> says it should. A pure geometric reflection would put it
    /// on the far side of the metal and quietly add a conductor thickness to every height.</para>
    /// </summary>
    private static List<Band> MirrorStack(List<Band> stack, double datumM) =>
        [.. stack
            .Select(b => new Band(
                b.Layer, datumM - b.TopM, datumM - b.BottomM, b.Index,
                SurfaceOf(b.Layer) == ConductorSheetSurface.Top ? datumM - b.BottomM
                                                                : datumM - b.TopM))
            .OrderBy(b => b.BottomM)];

    /// <summary>The non-ground conductor band a drawing layer binds to, if any — the same question
    /// the classification loop asks of every artwork shape, asked the same way so a port and the
    /// metal under it cannot land on different levels.</summary>
    private static Band? SignalBandFor(LayerKey? key, Dictionary<LayerKey, List<Band>> binding)
        => key is { } k && binding.TryGetValue(k, out var bands)
            ? bands.FirstOrDefault(b => b.Layer.Kind == StackupKind.Conductor && !b.Layer.IsGroundReference)
            : null;

    /// <summary><see cref="SheetM"/> is where this band's zero-thickness ANALYSIS SHEET sits, which
    /// is not the same question as where the band is. Every level-z, slab-height and medium-cut
    /// decision below reads <see cref="SheetM"/>; <see cref="BottomM"/>/<see cref="TopM"/> stay the
    /// band's own extent and are what the absorption arithmetic and the conductor's reported
    /// thickness are written in (MIM-6).</summary>
    private sealed record Band(StackupLayer Layer, double BottomM, double TopM, int Index, double SheetM);

    /// <summary>
    /// Replaces every group of conductor shapes that genuinely overlap on one level with their union;
    /// a shape overlapping nothing is returned as the SAME object, in its original order.
    /// </summary>
    private static List<(LayoutShape Shape, Band Band)> MergeOverlappingCopper(
        List<(LayoutShape Shape, Band Band)> shapes, Technology tech, out int mergedShapes, out int mergedInto)
    {
        mergedShapes = 0;
        mergedInto = 0;
        int n = shapes.Count;
        var parent = new int[n];
        for (int i = 0; i < n; i++) parent[i] = i;
        int Find(int i) { while (parent[i] != i) i = parent[i] = parent[parent[i]]; return i; }

        var boxes = new Bbox[n];
        var paths = new Clipper2Lib.Paths64?[n];
        for (int i = 0; i < n; i++) boxes[i] = LayoutGeometry.BboxOf(shapes[i].Shape);

        Clipper2Lib.Paths64 PathsOf(int i) =>
            paths[i] ??= LayoutClipper.ToClipperPaths(shapes[i].Shape, LayoutFlattener.ResolveTolDbu(shapes[i].Shape, tech));

        // A sweep in x: only boxes that overlap are ever tested, so a board is not n².
        var order = Enumerable.Range(0, n)
            .Where(i => LayoutBooleans.IsClipperOperand(shapes[i].Shape))
            .OrderBy(i => boxes[i].MinX).ToList();
        var active = new List<int>();
        bool any = false;
        foreach (int i in order)
        {
            active.RemoveAll(j => boxes[j].MaxX < boxes[i].MinX);
            foreach (int j in active)
            {
                if (shapes[j].Band.Index != shapes[i].Band.Index) continue;
                if (boxes[j].MaxY < boxes[i].MinY || boxes[i].MaxY < boxes[j].MinY) continue;
                if (Find(i) == Find(j)) continue;
                var common = Clipper2Lib.Clipper.Intersect(PathsOf(i), PathsOf(j), Clipper2Lib.FillRule.NonZero);
                if (common.Sum(p => System.Math.Abs(Clipper2Lib.Clipper.Area(p))) <= 0) continue;
                parent[Find(i)] = Find(j);
                any = true;
            }
            active.Add(i);
        }
        if (!any) return shapes;

        var groups = Enumerable.Range(0, n).GroupBy(Find).ToDictionary(g => g.Key, g => g.ToList());
        var result = new List<(LayoutShape Shape, Band Band)>(n);
        var emitted = new HashSet<int>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            var members = groups[root];
            if (members.Count == 1) { result.Add(shapes[i]); continue; }
            if (!emitted.Add(root)) continue;

            IReadOnlyList<LayoutShape> union;
            try { union = LayoutBooleans.Union(members.Select(m => shapes[m].Shape).ToList(), tech).Shapes; }
            catch (Exception) { foreach (int m in members) result.Add(shapes[m]); continue; }   // leave as drawn

            mergedShapes += members.Count;
            mergedInto   += union.Count;
            foreach (var u in union) result.Add((u, shapes[i].Band));
        }
        return result;
    }

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
    private static Dictionary<LayerKey, StackupLayer> BuildViaBinding(Stackup stackup, out int nonPlated)
    {
        var map = new Dictionary<LayerKey, StackupLayer>();
        nonPlated = 0;
        foreach (var l in stackup.Layers)
        {
            if (l.Kind != StackupKind.Via) continue;

            // GI1 R-gi1-2. THE ONE PLACE a non-plated via is excluded, and deliberately so: this map
            // is the sole route from a drawing layer to a via entry, so both the point-via branch
            // (a ViaShape) and the MIM-1 region branch resolve through it. Filtering here cannot be
            // bypassed by a third kind of via artwork arriving later; filtering at either use site
            // could. Null is plated, so nothing authored before StackupLayer.Plated existed moves.
            if (l.Plated == false) { nonPlated++; continue; }

            foreach (var key in l.DrawingLayers) map.TryAdd(key, l);
        }
        return map;
    }

    /// <summary>
    /// <b>EM-SEV R-emsev-6 — which conductor entries this layout actually DRAWS on, and which ones
    /// its drawn vias land on.</b> No stackup arithmetic, no ports, no mesh: just the two sets a
    /// panel needs to say <i>"'MIM Metal' carries artwork in this layout and is not ticked"</i>
    /// while the dialog is open, rather than after an eleven-minute solve has published the answer
    /// without it.
    ///
    /// <para><b>It lives HERE, next to the binding it reads</b>, and not in the view model. How a
    /// drawing layer binds to a stackup entry is this file's rule — including the two traps a second
    /// copy would miss: a via entry's drawing layer is never in the conductor binding (BuildStack
    /// skips every Via entry), and a NON-PLATED via entry is a hole rather than metal and binds to
    /// nothing at all.</para>
    ///
    /// <para>A label or a bitmap is annotation and counts as nothing, exactly as the extraction
    /// treats it.</para>
    /// </summary>
    public static EmArtworkSurvey SurveyArtwork(IReadOnlyList<LayoutShape> shapes, Technology tech)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(tech);

        var drawnOn    = new HashSet<string>(StringComparer.Ordinal);
        var viaLands   = new HashSet<string>(StringComparer.Ordinal);
        var binding    = BuildLayerBinding(BuildStack(tech.Stackup));
        var viaBinding = BuildViaBinding(tech.Stackup, out _);

        foreach (var s in shapes)
        {
            if (s is LabelShape or BitmapShape) continue;

            if (s is ViaShape vs)
            {
                if (viaBinding.TryGetValue(vs.Layer, out var viaEntry)) AddSpan(viaEntry);
                continue;
            }

            // A region on a via-bound drawing layer is a via FOOTPRINT (MIM-1) — a plate connection
            // is a rectangle, not a point — so it says the same thing about the levels it joins.
            if (viaBinding.TryGetValue(s.Layer, out var regionEntry)) { AddSpan(regionEntry); continue; }

            if (!binding.TryGetValue(s.Layer, out var bands)) continue;
            foreach (var b in bands)
                if (b.Layer.Kind == StackupKind.Conductor) drawnOn.Add(b.Layer.Name);
        }

        return new EmArtworkSurvey(drawnOn, viaLands);

        void AddSpan(StackupLayer entry)
        {
            if (entry.SpanFromLayer is { Length: > 0 } from) viaLands.Add(from);
            if (entry.SpanToLayer   is { Length: > 0 } to)   viaLands.Add(to);
        }
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
