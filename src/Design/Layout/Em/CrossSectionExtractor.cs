// docs/design/layout-view.md §10.3.3 — turn drawn layout geometry + a Technology into the neutral
// EmProblem the MoM kernel consumes (R-mom-1). This is where the phase is won or lost, and where
// every DBU stops (R-em-2: the physics is doubles-in-SI, and this is the one conversion point).
//
// R-em-1 — framework-free, and unit-testable without a document, a canvas or a workspace. Do not
// reach for a LayoutDocument, a LayoutEditorViewModel, Avalonia or SkiaSharp from this file.
//
// §10.3.3's own framing, which the refusal wording below follows: "If it does not reduce, refuse
// CLEARLY AND SPECIFICALLY… A vague failure here is what would make v1 feel broken rather than
// bounded." Every refusal names the specific feature, where it was found, and where the capability
// arrives — the same shape QuasiStaticKernel.CanSolve uses for the refusals IT owns. The split is:
// this file owns the GEOMETRIC refusals; the kernel owns the problem-level ones, and they are not
// duplicated here.

using System.Globalization;
using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em;

public static class CrossSectionExtractor
{
    /// <summary>
    /// <b>D6/R-res-10 — the sentence every GEOMETRIC refusal ends with, worded once.</b>
    ///
    /// <para>Before L8e these all read <i>"full-wave analysis of discontinuities arrives in L8"</i>.
    /// That was true as a promise and became MISLEADING the moment kernel B shipped: the capability
    /// exists NOW, and what a user needs is how to reach it — not which phase it landed in. A
    /// refusal that names a phase number is a refusal that goes stale silently.</para>
    /// </summary>
    internal const string PlanarAlternative =
        "The full-wave planar kernel (B) does analyse this — set this EM setup's analysis to Planar, " +
        "or to Auto, which picks it whenever the cross-section kernel refuses.";

    /// <summary>Relative tolerance for "these two edges are perpendicular" / "these two conductors
    /// are parallel". Integer DBU coordinates cannot represent a rotated rectangle exactly, so a
    /// real drawn line is a few parts in 10⁷ off square at 1 nm resolution; a genuine taper of even
    /// 0.1% is 1e-3. 1e-4 sits comfortably between the two.</summary>
    private const double AngleTol = 1e-4;

    /// <summary>Relative tolerance for "these two opposite sides are the same length".</summary>
    private const double LengthTol = 1e-4;

    /// <summary>
    /// <b>A technology's stackup thicknesses are DBU at the DEFAULT resolution, never at the
    /// layout's own.</b> Neither <c>Technology</c> nor the <c>.ctech</c> file carries a
    /// <c>DbuPerMicron</c>, so there is nothing else those integers could be relative to — and
    /// <c>SubstrateResolver</c> already names this same constant <c>FallbackDbuPerMicron</c> for
    /// exactly this reason. So R-em-2's "DBU → metres happens exactly once" is two conversions with
    /// two different scales, not one: shape coordinates use the layout's <c>DbuPerMicron</c>, stackup
    /// heights use this. Conflating them silently rescales every substrate height the moment a layout
    /// is drawn at anything but 1000 DBU/µm — a plausible-looking answer, wrong by that ratio.
    /// </summary>
    private const int StackupDbuPerMicron = LayoutUnits.DefaultDbuPerMicron;

    public static EmExtractionResult Extract(
        IReadOnlyList<LayoutShape> shapes,
        Technology                 tech,
        int                        dbuPerMicron,
        EmExtractionSettings?      settings = null)
    {
        ArgumentNullException.ThrowIfNull(shapes);
        ArgumentNullException.ThrowIfNull(tech);
        settings ??= EmExtractionSettings.Default;

        if (dbuPerMicron <= 0)
            return EmExtractionResult.No(
                $"The layout's resolution is {dbuPerMicron} DBU per micron, which is not a usable " +
                "scale. Set a positive DbuPerMicron on the layout.");

        var notes = new List<string>();

        // R-em-6, stripline row. Checked before anything else: it is a property of the stackup
        // alone, so there is no point analysing geometry first.
        if (tech.Stackup.Top == BoundaryCondition.Ground)
            return EmExtractionResult.No(
                "This stackup is closed at the top (Stackup.Top = Ground), which makes it stripline " +
                "rather than microstrip. Stripline needs an infinite image SERIES rather than the " +
                "single image this analysis uses — a bounded extension that is not yet built.");

        var stack = BuildStack(tech.Stackup);
        if (stack.Count == 0)
            return EmExtractionResult.No(
                $"Technology '{tech.Name}' has no stackup layers, so there is nothing to say how " +
                "thick the metal is, what is under it, or where the ground plane sits. Add a " +
                "stackup in the technology editor.");

        // ── Classify every drawn shape against the stackup's DrawingLayers bindings ────────────
        var binding = BuildLayerBinding(stack);
        var classified = Classify(shapes, tech, binding, notes);
        if (classified.Refusal is not null) return EmExtractionResult.No(classified.Refusal, notes);

        if (classified.Conductors.Count == 0)
            return EmExtractionResult.No(BuildNothingFoundRefusal(shapes, tech, settings), notes);

        // ── R-em-4b: which conductor stackup layer is the signal ──────────────────────────────
        var signalBands = classified.Conductors
            .Select(c => c.Band)
            .Distinct()
            .OrderBy(b => b.Index)
            .ToList();

        Band signal;
        if (settings.SignalStackupLayerName is { Length: > 0 } wanted)
        {
            var named = stack.FirstOrDefault(b =>
                b.Layer.Kind == StackupKind.Conductor &&
                string.Equals(b.Layer.Name, wanted, StringComparison.Ordinal));
            if (named is null)
                return EmExtractionResult.No(
                    $"This EM setup names '{wanted}' as its signal conductor, but technology " +
                    $"'{tech.Name}' has no conductor stackup layer with that name. Conductor layers " +
                    $"here: {string.Join(", ", stack.Where(b => b.Layer.Kind == StackupKind.Conductor).OrderBy(b => b.Index).Select(b => $"'{b.Layer.Name}'"))}.",
                    notes);
            var strays = signalBands.Where(b => b.Index != named.Index).ToList();
            if (strays.Count > 0)
                return EmExtractionResult.No(MultiLevelRefusal([named, .. strays]), notes);
            signal = named;
        }
        else if (signalBands.Count > 1)
        {
            return EmExtractionResult.No(MultiLevelRefusal(signalBands), notes);
        }
        else
        {
            signal = signalBands[0];
        }

        var conductorShapes = classified.Conductors.Where(c => c.Band.Index == signal.Index).ToList();
        if (conductorShapes.Count == 0)
            return EmExtractionResult.No(BuildNothingFoundRefusal(shapes, tech, settings), notes);

        // ── R-em-4: the ground plane is the TOP SURFACE of the highest ground-designated conductor
        // BELOW the signal. Stackup.Bottom == Ground is only the fallback. Getting this backwards
        // is a whole metal thickness of error, which on 1.6 mm FR-4 is a plausible-looking 2%.
        var groundBand = stack
            .Where(b => b.Layer.Kind == StackupKind.Conductor
                        && b.Layer.IsGroundReference
                        && b.TopM <= signal.BottomM)
            .OrderByDescending(b => b.TopM)
            .FirstOrDefault();

        double groundM;
        double groundSigma;
        string? groundName;
        bool haveGround;

        if (groundBand is not null)
        {
            groundM     = groundBand.TopM;
            groundSigma = groundBand.Layer.SigmaSm;
            groundName  = groundBand.Layer.Name;
            haveGround  = true;
        }
        else if (tech.Stackup.Bottom == BoundaryCondition.Ground)
        {
            groundM     = stack[0].BottomM;   // stack[0] is the lowest band
            groundSigma = double.PositiveInfinity;
            groundName  = null;
            haveGround  = true;
            notes.Add(
                $"No conductor layer in technology '{tech.Name}' is marked as a ground reference, so " +
                "the ground plane was taken from Stackup.Bottom = Ground at the bottom of the stack, " +
                "as a perfect conductor. Mark the return-path conductor as a ground reference in the " +
                "technology editor to place it exactly and give it a real conductivity.");
        }
        else
        {
            groundM     = stack[0].BottomM;
            groundSigma = 0;
            groundName  = null;
            haveGround  = false;
        }

        // ── MIM-7 — a PATTERNED dielectric is in this cross-section only if its plate is ──────
        //
        // A uniform-line cross-section has exactly ONE signal conductor (multi-level geometry is
        // refused above), so that is what "in this run" means here — the set-of-levels question the
        // planar extractor asks, with a set of one. A capacitor's thin film is deposited under its
        // plate and etched away everywhere else, so it is superstrate over a line on the metal below
        // it only where a capacitor actually is; carrying it on every line would move Z0 and ε_eff
        // on every interconnect run, which is the cost that kept the MIM module in a second
        // technology until this tie replaced it.
        //
        // Rebuilt rather than patched, and the bands re-resolved by stackup index — `signal` and
        // `groundBand` are Band objects out of the OLD list and the arithmetic below reads their
        // materials. There is no sheet surface to revert: this kernel models real metal of real
        // thickness and never reads SheetAt (MIM-6).
        if (PatternedDielectric.Deactivate(
                tech.Stackup,
                name => string.Equals(name, signal.Layer.Name, StringComparison.Ordinal),
                revertSheetSurface: false, notes) is { } effectiveStackup)
        {
            stack      = BuildStack(effectiveStackup);
            signal     = stack.First(b => b.Index == signal.Index);
            groundBand = groundBand is null ? null : stack.First(b => b.Index == groundBand.Index);
        }

        // ── R-em-5: a missing or nonsensical stackup value is a refusal, not a default ─────────
        var bad = ValidateStack(stack, signal, groundBand);
        if (bad is not null) return EmExtractionResult.No(bad, notes);

        // ── Geometry ──────────────────────────────────────────────────────────────────────────
        double Metres(long dbu) => dbu / (dbuPerMicron * 1e6);

        var profiles = new List<Profile>(conductorShapes.Count);
        foreach (var c in conductorShapes)
        {
            var made = BuildProfile(c.Shape, tech, dbuPerMicron);
            if (made.Refusal is not null) return EmExtractionResult.No(made.Refusal, notes);
            profiles.Add(made.Profile!);
        }

        // R-em-7: the axis is DERIVED, never assumed to be x or y.
        var axisRef = profiles[0];
        for (int i = 1; i < profiles.Count; i++)
        {
            double cross = axisRef.DirX * profiles[i].DirY - axisRef.DirY * profiles[i].DirX;
            if (Math.Abs(cross) > AngleTol)
                return EmExtractionResult.No(NotParallelRefusal(axisRef, profiles[i], tech), notes);
        }

        double axisMin = profiles.Min(p => p.AxisMin);
        double axisMax = profiles.Max(p => p.AxisMax);
        double lengthDbu = axisMax - axisMin;
        if (!(lengthDbu > 0))
            return EmExtractionResult.No(
                $"The conductor on layer '{LayerLabel(tech, conductorShapes[0].Shape.Layer)}' has zero " +
                "extent along the propagation axis, so ℓ = 0. A per-unit-length kernel needs a " +
                "positive line length to form s-parameters from.",
                notes);

        // Centre the cross-section on the conductors' own extent so truncation is symmetric and a
        // rectangle drawn at arbitrary DBU coordinates produces the same problem as one at the origin.
        double crossMinAll = profiles.Min(p => p.CrossMin);
        double crossMaxAll = profiles.Max(p => p.CrossMax);
        double crossCentre = 0.5 * (crossMinAll + crossMaxAll);

        double zBot = signal.BottomM - groundM;
        double zTop = signal.TopM    - groundM;

        var ordered = profiles.OrderBy(p => 0.5 * (p.CrossMin + p.CrossMax)).ToList();
        var conductors = new List<EmConductor>(ordered.Count);
        var conductorReadback = new List<EmConductorReadback>(ordered.Count);
        var usedNames = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < ordered.Count; i++)
        {
            var p = ordered[i];
            double x0 = Metres((long)Math.Round(p.CrossMin - crossCentre));
            double x1 = Metres((long)Math.Round(p.CrossMax - crossCentre));
            string name = UniqueName(p.Shape.Net, i, usedNames);

            conductors.Add(new EmConductor(name,
            [
                new EmPoint(x0, zBot), new EmPoint(x1, zBot),
                new EmPoint(x1, zTop), new EmPoint(x0, zTop),
            ], signal.Layer.SigmaSm));

            conductorReadback.Add(new EmConductorReadback(
                name, x1 - x0, zTop - zBot, 0.5 * (x0 + x1), zBot, zTop));
        }

        var gaps = new List<double>();
        for (int i = 0; i + 1 < conductorReadback.Count; i++)
        {
            var a = conductorReadback[i];
            var b = conductorReadback[i + 1];
            gaps.Add((b.CenterMeters - 0.5 * b.WidthMeters) - (a.CenterMeters + 0.5 * a.WidthMeters));
        }

        // ── R-em-4a: regions come from DIELECTRIC layers only, each extended DOWNWARD through any
        // conductor band beneath it. A conductor's own z band is absorbed into the dielectric above
        // it — the stackup does not say what fills a conductor band where no metal is drawn, and
        // "whatever is above it" is what matches the validated engine problems (metal is deposited
        // on the layer below and encapsulated by what comes after).
        var regions = BuildRegions(stack, groundM, out var regionNames);

        // R-cpl-6 / D3 — 2N ports, not 2: port 2k−1 is conductor k's NEAR end and 2k its FAR end.
        // Kernel A built exactly two, both on conductors[0], which was right when only one line
        // could be solved and silently wrong the moment a coupled pair could. The numbering is
        // stated once here and once in the kernel; a transposed map produces a coupler whose through
        // and coupled ports are swapped, which no magnitude plot of a symmetric structure would show.
        var ports = new List<EmPort>(2 * conductors.Count);
        for (int k = 0; k < conductors.Count; k++)
        {
            ports.Add(new EmPort(2 * k + 1, conductors[k].Name, null, settings.ResolvePortZ0(2 * k)));
            ports.Add(new EmPort(2 * k + 2, conductors[k].Name, null, settings.ResolvePortZ0(2 * k + 1)));
        }

        var problem = new EmProblem(
            Conductors:   conductors,
            Regions:      regions,
            Ground:       haveGround ? new EmGroundPlane(0, groundSigma) : null,
            Ports:        ports,
            LengthMeters: Metres((long)Math.Round(lengthDbu)));

        var (axisKind, axisDeg) = ClassifyAxis(axisRef.DirX, axisRef.DirY);

        var regionReadback = new List<EmRegionReadback>(regions.Count);
        for (int i = 0; i < regions.Count; i++)
            regionReadback.Add(new EmRegionReadback(
                regionNames[i], regions[i].Material.EpsR, regions[i].Material.TanD,
                regions[i].Material.MuR, regions[i].YBottom, regions[i].YTop));

        var readback = new EmCrossSectionReadback(
            Conductors:      conductorReadback,
            GapsMeters:      gaps,
            LengthMeters:    problem.LengthMeters,
            Axis:            axisKind,
            AxisAngleDeg:    axisDeg,
            SignalLayerName: signal.Layer.Name,
            GroundLayerName: groundName,
            GroundYMeters:   0,
            GroundSigmaSm:   groundSigma,
            SignalSigmaSm:   signal.Layer.SigmaSm,
            Regions:         regionReadback,
            Summary:         BuildSummary(conductorReadback, gaps, problem.LengthMeters));

        return EmExtractionResult.Yes(problem, readback, notes);
    }

    // ── Stackup → z bands ─────────────────────────────────────────────────────────────────────

    /// <summary>One stackup entry with the z band it occupies, <b>in metres</b> (see
    /// <see cref="StackupDbuPerMicron"/> for why stackup DBU are converted here and not alongside
    /// the shape coordinates). <c>Index</c> is its position in <c>Stackup.Layers</c>
    /// (top-to-bottom), so a smaller index is HIGHER in z.</summary>
    private sealed record Band(StackupLayer Layer, double BottomM, double TopM, int Index);

    /// <summary>
    /// R-em-3: <c>Stackup.Layers</c> is ordered TOP to BOTTOM, so the stack is built by accumulating
    /// thickness UPWARD from the bottom — walk the list in reverse. R-em-4c: a <c>Via</c> entry is
    /// ignored (a uniform cross-section has no vias) and contributes no thickness.
    /// Returned bottom-to-top.
    /// </summary>
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
        return bands;   // already bottom-to-top
    }

    private static string? ValidateStack(List<Band> stack, Band signal, Band? ground)
    {
        foreach (var b in stack)
        {
            if (b.Layer.ThicknessDbu <= 0)
                return $"Stackup layer '{b.Layer.Name}' has zero thickness, so every height above it " +
                       "would be wrong by however thick it really is. Set its thickness in the " +
                       "technology editor's Stackup tab.";

            if (b.Layer.Kind == StackupKind.Dielectric && !(b.Layer.Epsr >= 1))
                return $"Stackup layer '{b.Layer.Name}' is a dielectric with εr = " +
                       $"{b.Layer.Epsr.ToString("G4", CultureInfo.InvariantCulture)}. Relative " +
                       "permittivity is ≥ 1 — set it in the technology editor's Stackup tab " +
                       "(FR-4 is 4.4, GaAs 12.9, air 1.0).";
        }

        if (!(signal.Layer.SigmaSm > 0))
            return $"Stackup layer '{signal.Layer.Name}' is the signal conductor but has σ = " +
                   $"{signal.Layer.SigmaSm.ToString("G4", CultureInfo.InvariantCulture)} S/m. Set its " +
                   "conductivity in the technology editor's Stackup tab (copper is 5.8e7 S/m, " +
                   "gold 4.1e7 S/m).";

        if (ground is not null && !(ground.Layer.SigmaSm > 0))
            return $"Stackup layer '{ground.Layer.Name}' is the ground reference but has σ = " +
                   $"{ground.Layer.SigmaSm.ToString("G4", CultureInfo.InvariantCulture)} S/m. Set its " +
                   "conductivity in the technology editor's Stackup tab (copper is 5.8e7 S/m, " +
                   "gold 4.1e7 S/m).";

        return null;
    }

    private static List<EmDielectricRegion> BuildRegions(
        List<Band> stack, double groundM, out List<string> names)
    {
        // Bottom-up: each dielectric band's region starts where the previous dielectric region
        // ended, so it absorbs every conductor band between them (R-em-4a).
        var raw     = new List<(double Bottom, double Top, EmMaterial Mat, string Name)>();
        double prev = stack[0].BottomM;

        foreach (var b in stack)
        {
            if (b.Layer.Kind != StackupKind.Dielectric) continue;
            var mat = new EmMaterial(b.Layer.Epsr, b.Layer.TanD, b.Layer.Mur <= 0 ? 1.0 : b.Layer.Mur);
            raw.Add((prev - groundM, b.TopM - groundM, mat, b.Layer.Name));
            prev = b.TopM;
        }

        // Above the topmost dielectric is whatever Stackup.Top says, which for an open stackup is
        // air — extended downward through any conductor band above the last dielectric.
        raw.Add((prev - groundM, double.PositiveInfinity, EmMaterial.Air, "Air"));

        // Adjacent regions of identical material are one region. This is what collapses MmicGaAs's
        // explicit air layer, Metal1's empty band and Metal2's band into a single air region.
        var merged = new List<(double Bottom, double Top, EmMaterial Mat, string Name)>();
        foreach (var r in raw)
        {
            if (merged.Count > 0 && merged[^1].Mat == r.Mat)
            {
                var last = merged[^1];
                merged[^1] = (last.Bottom, r.Top, last.Mat, last.Name);
            }
            else merged.Add(r);
        }

        // R-em-4: below the ground plane everything is shielded, so the lowest surviving region
        // extends to −∞. This is exactly what EmProblemBuilders.Microstrip does.
        int first = 0;
        for (int i = 0; i < merged.Count; i++)
        {
            if (merged[i].Top > 0) { first = i; break; }
            first = i;
        }

        var regions = new List<EmDielectricRegion>();
        names = [];
        for (int i = first; i < merged.Count; i++)
        {
            double bottom = i == first ? double.NegativeInfinity : merged[i].Bottom;
            regions.Add(new EmDielectricRegion(bottom, merged[i].Top, merged[i].Mat));
            names.Add(merged[i].Name);
        }
        return regions;
    }

    // ── Shape classification ──────────────────────────────────────────────────────────────────

    private sealed record ConductorShape(LayoutShape Shape, Band Band);

    private sealed record Classification(
        List<ConductorShape> Conductors,
        string?              Refusal);

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

    private static Classification Classify(
        IReadOnlyList<LayoutShape> shapes, Technology tech,
        Dictionary<LayerKey, List<Band>> binding, List<string> notes)
    {
        // Via stackup entries are excluded from `binding` (BuildStack skips them), so a shape on a
        // via drawing layer has to be recognised separately — R-em-4c: ignored, and REPORTED.
        var viaLayers = new HashSet<LayerKey>();
        foreach (var l in tech.Stackup.Layers)
            if (l.Kind == StackupKind.Via)
                foreach (var k in l.DrawingLayers) viaLayers.Add(k);

        // R-em-4c superseded, 2026-08-30: what makes a shape "not metal" is that the TECHNOLOGY
        // DECLARES its layer and binds it to no conductor — not that a dielectric entry happens to
        // name it. See the refusal below for why that distinction had to move.
        var declaredLayers = new HashSet<LayerKey>(tech.Layers.Select(l => l.Key));

        var conductors = new List<ConductorShape>();
        int ignoredGround = 0, ignoredVia = 0, ignoredDielectric = 0, ignoredAnnotation = 0;
        var ignoredUnbound = new List<string>();
        string? groundLayerName = null;

        foreach (var s in shapes)
        {
            // Annotation is never artwork. A silkscreen label or a tracing bitmap must not make a
            // whole extraction refuse.
            if (s is LabelShape or BitmapShape) { ignoredAnnotation++; continue; }

            if (binding.TryGetValue(s.Layer, out var bands))
            {
                var signalBand = bands.FirstOrDefault(b =>
                    b.Layer.Kind == StackupKind.Conductor && !b.Layer.IsGroundReference);
                if (signalBand is not null) { conductors.Add(new ConductorShape(s, signalBand)); continue; }

                var groundConductor = bands.FirstOrDefault(b =>
                    b.Layer.Kind == StackupKind.Conductor && b.Layer.IsGroundReference);
                if (groundConductor is not null)
                {
                    ignoredGround++;
                    groundLayerName = groundConductor.Layer.Name;
                    continue;
                }

                // A Dielectric entry may legitimately carry DrawingLayers (MmicGaAs's GaAs layer
                // does — a substrate outline). R-em-4c: that binding is for other purposes and must
                // never make the shape a conductor.
                ignoredDielectric++;
                continue;
            }

            if (viaLayers.Contains(s.Layer)) { ignoredVia++; continue; }

            // ── DECLARED-BUT-UNBOUND IS NOT AN ERROR. UNDECLARED IS. ──────────────────────────
            //
            // This used to refuse on any layer no stackup entry named, and the advice it gave was
            // "add this drawing layer to a conductor entry" — i.e. declare your board outline to be
            // copper. Measured on the shipped 2-layer PCB starter: a shape on Outline, Silk Top,
            // Silk Bottom, Soldermask Top or Soldermask Bottom refused the whole extraction. Every
            // PCB layout has a board outline, so the normal case was the failing one, and the only
            // escape was the dielectric-DrawingLayers workaround the MMIC starter used for its die
            // outline (which is why that binding existed at all).
            //
            // The discriminator was always available and was simply not being asked for: a layer
            // the technology DECLARES but binds to no stackup entry is the technology stating that
            // the layer is not metal. Silk, soldermask and outline are exactly that. A layer the
            // technology does not declare at all is the case nobody has said anything about — a
            // foreign import, a hand-edited file — and that still refuses, because there the old
            // reasoning holds in full: nothing says how thick it is or what it is made of.
            //
            // Ignoring is REPORTED, never silent, and names the layers (see the note below), so a
            // trace genuinely drawn on a forgotten layer is still visible in the run's own output
            // rather than vanishing.
            if (declaredLayers.Contains(s.Layer))
            {
                string label = LayerLabel(tech, s.Layer);
                if (!ignoredUnbound.Contains(label)) ignoredUnbound.Add(label);
                continue;
            }

            return new Classification(conductors,
                $"There is geometry on layer {LayerLabel(tech, s.Layer)}, which technology " +
                $"'{tech.Name}' does not declare at all, so nothing says whether it is metal, how " +
                "thick it is, or what it is made of. Add the layer to the technology's Layers tab — " +
                "and if it IS metal, bind it to a conductor entry on the Stackup tab.");
        }

        if (ignoredGround > 0)
            notes.Add(
                $"{ignoredGround} shape(s) on the ground-designated conductor layer" +
                (groundLayerName is null ? "" : $" '{groundLayerName}'") +
                " were ignored. The ground plane is laterally infinite and is handled by an " +
                "exact image, never meshed — it cannot model a finite ground pour, so meshing one " +
                "silently would be worse than saying so. The full-wave planar analysis does not " +
                "model one either: its ground is the grounded slab's own infinite plane, handled " +
                "analytically, and that is true of the GENERAL layered stack too — a LayerStack's " +
                "PEC termination is an infinite plane by definition. A finite pour needs the ground " +
                "MESHED as ordinary metal, which neither kernel does today.");

        if (ignoredVia > 0)
            notes.Add($"{ignoredVia} via shape(s) were ignored — a uniform cross-section has no vias. " +
                      "The full-wave planar kernel (B) carries them: switch this setup's analysis " +
                      "kind to Planar (or leave it on Auto).");

        if (ignoredDielectric > 0)
            notes.Add($"{ignoredDielectric} shape(s) on a layer bound only to a DIELECTRIC stackup " +
                      "entry were ignored — that binding marks a substrate extent, not metal.");

        if (ignoredUnbound.Count > 0)
            notes.Add($"Shape(s) on {ignoredUnbound.Count} layer(s) this technology declares but " +
                      $"binds to no stackup entry ({string.Join(", ", ignoredUnbound)}) were ignored. " +
                      "They are not metal as far as this technology is concerned — which is what a " +
                      "board outline, a silkscreen or a soldermask opening should be. If one of them " +
                      "IS metal, bind it to a conductor entry on the Stackup tab.");

        // Labels and bitmaps are DELIBERATELY not reported (owner request, 2026-08-11: "too
        // obvious/redundant to show users for every setup"). Every other ignored-shape note above
        // describes something a user might reasonably have expected to simulate; annotation is not
        // artwork by definition, and saying so on every single extraction is noise that crowds out
        // the notes that do carry information. The counter stays so the classifier's own accounting
        // is still complete.
        _ = ignoredAnnotation;

        return new Classification(conductors, null);
    }

    private static string BuildNothingFoundRefusal(
        IReadOnlyList<LayoutShape> shapes, Technology tech, EmExtractionSettings settings)
    {
        var layers = shapes
            .Where(s => s is not (LabelShape or BitmapShape))
            .Select(s => LayerLabel(tech, s.Layer))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();

        string subject = settings.SubjectDescription is { Length: > 0 } d ? d : "this layout";
        string found = layers.Count == 0
            ? "it holds no drawn geometry at all"
            : $"its geometry is on {(layers.Count == 1 ? "layer" : "layers")} {string.Join(", ", layers)}";

        return $"This EM setup is pointed at {subject}, and {found} — none of it on a layer bound to a " +
               $"stackup conductor layer in technology '{tech.Name}'. Draw the line on a conductor " +
               "layer, or bind the layer it is on to a conductor entry in the technology editor's " +
               "Stackup tab.";
    }

    private static string MultiLevelRefusal(IReadOnlyList<Band> bands)
    {
        var names = bands.OrderBy(b => b.Index).Select(b => $"'{b.Layer.Name}'");
        return $"Geometry is drawn on {bands.Count} signal conductor layers ({string.Join(", ", names)}). " +
               "The uniform-line analysis solves one cross-section on one metal level. " +
               $"\"{EmKernelRegistry.PlanarChoiceLabel}\" carries z-directed current and multi-level " +
               $"stacks: set Analysis to \"{EmKernelRegistry.PlanarChoiceLabel}\" (or leave it on " +
               $"\"{EmKernelRegistry.AutoChoiceLabel}\"). Otherwise pick which level is the signal in " +
               "this EM setup, or move the geometry onto one layer.";
    }

    // ── One conductor shape → its cross-section profile ───────────────────────────────────────

    /// <summary>Extents in DBU, in the propagation frame. <c>Dir</c> is a unit vector.</summary>
    private sealed record Profile(
        double DirX, double DirY,
        double AxisMin, double AxisMax,
        double CrossMin, double CrossMax,
        LayoutShape Shape, string LayerLabel);

    private sealed record ProfileResult(Profile? Profile, string? Refusal);

    private static ProfileResult BuildProfile(LayoutShape shape, Technology tech, int dbuPerMicron)
    {
        string label = LayerLabel(tech, shape.Layer);
        var corners = CornersOf(shape, tech, dbuPerMicron, label, out string? refusal);
        if (refusal is not null) return new ProfileResult(null, refusal);

        // Four corners. Reject anything that is not a rectangle: a trapezoid is a taper, and any
        // other quadrilateral is a bend we cannot reduce.
        var v0 = Sub(corners![1], corners[0]);
        var v1 = Sub(corners[2], corners[1]);
        var v2 = Sub(corners[3], corners[2]);
        var v3 = Sub(corners[0], corners[3]);

        double l0 = Len(v0), l1 = Len(v1), l2 = Len(v2), l3 = Len(v3);
        if (l0 <= 0 || l1 <= 0)
            return new ProfileResult(null,
                $"The conductor on layer {label} has zero extent in one direction. A uniform line " +
                "needs a positive width and a positive length — ℓ must be positive for a " +
                "per-unit-length kernel to form s-parameters from.");

        bool sidesMatch = Math.Abs(l0 - l2) <= LengthTol * Math.Max(l0, l2)
                       && Math.Abs(l1 - l3) <= LengthTol * Math.Max(l1, l3);
        bool square = Math.Abs(Dot(v0, v1)) <= AngleTol * l0 * l1;

        if (!sidesMatch || !square)
            return new ProfileResult(null, TaperRefusal(corners, label, l0, l1, l2, l3, tech, dbuPerMicron));

        // The longer pair of sides runs along the propagation axis.
        var along = l0 >= l1 ? v0 : v1;
        double an = Len(along);
        double dx = along.X / an, dy = along.Y / an;
        // Canonicalise the direction's sign so two anti-parallel conductors compare as parallel.
        if (dx < 0 || (dx == 0 && dy < 0)) { dx = -dx; dy = -dy; }

        double px = -dy, py = dx;
        double aMin = double.PositiveInfinity, aMax = double.NegativeInfinity;
        double cMin = double.PositiveInfinity, cMax = double.NegativeInfinity;
        foreach (var c in corners)
        {
            double a = c.X * dx + c.Y * dy;
            double x = c.X * px + c.Y * py;
            aMin = Math.Min(aMin, a); aMax = Math.Max(aMax, a);
            cMin = Math.Min(cMin, x); cMax = Math.Max(cMax, x);
        }

        return new ProfileResult(new Profile(dx, dy, aMin, aMax, cMin, cMax, shape, label), null);
    }

    private readonly record struct Pt(double X, double Y);

    private static Pt Sub(Pt a, Pt b) => new(a.X - b.X, a.Y - b.Y);
    private static double Dot(Pt a, Pt b) => a.X * b.X + a.Y * b.Y;
    private static double Len(Pt a) => Math.Sqrt(a.X * a.X + a.Y * a.Y);
    private static double Cross(Pt a, Pt b) => a.X * b.Y - a.Y * b.X;

    /// <summary>
    /// The four corners of a conductor shape, in DBU. Anything that is not four straight sides is a
    /// refusal here rather than a silently-approximated rectangle later.
    /// </summary>
    private static Pt[]? CornersOf(
        LayoutShape shape, Technology tech, int dbuPerMicron, string label, out string? refusal)
    {
        refusal = null;
        switch (shape)
        {
            case RectShape r:
                return [new(r.X1, r.Y1), new(r.X2, r.Y1), new(r.X2, r.Y2), new(r.X1, r.Y2)];

            case PolygonShape p:
                if (p.Holes is { Count: > 0 })
                {
                    refusal = $"The conductor on layer {label} has {p.Holes.Count} hole(s). A uniform " +
                              "cross-section is a solid profile; a conductor with a hole in it in the " +
                              "layout plane is a discontinuity. " + PlanarAlternative;
                    return null;
                }
                return FromVertexList(p.Xy, null, label, tech, dbuPerMicron, out refusal);

            case CurveShape c:
                if (c.Holes is { Count: > 0 })
                {
                    refusal = $"The conductor on layer {label} has {c.Holes.Count} hole(s). A uniform " +
                              "cross-section is a solid profile. " + PlanarAlternative;
                    return null;
                }
                return FromVertexList(c.Xy, c.Edges, label, tech, dbuPerMicron, out refusal);

            case PathShape path:
                return FromPath(path, label, tech, dbuPerMicron, out refusal);

            case CircleShape circle:
                refusal = $"The conductor on layer {label} is a circle centred at " +
                          $"{Coord(circle.Cx, circle.Cy, tech, dbuPerMicron)}. The quasi-static solver " +
                          "handles uniform cross-sections only. " + PlanarAlternative;
                return null;

            case RoundedRectShape rr:
                refusal = $"The conductor on layer {label} is a rounded rectangle with corners at " +
                          $"{Coord(rr.X1, rr.Y1, tech, dbuPerMicron)}. Its rounded corners are curved " +
                          "edges and the quasi-static solver handles uniform cross-sections only. " +
                          PlanarAlternative;
                return null;

            case ViaShape via:
                refusal = $"There is a via at {Coord(via.X, via.Y, tech, dbuPerMicron)} on layer {label}, " +
                          "which is bound to a conductor stackup layer. A via carries z-directed " +
                          "current, which a 2D cross-section cannot represent. The full-wave planar " +
                          "kernel (B) does carry it — switch this setup's analysis kind to Planar, " +
                          "or leave it on Auto and let the registry choose.";
                return null;

            default:
                refusal = $"A {shape.GetType().Name} on layer {label} is not geometry the quasi-static " +
                          "extractor can reduce to a uniform cross-section.";
                return null;
        }
    }

    private static Pt[]? FromVertexList(
        long[] xy, List<LayoutEdge>? edges, string label, Technology tech, int dbuPerMicron,
        out string? refusal)
    {
        refusal = null;
        if (xy.Length < 6)
        {
            refusal = $"The conductor on layer {label} has {xy.Length / 2} vertices. A uniform " +
                      "cross-section needs a closed profile of at least three.";
            return null;
        }

        if (edges is not null)
        {
            for (int i = 0; i < edges.Count && i * 2 + 1 < xy.Length; i++)
            {
                if (edges[i].Kind == EdgeKind.Line) continue;
                refusal =
                    $"The conductor on layer {label} has a curved ({edges[i].Kind.ToString().ToLowerInvariant()}) " +
                    $"edge starting at {Coord(xy[i * 2], xy[i * 2 + 1], tech, dbuPerMicron)}. The " +
                    "quasi-static solver handles uniform cross-sections only. " + PlanarAlternative;
                return null;
            }
        }

        var pts = new List<Pt>(xy.Length / 2);
        for (int i = 0; i + 1 < xy.Length; i += 2) pts.Add(new Pt(xy[i], xy[i + 1]));

        var corners = RemoveCollinear(pts);
        if (corners.Count == 4) return [.. corners];

        if (corners.Count < 4)
        {
            refusal = $"The conductor on layer {label} reduces to {corners.Count} distinct corners, " +
                      "which encloses no area. A uniform line needs a positive width and length.";
            return null;
        }

        var bend = PickBend(corners);
        refusal = $"The conductor on layer {label} has a bend at " +
                  $"{Coord((long)Math.Round(bend.X), (long)Math.Round(bend.Y), tech, dbuPerMicron)} " +
                  $"({corners.Count} corners, a uniform line has 4). The quasi-static solver handles " +
                  "uniform cross-sections only. " + PlanarAlternative;
        return null;
    }

    private static Pt[]? FromPath(
        PathShape path, string label, Technology tech, int dbuPerMicron, out string? refusal)
    {
        refusal = null;
        if (path.Edges is not null)
        {
            for (int i = 0; i < path.Edges.Count && i * 2 + 1 < path.Xy.Length; i++)
            {
                if (path.Edges[i].Kind == EdgeKind.Line) continue;
                refusal =
                    $"The trace on layer {label} has a curved " +
                    $"({path.Edges[i].Kind.ToString().ToLowerInvariant()}) edge starting at " +
                    $"{Coord(path.Xy[i * 2], path.Xy[i * 2 + 1], tech, dbuPerMicron)}. The quasi-static " +
                    "solver handles uniform cross-sections only. " + PlanarAlternative;
                return null;
            }
        }

        var pts = new List<Pt>();
        for (int i = 0; i + 1 < path.Xy.Length; i += 2) pts.Add(new Pt(path.Xy[i], path.Xy[i + 1]));
        pts = RemoveCollinearOpen(pts);

        if (pts.Count < 2)
        {
            refusal = $"The trace on layer {label} has fewer than two distinct centreline points, so " +
                      "it has zero extent along the propagation axis — ℓ must be positive for a " +
                      "per-unit-length kernel to form s-parameters from.";
            return null;
        }
        if (pts.Count > 2)
        {
            refusal = $"The trace on layer {label} has a bend at " +
                      $"{Coord((long)Math.Round(pts[1].X), (long)Math.Round(pts[1].Y), tech, dbuPerMicron)}. " +
                      "The quasi-static solver handles uniform cross-sections only. " + PlanarAlternative;
            return null;
        }
        if (path.Width <= 0)
        {
            refusal = $"The trace on layer {label} has zero width. A conductor is a closed profile of " +
                      "finite width, not a line.";
            return null;
        }

        var d = Sub(pts[1], pts[0]);
        double n = Len(d);
        double hx = -d.Y / n * (path.Width * 0.5);
        double hy =  d.X / n * (path.Width * 0.5);
        return
        [
            new(pts[0].X + hx, pts[0].Y + hy),
            new(pts[1].X + hx, pts[1].Y + hy),
            new(pts[1].X - hx, pts[1].Y - hy),
            new(pts[0].X - hx, pts[0].Y - hy),
        ];
    }

    private static List<Pt> RemoveCollinear(List<Pt> pts)
    {
        var deduped = new List<Pt>(pts.Count);
        foreach (var p in pts)
            if (deduped.Count == 0 || Len(Sub(p, deduped[^1])) > 0.5) deduped.Add(p);
        if (deduped.Count > 1 && Len(Sub(deduped[0], deduped[^1])) <= 0.5) deduped.RemoveAt(deduped.Count - 1);

        var kept = new List<Pt>(deduped.Count);
        int n = deduped.Count;
        for (int i = 0; i < n; i++)
        {
            var prev = deduped[(i - 1 + n) % n];
            var cur  = deduped[i];
            var next = deduped[(i + 1) % n];
            var a = Sub(cur, prev);
            var b = Sub(next, cur);
            double la = Len(a), lb = Len(b);
            if (la <= 0 || lb <= 0) continue;
            if (Math.Abs(Cross(a, b)) <= AngleTol * la * lb) continue;   // collinear — not a corner
            kept.Add(cur);
        }
        return kept.Count >= 3 ? kept : deduped;
    }

    private static List<Pt> RemoveCollinearOpen(List<Pt> pts)
    {
        var deduped = new List<Pt>(pts.Count);
        foreach (var p in pts)
            if (deduped.Count == 0 || Len(Sub(p, deduped[^1])) > 0.5) deduped.Add(p);
        if (deduped.Count < 3) return deduped;

        var kept = new List<Pt> { deduped[0] };
        for (int i = 1; i + 1 < deduped.Count; i++)
        {
            var a = Sub(deduped[i], deduped[i - 1]);
            var b = Sub(deduped[i + 1], deduped[i]);
            double la = Len(a), lb = Len(b);
            if (la > 0 && lb > 0 && Math.Abs(Cross(a, b)) <= AngleTol * la * lb) continue;
            kept.Add(deduped[i]);
        }
        kept.Add(deduped[^1]);
        return kept;
    }

    /// <summary>The most informative corner to name in a bend refusal: the first reflex corner if
    /// there is one (an L-shape's inner corner is what a user recognises as "the bend"), else the
    /// third corner, which is the first one a rectangle would not have had.</summary>
    private static Pt PickBend(List<Pt> corners)
    {
        int n = corners.Count;
        double area = 0;
        for (int i = 0; i < n; i++)
        {
            var a = corners[i];
            var b = corners[(i + 1) % n];
            area += a.X * b.Y - b.X * a.Y;
        }
        double sign = area >= 0 ? 1 : -1;
        for (int i = 0; i < n; i++)
        {
            var prev = corners[(i - 1 + n) % n];
            var cur  = corners[i];
            var next = corners[(i + 1) % n];
            if (sign * Cross(Sub(cur, prev), Sub(next, cur)) < 0) return cur;
        }
        return corners[2 % n];
    }

    private static string TaperRefusal(
        Pt[] corners, string label, double l0, double l1, double l2, double l3,
        Technology tech, int dbuPerMicron)
    {
        // The two sides that ARE parallel run along the line; the other pair are its two ends, and
        // their differing lengths are the taper.
        var v0 = Sub(corners[1], corners[0]);
        var v2 = Sub(corners[3], corners[2]);
        var v1 = Sub(corners[2], corners[1]);
        var v3 = Sub(corners[0], corners[3]);

        bool pair02 = Math.Abs(Cross(v0, v2)) <= AngleTol * l0 * l2;
        bool pair13 = Math.Abs(Cross(v1, v3)) <= AngleTol * l1 * l3;

        if (pair02 || pair13)
        {
            (double wA, Pt mA, double wB, Pt mB) = pair02
                ? (l3, Mid(corners[3], corners[0]), l1, Mid(corners[1], corners[2]))
                : (l0, Mid(corners[0], corners[1]), l2, Mid(corners[2], corners[3]));

            return $"The conductor on layer {label} is a taper: its width changes from " +
                   $"{Dim(wA, tech, dbuPerMicron)} at {Coord((long)Math.Round(mA.X), (long)Math.Round(mA.Y), tech, dbuPerMicron)} " +
                   $"to {Dim(wB, tech, dbuPerMicron)} at {Coord((long)Math.Round(mB.X), (long)Math.Round(mB.Y), tech, dbuPerMicron)}. " +
                   "The quasi-static solver handles uniform cross-sections only. " + PlanarAlternative +
                   " A smooth taper is also covered exactly by the shipped MicrostripTaperModel / " +
                   "MicrostripKlopfModel cascade, at no cost — the planar kernel exists for " +
                   "discontinuities, radiation and resonance rather than for slowly-varying shapes.";
        }

        var bend = PickBend([.. corners]);
        return $"The conductor on layer {label} is not a rectangle — its sides turn at " +
               $"{Coord((long)Math.Round(bend.X), (long)Math.Round(bend.Y), tech, dbuPerMicron)}. The " +
               "quasi-static solver handles uniform cross-sections only. " + PlanarAlternative;
    }

    private static Pt Mid(Pt a, Pt b) => new(0.5 * (a.X + b.X), 0.5 * (a.Y + b.Y));

    private static string NotParallelRefusal(Profile a, Profile b, Technology tech)
    {
        _ = tech;
        double da = Math.Atan2(a.DirY, a.DirX) * 180.0 / Math.PI;
        double db = Math.Atan2(b.DirY, b.DirX) * 180.0 / Math.PI;
        double diff = Math.Abs(da - db);
        if (diff > 90) diff = 180 - diff;
        return $"The conductors on layers {a.LayerLabel} and {b.LayerLabel} are not parallel: one runs " +
               $"at {da.ToString("0.###", CultureInfo.InvariantCulture)}°, the other at " +
               $"{db.ToString("0.###", CultureInfo.InvariantCulture)}° — " +
               $"{diff.ToString("0.###", CultureInfo.InvariantCulture)}° apart. A uniform cross-section " +
               "needs every conductor parallel. " + PlanarAlternative;
    }

    // ── Presentation helpers ──────────────────────────────────────────────────────────────────

    private static (EmPropagationAxis Kind, double Deg) ClassifyAxis(double dx, double dy)
    {
        double deg = Math.Atan2(dy, dx) * 180.0 / Math.PI;
        if (Math.Abs(dy) <= AngleTol) return (EmPropagationAxis.X, 0);
        if (Math.Abs(dx) <= AngleTol) return (EmPropagationAxis.Y, 90);
        return (EmPropagationAxis.Oblique, deg);
    }

    private static string UniqueName(string? net, int index, HashSet<string> used)
    {
        string baseName = net is { Length: > 0 } ? net : index == 0 ? "signal" : $"signal{index + 1}";
        string name = baseName;
        int n = 2;
        while (!used.Add(name)) name = $"{baseName}_{n++}";
        return name;
    }

    private static string LayerLabel(Technology tech, LayerKey key)
    {
        foreach (var l in tech.Layers)
            if (l.Key.Layer == key.Layer && l.Key.Datatype == key.Datatype)
                return $"{key.Layer}/{key.Datatype} ('{l.Name}')";
        return $"{key.Layer}/{key.Datatype}";
    }

    private static string Coord(long x, long y, Technology tech, int dbuPerMicron)
    {
        var u = tech.DefaultDisplayUnit;
        return $"({LayoutUnits.Format(x, u, dbuPerMicron)}, {LayoutUnits.Format(y, u, dbuPerMicron)} " +
               $"{LayoutUnits.Suffix(u)})";
    }

    private static string Dim(double dbu, Technology tech, int dbuPerMicron)
    {
        var u = tech.DefaultDisplayUnit;
        return $"{LayoutUnits.Format((long)Math.Round(dbu), u, dbuPerMicron)} {LayoutUnits.Suffix(u)}";
    }

    /// <summary>The §10.3.3 R16a one-liner. Built here, not in a view model — R-em-8.</summary>
    private static string BuildSummary(
        IReadOnlyList<EmConductorReadback> conductors, IReadOnlyList<double> gaps, double length)
    {
        string widths = string.Join(", ", conductors.Select(c => FormatMeters(c.WidthMeters)));
        string gapText = gaps.Count == 0
            ? "—"
            : string.Join(", ", gaps.Select(FormatMeters));
        return $"uniform {conductors.Count}-conductor cross-section · " +
               $"W = {widths} · gap {gapText} · ℓ = {FormatMeters(length)}";
    }

    /// <summary>Engineering formatting for an SI length. The extractor works in metres from R-em-2
    /// onward, so this is deliberately unit-table-free rather than routed back through DBU.</summary>
    public static string FormatMeters(double m)
    {
        double a = Math.Abs(m);
        if (a >= 1e-1)  return $"{m.ToString("0.####", CultureInfo.InvariantCulture)} m";
        if (a >= 1e-4)  return $"{(m * 1e3).ToString("0.####", CultureInfo.InvariantCulture)} mm";
        if (a >= 1e-7)  return $"{(m * 1e6).ToString("0.####", CultureInfo.InvariantCulture)} µm";
        return $"{(m * 1e9).ToString("0.####", CultureInfo.InvariantCulture)} nm";
    }
}
