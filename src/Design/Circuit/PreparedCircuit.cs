using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.Circuit;

/// <summary>
/// A circuit read once and evaluated many times (brief-tuneopt-2 R-to2-4).
///
/// <para><b>Everything that touches the disk happens here, once.</b> The netlist is read and parsed,
/// the sub-cell schematics are resolved (for a <c>.csch</c>), and the technology is bound; what is kept
/// is the parsed <see cref="Library"/> and <see cref="TestBench"/>. Each evaluation then works on a
/// COPY of the bench (<see cref="Optimization.TunableOverrides.Apply"/> makes one whether or not any
/// value changed), so tuned values move the netlist without re-reading anything, and two evaluations
/// on two threads never share a list a sweep writes into.</para>
///
/// <para><b>Never mutated after construction</b>, which is what makes it safe to hand to several
/// threads at once. Whether the DESIGN is safe to evaluate concurrently is a separate question,
/// answered by <see cref="NotReentrantReason"/>.</para>
/// </summary>
public sealed class PreparedCircuit
{
    private long _elaborations;

    /// <summary>The library and bench as read — null when the read failed. Shared by every evaluation,
    /// which works on its own copy (<see cref="CircuitRF.Design.Optimization.TunableOverrides"/>), so a caller reads them and never
    /// changes them: <c>circuitrf opt</c> reads the bench's tuning setup and catalog from here, the
    /// circuit the run itself evaluates.</summary>
    public Library?   Lib { get; }
    public TestBench? Tb  { get; }

    /// <summary>Why the netlist could not be read, or null. A failed read evaluates to
    /// <see cref="RunStatus.EngineError"/> carrying this sentence.</summary>
    public string? ReadError { get; }

    /// <summary>What every evaluation's elaboration resolves relative references against — the
    /// workspace root, as Simulate passes it.</summary>
    public string? BaseDirectory { get; }

    /// <summary>The files reading this circuit opened: the netlist, or the schematic and every sub-cell
    /// schematic and <c>.ccell</c> its extraction resolved. Fixed at construction — an evaluation adds
    /// nothing to it.</summary>
    public IReadOnlyList<string> FilesRead { get; }

    /// <summary>Which cells a loose schematic's instances were run with from its host workspace
    /// (<see cref="NetExtractor.ExtractionResult.HostCellChoices"/>). Empty for every other input.</summary>
    public IReadOnlyList<NetExtractor.HostCellChoice> HostCellChoices { get; private set; } = [];

    /// <summary>The vendor parts whose stated frequency range the analyses leave
    /// (<see cref="NetExtractor.ExtractionResult.PartRangeWarnings"/>). Empty for every other input.</summary>
    public IReadOnlyList<string> PartRangeWarnings { get; private set; } = [];

    /// <summary>The placed vendor parts the extraction could not read
    /// (<see cref="NetExtractor.ExtractionResult.PartRefusals"/>). Empty for every other input.</summary>
    public IReadOnlyList<string> PartRefusals { get; private set; } = [];

    /// <summary>What the extraction could not build — an instance it SKIPPED above all
    /// (<see cref="NetExtractor.ExtractionResult.Conflicts"/>), which is otherwise simply absent from the
    /// circuit with nothing said. Empty for every other input.</summary>
    public IReadOnlyList<string> ExtractionConflicts { get; private set; } = [];

    /// <summary>The placed vendor parts as tunables, with their candidates
    /// (<see cref="NetExtractor.ExtractionResult.PartTunables"/>) — what the catalog an evaluation of this circuit
    /// builds offers as <c>Instance.Part</c>. Empty for every other input: a <c>.cnl</c> cannot say it.</summary>
    public IReadOnlyList<VendorLibraries.VendorPartTunable> PartTunables { get; private set; } = [];

    /// <summary>How many times the netlist was parsed and its cell library built: one, for the life of
    /// this object. The counter R-to2-4 asks a test to hold.</summary>
    public int LibraryResolutions { get; }

    /// <summary>How many elaborations evaluations of this circuit have performed (one per plan; a
    /// parametric sweep's own per-point elaborations are the engine's and are not counted).</summary>
    public long Elaborations => Interlocked.Read(ref _elaborations);

    /// <summary>
    /// Why several evaluations of this circuit must not run at once, or null when they may
    /// (R-to2-5). A caller that wants parallel evaluation checks this and falls back to one at a time.
    /// </summary>
    public string? NotReentrantReason { get; }

    /// <summary>True when several evaluations may run concurrently on different threads.</summary>
    public bool IsReentrant => NotReentrantReason is null;

    private PreparedCircuit(Library? lib, TestBench? tb, string? readError, string? baseDirectory,
                            IReadOnlyList<string> filesRead)
    {
        Lib                = lib;
        Tb                 = tb;
        ReadError          = readError;
        BaseDirectory      = baseDirectory;
        FilesRead          = filesRead;
        LibraryResolutions = lib is null ? 0 : 1;
        NotReentrantReason = lib is null || tb is null ? null : AssessReentrancy(lib, tb);
    }

    /// <summary>A <c>.cnl</c> on disk, read exactly as Simulate reads the one it writes.</summary>
    public static PreparedCircuit FromFile(string netlistPath, string? baseDirectory)
    {
        try
        {
            var (lib, tb) = CnlTechnologyBinding.ReadFile(netlistPath, out var parts);
            // A .cnl's own SpiceModel lines place vendor parts too (EMB-5 R-emb5-13), and a run offers them as C1.Part.
            return new PreparedCircuit(lib, tb, null, baseDirectory, [Path.GetFullPath(netlistPath)])
                { PartTunables = parts, SourcePath = Path.GetFullPath(netlistPath) };
        }
        catch (Exception ex)
        {
            return new PreparedCircuit(null, null, $"Netlist read failed: {ex.Message}", baseDirectory, []);
        }
    }

    /// <summary>
    /// Netlist text — what <see cref="SchematicCircuit.CnlTextOf(string)"/> produces — read as if it
    /// were a file in <paramref name="sourceDirectory"/>, which is what a relative reference inside it
    /// (a Touchstone file, a loadpull grid) resolves against.
    /// </summary>
    public static PreparedCircuit FromText(string cnlText, string? sourceDirectory, string? baseDirectory,
                                           string testBenchName = "tb")
        => FromText(cnlText, sourceDirectory, baseDirectory, testBenchName, []);

    /// <summary>
    /// A bench already read — what <c>circuitrf check</c> holds when it asks whether an optimization
    /// would start (brief-tuneopt-11 R-to11-7), so the question goes to the run's own constructor
    /// rather than to a second copy of its rules. Nothing is read from disk.
    /// </summary>
    /// <param name="partTunables">The vendor parts of the design the bench came from (<see cref="PartTunables"/>) —
    /// passed on by a caller that rebuilt the bench from a circuit that had them.</param>
    public static PreparedCircuit FromBench(Library lib, TestBench tb, string? baseDirectory,
                                            IReadOnlyList<VendorLibraries.VendorPartTunable>? partTunables = null)
        => new(lib, tb, null, baseDirectory, []) { PartTunables = partTunables ?? [] };

    /// <summary>
    /// <see cref="FromText(string, string?, string?, string)"/> for netlist text a caller extracted itself — the
    /// GUI's tuning and optimizer sessions — keeping what the extraction knows that the text cannot say: the
    /// vendor parts and their candidates, the range warnings, the refusals.
    /// </summary>
    public static PreparedCircuit FromText(string cnlText, string? sourceDirectory, string? baseDirectory,
                                           string testBenchName, NetExtractor.ExtractionResult extracted)
    {
        var prepared = extracted.RunRefusal is { } refusal
            ? new PreparedCircuit(null, null, refusal, baseDirectory, [])
            : FromText(cnlText, sourceDirectory, baseDirectory, testBenchName, []);
        prepared.PartRangeWarnings = extracted.PartRangeWarnings;
        prepared.PartRefusals      = extracted.PartRefusals;
        prepared.PartTunables      = extracted.PartTunables;
        prepared.ExtractionConflicts = extracted.Conflicts;
        return prepared;
    }

    /// <summary>
    /// A schematic, extracted through the resolver Simulate uses and read back as the netlist Simulate
    /// writes at the workspace root. The base directory is that workspace root (null for a schematic
    /// belonging to no workspace), as Simulate passes it.
    /// </summary>
    /// <param name="hostWorkspaceRoot">The workspace a LOOSE schematic is run from (the CLI's
    /// <c>--workspace</c>), standing in for the workspace root it does not have.</param>
    public static PreparedCircuit FromSchematic(string cschPath, string? hostWorkspaceRoot = null)
    {
        string? wsRoot = WorkspaceRootFinder.WorkspaceDirOf(Path.GetDirectoryName(Path.GetFullPath(cschPath)))
                      ?? (string.IsNullOrWhiteSpace(hostWorkspaceRoot) ? null : WorkspaceRootFinder.Normalize(hostWorkspaceRoot));
        try
        {
            var (text, files, choices, extracted) = SchematicCircuit.CnlTextAndFilesOf(cschPath, hostWorkspaceRoot);
            // A placed cell the netlist would leave out refuses the run (NetExtractor.ExtractionResult.DroppedInstances).
            var prepared = extracted.RunRefusal is { } refusal
                ? new PreparedCircuit(null, null, refusal, wsRoot, files)
                : FromText(text, SchematicCircuit.ReferenceBaseOf(cschPath, hostWorkspaceRoot), wsRoot,
                           Path.GetFileNameWithoutExtension(cschPath), files);
            prepared.HostCellChoices = choices;
            prepared.SourcePath = Path.GetFullPath(cschPath);
            prepared.PartRangeWarnings = extracted.PartRangeWarnings;
            prepared.PartRefusals      = extracted.PartRefusals;
            prepared.PartTunables      = extracted.PartTunables;
            prepared.ExtractionConflicts = extracted.Conflicts;
            return prepared;
        }
        catch (Exception ex)
        {
            return new PreparedCircuit(null, null, $"Netlist read failed: {ex.Message}", wsRoot, []);
        }
    }

    private static PreparedCircuit FromText(string cnlText, string? sourceDirectory, string? baseDirectory,
                                            string testBenchName, IReadOnlyList<string> filesRead)
    {
        try
        {
            var (lib, tb) = new CnlReader().Read(cnlText, testBenchName, sourceDirectory);
            var parts = CnlTechnologyBinding.Bind(lib, tb, sourceDirectory);
            return new PreparedCircuit(lib, tb, null, baseDirectory, filesRead) { PartTunables = parts };
        }
        catch (Exception ex)
        {
            return new PreparedCircuit(null, null, $"Netlist read failed: {ex.Message}", baseDirectory, filesRead);
        }
    }

    /// <summary>brief-sdl-6 — the document this circuit was read from (a <c>.csch</c>, or the <c>.cnl</c> itself): what a
    /// <c>model=em</c> analysis resolves its own cell from. Null when unknown.</summary>
    public string? SourcePath { get; set; }

    internal void CountElaboration() => Interlocked.Increment(ref _elaborations);

    /// <summary>
    /// The audit's verdict for one design (src/Design/RESOLVED.md, TO-2). A device an external provider
    /// supplies is served by a WORKER process with a bounded instance pool and one request in flight
    /// per pipe; two evaluations would compete for both. Everything else a circuit run touches is
    /// either per-run (the elaborated netlist, the engines, a sweep's own bench copy) or a concurrent
    /// cache.
    /// </summary>
    private static string? AssessReentrancy(Library lib, TestBench tb)
    {
        foreach (var inst in tb.Instances.Concat(lib.Cells.SelectMany(c => c.Instances)))
            if (inst.Reference.Equals("ExtDevice", StringComparison.OrdinalIgnoreCase)
                || inst.Reference.Equals("VerilogA", StringComparison.OrdinalIgnoreCase))
                return $"'{inst.InstanceName}' is evaluated by an external device worker, which serves one " +
                       "evaluation at a time.";
        return null;
    }
}
