// The one function that imports an IPC-2581 file (docs/briefs/brief-ipx-4-surfaces-and-acceptance.md §1). File ▸ Import
// and `convert` both call it, so neither holds a second copy of the sequence: read one step with Ipc2581Reader, hand the
// board to PcbImport.ImportBoard, and put the stackup at the head of the report.
//
// The file is never decoded to one string on the way in, because it can be a quarter of a gigabyte.

using System.Globalization;
using CircuitRF.Design.Cells;
using CircuitRF.Engine;

namespace CircuitRF.Design.Layout.Interchange;

public static class Ipc2581Import
{
    /// <summary>The steps <paramref name="path"/> holds, or the reader's sentence for why it cannot say.</summary>
    public static Ipc2581Reader.StepListing ListSteps(string path, RunControl? control = null)
    {
        using var stream = File.OpenRead(path);
        return Ipc2581Reader.ListSteps(stream, control);
    }

    /// <summary>
    /// Imports one step of the file at <paramref name="path"/> as cell folders under <paramref name="parentDir"/>.
    /// </summary>
    /// <param name="stepName">The step to import. Null with no <paramref name="chooseStep"/> imports the file's one step,
    /// and a file holding several is the reader's refusal, which lists them.</param>
    /// <param name="chooseStep">Asked only when a file holds several steps and <paramref name="stepName"/> is null. Its
    /// null is a cancellation.</param>
    /// <param name="control">Progress and cancellation. A cancellation during the read returns a cancelled result with
    /// nothing written. Once the mapping is settled the cells are written whole.</param>
    /// <remarks>See <see cref="PcbImport.ImportBoard"/> for the parameters it shares.</remarks>
    public static PcbImport.ImportResult Import(
        string path,
        string parentDir,
        string boardName,
        Technology? destTech,
        int dbuPerMicron,
        string? stepName = null,
        Func<IReadOnlyList<Ipc2581Step>, string?>? chooseStep = null,
        Func<IReadOnlyList<LayerMappingRow>, IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>?>? resolveLayerMapping = null,
        bool coalesceRasterFill = true,
        ImportTechnologyTarget? target = null,
        Func<ImportMappingRequest, ImportMappingAnswer?>? resolveMapping = null,
        string? destTechPath = null,
        RunControl? control = null)
    {
        try
        {
            if (stepName is null && chooseStep is not null)
            {
                var listing = ListSteps(path, control);
                if (listing.Refusal is { } refusal) return Refused(refusal);
                if (listing.Steps is { Count: > 1 } steps)
                {
                    stepName = chooseStep(steps);
                    if (stepName is null) return Cancelled();
                }
            }

            using var stream = File.OpenRead(path);
            return Import(stream, parentDir, boardName, destTech, dbuPerMicron, stepName, resolveLayerMapping,
                          coalesceRasterFill, target, resolveMapping, destTechPath, control);
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
    }

    /// <summary>Imports one step of an unconsumed <paramref name="stream"/>. See the overload taking a path.</summary>
    public static PcbImport.ImportResult Import(
        Stream stream,
        string parentDir,
        string boardName,
        Technology? destTech,
        int dbuPerMicron,
        string? stepName = null,
        Func<IReadOnlyList<LayerMappingRow>, IReadOnlyDictionary<LayerKey, LayoutFragment.LayerReconciliationChoice>?>? resolveLayerMapping = null,
        bool coalesceRasterFill = true,
        ImportTechnologyTarget? target = null,
        Func<ImportMappingRequest, ImportMappingAnswer?>? resolveMapping = null,
        string? destTechPath = null,
        RunControl? control = null)
    {
        try
        {
            var read = Ipc2581Reader.Read(stream, dbuPerMicron, stepName, control: control);
            if (read.Refusal is { } refusal) return Refused(refusal);

            var board = read.Board!;
            control?.Token.ThrowIfCancellationRequested();
            control?.BeginStage("writing the cells");
            var result = PcbImport.ImportBoard(board, parentDir, boardName, destTech, dbuPerMicron, resolveLayerMapping,
                                               coalesceRasterFill, target, resolveMapping, destTechPath);
            return result.Technology is { } tech
                ? result with { Messages = [.. StackupReport(board, result.Stackup, tech, dbuPerMicron), .. result.Messages] }
                : result;
        }
        catch (OperationCanceledException)
        {
            return Cancelled();
        }
    }

    private static PcbImport.ImportResult Cancelled() => new(true, [], null, [], null, [], []);

    private static PcbImport.ImportResult Refused(string refusal) => Cancelled() with { Refusal = refusal };

    /// <summary>
    /// The stackup a board landed on, one line per layer, top first: name, kind, thickness, then εr and tanδ or
    /// conductivity. A value the file did not state is marked. The technology's stackup is what is in force, which is
    /// not the file's when the import kept the technology's own, and then there is nothing to mark.
    /// </summary>
    /// <param name="applied">The stackup built from the file, or null when the file gave none.</param>
    internal static IReadOnlyList<string> StackupReport(PcbBoard board, Stackup? applied, Technology tech, int dbuPerMicron)
    {
        var layers = tech.Stackup.Layers;
        if (layers.Count == 0) return [];

        bool fromFile = applied is not null && SameLayers(applied, tech.Stackup);
        var stated = board.Stackup?.GroupBy(e => e.Name, StringComparer.Ordinal)
                         .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal)
                     ?? new Dictionary<string, PcbStackupEntry>(StringComparer.Ordinal);

        var lines = new List<string>(layers.Count + 2)
        {
            fromFile
                ? "Stackup, top first. A value marked * is not stated by the file and was defaulted; the others are as stated."
                : $"Stackup, top first. These are the values of the technology '{tech.Name}', not this file's.",
        };

        foreach (var layer in layers)
        {
            PcbStackupEntry? entry = fromFile ? stated.GetValueOrDefault(layer.Name) : null;
            bool Marked(double? value) => fromFile && value is null;

            var fields = new List<string> { layer.Name, layer.Kind.ToString().ToLowerInvariant() };
            if (layer.Kind != StackupKind.Via)
                fields.Add(Number(layer.ThicknessDbu / (double)dbuPerMicron) + " µm" + Star(Marked(entry?.ThicknessMm)));

            if (layer.Kind == StackupKind.Dielectric)
            {
                fields.Add("εr " + Number(layer.Epsr) + Star(Marked(entry?.EpsilonR)));
                fields.Add("tanδ " + Number(layer.TanD) + Star(Marked(entry?.LossTangent)));
            }
            else if (layer.SigmaSm > 0)
            {
                fields.Add("σ " + layer.SigmaSm.ToString("0.###e+0", CultureInfo.InvariantCulture) + " S/m" + Star(Marked(entry?.ConductivitySm)));
            }

            lines.Add(string.Join(" · ", fields));
        }
        return lines;
    }

    private static string Star(bool marked) => marked ? " *" : "";

    private static string Number(double value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Whether the technology holds the file's own rows: the same names and thicknesses, in order. Via entries
    /// are left out, because the import adds them to whichever stackup is in force.</summary>
    private static bool SameLayers(Stackup a, Stackup b)
    {
        static IEnumerable<(string, long)> Rows(Stackup s) =>
            s.Layers.Where(l => l.Kind != StackupKind.Via).Select(l => (l.Name, l.ThicknessDbu));
        return Rows(a).SequenceEqual(Rows(b));
    }
}
