// ================================================================
//  PlotSourceFile.cs  —  one result file, read the way the Data
//  Display reads one
//
//  There are three seams over `IPlotDataSources` — the application's
//  library, `circuitrf render`'s files, and the Smith Chart's own
//  document-relative references — and every one of them has to turn a
//  PATH into the same two things: the `DataSet` a cube trace resolves
//  against, and the narrow network view that stands in for an SNP.
//
//  It is here, once, because a second loader is a file the CLI and the
//  GUI could disagree about: the two lines below `.npy` are not
//  optional, and each of them was found missing by a gate rather than
//  by reading the code.
// ================================================================

using RfCore;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Render.DataDisplay;

public static class PlotSourceFile
{
    /// <summary>
    /// One result file as the Data Display sees it — the <see cref="DataSet"/> and the network view.
    /// </summary>
    /// <returns>The pair, or the sentence saying why the file could not be read. Never throws.</returns>
    public static (DataSet? Data, SNP? Snp, string? Error) Load(string path)
    {
        var (data, snp, error, _) = LoadWithNotes(path);
        return (data, snp, error);
    }

    /// <summary>
    /// <see cref="Load"/>, plus what the load did NOT do as plainly as it does for a <c>.npy</c> — a
    /// binary simulation dataset's text variables, which are not cubes, and a <c>PortZ</c> that varies
    /// so no <c>Z0</c> was added. Each is one sentence, said once; empty for every other kind.
    /// </summary>
    public static (DataSet? Data, SNP? Snp, string? Error, IReadOnlyList<string> Notes) LoadWithNotes(string path)
    {
        try
        {
            if (IsBinaryDataset(path))
            {
                var skipped = new List<(string Name, string Why)>();
                var data = BinaryDatasetReader.Read(path).ToDataSet(skipped);
                var notes = new List<string>();
                if (skipped.Count > 0)
                    notes.Add($"Not read, text rather than numbers: {string.Join(", ", skipped.Select(s => s.Name))}.");
                notes.AddRange(DataSourceView.AddZ0FromPortZ(data));

                // The same two lines the .npy branch below needs, for the same reasons.
                DataSourceView.MaterializeNetworkParamCubes(data);
                return (data, DataSourceView.NetworkViewOf(data), null, notes);
            }

            if (string.Equals(Path.GetExtension(path), ".npy", StringComparison.OrdinalIgnoreCase))
            {
                var (data, _) = DataSetImporter.Import(path);
                data.BindsAnalysisNamesToFlatCubes = true;   // a CLI -o result answers a display made from Simulate's

                // The two things a loaded source GAINS before a trace can be resolved against it.
                // Without the first, a trace on "SP1.Z" — a VIRTUAL cube, converted from S and Z0 on
                // first read — resolves to nothing. Without the second, every DERIVED trace (Max
                // Gain, µ, a stability circle) is dropped as the display opens, because a simulated
                // run has no SNP by design and this narrow view is what stands in for one.
                DataSourceView.MaterializeNetworkParamCubes(data);
                return (data, DataSourceView.NetworkViewOf(data), null, []);
            }

            if (TouchstoneIO.ParsePortsFromExtension(path) is not null)
            {
                // A FRESH READ, not TouchstoneCache: the cache hands back a SHARED SNP that its own
                // header says to treat as immutable, and what a caller does with one of these is
                // hold it on a Trace and renormalize it. Callers that want a file read once cache
                // the RESULT of this call, which is per-document and cannot be shared by accident.
                var snp = TouchstoneIO.ReadFile(path);
                return (DataSetBuilder.FromSnp(snp), snp, null, []);
            }

            // .spl / .lpcwave and anything else the importer recognizes.
            var (other, _) = DataSetImporter.Import(path);
            other.BindsAnalysisNamesToFlatCubes = true;     // a CLI -o .mat, as the .npy above
            return (other, null, null, []);
        }
        catch (Exception ex) { return (null, null, ex.Message, []); }
    }

    /// <summary>
    /// A binary simulation dataset by its <c>.ds</c> extension, or by its CONTENT whatever it is called —
    /// a renamed file is still one. The extension is tested first so the ordinary case reads no bytes;
    /// a known extension of another kind is never sniffed.
    /// </summary>
    public static bool IsBinaryDataset(string path)
    {
        string ext = Path.GetExtension(path);
        if (string.Equals(ext, ".ds", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(ext, ".npy", StringComparison.OrdinalIgnoreCase)
            || TouchstoneIO.ParsePortsFromExtension(path) is not null) return false;
        return BinaryDatasetReader.IsBinaryDataset(path);
    }
}
