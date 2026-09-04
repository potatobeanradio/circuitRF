// Rebuilding vias from a drill file and the artwork that goes with it
// (docs/sonnet-briefs/brief-L4f-excellon-drill-and-vias.md §4).
//
// THE WHOLE PAYOFF OF THE PHASE IS ONE SENTENCE: a drill hit plus a copper flash at the same point IS
// a via, and pairing them re-joins exactly what L4c's export split apart. Export writes a via twice —
// its pad as a flash in the Gerber file, its hole as a hit in the Excellon file — and neither half
// alone is a via. Without this file the round trip is lossy on every via in the design; with it, it
// closes.
//
// Pure, like both readers: shapes and hits in, shapes out. No CellFolder, no Technology, no Messages,
// no dialog. Which LayerKey the barrel and the landing are is decided by L4g and passed in.

namespace CircuitRF.Design.Layout.Interchange;

/// <summary>R-L4f-6's mapping, and the one place its limitation is stated as a fact rather than
/// discovered later. A declared span names TWO copper layers; <see cref="ViaShape"/> carries a barrel
/// layer and ONE landing layer, so <see cref="FarSide"/> — the other end of the span — has nowhere to
/// live and is reported instead of being silently dropped.</summary>
public sealed record DrillSpanLayers(LayerKey Barrel, LayerKey? Landing, LayerKey? FarSide, string Note);

/// <summary>Everything the pairing produced. <see cref="Refusal"/> non-null means nothing was built —
/// R-L4f-12: a hit with no drill layer to land on is a refusal, never a circle scattered onto whatever
/// layer was nearest.</summary>
public sealed class DrillPairingResult
{
    public string? Refusal { get; init; }

    /// <summary>R-L4f-9: a hit paired with a flash.</summary>
    public IReadOnlyList<ViaShape> Vias { get; init; } = [];

    /// <summary>R-L4f-11: a hit with no flash to pair with, as a bare circle on the drill layer —
    /// precisely the shape L4c's export already recognizes and drills for (its R-via-5), so an
    /// unpaired hit survives a re-export as a hole rather than vanishing.</summary>
    public IReadOnlyList<CircleShape> UnpairedHoles { get; init; } = [];

    /// <summary>R-L4f-10: a hit whose tool DECLARED itself a component or mechanical drill. Also a
    /// bare circle on the drill layer — the hole is real, but it is not a via and its pad stays in the
    /// copper artwork where the file put it.</summary>
    public IReadOnlyList<CircleShape> ComponentHoles { get; init; } = [];

    /// <summary>R-L4f-7: one <see cref="PathShape"/> per routed slot, never two holes.</summary>
    public IReadOnlyList<PathShape> Slots { get; init; } = [];

    /// <summary>The artwork with every flash a via consumed removed — the flash became half of a
    /// <see cref="ViaShape"/>, and leaving it behind would re-export the same copper twice and drill
    /// a second hole through it.</summary>
    public IReadOnlyList<GerberImportedShape> RemainingArtwork { get; init; } = [];

    /// <summary>The COMPOSITED pads a via consumed, which the caller owes the layer a subtraction of.
    /// A composited pad is not a shape that can be removed from <see cref="RemainingArtwork"/> — its
    /// copper is inside a pour polygon — so the equivalent of consuming it is to cut the same disc out
    /// of that pour. Each pad was verified to lie WHOLLY inside its layer's copper before it was
    /// paired, which is what makes cutting it and putting the via's pad back exactly cancel: the copper
    /// on the layer is unchanged and only its OWNERSHIP moves, from the pour to the via.</summary>
    public IReadOnlyList<CircleShape> CarvedPads { get; init; } = [];

    /// <summary>The composited pads no via claimed — threaded into the next drill file's pairing the
    /// same way <see cref="RemainingArtwork"/> is, so two drill files cannot both claim one pad.</summary>
    public IReadOnlyList<GerberImportedShape> RemainingCompositedPads { get; init; } = [];

    /// <summary>How many vias were DECLARED as such (a <c>ViaDrill</c> tool, or a <c>ViaPad</c> flash)
    /// rather than inferred from the geometry alone. The number R-L4f-10's completion note asks
    /// for.</summary>
    public int DeclaredVias { get; init; }
    public int InferredVias { get; init; }

    public IReadOnlyList<string> Diagnostics { get; init; } = [];

    /// <summary>Every shape this produced, in one list, for a caller that just wants to add them to a
    /// cell.</summary>
    public IEnumerable<LayoutShape> AllShapes =>
        Vias.Cast<LayoutShape>().Concat(UnpairedHoles).Concat(ComponentHoles).Concat(Slots);
}

public static class DrillViaPairing
{
    /// <summary>R-L4f-6. Copper layers are given top-to-bottom, so a span of <c>1,4</c> on a four-layer
    /// board lands on <c>copperTopToBottom[0]</c> and <c>[3]</c>. A span the board cannot honour
    /// (indices outside the resolved stack) falls back to through-hole and says so — an import must
    /// not fail because a drill file counts its layers differently from the artwork set.</summary>
    public static DrillSpanLayers MapSpan(
        DrillSpan? span, LayerKey drillLayer, IReadOnlyList<LayerKey> copperTopToBottom)
    {
        LayerKey? top = copperTopToBottom.Count > 0 ? copperTopToBottom[0] : null;
        LayerKey? bottom = copperTopToBottom.Count > 0 ? copperTopToBottom[^1] : null;

        if (span is null || (span.FromLayer == 0 && span.ToLayer == 0))
            return new DrillSpanLayers(drillLayer, top, bottom,
                "No layer span was declared; the holes are treated as through-hole.");

        if (span.FromLayer < 1 || span.ToLayer < 1 ||
            span.FromLayer > copperTopToBottom.Count || span.ToLayer > copperTopToBottom.Count)
            return new DrillSpanLayers(drillLayer, top, bottom,
                $"The declared span {span.FromLayer}-{span.ToLayer} names copper layers this import " +
                $"does not have ({copperTopToBottom.Count} were resolved); the holes are treated as " +
                "through-hole.");

        var landing = copperTopToBottom[span.FromLayer - 1];
        var far = copperTopToBottom[span.ToLayer - 1];

        // The honest statement of what the model can and cannot hold. A ViaShape has ONE landing
        // layer, so the far side of a blind or buried span is carried by the drill layer the caller
        // minted for this file and by nothing on the via itself.
        string note = span.IsThroughHole
            ? $"Through-hole span {span.FromLayer}-{span.ToLayer}."
            : $"{span.Kind} span {span.FromLayer}-{span.ToLayer}: the via lands on copper layer " +
              $"{span.FromLayer}. Its far side (copper layer {span.ToLayer}) is carried by the drill " +
              "layer this file was given, because a via holds one landing layer and not two.";

        return new DrillSpanLayers(drillLayer, landing, far, note);
    }

    /// <summary>
    /// Pairs every hit against the artwork and builds the vias, the loose holes and the slots.
    ///
    /// <para><b>Pairing is exact first, and otherwise snaps by at most <see cref="SnapMicrons"/>
    /// micron</b> (R-L4f-9, amended on measurement). L4c writes the pad flash and the drill hit from
    /// the same X/Y, so for a file set circuitRF wrote the match is bit-exact and the snap never
    /// engages. A third-party set is a different matter: the artwork and the drill file are written by
    /// different halves of the same tool, in different units and digit formats, and one measured
    /// four-layer board put its blind-via pad at (177.500001, -45.000012) mm against a drill hit at
    /// (177.5, -45.0) — 12 nanometres apart, in files that unquestionably describe one via. Exact-only
    /// pairing turned every blind and buried via on that board into an unpaired hole plus an orphaned
    /// pad, silently and by 12 nm.</para>
    ///
    /// <para>The reason the brief gave for exactness still holds and is what bounds the snap: a
    /// tolerance must never reach a NEIGHBOURING pad on a fine-pitch part. One micron cannot — the
    /// tightest pad pitch in circulation is hundreds of microns, so the snap is two to three orders of
    /// magnitude below the nearest thing it could wrongly reach — and the nearest candidate wins
    /// inside it, so an exact match is never displaced by a snapped one.</para>
    ///
    /// <para><b>Which field is which layer is pinned by <see cref="ViaShape"/>'s own doc comment</b>
    /// and getting it backwards produces a plausible-looking export with copper where the hole should
    /// be. <paramref name="drillLayer"/> is the BARREL (<see cref="LayoutShape.Layer"/>);
    /// <paramref name="landingLayer"/> is the PAD's own copper layer
    /// (<see cref="ViaShape.LandingLayer"/>). L4d's R-L4d-10 discipline applies verbatim: the gate
    /// proves this by EXPORTING and comparing, never by reading the two fields back.</para>
    /// </summary>
    /// <summary>How far a drill hit may reach for its pad when the two do not land on the same DBU —
    /// see <see cref="Pair"/>. One micron: far enough to absorb the round-off between an artwork file
    /// and a drill file written in different units, and orders of magnitude short of any pad pitch.</summary>
    public const int SnapMicrons = 1;

    /// <param name="compositedCopperLayers">How many COPPER layers in the set had to be composited for
    /// polarity — reported when a hole still finds no pad, so the diagnostic can name compositing as
    /// the cause instead of accusing the set of being two different boards.</param>
    /// <param name="compositedPads">The pads those layers painted before compositing swallowed them
    /// (<see cref="GerberReadResult.CompositedFlashes"/>), each already verified to lie wholly inside
    /// its layer's copper. They pair exactly as a surviving flash does; consuming one records a
    /// <see cref="DrillPairingResult.CarvedPads"/> entry instead of removing an artwork shape.</param>
    /// <param name="copperLayers">The set's COPPER layers. A via's pad is copper, and without this the
    /// candidate ranking's last resort ("a flash on any layer at all") took whatever was at the hole —
    /// on a real board the SOLDER MASK OPENING around each mounting hole, which turned six 4.6 mm mask
    /// clearances into 4.6 mm copper pads sitting on a pour that has a deliberate hole there. Null or
    /// empty means the caller could not say, and every layer stays eligible as before.</param>
    public static DrillPairingResult Pair(
        IReadOnlyList<GerberImportedShape> artwork,
        ExcellonReadResult drill,
        LayerKey? drillLayer,
        LayerKey? landingLayer = null,
        int dbuPerMicron = LayoutUnits.DefaultDbuPerMicron,
        int compositedCopperLayers = 0,
        IReadOnlyList<GerberImportedShape>? compositedPads = null,
        IReadOnlyCollection<LayerKey>? copperLayers = null)
    {
        if (drill.Refusal is not null)
            return new DrillPairingResult { Refusal = drill.Refusal };

        bool anythingToPlace = drill.Hits.Count > 0 || drill.Slots.Count > 0;
        if (anythingToPlace && drillLayer is null)
            return new DrillPairingResult
            {
                Refusal = $"This drill file's {drill.Hits.Count} hole(s) have no drill layer to land on — " +
                          "no layer in the import was identified as a drill layer, so nothing was " +
                          "created. Map a drill layer for this file and import it again.",
            };

        var barrel = drillLayer ?? default;
        long snap = Math.Max(1, (long)SnapMicrons * dbuPerMicron);

        // Every flash the artwork offers, bucketed on a grid of the snap distance. A list per bucket
        // because a through-hole pad exists on more than one copper layer at the same point; a grid
        // rather than the exact coordinate because a third-party set's two halves can disagree by a
        // few DBU (see Pair's own note). Nine buckets cover everything within one snap of a hit.
        //
        // The composited pads sit at the END of one shared index space, so PickFlash needs no second
        // code path and its ranking (landing layer first, then nearest, then largest) applies to both
        // kinds at once. An index at or above artwork.Count is a composited pad.
        //
        // ONLY pads on the layer the via will actually LAND on. Carving a pad out of one layer's pour
        // while the via puts its pad back on another moves copper between layers — measured on a real
        // six-layer board as 4.5 mm² vanishing from an inner plane and reappearing on the top, because
        // a via whose top pad was missing paired with the inner one instead. A surviving discrete flash
        // may still be taken from any layer, exactly as before: consuming one REMOVES that shape, so
        // the same asymmetry is at least visible as a shape that left. A composited pad has no shape to
        // remove, so it may only be claimed where cutting it and putting the via's pad back cancel.
        var pads = (compositedPads ?? [])
            .Where(p => landingLayer is not { } landing || p.Shape.Layer == landing)
            .ToList();

        var candidates = new List<GerberImportedShape>(artwork.Count + pads.Count);
        candidates.AddRange(artwork);
        candidates.AddRange(pads);

        var copper = copperLayers is { Count: > 0 } ? new HashSet<LayerKey>(copperLayers) : null;

        var flashesAt = new Dictionary<(long X, long Y), List<int>>();
        for (int i = 0; i < candidates.Count; i++)
        {
            if (candidates[i].Shape is not CircleShape c) continue;
            if (copper is not null && !copper.Contains(c.Layer)) continue;   // a mask opening is not a pad
            var key = (Cell(c.Cx, snap), Cell(c.Cy, snap));
            if (!flashesAt.TryGetValue(key, out var list)) flashesAt[key] = list = [];
            list.Add(i);
        }

        var consumed = new HashSet<int>();
        var carvedPads = new List<CircleShape>();
        var vias = new List<ViaShape>();
        var unpaired = new List<CircleShape>();
        var componentHoles = new List<CircleShape>();
        var diagnostics = new List<string>();
        int declared = 0, inferred = 0, declaredViaWithNoPad = 0, indistinguishable = 0;

        foreach (var hit in drill.Hits)
        {
            // R-L4f-10: where the file DECLARES what a tool is for, that is a lookup and not a
            // judgement call. A heuristic that overrides a declaration is a bug, so there is no
            // geometric tiebreak anywhere below — not a diameter ratio, not an annular-ring test.
            bool declaredComponent = IsComponentFunction(hit.Function);
            bool declaredVia = string.Equals(hit.Function, "ViaDrill", StringComparison.OrdinalIgnoreCase);

            if (declaredComponent)
            {
                componentHoles.Add(Hole(hit, barrel));
                continue;
            }

            int flash = PickFlash(candidates, flashesAt, consumed, hit.X, hit.Y, landingLayer, barrel, snap);
            if (flash < 0)
            {
                if (declaredVia) declaredViaWithNoPad++;
                unpaired.Add(Hole(hit, barrel));
                continue;
            }

            bool viaPad = string.Equals(candidates[flash].AperFunction, "ViaPad", StringComparison.OrdinalIgnoreCase);
            if (declaredVia || viaPad) declared++;
            else { inferred++; indistinguishable++; }

            var pad = (CircleShape)candidates[flash].Shape;
            consumed.Add(flash);
            // A composited pad has no shape to drop out of the artwork; the caller cuts the same disc
            // out of the pour it was merged into instead.
            if (flash >= artwork.Count) carvedPads.Add(pad);
            vias.Add(new ViaShape
            {
                X = hit.X,
                Y = hit.Y,
                PadSize = pad.R * 2,
                DrillSize = hit.DiameterDbu,
                Layer = barrel,
                LandingLayer = landingLayer ?? pad.Layer,
                Net = pad.Net,
            });
        }

        var slots = new List<PathShape>(drill.Slots.Count);
        foreach (var slot in drill.Slots)
            slots.Add(new PathShape
            {
                Xy = slot.Xy,
                Width = slot.WidthDbu,
                End = PathEndStyle.Round,
                Layer = barrel,
            });

        if (unpaired.Count > 0)
            // The cause matters more than the count, and there are two of them. A board whose copper
            // was composited has no discrete flashes AT ALL, so every hit is unpaired no matter how
            // exactly the two files agree — telling that user their drill file belongs to another
            // board is not a hedge, it is wrong, and it is the common case on any board with a pour.
            diagnostics.Add(
                compositedCopperLayers > 0
                    ? $"{unpaired.Count} drill hit(s) were imported as plain holes on the drill layer " +
                      $"rather than rejoined into vias: {compositedCopperLayers} copper layer(s) in this " +
                      "set paint in clear polarity and had to be composited, which unions each pad into " +
                      "the pour around it, so there is no separate pad left for a hole to pair with. The " +
                      "holes and the copper are both correct; only the via OBJECTS could not be rebuilt."
                    : $"{unpaired.Count} drill hit(s) had no copper flash at the same coordinate and " +
                      "were imported as plain holes on the drill layer. A large number here usually " +
                      "means the drill file and the artwork do not belong to the same board.");
        if (declaredViaWithNoPad > 0)
            diagnostics.Add($"{declaredViaWithNoPad} hole(s) whose tool declares itself a via drill have no " +
                            "pad in the artwork that was imported; they were kept as plain holes.");
        if (componentHoles.Count > 0)
            diagnostics.Add($"{componentHoles.Count} hole(s) are declared component or mechanical drills, so " +
                            "they were imported as holes rather than vias and their pads were left in the " +
                            "copper artwork.");
        if (indistinguishable > 0)
            diagnostics.Add($"{indistinguishable} hole(s) were reconstructed as vias without the file saying " +
                            "so: with no ViaDrill/ComponentDrill attribute and no ViaPad flash, a via and a " +
                            "plated component hole are indistinguishable from artwork alone — both are a " +
                            "plated hole with copper landing on it. The distinction was not available.");
        if (slots.Count > 0)
            diagnostics.Add($"{slots.Count} routed slot(s) were imported as single openings on the drill layer.");

        return new DrillPairingResult
        {
            Vias = vias,
            UnpairedHoles = unpaired,
            ComponentHoles = componentHoles,
            Slots = slots,
            RemainingArtwork = [.. artwork.Where((_, i) => !consumed.Contains(i))],
            CarvedPads = carvedPads,
            // Against the list that came IN: a pad this call filtered out (wrong landing layer) is
            // still unclaimed and must survive for the next drill file, whose span may land on it.
            RemainingCompositedPads =
            [
                .. (compositedPads ?? []).Where(p =>
                {
                    int at = pads.IndexOf(p);
                    return at < 0 || !consumed.Contains(artwork.Count + at);
                }),
            ],
            DeclaredVias = declared,
            InferredVias = inferred,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>The artwork's own extent, for R-L4f-1's cross-check. Circle flashes and every vertex
    /// of everything else — enough to catch a scale error by orders of magnitude, which is the only
    /// thing the cross-check is for.</summary>
    public static DrillExtents ArtworkExtents(IEnumerable<GerberImportedShape> artwork)
    {
        var extents = DrillExtents.Empty;
        foreach (var imported in artwork)
        {
            switch (imported.Shape)
            {
                case CircleShape c:
                    extents = extents.Include(c.Cx - c.R, c.Cy - c.R).Include(c.Cx + c.R, c.Cy + c.R);
                    break;
                case RectShape r:
                    extents = extents.Include(r.X1, r.Y1).Include(r.X2, r.Y2);
                    break;
                case RoundedRectShape rr:
                    extents = extents.Include(rr.X1, rr.Y1).Include(rr.X2, rr.Y2);
                    break;
                case PolygonShape p:
                    for (int i = 0; i + 1 < p.Xy.Length; i += 2) extents = extents.Include(p.Xy[i], p.Xy[i + 1]);
                    break;
                case CurveShape cv:
                    for (int i = 0; i + 1 < cv.Xy.Length; i += 2) extents = extents.Include(cv.Xy[i], cv.Xy[i + 1]);
                    break;
                case PathShape path:
                    for (int i = 0; i + 1 < path.Xy.Length; i += 2) extents = extents.Include(path.Xy[i], path.Xy[i + 1]);
                    break;
                case ViaShape v:
                    extents = extents.Include(v.X, v.Y);
                    break;
            }
        }
        return extents;
    }

    private static bool IsComponentFunction(string? function) =>
        function is not null &&
        (function.Equals("ComponentDrill", StringComparison.OrdinalIgnoreCase) ||
         function.Equals("MechanicalDrill", StringComparison.OrdinalIgnoreCase) ||
         function.Equals("CastellatedDrill", StringComparison.OrdinalIgnoreCase));

    private static long Cell(long v, long snap) => (long)Math.Floor((double)v / snap);

    /// <summary>Picks WHICH flash this hit consumes. A through-hole pad exists on every copper layer,
    /// and consuming all of them would delete real copper from the layers the via cannot represent —
    /// so exactly one is taken and the rest are left where the file put them.
    ///
    /// <para>Ranked, in order: the LANDING layer's pad beats the barrel layer's beats any other
    /// layer's — that is the whole point of resolving a span — then the NEAREST of those, then the
    /// largest. Distance last within a layer rather than first, because "the pad on the layer this via
    /// lands on" is a statement the file made and proximity is only a tiebreak between copies of the
    /// same pad. Candidates further than <paramref name="snap"/> are not candidates at all.</para></summary>
    private static int PickFlash(
        IReadOnlyList<GerberImportedShape> flashes, Dictionary<(long, long), List<int>> flashesAt,
        HashSet<int> consumed, long x, long y, LayerKey? landingLayer, LayerKey barrel, long snap)
    {
        int best = -1;
        (int Rank, long Distance2, long NegRadius) bestScore = default;

        long cx = Cell(x, snap), cy = Cell(y, snap);
        for (long gx = cx - 1; gx <= cx + 1; gx++)
        for (long gy = cy - 1; gy <= cy + 1; gy++)
        {
            if (!flashesAt.TryGetValue((gx, gy), out var candidates)) continue;
            foreach (int i in candidates)
            {
                if (consumed.Contains(i)) continue;
                var c = (CircleShape)flashes[i].Shape;
                long dx = c.Cx - x, dy = c.Cy - y;
                if (Math.Abs(dx) > snap || Math.Abs(dy) > snap) continue;

                int rank = landingLayer is { } landing && c.Layer == landing ? 0 : c.Layer == barrel ? 1 : 2;
                var score = (rank, dx * dx + dy * dy, -c.R);
                if (best < 0 || score.CompareTo(bestScore) < 0) { best = i; bestScore = score; }
            }
        }
        return best;
    }

    private static CircleShape Hole(DrillHit hit, LayerKey drillLayer) => new()
    {
        Cx = hit.X,
        Cy = hit.Y,
        R = hit.DiameterDbu / 2,
        Layer = drillLayer,
    };
}
