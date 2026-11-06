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
// So the gaps are named, once, in one paragraph, and nothing is inferred FROM THE FILES:
//  * permittivity and loss tangent are read if present;
//  * conductivity and Mur are defaulted exactly as PcbStackupMapping already defaults them, through
//    the same constant, and are NAMED as defaults in the same note;
//  * with no job file at all the STRUCTURE is built from the artwork alone (GI2);
//  * and every substrate value still missing after all of that is completed from ONE generic FR-4
//    board and reported (owner, 2026-09-08 — see SubstrateDefaults).
//
// GI2 (brief-gi2-stackup-skeleton.md) separates two things this file used to refuse together. How many
// conductors there are, in what order, called what, bound to which drawing layer, is bookkeeping the
// IMPORT HAS ALREADY DONE — it is in the files and the cascade resolved it. Thickness, permittivity and
// loss tangent are not in the files at all.
//
// WHAT CHANGED ON 2026-09-08, AND WHAT DID NOT. GI2 spelled the second half ZERO — zero thickness and
// Epsr = 0, outside every extractor's guard and outside TechValidation's `Epsr < 1`, so a skeleton was
// UNSIMULATABLE by construction. That was aimed squarely at Epsr's own C# default of 1.0, which is AIR:
// a perfectly valid substrate that runs and answers the wrong question. The owner's decision is that
// the zeros were being paid for on every Gerber import — eleven rows of numbers typed by hand on a
// six-layer board — and that a NAMED, REPORTED default is not the silent-air failure GI2 guarded
// against. So the values are supplied and the message says which ones and that they are guesses.
// The guard against a value nobody checked now lives in two places instead of the zeros: this file's
// own message, and the Technology editor marking any field an EM run cannot use. What has NOT changed
// is the refusal to read a substrate out of the FILES: no material-name lookup, ever — see below.
//
// Never infer permittivity from a material name. It is a lookup table of laminate trade names, it is
// out of scope, and it would put third-party product names into this repo (root CLAUDE.md
// §"Commercial Vendor References"). PcbStackupMapping refuses this by name already; this refuses it
// for the same reason, and neither may ever grow the table.

namespace CircuitRF.Design.Layout.Interchange;

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
    /// <param name="copperLayerNames">What those drawing layers are CALLED, in the same order — the
    /// names the technology's own layer table gives them. GI2's skeleton names each conductor entry
    /// after its drawing layer, so the Stackup tab and the layer table read as one document. Shorter
    /// than <paramref name="copperLayers"/> (or null) falls back to the key.</param>
    /// <param name="maskAndPasteLayerNames">The mask/paste/legend drawing layers this set imported as
    /// artwork — R-gi2-6. None of them becomes a stackup entry; their presence is stated once so the
    /// omission is a decision rather than an oversight.</param>
    public static Result Build(
        IReadOnlyList<GerberJobFile.JobStackupEntry>? entries,
        double? boardThicknessMm,
        int? layerNumber,
        IReadOnlyList<LayerKey> copperLayers,
        int dbuPerMicron,
        IReadOnlyList<string>? copperLayerNames = null,
        IReadOnlyList<string>? maskAndPasteLayerNames = null)
    {
        var messages = new List<string>();

        // R-L4g-9's second branch, as GI2 rewrote it. An individual Gerber file carries no substrate
        // data whatsoever — so there is still nothing to build a substrate FROM, and L4d's R-L4d-6
        // holds unchanged: do not fabricate a plausible one. What there IS is the STRUCTURE, already
        // resolved by the identity cascade before this is called, and discarding it left the person
        // who imported a six-layer board to hand-author eleven rows reproducing a result the import
        // had already computed.
        if (entries is null || entries.Count == 0)
            return Skeleton(boardThicknessMm, copperLayers, dbuPerMicron,
                            copperLayerNames, maskAndPasteLayerNames, messages);

        var stackup = new Stackup();
        int conductors = 0, dielectrics = 0, ignored = 0;

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

            if (kind == StackupKind.Dielectric)
            {
                // Read if present; spelled UNSET as ZERO if not — NOT left at StackupLayer's own C#
                // default of 1.0, which is air and which reads back as a measured vacuum. Zero is the
                // spelling the skeleton branch has always used and the one SubstrateDefaults.Fill
                // below looks for, so both branches complete through the same pass.
                if (entry.DielectricConstant is { } er) layer.Epsr = er; else layer.Epsr = 0;
                if (entry.LossTangent is { } td) layer.TanD = td;
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
        messages.Add(
            $"Not carried by the job file, so defaulted: conductor conductivity " +
            $"({PcbStackupMapping.DefaultCopperConductivitySm:0.###e+0} S/m, copper) and relative permeability " +
            "(1.0) — neither was inferred from the file's material names, which are trade names and not " +
            "electrical data. The stackup's top and bottom boundary conditions have no counterpart in " +
            "the format either and were left at the technology's own defaults.");

        // Owner, 2026-09-08. A job file routinely omits the two optional electrical fields and
        // sometimes a thickness, so this branch reaches the same half-filled stack the artwork-only
        // one does — and the same completion applies, reported the same way.
        AddSubstrateNote(messages, SubstrateDefaults.Fill(stackup, MicronsOf(boardThicknessMm), dbuPerMicron));

        return new Result(stackup, conductors, dielectrics, messages);
    }

    /// <summary>
    /// GI2's skeleton: the STRUCTURE the import already resolved (R-gi2-1 … R-gi2-6), completed with
    /// one ordinary FR-4 board (owner, 2026-09-08).
    ///
    /// <para><b>Every number in the rows built here is still written as ZERO, and that is not
    /// vestigial.</b> Zero is this codebase's spelling of "nobody said" — outside
    /// <c>TechValidation</c>'s <c>Epsr &lt; 1</c> check and outside both extractors' own
    /// <c>Epsr &gt;= 1</c> guards — and it is what <see cref="SubstrateDefaults.Fill"/> looks for
    /// when it decides which values it is allowed to supply. In particular <see cref="StackupLayer.Epsr"/>
    /// is written explicitly rather than left at its C# default of <c>1.0</c>: 1.0 is AIR, an
    /// entirely simulatable substrate that would be indistinguishable from a measured one, and Fill
    /// would leave it alone. Build the structure with zeros; let one pass complete it.</para>
    ///
    /// <para>Conductor conductivity is the one thing filled in HERE, through the same constant
    /// <c>PcbStackupMapping</c> already exposes. Copper's bulk conductivity is physics, not a guess
    /// about this board, and R-L4d-7's precedent is to default it and NAME it as a default.</para>
    /// </summary>
    private static Result Skeleton(
        double? boardThicknessMm,
        IReadOnlyList<LayerKey> copperLayers,
        int dbuPerMicron,
        IReadOnlyList<string>? copperLayerNames,
        IReadOnlyList<string>? maskAndPasteLayerNames,
        List<string> messages)
    {
        // No copper at all — a drill-only set, or a folder of silkscreen. There is no structure to
        // emit either, so this is the one case that still leaves the stackup genuinely EMPTY.
        if (copperLayers.Count == 0)
        {
            messages.Add(
                "This file set carries no job-file stackup and no copper artwork, so the technology's " +
                "stackup was left EMPTY and no substrate was invented — an individual Gerber file " +
                "states nothing about the substrate at all." +
                (boardThicknessMm is { } t0 ? $" The job file does state an overall board thickness of {t0:0.###} mm." : "") +
                " Before the EM path can run, the technology needs its conductor and dielectric layers, " +
                "each with a thickness, and each dielectric with a relative permittivity and a loss tangent.");
            return new Result(null, 0, 0, messages);
        }

        var stackup = new Stackup();
        int conductors = 0, dielectrics = 0;

        for (int i = 0; i < copperLayers.Count; i++)
        {
            if (i > 0)
            {
                // R-gi2-5. Positional and neutral, ALWAYS. The number and construction of the layers
                // between two copper sheets is a fabrication decision that no artwork file states, and
                // a laminate trade name here would be both an invention and a third-party product name
                // in this repo (root CLAUDE.md §"Commercial Vendor References"). A plausible name is
                // also the thing that stops someone checking.
                stackup.Layers.Add(new StackupLayer
                {
                    Kind = StackupKind.Dielectric,
                    Name = $"Dielectric {dielectrics + 1}",
                    ThicknessDbu = 0,
                    Epsr = 0,           // NOT 1.0 — see this method's own note. 1.0 is air, and air runs.
                    TanD = 0,
                    Mur = 1.0,
                });
                dielectrics++;
            }

            stackup.Layers.Add(new StackupLayer
            {
                Kind = StackupKind.Conductor,
                Name = copperLayerNames is { } names && i < names.Count && names[i] is { Length: > 0 } n
                    ? n
                    : $"Conductor {i + 1}",
                ThicknessDbu = 0,
                SigmaSm = PcbStackupMapping.DefaultCopperConductivitySm,
                DrawingLayers = [copperLayers[i]],
            });
            conductors++;
        }

        // R-gi2-12 — ONE paragraph replacing the old "left EMPTY" line. It said "NO SUBSTRATE WAS
        // INVENTED" until 2026-09-08; one now is, so this half states only what the FILES said and
        // the note that follows states what circuitRF supplied. The two must not be merged: a
        // paragraph that mixes read values with guessed ones is one nobody can act on.
        messages.Add(
            $"{conductors} conductor layer(s) and {dielectrics} dielectric layer(s) were created from the " +
            "artwork, in the order the copper files were resolved into, each conductor bound to its own " +
            "drawing layer. An individual Gerber file states nothing about the substrate at all." +
            (boardThicknessMm is { } t ? $" The job file does state an overall board thickness of {t:0.###} mm." : "") +
            $" Conductor conductivity is defaulted to {PcbStackupMapping.DefaultCopperConductivitySm:0.###e+0} S/m " +
            "(copper) and is named here as a default; it was not inferred from anything in the files.");

        AddSubstrateNote(messages, SubstrateDefaults.Fill(stackup, MicronsOf(boardThicknessMm), dbuPerMicron));

        // R-gi2-6. Mask IS a dielectric in the physical stack, and its artwork states neither its
        // thickness nor its permittivity — so an entry for it would be exactly the invention the rest
        // of this file refuses, and it would silently change the conductor-to-conductor geometry a
        // solver sees. Left out, and said once.
        if (maskAndPasteLayerNames is { Count: > 0 } masks)
            messages.Add(
                $"{masks.Count} soldermask, paste or legend drawing layer(s) ({string.Join(", ", masks)}) " +
                "were imported as artwork and are NOT in the stackup — their artwork states neither a " +
                "thickness nor a permittivity, and adding them would change the conductor-to-conductor " +
                "geometry a solver sees. Add them on the Stackup tab if your run needs them.");

        return new Result(stackup, conductors, dielectrics, messages);
    }

    /// <summary>The board thickness in microns, for <see cref="SubstrateDefaults.Fill"/>. Null stays
    /// null: "the files stated no overall thickness" is a different fact from zero.</summary>
    private static decimal? MicronsOf(double? boardThicknessMm) =>
        boardThicknessMm is { } mm && mm > 0 ? (decimal)mm * 1000m : null;

    /// <summary>
    /// <b>What circuitRF supplied, said in its own paragraph</b> (owner, 2026-09-08).
    ///
    /// <para>Separate from the paragraph above it on purpose. That one reports what the FILES said;
    /// this one reports what nothing said and circuitRF filled in anyway, and the two must stay
    /// apart — a single paragraph mixing read values with guessed ones is one nobody can act on,
    /// and it is the guessed half that has to survive being skim-read.</para>
    ///
    /// <para>It names every quantity and every number, because the whole safety of defaulting at all
    /// rests on the person who imported the board being able to see, without opening anything, which
    /// values describe their board and which describe a generic one. Nothing is said when nothing was
    /// supplied: a set whose job file carried a complete stackup gets no note.</para>
    /// </summary>
    internal static void AddSubstrateNote(List<string> messages, SubstrateDefaults.Filled filled)
    {
        if (!filled.Any) return;

        var supplied = new List<string>();
        if (filled.ConductorThickness > 0)
            supplied.Add(
                $"copper thickness on {filled.ConductorThickness} conductor(s) " +
                $"({SubstrateDefaults.OuterConductorUm:0.###} um outer, {SubstrateDefaults.InnerConductorUm:0.###} um inner)");
        if (filled.DielectricThickness > 0 && filled.DielectricUm is { } um)
            supplied.Add(
                $"thickness on {filled.DielectricThickness} dielectric(s) ({um:0.###} um each, " +
                (filled.FromStatedBoardThickness
                    ? "sharing out the overall board thickness the files state"
                    : $"sharing out a {SubstrateDefaults.DielectricBudgetUm:0.###} um board") + ")");
        if (filled.Epsr > 0)
            supplied.Add($"relative permittivity {SubstrateDefaults.Epsr:0.###} on {filled.Epsr} dielectric(s)");
        if (filled.TanD > 0)
            supplied.Add($"loss tangent {SubstrateDefaults.TanD:0.####} on {filled.TanD} dielectric(s)");
        if (filled.Mur > 0)
            supplied.Add($"relative permeability {SubstrateDefaults.Mur:0.###} on {filled.Mur} dielectric(s)");

        messages.Add(
            "NOT STATED BY ANY FILE IN THIS SET, so circuitRF filled it in with ordinary FR-4 so the " +
            $"technology can be simulated as it stands: {string.Join("; ", supplied)}. These are guesses " +
            "about a board circuitRF has not seen — check them against your fabricator's stackup on the " +
            "Technology editor's Stackup tab before you trust an EM result. Nothing here was inferred " +
            "from the material names in the files.");
    }

    /// <summary>R-L4g-9's mapping. Everything else the format lists — solder mask, paste, legend,
    /// coverlay, stiffener, adhesive — is not an electrical layer and is skipped rather than forced
    /// into one of the two kinds.</summary>
    private static StackupKind? KindOf(string type) =>
        type.Equals("Copper", StringComparison.OrdinalIgnoreCase) ? StackupKind.Conductor :
        type.Equals("Dielectric", StringComparison.OrdinalIgnoreCase) ? StackupKind.Dielectric :
        null;
}
