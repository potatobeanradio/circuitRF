using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.VendorLibraries.ModelLibrary;

/// <summary>
/// A model's netlist, both ways (brief vendorlib-9 R-vl9-3a): a cell's schematic extracted as <c>define</c> blocks
/// only, and a model folder's <c>.cnl</c> read back into cells.
///
/// <para><b>One extraction.</b> <see cref="FromCell"/> is <see cref="NetExtractor.ExtractCell"/> written by
/// <see cref="CnlWriter"/> — the extractor and the writer a run uses — so Export (brief 10) writes exactly what a
/// cell's instance would have contributed to a run, and Validate's D1 gate compares against the same thing.</para>
///
/// <para><b>D1: the <c>.cnl</c> is what runs.</b> <see cref="Read"/> is the only reader a placed model's run calls;
/// nothing reads a model's <c>.csch</c> to run it.</para>
/// </summary>
public static class ModelNetlist
{
    /// <summary>The comment <see cref="FromCell"/> heads its text with. Constant, so two extractions of one cell are
    /// the same bytes.</summary>
    public const string Header = "model netlist: define blocks only; extracted from the cell's schematic";

    /// <summary>
    /// The <c>.cnl</c> text of the cell at <paramref name="cellDir"/>: its definition and its sub-cells', and nothing
    /// at the top level but the header comment. Null with the reason when the cell has no schematic that reads.
    /// </summary>
    public static string? FromCell(string cellDir, out string? refusal)
    {
        if (NetExtractor.ExtractCell(cellDir, out refusal) is not { } extracted) return null;
        return CnlWriter.Write(new TestBench("model"), extracted.Library, Header);
    }

    private static readonly Dictionary<string, long> _reads = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock _gate = new();

    /// <summary>How many times this process has read the <c>.cnl</c> of the model folder at
    /// <paramref name="modelDir"/> — the counter R-vl9-3b's "once per distinct part per run" is gated on.</summary>
    public static long Reads(string modelDir)
    {
        lock (_gate) return _reads.GetValueOrDefault(Key(modelDir));
    }

    /// <summary>The model's netlist path: the folder's own name, <c>.cnl</c> (the rule <see cref="ModelFolderCheck"/>
    /// holds a folder to).</summary>
    public static string PathOf(string modelDir)
    {
        string dir = Key(modelDir);
        return ModelFolder.NetlistPath(dir, Path.GetFileName(dir));
    }

    /// <summary>
    /// The cells of the model folder at <paramref name="modelDir"/>, as its <c>.cnl</c> writes them — file references
    /// as written, relative to the model folder — or null with the reason it could not be read.
    /// </summary>
    public static Library? Read(string modelDir, out string? problem)
    {
        problem = null;
        string path = PathOf(modelDir);
        lock (_gate) _reads[Key(modelDir)] = _reads.GetValueOrDefault(Key(modelDir)) + 1;
        if (!File.Exists(path)) { problem = $"{Path.GetFileName(path)} is missing"; return null; }
        try
        {
            var (lib, _) = new CnlReader().Read(File.ReadAllText(path), "model", sourceDirectory: null);
            if (lib.Cells.Count == 0) { problem = $"{Path.GetFileName(path)} defines no cell"; return null; }
            return lib;
        }
        catch (Exception ex) { problem = $"{Path.GetFileName(path)} could not be read ({ex.Message})"; return null; }
    }

    /// <summary>
    /// Whether two model netlists hold the same definitions, judged by what they MEAN rather than how they are laid
    /// out: each is read and written back through the one reader and writer, so a comment, a blank line or spacing
    /// is not a difference and a changed value, net, cell or parameter is. Null when either does not read.
    /// </summary>
    public static bool? SameDefinitions(string a, string b)
    {
        string? ca = Canonical(a), cb = Canonical(b);
        return ca is null || cb is null ? null : string.Equals(ca, cb, StringComparison.Ordinal);
    }

    private static string? Canonical(string text)
    {
        try
        {
            var (lib, _) = new CnlReader().Read(text, "model", sourceDirectory: null);
            return CnlWriter.Write(new TestBench("model"), lib, null);
        }
        catch { return null; }
    }

    private static string Key(string dir) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(dir));
}
