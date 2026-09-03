// A job file's MaterialStackup -> circuitRF's Stackup (docs/sonnet-briefs/
// brief-L4g-gerber-import-orchestration.md §3, R-L4g-9).
//
// THE POINT OF THIS FILE IS WHAT IT REFUSES TO DO. A .gbrjob carries an ordered, top-to-bottom list of
// copper, dielectric, mask, paste and legend entries, each with a THICKNESS and a material name, plus
// the board's overall thickness and layer count. That is a real fraction of a .ctech and it is taken.
// What the format still does not carry is the ELECTRICAL part — relative permittivity, loss tangent,
// conductivity, permeability, and the top/bottom boundary conditions — and the two optional fields it
// does define for the first two are routinely absent from a real export.
//
// So the gaps are named, once, in one paragraph, and NOTHING is invented:
//  * permittivity and loss tangent are read if present and left UNSET if not;
//  * conductivity and Mur are defaulted exactly as PcbStackupMapping already defaults them, through
//    the same constant, and are NAMED as defaults in the same note;
//  * with no job file at all the stackup stays EMPTY and one message says so.
//
// Never infer permittivity from a material name. It is a lookup table of laminate trade names, it is
// out of scope, and it would put third-party product names into this repo (root CLAUDE.md
// §"Commercial Vendor References"). PcbStackupMapping refuses this by name already; this refuses it
// for the same reason, and neither may ever grow the table.

namespace CircuitRF.Ui.Layout.Interchange;

public static class GerberStackupMapping
{
    public sealed record Result(Stackup? Stackup, int Conductors, int Dielectrics, IReadOnlyList<string> Messages);

    /// <summary>
    /// Builds a <see cref="Stackup"/> from a job file's own <c>MaterialStackup</c>.
    /// </summary>
    /// <param name="entries">The job file's stackup entries, top to bottom, or null when the set has
    /// no job file (or its job file carries no stackup) — R-L4g-9's second branch.</param>
    /// <param name="boardThicknessMm">The board's overall thickness, when the job file states one.</param>
    /// <param name="layerNumber">The job file's own copper layer count, when it states one — worth
    /// reporting when it disagrees with the number of copper files actually imported.</param>
    /// <param name="copperLayers">The drawing layers the cascade resolved for copper, top to bottom.
    /// The i-th <see cref="StackupKind.Conductor"/> entry links to the i-th of these.</param>
    public static Result Build(
        IReadOnlyList<GerberJobFile.JobStackupEntry>? entries,
        double? boardThicknessMm,
        int? layerNumber,
        IReadOnlyList<LayerKey> copperLayers,
        int dbuPerMicron)
    {
        var messages = new List<string>();

        // R-L4g-9, second branch. An individual Gerber file carries no substrate data whatsoever, so a
        // set with no job file has nothing to build a stackup FROM. Leave it empty and say so.
        // L4d's R-L4d-6 holds here unchanged: do not fabricate a plausible substrate. An invented
        // stackup is worse than none, because nothing downstream will ever question it and it WILL be
        // simulated.
        if (entries is null || entries.Count == 0)
        {
            messages.Add(
                "This file set carries no job-file stackup, so the technology's stackup was left EMPTY " +
                "and no substrate was invented — an individual Gerber file states nothing about the " +
                "substrate at all." +
                (boardThicknessMm is { } t ? $" The job file does state an overall board thickness of {t:0.###} mm." : "") +
                " Before the EM path can run, the technology needs a dielectric thickness, a relative " +
                "permittivity and a loss tangent for each dielectric, and a thickness for each conductor.");
            return new Result(null, 0, 0, messages);
        }

        var stackup = new Stackup();
        int conductors = 0, dielectrics = 0, ignored = 0;
        int missingThickness = 0, missingEpsr = 0, missingTanD = 0;

        // The file's order IS top-to-bottom and must STAY top-to-bottom (R-L4g-10, and L4d's R-L4d-5
        // before it): a reversed stack simulates cleanly and answers a different question.
        foreach (var entry in entries)
        {
            var kind = KindOf(entry.Type);
            if (kind is null) { ignored++; continue; }

            var layer = new StackupLayer
            {
                Kind = kind.Value,
                Name = entry.Name is { Length: > 0 } n ? n : entry.Type,
                ThicknessDbu = entry.ThicknessMm is { } mm ? PcbUnits.Length(mm, dbuPerMicron) : 0,
            };
            if (entry.ThicknessMm is null) missingThickness++;

            if (kind == StackupKind.Dielectric)
            {
                // Read if present; leave UNSET if not. StackupLayer's own defaults (Epsr 1.0, TanD 0)
                // are what "unset" looks like, and the note below says so rather than letting a
                // vacuum-permittivity dielectric read as a measured one.
                if (entry.DielectricConstant is { } er) layer.Epsr = er; else missingEpsr++;
                if (entry.LossTangent is { } td) layer.TanD = td; else missingTanD++;
                layer.Mur = 1.0;                                        // the format states none
                dielectrics++;
            }
            else
            {
                layer.SigmaSm = PcbStackupMapping.DefaultCopperConductivitySm;   // the format states none
                if (conductors < copperLayers.Count) layer.DrawingLayers.Add(copperLayers[conductors]);
                conductors++;
            }

            stackup.Layers.Add(layer);
        }

        if (stackup.Layers.Count == 0)
        {
            messages.Add(
                $"The job file's stackup declares {entries.Count} entr{(entries.Count == 1 ? "y" : "ies")}, " +
                "none of them copper or dielectric — the technology's stackup was left EMPTY and no " +
                "substrate was invented.");
            return new Result(null, 0, 0, messages);
        }

        messages.Add(
            $"Stackup from the job file: {conductors} conductor(s) and {dielectrics} dielectric(s), in the " +
            "file's own top-to-bottom order" +
            (ignored > 0 ? $"; {ignored} non-electrical entr{(ignored == 1 ? "y" : "ies")} (mask, paste, legend) skipped" : "") +
            ".");

        if (conductors > copperLayers.Count)
            messages.Add(
                $"{conductors - copperLayers.Count} conductor entr{(conductors - copperLayers.Count == 1 ? "y" : "ies")} " +
                "in the stackup have no copper artwork in this set, so they carry no drawing layer.");
        if (layerNumber is { } declared && declared != copperLayers.Count)
            messages.Add(
                $"The job file declares {declared} copper layer(s) but {copperLayers.Count} copper file(s) were " +
                "imported. The stackup was built as the file states it.");

        // R-L4g-9's one paragraph, deliberately not three: three separate lines read as three small
        // caveats, and the point is that these are the values a simulation will silently use.
        var gaps = new List<string>();
        if (missingEpsr > 0) gaps.Add($"relative permittivity ({missingEpsr} dielectric(s) left unset)");
        if (missingTanD > 0) gaps.Add($"loss tangent ({missingTanD} dielectric(s) left unset)");
        if (missingThickness > 0) gaps.Add($"thickness ({missingThickness} entr{(missingThickness == 1 ? "y" : "ies")} left at zero)");

        messages.Add(
            $"Not carried by the job file, so defaulted: conductor conductivity " +
            $"({PcbStackupMapping.DefaultCopperConductivitySm:0.###e+0} S/m, copper) and relative permeability " +
            "(1.0) — neither was inferred from the file's material names, which are trade names and not " +
            "electrical data. The stackup's top and bottom boundary conditions have no counterpart in " +
            "the format either and were left at the technology's own defaults" +
            (gaps.Count > 0 ? $". The job file also omitted: {string.Join("; ", gaps)}" : "") +
            ". Check them all before simulating.");

        return new Result(stackup, conductors, dielectrics, messages);
    }

    /// <summary>R-L4g-9's mapping. Everything else the format lists — solder mask, paste, legend,
    /// coverlay, stiffener, adhesive — is not an electrical layer and is skipped rather than forced
    /// into one of the two kinds.</summary>
    private static StackupKind? KindOf(string type) =>
        type.Equals("Copper", StringComparison.OrdinalIgnoreCase) ? StackupKind.Conductor :
        type.Equals("Dielectric", StringComparison.OrdinalIgnoreCase) ? StackupKind.Dielectric :
        null;
}
