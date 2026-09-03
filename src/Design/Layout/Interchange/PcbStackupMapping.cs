// The board file's stackup section -> circuitRF's Stackup/StackupLayer (docs/sonnet-briefs/
// brief-L4d-kicad-pcb-import.md §4, R-L4d-5..8).
//
// §4 is this phase's centre of gravity, not an accessory to the geometry: per layer the file carries a
// thickness, a relative permittivity and a loss tangent, which is most of a .ctech and most of an EM
// setup arriving for free. It also carries two of those things NOT AT ALL — conductivity and Mur — and
// a third (the open/ground boundary condition) has no counterpart in the format whatsoever. What this
// file does with the gaps matters more than what it does with the values: one honest paragraph about
// what was and was not recovered beats three separate silent assumptions.

namespace CircuitRF.Design.Layout.Interchange;

public static class PcbStackupMapping
{
    /// <summary>Copper's bulk conductivity, S/m — the default R-L4d-7 names, applied because the format
    /// states no conductivity at all. <b>Never inferred from the entry's <c>material</c> string</b>:
    /// that is a lookup table of laminate trade names, it is out of scope, and it would put third-party
    /// product names into this repo (root <c>CLAUDE.md</c> §"Commercial Vendor References").</summary>
    public const double DefaultCopperConductivitySm = 5.8e7;

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

        var stackup = new Stackup();
        int conductors = 0, dielectrics = 0, ignored = 0;

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
                layer.Mur = 1.0;                                    // R-L4d-7 — the format states none
                dielectrics++;
            }
            else
            {
                layer.SigmaSm = DefaultCopperConductivitySm;        // R-L4d-7 — the format states none
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
        messages.Add(
            $"Not carried by this format, so defaulted: conductor conductivity ({DefaultCopperConductivitySm:0.###e+0} S/m, " +
            "copper) and relative permeability (1.0) — neither was inferred from the file's material " +
            "names. The stackup's top and bottom boundary conditions have no counterpart in the format " +
            "either and were left at the technology's own defaults. Check all three before simulating.");

        return new Result(stackup, messages);
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
