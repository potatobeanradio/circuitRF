// The board file's stackup section -> circuitRF's Stackup/StackupLayer (docs/sonnet-briefs/
// brief-L4d-kicad-pcb-import.md §4, R-L4d-5..8).
//
// §4 is this phase's centre of gravity, not an accessory to the geometry: per layer the file carries a
// thickness, a relative permittivity and a loss tangent, which is most of a .ctech and most of an EM
// setup arriving for free. The board format carries two things NOT AT ALL — conductivity and Mur — and
// a third (the open/ground boundary condition) has no counterpart in it whatsoever; another reader's
// format may state conductivity (R-ipx0-2). What this file does with the gaps matters more than what it
// does with the values: one honest paragraph about what was and was not recovered beats three separate
// silent assumptions.

namespace CircuitRF.Design.Layout.Interchange;

public static class PcbStackupMapping
{
    /// <summary>Copper's bulk conductivity, S/m — the default R-L4d-7 names, applied to a conductor whose
    /// entry states no conductivity, which is every conductor of a board file. <b>Never inferred from the entry's <c>material</c> string</b>:
    /// that is a lookup table of laminate trade names, it is out of scope, and it would put third-party
    /// product names into this repo (root <c>CLAUDE.md</c> §"Commercial Vendor References").
    ///
    /// <para>GI3 R-gi3-2: the NUMBER now comes from <see cref="ConductorMaterials.Copper"/>, the one
    /// table in this repository that holds it. The name stays because R-L4d-7 is what this default IS,
    /// and every message here says so by it — but it is no longer a second copy of the value.</para>
    /// </summary>
    public static double DefaultCopperConductivitySm => ConductorMaterials.Copper.SigmaSm;

    /// <summary>The material a defaulted conductor names — the record every shipped technology carries.</summary>
    public const string CopperMaterialName = "Copper";

    /// <summary>
    /// Names <see cref="CopperMaterialName"/> on every conductor entry of <paramref name="tech"/> that names no
    /// material and carries exactly <see cref="DefaultCopperConductivitySm"/> — the copper an import defaulted —
    /// adding the shipped generic library's Copper record when the technology has none (owner request,
    /// 2026-10-12: an imported board's copper read "(none)" in the Material combo). The material is what a
    /// thermal run looks up k, density and heat capacity by, and what the 3D view and its exports call the
    /// metal; a bare σ carries none of it.
    ///
    /// <para><b>No conductor's electrical answer moves</b>: the record's σ₂₀ IS that default, and nothing is named
    /// where it would not be — a conductor with another σ, or a technology whose own "Copper" states another
    /// σ₂₀, is left as it was.</para>
    ///
    /// <para><b>A conductive via entry the import made is named too, and that one DOES move an answer</b>
    /// (owner decision, 2026-10-12: match the shipped technologies, whose plated vias name Copper at 5.8e7).
    /// An import leaves a via's σ at 0, which every solver reads as a PERFECT barrel — zero barrel impedance
    /// in the planar fill, zero resistance at its DC point, PEC in Palace — and a thermal run finds no k for
    /// it by name. Named, the barrel is copper: a few milliohms of barrel resistance per via (negligible in
    /// an S-parameter, real in a DC IR drop) and a via a thermal run can conduct heat through. A
    /// non-plated hole (no fill, or <see cref="StackupLayer.Plated"/> false) is a hole and is left alone,
    /// and so is a via that already states a σ or a material.</para>
    /// </summary>
    /// <returns>How many stackup entries now name it.</returns>
    public static int NameDefaultedCopper(Technology tech)
    {
        var conductors = tech.Stackup.Layers
            .Where(l => l.Material is not { Length: > 0 } &&
                        ((l.Kind == StackupKind.Conductor && l.SigmaSm == DefaultCopperConductivitySm) ||
                         (l.Kind == StackupKind.Via && l.Fill is not null && l.Plated != false && l.SigmaSm == 0)))
            .ToList();
        if (conductors.Count == 0) return 0;

        if (tech.FindMaterial(CopperMaterialName) is { } stated)
        {
            if (stated.Sigma20 != DefaultCopperConductivitySm) return 0;
        }
        else
        {
            var copper = MaterialLibraries.LoadGeneric()
                .FirstOrDefault(m => string.Equals(m.Name, CopperMaterialName, StringComparison.Ordinal));
            if (copper?.Sigma20 != DefaultCopperConductivitySm) return 0;
            tech.Materials.Add(copper);
        }

        foreach (var l in conductors)
        {
            l.Material = CopperMaterialName;
            l.SigmaSm  = DefaultCopperConductivitySm;   // what resolve-on-read would write on the next load
        }
        return conductors.Count;
    }

    public sealed record Result(Stackup? Stackup, IReadOnlyList<string> Messages);

    /// <summary>
    /// Builds a <see cref="Stackup"/> from <paramref name="entries"/>, or reports honestly when there
    /// is nothing to build from.
    /// </summary>
    /// <param name="entries">The file's stackup entries, top-to-bottom, or null when the file carries
    /// no stackup section at all.</param>
    /// <param name="overallThicknessMm">The board's overall thickness, when the file states one — the
    /// single substrate fact a stackup-less file still carries, worth naming in the note that says
    /// nothing was built.</param>
    /// <param name="dbuPerMicron">Destination resolution.</param>
    /// <param name="resolveDrawingLayer">Maps a stackup entry's own name onto the reconciled drawing
    /// <see cref="LayerKey"/> it corresponds to, or null when nothing matches. This is what links a
    /// conductor entry to the artwork drawn on it.</param>
    public static Result Build(
        IReadOnlyList<PcbStackupEntry>? entries,
        double? overallThicknessMm,
        int dbuPerMicron,
        Func<string, LayerKey?> resolveDrawingLayer)
    {
        var messages = new List<string>();

        // R-L4d-6 — a board whose author never opened the stackup page has only an overall thickness.
        // Import the geometry, leave the stackup EMPTY, and say so. Do not fabricate a plausible default
        // substrate: an invented stackup is worse than none, because nothing downstream will ever
        // question it and it WILL be simulated.
        if (entries is null || entries.Count == 0)
        {
            string thickness = overallThicknessMm is { } t
                ? $" The file states an overall board thickness of {t:0.###} mm and nothing else about the substrate."
                : "";
            messages.Add(
                "This board carries no stackup section — the technology's stackup was left EMPTY and no " +
                "substrate was invented." + thickness +
                " Before the EM path can run, the technology needs a dielectric thickness, a relative " +
                "permittivity and a loss tangent, and each conductor needs a thickness.");
            return new Result(null, messages);
        }

        var stackup = new Stackup
        {
            // GI3 R-gi3-7, extended from the Gerber path to this one. The board file states its overall
            // thickness and this reader had always dropped it after one sentence — so the one number
            // that could check a stackup was never in the document anyone checks a stackup in. Carried
            // on the stackup this mapping BUILDS rather than onto the destination technology, which is
            // the difference from GerberImport: a board import never replaces a stackup that is already
            // there, and a thickness belonging to a different board must be refused with the rows it
            // came with. Nothing derives geometry from it — see Stackup.BoardThicknessDbu.
            BoardThicknessDbu = overallThicknessMm is { } overallMm && overallMm > 0
                ? PcbUnits.Length(overallMm, dbuPerMicron)
                : null,
        };
        int conductors = 0, dielectrics = 0, ignored = 0, defaultedSigma = 0;

        // R-L4d-5: the file's order is top-to-bottom and must STAY top-to-bottom. A reversed stackup
        // simulates cleanly and answers the wrong question.
        foreach (var entry in entries)
        {
            var kind = KindOf(entry.Type);
            if (kind is null) { ignored++; continue; }

            var layer = new StackupLayer
            {
                Kind = kind.Value,
                Name = entry.Name,
                ThicknessDbu = entry.ThicknessMm is { } mm ? PcbUnits.Length(mm, dbuPerMicron) : 0,
            };

            if (kind == StackupKind.Dielectric)
            {
                if (entry.EpsilonR is { } er) layer.Epsr = er;
                if (entry.LossTangent is { } td) layer.TanD = td;
                layer.Mur = 1.0;                                    // R-L4d-7 — no entry states one
                dielectrics++;
            }
            else
            {
                // R-ipx0-2a: a stated value is used; R-L4d-7's default is for an entry that states none.
                if (entry.ConductivitySm is null) defaultedSigma++;
                layer.SigmaSm = entry.ConductivitySm ?? DefaultCopperConductivitySm;
                if (resolveDrawingLayer(entry.Name) is { } key) layer.DrawingLayers.Add(key);
                conductors++;
            }

            stackup.Layers.Add(layer);
        }

        if (stackup.Layers.Count == 0)
        {
            messages.Add(
                $"This board's stackup section declares {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}, " +
                "none of them a copper or dielectric layer — the technology's stackup was left EMPTY and " +
                "no substrate was invented.");
            return new Result(null, messages);
        }

        messages.Add(
            $"Stackup: {conductors} conductor(s) and {dielectrics} dielectric(s) imported top-to-bottom" +
            (ignored > 0 ? $"; {ignored} non-electrical entr{(ignored == 1 ? "y" : "ies")} (mask, silkscreen, paste) skipped" : "") +
            ".");

        // R-L4d-7 and R-L4d-8 in ONE note, deliberately: three separate lines read as three separate
        // small caveats, and the point is that these are the things a simulation will silently use.
        messages.Add(DefaultsNote(conductors, defaultedSigma, dielectrics));

        return new Result(stackup, messages);
    }

    /// <summary>R-ipx0-2b: says what THIS stackup defaulted, so a conductivity the file stated is not reported as
    /// missing. No entry states a permeability or a boundary condition, so those are named whenever they apply.</summary>
    private static string DefaultsNote(int conductors, int defaultedSigma, int dielectrics)
    {
        var defaulted = new List<string>(2);
        if (defaultedSigma > 0)
            defaulted.Add("conductor conductivity " +
                          (defaultedSigma < conductors ? $"on {defaultedSigma} of {conductors} conductors " : "") +
                          $"({DefaultCopperConductivitySm:0.###e+0} S/m, copper)");
        if (dielectrics > 0) defaulted.Add("relative permeability (1.0)");

        const string Boundaries = "the stackup's top and bottom boundary conditions";
        const string LeftAtDefaults = "left at the technology's own defaults";
        return defaulted.Count switch
        {
            2 => $"Not stated by this file, so defaulted: {defaulted[0]} and {defaulted[1]} — neither was inferred from the " +
                 $"file's material names. It does not state {Boundaries} either, and they were {LeftAtDefaults}. " +
                 "Check all three before simulating.",
            1 => $"Not stated by this file, so defaulted: {defaulted[0]} — it was not inferred from the file's material " +
                 $"names. It does not state {Boundaries} either, and they were {LeftAtDefaults}. Check both before simulating.",
            _ => $"This file does not state {Boundaries}, and they were {LeftAtDefaults}. Check them before simulating.",
        };
    }

    /// <summary>R-L4d-5's mapping. Anything else — solder mask, silkscreen, paste — is not an
    /// electrical layer and is skipped rather than forced into one of the two kinds.</summary>
    private static StackupKind? KindOf(string type) => type switch
    {
        "copper" => StackupKind.Conductor,
        "core" or "prepreg" => StackupKind.Dielectric,
        _ => null,
    };
}
