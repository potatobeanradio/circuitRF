// MIM-7 — a dielectric that is PATTERNED with a conductor, and what an extractor does with one.
//
// WHY THIS IS ITS OWN FILE, when PlanarExtractor's header is emphatic that the two extractors
// restate the stackup rules rather than call into each other. That stance is about the hard part of
// the cross-section extractor — the reduction test and its refusals, which must never appear on the
// planar acceptance path. This rule is the opposite shape: it is one paragraph of policy plus one
// sentence of USER-FACING TEXT, and the sentence is the reason it is shared. A run whose medium
// silently lost a layer is the failure the note exists to prevent, and two copies of the note would
// drift into two different accounts of the same decision.
//
// The two callers ask the same question with different words for "in this run":
//   * PlanarExtractor  — is the plate conductor one of the ANALYSIS LEVELS? (a set)
//   * CrossSectionExtractor — is the plate conductor THE signal conductor? (exactly one, since a
//     uniform-line cross-section refuses multi-level geometry outright)
// and only the planar one has a sheet surface to revert, because only it solves a conductor as a
// zero-thickness sheet — the cross-section kernel models real metal of real thickness and does not
// read SheetAt at all (MIM-6's own recorded decision).
//
// ── MIM-11 — THE TIE MAY NAME A MASK, AND THAT IS THE NAME THAT IS TRUE ──────────────────────────
//
// MIM-7 tied the film to its PLATE conductor because the plate is what a run's level list talks
// about, and on this process that is a proxy: what actually defines where a thin film exists is the
// nitride mask the fab streams out, and the plate is deposited on top of what that mask left. The
// proxy was load-bearing in one direction only — a layout with no plate on it has no film — and it
// broke down the moment the mask itself became drawable (MIM-11 item 1): a nitride-only structure
// could not be expressed at all, because the film's presence was a statement about a different
// layer.
//
// So `PresentWithLayer` now resolves in TWO namespaces, and the precedence is the load-bearing part:
// a CONDUCTOR stackup entry first, exactly as before, so every technology authored against MIM-7
// resolves the same name to the same rule and extracts bit for bit as it did; a DRAWING LAYER
// second, in which case "in this run" means "this run's layout carries artwork on that layer".
//
// WHAT DOES NOT CHANGE. It is still a per-run ON/OFF and never a lateral pattern. Inside a run every
// dielectric is laterally infinite — that is the 2.5D formulation and not a limitation of this file
// — so a drawn nitride polygon does not make the film finite, it only decides whether the film is in
// the medium at all. MIM-11's own measurement of what that costs is in src/Design/RESOLVED.md, and
// PlanarExtractor says it in the run's own notes with the coverage fraction attached.

using CircuitRF.Engine.Mom;

namespace CircuitRF.Design.Layout.Em;

/// <summary>
/// MIM-11 — the DRAWING-LAYER half of a <see cref="StackupLayer.PresentWithLayer"/> tie: the
/// technology's own layer table, and the one question a run can answer about it.
/// </summary>
/// <param name="DrawingLayers">The technology's <see cref="Technology.Layers"/>, which is the
/// namespace a tie falls through to when no CONDUCTOR entry carries the name.</param>
/// <param name="HasArtwork">Does this run's layout draw anything on the named layer? This is the
/// mask spelling of "the capacitor is in this run", and it is the caller's to answer because only
/// the caller has walked the shapes.</param>
internal readonly record struct PatternedFilmMask(
    IReadOnlyList<LayerDef> DrawingLayers,
    Func<string, bool>      HasArtwork);

/// <summary>
/// MIM-11 — one patterned film this run CARRIES, reported back so the caller can say so.
///
/// <para>A film that was deactivated already produces a note here; a film that is PRESENT produced
/// nothing at all, which is the silence MIM-11 item 3 closes — the medium string lists the band
/// correctly and nowhere says it is laterally infinite. The extractor needs three things to write
/// that sentence and only the first is in the stackup, so they travel together.</para>
/// </summary>
/// <param name="Film">The dielectric entry, at its stated material — not the air clone.</param>
/// <param name="Tie">The name <see cref="StackupLayer.PresentWithLayer"/> carries.</param>
/// <param name="TieIsMask">True when <paramref name="Tie"/> resolved as a DRAWING layer rather than
/// as a conductor stackup entry — which decides which artwork the coverage fraction is measured
/// over, and which sentence tells the reader how to turn the film off.</param>
/// <param name="SheetRaisedConductor">The conductor directly beneath the film whose
/// <see cref="StackupLayer.SheetAt"/> is <see cref="ConductorSheetSurface.Top"/> — i.e. the one
/// whose analysis sheet this carried film LEAVES at the top of its band, where deactivating would
/// have put it back on the bottom. Null when there is no such conductor. MIM-11 measured this as the
/// same order of effect as the film itself (Z_c +0.77 % on a Metal1 line), and it is invisible in
/// every other report.</param>
internal readonly record struct CarriedFilm(
    StackupLayer Film, string Tie, bool TieIsMask, string? SheetRaisedConductor);

internal static class PatternedDielectric
{
    /// <summary>
    /// <b>The drawing layers this technology's ties name</b>, by <see cref="LayerKey"/> — empty for
    /// every technology whose ties all name conductors, which is every technology authored before
    /// MIM-11.
    ///
    /// <para>Exposed so the caller can collect that artwork while it walks the shapes ONCE, and so
    /// the two-namespace precedence is written here rather than restated at a call site: a name that
    /// a conductor entry carries is a conductor tie and never a mask one, whatever else is called
    /// that.</para>
    /// </summary>
    public static IReadOnlyDictionary<LayerKey, string> MaskLayers(Technology tech)
    {
        var map = new Dictionary<LayerKey, string>();
        if (tech.Stackup is null) return map;

        foreach (var film in tech.Stackup.Layers)
        {
            if (film.Kind != StackupKind.Dielectric ||
                film.PresentWithLayer is not { Length: > 0 } tie) continue;
            if (NamesAConductor(tech.Stackup, tie)) continue;

            foreach (var l in tech.Layers)
                if (string.Equals(l.Name, tie, StringComparison.Ordinal))
                    map[l.Key] = tie;
        }
        return map;
    }

    private static bool NamesAConductor(Stackup stackup, string name) =>
        stackup.Layers.Any(l => l.Kind == StackupKind.Conductor &&
                                string.Equals(l.Name, name, StringComparison.Ordinal));

    /// <summary>
    /// <b>Rebuilds <paramref name="stackup"/> with every tied dielectric whose plate is not in this
    /// run turned into air, or returns <c>null</c> when there is nothing to do</b> — in which case
    /// not one byte of the caller's arithmetic changes.
    ///
    /// <para>A thin-film capacitor's dielectric is patterned: it exists under the plates and nowhere
    /// else. The 2.5D premise cannot express that laterally — inside a run every dielectric is
    /// laterally infinite — but it does not force the film to be present in EVERY run, and
    /// <paramref name="isInRun"/> is the honest per-run proxy for "this run has capacitors in it".
    /// With the planar extractor's default level selection ("every signal conductor that carries
    /// artwork") an interconnect-only layout answers no with no configuration at all.</para>
    ///
    /// <para><b>Deactivating is two changes, and they are one decision.</b> The band enters the
    /// medium as air — εᵣ 1, tanδ 0, µᵣ 1 — with its thickness untouched, so every band above it
    /// keeps the height the process states; and, where the caller solves sheets
    /// (<paramref name="revertSheetSurface"/>), <see cref="ConductorSheetSurface.Top"/> on the
    /// conductor whose band sits directly BENEATH the film is treated as unset for this run. MIM-6
    /// exists so a plate gap reads as the capacitor dielectric alone; with no capacitor dielectric
    /// there is no gap to read, and the pre-MIM-6 placement is the established baseline for ordinary
    /// interconnect. Reverting it is what makes an interconnect run on a technology carrying the
    /// module BIT-identical to the same run on one without it, rather than merely close.</para>
    ///
    /// <para><b>MIM-11 — <paramref name="mask"/> is the second namespace</b>, and the tie resolves
    /// in it only when no CONDUCTOR entry carries the name. A mask tie asks whether the layout DRAWS
    /// the layer, which is the true statement of where a thin film is (the nitride is a mask; the
    /// plate is deposited on what it left), where the plate was always a proxy for it. Omit it and
    /// this behaves exactly as MIM-7 wrote it. <paramref name="carried"/> collects the films that
    /// came out PRESENT, so the caller can say what the laterally-infinite approximation costs on
    /// this layout — which nothing said before MIM-11 item 3.</para>
    ///
    /// <para><b>A tie naming neither a conductor entry nor a drawing layer leaves the film
    /// ACTIVE</b> and says so. Deactivating on a typo would silently thin the medium, which is the
    /// failure this whole mechanism exists to avoid.</para>
    ///
    /// <para>Nothing here touches the kernel: a hand-authored stack whose dielectric genuinely is
    /// everywhere still meets every refusal it always did.</para>
    /// </summary>
    /// <param name="plateHasArtwork">EM-SEV R-emsev-3: does the LAYOUT contain shapes on the plate
    /// conductor? Null where the caller cannot tell, which is the same as "no". The two cases are
    /// genuinely different findings and the caller knows both halves, so the split is made here
    /// rather than left to the reader:
    /// <list type="bullet">
    /// <item>No plate drawn — MIM-7's own case, and the film's absence is CORRECT. A note, worded
    /// exactly as it always has been, byte for byte: an interconnect-only run on a technology
    /// carrying a capacitor module must read as though the module were not there.</item>
    /// <item>Plate drawn, level excluded — the user drew a capacitor and this run does not contain
    /// one. A WARNING, because half the structure on screen is not in the answer.</item>
    /// </list></param>
    public static Stackup? Deactivate(
        Stackup stackup, Func<string, bool> isInRun, bool revertSheetSurface,
        List<EmFinding> notes, Func<string, bool>? plateHasArtwork = null,
        PatternedFilmMask? mask = null, List<CarriedFilm>? carried = null)
    {
        if (!stackup.Layers.Any(l => l.Kind == StackupKind.Dielectric &&
                                     l.PresentWithLayer is { Length: > 0 }))
            return null;

        var layers  = new List<StackupLayer>(stackup.Layers);
        bool changed = false;

        for (int i = 0; i < layers.Count; i++)
        {
            var film = layers[i];
            if (film.Kind != StackupKind.Dielectric || film.PresentWithLayer is not { Length: > 0 } plate)
                continue;

            // ── MIM-11 — which namespace the tie resolved in, CONDUCTOR first ────────────────
            //
            // Conductor first is not a preference: it is what makes every MIM-7 technology extract
            // bit for bit as it did, whatever a drawing layer happens to be called. A name in
            // neither namespace is the typo case below, unchanged.
            bool tiedToConductor = NamesAConductor(stackup, plate);
            bool tiedToMask      = !tiedToConductor && mask is { } m &&
                                   m.DrawingLayers.Any(l => string.Equals(l.Name, plate,
                                                                          StringComparison.Ordinal));

            if (!tiedToConductor && !tiedToMask)
            {
                notes.Add($"Dielectric '{film.Name}' says it is patterned with '{plate}', which this " +
                          "technology has neither a conductor stackup entry nor a drawing layer for. " +
                          "It is carried into the " +
                          "medium as stated rather than dropped — a broken tie must not silently thin " +
                          "the stack. Fix the name in the technology editor's Stackup tab.");
                continue;
            }

            // The two namespaces ask different questions and both mean "is the capacitor in this
            // run": a conductor tie asks whether the PLATE is an analysis level, a mask tie asks
            // whether the layout DRAWS the mask that defines the film.
            bool present = tiedToConductor ? isInRun(plate) : mask!.Value.HasArtwork(plate);

            if (present)
            {
                // MIM-11 item 3 — a carried film is reported to the caller, which is the only place
                // that can measure how much of the layout it actually covers and say so. The
                // sheet-surface half is answered HERE because `Beneath` is this file's rule and a
                // second reading of it at a call site would be the drift this file exists to avoid.
                carried?.Add(new CarriedFilm(
                    film, plate, tiedToMask,
                    revertSheetSurface && Beneath(layers, i) is
                        { Kind: StackupKind.Conductor, SheetAt: ConductorSheetSurface.Top } raised
                        ? raised.Name : null));
                continue;
            }

            var air = Clone(film);
            air.Epsr = 1.0;
            air.TanD = 0;
            air.Mur  = 1.0;
            air.PresentWithLayer = null;
            layers[i] = air;

            string reverted = "";
            if (revertSheetSurface && Beneath(layers, i) is
                { Kind: StackupKind.Conductor, SheetAt: ConductorSheetSurface.Top } lower)
            {
                int li = layers.IndexOf(lower);
                var flat = Clone(lower);
                flat.SheetAt = null;
                layers[li] = flat;
                reverted = $" and '{lower.Name}'s analysis sheet is put back on the BOTTOM of its band";
            }

            changed = true;

            // The sentence splits on ONE question and the extractor knows the answer to it. Both
            // halves state the same mechanism; what differs is whether it is the right outcome.
            //
            // MIM-11 — a MASK tie does not take the CONDUCTOR halves below, and that is not an
            // omission: both of those sentences end by naming an analysis level to add, which is
            // the remedy for a tie to a conductor, while a mask tie's question is whether the mask
            // is DRAWN and a level list cannot answer it.
            //
            // ── BUT "no mask artwork means no capacitor" IS NOT ALWAYS TRUE, AND THAT IS THE
            //    DANGEROUS HALF ────────────────────────────────────────────────────────────────
            //
            // This branch first read as though a layout with no mask on it had no capacitor in it,
            // which holds for artwork drawn by a generator that draws the mask. It does not hold
            // for the three ways a plate reaches a layout without one: a generator that PREDATES
            // the mask (every capacitor KIT_MIMCAP drew before MIM-11), artwork imported from a
            // GDSII whose nitride layer was not mapped, and a capacitor drawn by hand. In all
            // three the plate metal IS in the layout, the film is silently air, and a plate pair
            // reads its capacitance by a factor of εᵣ low — the same outcome the CONDUCTOR tie's
            // own branch below already treats as a WARNING, so this one does too rather than
            // leaving it as one note among thirty. The plate is the conductor directly ABOVE the
            // film, which is what StackupLayer.PresentWithLayer's documentation tells an author to
            // name.
            string? plateAbove = Above(layers, i) is { Kind: StackupKind.Conductor } pa ? pa.Name : null;
            bool plateIsDrawn = tiedToMask && plateAbove is not null &&
                                plateHasArtwork is not null && plateHasArtwork(plateAbove);

            notes.Add(tiedToMask
                ? plateIsDrawn
                  ? EmFinding.Warn(
                      $"'{film.Name}' is a patterned thin film defined by the '{plate}' mask, this " +
                      $"layout draws nothing on '{plate}', and it DOES draw '{plateAbove}' — the " +
                      "plate this film sits under. So the film enters the medium as AIR at its " +
                      $"stated thickness{reverted}, and the capacitor you drew is solved with air " +
                      $"between its plates: a plate pair on it reads about {film.Epsr:G3}x low. " +
                      $"Draw the '{plate}' mask over the plate. A layout reaches this state when " +
                      "its artwork predates the mask, when it was imported without that layer " +
                      "mapped, or when the capacitor was drawn by hand.")
                  : $"'{film.Name}' is a patterned thin film defined by the '{plate}' mask, and " +
                    $"this layout draws nothing on '{plate}' — so it enters the medium as AIR at " +
                    $"its stated thickness{reverted}. The film exists only where the mask opens " +
                    "it, so a layout that draws none is modelled exactly as it would be on a " +
                    $"technology with no capacitor module at all. Draw the '{plate}' mask to put " +
                    "the film in the run."
                : plateHasArtwork is not null && plateHasArtwork(plate)
                ? EmFinding.Warn(
                    $"'{film.Name}' is a patterned thin film tied to '{plate}', '{plate}' CARRIES " +
                    "ARTWORK in this layout, and it is not in this run's analysis levels — so the " +
                    $"film enters the medium as AIR at its stated thickness{reverted} and the " +
                    "capacitor you drew is not in this answer. A thin-film capacitor is its two " +
                    "plates and the film between them; with the plate level excluded, what is solved " +
                    "is the remaining metal with a gap where the capacitor was. Add " +
                    $"'{plate}' to this EM setup's analysis levels.")
                : $"'{film.Name}' is a patterned thin film tied to '{plate}', and '{plate}' is not in " +
                  $"this run — so it enters the medium as AIR at its stated thickness{reverted}. The " +
                  "film exists only where its plate's artwork is, so a run with no plate in it does not " +
                  "carry it, and this run's interconnect is modelled exactly as it would be on a " +
                  $"technology with no capacitor module at all. Put '{plate}' in the run (draw on it, or " +
                  "add it to the analysis levels) to solve the capacitor.");
        }

        return changed
            ? new Stackup { Top = stackup.Top, Bottom = stackup.Bottom, Layers = layers }
            : null;
    }

    /// <summary>The entry directly BENEATH <paramref name="i"/> that has a z band at all — a Via
    /// entry has none, so it is skipped. <c>Stackup.Layers</c> is ordered TOP to BOTTOM.</summary>
    private static StackupLayer? Beneath(List<StackupLayer> layers, int i)
    {
        for (int j = i + 1; j < layers.Count; j++)
            if (layers[j].Kind != StackupKind.Via)
                return layers[j];
        return null;
    }

    /// <summary>The entry directly ABOVE <paramref name="i"/> that has a z band at all — the mirror
    /// of <see cref="Beneath"/>, and for a patterned film that entry is the PLATE it is deposited
    /// under (<c>StackupLayer.PresentWithLayer</c>'s own recommendation to an author).</summary>
    private static StackupLayer? Above(List<StackupLayer> layers, int i)
    {
        for (int j = i - 1; j >= 0; j--)
            if (layers[j].Kind != StackupKind.Via)
                return layers[j];
        return null;
    }

    /// <summary>A field-for-field copy, so a deactivated tie is a change to THIS RUN's stackup and
    /// never to the <see cref="Technology"/> object the caller was handed — which is a live document
    /// in the application, and is re-extracted at every frequency of a sweep.</summary>
    private static StackupLayer Clone(StackupLayer l) => new()
    {
        Kind = l.Kind, Name = l.Name, ThicknessDbu = l.ThicknessDbu,
        Epsr = l.Epsr, TanD = l.TanD, Mur = l.Mur, SigmaSm = l.SigmaSm,
        DrawingLayers = [.. l.DrawingLayers],
        Films = l.Films,
        IsGroundReference = l.IsGroundReference, SheetAt = l.SheetAt,
        PresentWithLayer = l.PresentWithLayer,
        Fill = l.Fill, WallThicknessDbu = l.WallThicknessDbu,
        SpanFromLayer = l.SpanFromLayer, SpanToLayer = l.SpanToLayer,
    };
}
