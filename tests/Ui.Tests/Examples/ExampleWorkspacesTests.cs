// ================================================================
//  ExampleWorkspacesTests.cs — Tools ▸ Examples.
//
//  Three things are gated here, and each of them has failed silently in this repo before:
//
//   1. THE EXAMPLES SHIP. `tools/pcell-python` was correct under `dotnet run` for months and absent
//      from every packaged build, because the item group that copied it was never checked anywhere
//      but in a development tree. So the assertion here is about the BUILD OUTPUT, not the source
//      folder, and about the item group reaching publish as well as build.
//
//   2. THE INDEX AND THE DISK AGREE. `examples.json` gives the menu its order and its titles; a
//      folder named there that is not on disk is skipped at run time rather than shown as a row that
//      fails when pressed — which is right, and is also exactly how a missing example would go
//      unnoticed. The test is the other direction too: a workspace nobody indexed never appears.
//
//   3. THE EXAMPLES RUN. Every one of them was authored against a real engine, and an example that
//      silently stopped elaborating is worse than no example. Elaboration, not simulation — the
//      loadpull and EM benches are minutes of work each and belong to nobody's routine gate.
//
//   4. EVERY FILE AN EXAMPLE NAMES RESOLVES AGAINST THE WORKSPACE ROOT. The S-Parameters example
//      shipped its Touchstone reference spelled relative to the SCHEMATIC, which is not the base
//      anything resolves against (SnpPathPolicy: the root, on all three sides). It ran headlessly,
//      because a run verb used the schematic's own folder, and reported the file missing the moment
//      anyone opened it — one design, two answers, and no way to tell from a green CLI run.
//
//   5. AND EVERY DOCUMENT IT SHIPS HAS A ROW IN THE TREE. The Patch Antenna example's `.cem` was
//      committed, shipped, copied into the installed workspace and openable by path — and had no
//      row anywhere, because WorkspaceScanner walked a cell's three view folders and returned. A
//      file the scanner never looks at cannot be reported missing, so the only symptom was someone
//      saying the example had no EM setup.
// ================================================================

using System.Text.Json;
using CircuitRF.Core.Elaboration;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class ExampleWorkspacesTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _tmp = Path.Combine(
        Path.GetTempPath(), "crf-examples-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_tmp, true); } catch { /* best effort */ } }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir.Length > 0 ? dir : AppContext.BaseDirectory;
    }

    private static string SourceExamplesRoot() => Path.Combine(RepoRoot(), "examples");

    // ══ 1. They ship ════════════════════════════════════════════════════════

    /// <summary>
    /// The examples are in the BUILD OUTPUT beside the test assembly, not merely in the repository.
    ///
    /// <para>This is the assertion `tools/pcell-python` did not have. `ExampleWorkspaces.ResolveRoot`
    /// looks beside the executable first and only then walks up for the source tree, so a broken
    /// item group is invisible in a development build: the walk-up finds the repository and
    /// everything works until somebody installs it.</para>
    /// </summary>
    [Fact]
    public void TheExamplesAreCopiedBesideTheExecutable()
    {
        string beside = Path.Combine(AppContext.BaseDirectory, ExampleWorkspaces.RootFolderName);

        Assert.True(File.Exists(Path.Combine(beside, ExampleWorkspaces.IndexFileName)),
            $"No {ExampleWorkspaces.IndexFileName} at '{beside}'. The examples did not reach the "
          + "build output, so an installed copy would have no Tools ▸ Examples menu — check the "
          + "None/CopyToOutputDirectory item group in src/Ui/CircuitRF.Ui.csproj.");

        // Searched from the output directory ONLY, with no walk-up available to rescue it: passing
        // the base directory is not enough, because ResolveRoot walks up from whatever it is given.
        foreach (var example in ExampleWorkspaces.All(AppContext.BaseDirectory))
            Assert.True(File.Exists(Path.Combine(beside, example.Folder, ".cws")),
                $"'{example.Folder}' has no .cws in the build output — a workspace whose manifest "
              + "did not travel is not a workspace.");
    }

    /// <summary>
    /// The item group carries the examples to PUBLISH as well as to build, and excludes results.
    ///
    /// <para>A source scan rather than a publish run: publishing every runtime identifier costs
    /// minutes, and what can actually go wrong here is a metadata element, not a race. `None` with
    /// `CopyToOutputDirectory` flows to publish by default — what would break it is somebody adding
    /// `CopyToPublishDirectory=Never`, and that is what this reads for.</para>
    /// </summary>
    [Fact]
    public void TheItemGroupShipsTheExamplesAndNotTheirResults()
    {
        string proj = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "CircuitRF.Ui.csproj"));

        int at = proj.IndexOf("../../examples/**", StringComparison.Ordinal);
        Assert.True(at >= 0, "src/Ui/CircuitRF.Ui.csproj no longer copies examples/ into the app output.");

        string group = proj[at..proj.IndexOf("</None>", at, StringComparison.Ordinal)];
        Assert.Contains("<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>", group, StringComparison.Ordinal);
        Assert.DoesNotContain("CopyToPublishDirectory>Never", group, StringComparison.Ordinal);
        Assert.Contains("results/**", group, StringComparison.Ordinal);
    }

    /// <summary>
    /// The item group excludes the state a machine leaves in an example workspace when someone
    /// OPENS it — so a release built on a working machine is the release built from a clean clone.
    ///
    /// <para><c>examples/</c> is a tree the owner edits in place, so running circuitRF drops things
    /// in it that belong to that machine: the rebuildable <c>.generated-cells</c> PCell cache, the
    /// <c>.cwsuser</c> dock layout (one person's panel proportions, tab set and screen geometry),
    /// and the <c>.crf-*</c> session bookkeeping. Each example's own <c>.gitignore</c> already lists
    /// exactly these and <c>WorkspaceArchive</c> already prunes them; this copy did not, so what
    /// shipped depended on who built it.</para>
    ///
    /// <para><c>.generated-cells</c> is also the one that BROKE the macOS release build (2026-09-16),
    /// for a reason that has nothing to do with its contents — see
    /// <c>PackagingScriptTests.MacBundleScripts_RefuseADottedDirectoryUnderContentsMacOS</c>.</para>
    /// </summary>
    [Theory]
    [InlineData(".generated-cells/**/*.*", "a rebuildable PCell cache, machine-local, and a dotted "
        + "DIRECTORY that codesign rejects the whole .app over")]
    [InlineData(".cwsuser", "one person's dock layout and screen geometry")]
    [InlineData(".crf-*", "circuitRF's own per-session bookkeeping — the advisory open-notice and "
        + "the write probe")]
    public void TheItemGroupDoesNotShipMachineLocalState(string excluded, string why)
    {
        string proj = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Ui", "CircuitRF.Ui.csproj"));

        int at = proj.IndexOf("../../examples/**", StringComparison.Ordinal);
        Assert.True(at >= 0, "src/Ui/CircuitRF.Ui.csproj no longer copies examples/ into the app output.");

        string group = proj[at..proj.IndexOf("</None>", at, StringComparison.Ordinal)];
        Assert.True(group.Contains($"examples/**/{excluded}", StringComparison.Ordinal),
            $"src/Ui/CircuitRF.Ui.csproj no longer excludes examples/**/{excluded} from the copy, so "
            + $"the app now ships {why} — whatever happened to be in the builder's own tree.");
    }

    // ══ 2. The index and the disk agree ═════════════════════════════════════

    [Fact]
    public void EveryIndexedExampleIsOnDiskAndEveryWorkspaceOnDiskIsIndexed()
    {
        string root = SourceExamplesRoot();

        var indexed = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, ExampleWorkspaces.IndexFileName)))
            .RootElement.GetProperty("Examples")
            .EnumerateArray()
            .Select(e => e.GetProperty("Folder").GetString()!)
            .ToList();

        foreach (string folder in indexed)
            Assert.True(File.Exists(Path.Combine(root, folder, ".cws")),
                $"examples.json names '{folder}', which has no .cws. A named folder that is not "
              + "there is SKIPPED at run time, so the menu would quietly be one row shorter.");

        var onDisk = Directory.EnumerateDirectories(root)
            .Where(d => File.Exists(Path.Combine(d, ".cws")))
            .Select(Path.GetFileName)
            .ToList();

        foreach (string? folder in onDisk)
            Assert.True(indexed.Contains(folder!, StringComparer.Ordinal),
                $"'{folder}' is a workspace under examples/ that examples.json does not name, so "
              + "nothing offers it. Add it to the index or move it out of examples/.");

        Assert.NotEmpty(indexed);
    }

    /// <summary>
    /// Discovery returns the index's own ORDER, with a title and a summary on every row — the three
    /// things a directory scan cannot supply, and the reason the index exists at all.
    /// </summary>
    [Fact]
    public void DiscoveryPreservesIndexOrderAndCarriesTitles()
    {
        var found = ExampleWorkspaces.All(SourceExamplesRoot());
        Assert.NotEmpty(found);

        var indexed = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(SourceExamplesRoot(), ExampleWorkspaces.IndexFileName)))
            .RootElement.GetProperty("Examples")
            .EnumerateArray().Select(e => e.GetProperty("Folder").GetString()!).ToList();

        Assert.Equal(indexed, found.Select(f => f.Folder).ToList());

        foreach (var e in found)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Title));
            Assert.False(string.IsNullOrWhiteSpace(e.Summary));
            Assert.True(File.Exists(e.CwsPath));
        }
    }

    /// <summary>A folder entry that tries to leave the examples tree is dropped, not resolved.</summary>
    [Fact]
    public void AnIndexEntryCannotNameAPathOutsideTheExamplesTree()
    {
        string root = Path.Combine(_tmp, "examples");
        Directory.CreateDirectory(Path.Combine(root, "Good"));
        File.WriteAllText(Path.Combine(root, "Good", ".cws"), "{}");
        File.WriteAllText(Path.Combine(root, ExampleWorkspaces.IndexFileName), """
            { "SchemaVersion": 1, "Examples": [
                { "Folder": "../..", "Title": "Escape" },
                { "Folder": "sub/dir", "Title": "Nested" },
                { "Folder": "Good", "Title": "Good" } ] }
            """);

        var found = ExampleWorkspaces.All(root);
        Assert.Equal(["Good"], found.Select(f => f.Folder));
    }

    // ══ 3. Installing one ═══════════════════════════════════════════════════

    /// <summary>
    /// The copy is a working workspace at the picked location, and the ORIGINAL is untouched — the
    /// examples are shipped files, and an install that wrote into them would corrupt the one copy
    /// every later install comes from.
    /// </summary>
    [Fact]
    public void InstallingAnExampleCopiesItAndLeavesTheOriginalAlone()
    {
        var example = ExampleWorkspaces.All(SourceExamplesRoot())[0];

        int before = Directory.GetFiles(example.Directory, "*", SearchOption.AllDirectories).Length;
        string parent = Path.Combine(_tmp, "picked");
        Directory.CreateDirectory(parent);

        var result = ExampleWorkspaceInstall.Run(example, parent);

        Assert.True(File.Exists(result.CwsPath));
        Assert.Empty(result.Failures);
        Assert.Equal(Path.Combine(parent, example.Folder), result.WorkspaceDir);
        Assert.True(result.FileCount > 0);

        Assert.Equal(before,
            Directory.GetFiles(example.Directory, "*", SearchOption.AllDirectories).Length);

        // Every cell folder made the trip. A .cws with no cells under it opens to an empty tree.
        Assert.Contains(Directory.EnumerateDirectories(result.WorkspaceDir),
            d => File.Exists(Path.Combine(d, ".ccell")));

        output.WriteLine($"{example.Title}: {result.FileCount} file(s) -> {result.WorkspaceDir}");
    }

    /// <summary>
    /// Installing twice into one folder is REFUSED by name, never overwritten and never renamed.
    /// The whole point of handing someone an editable copy is that their edits survive.
    /// </summary>
    [Fact]
    public void InstallingOverAnExistingFolderIsRefused()
    {
        var example = ExampleWorkspaces.All(SourceExamplesRoot())[0];
        string parent = Path.Combine(_tmp, "twice");
        Directory.CreateDirectory(parent);

        ExampleWorkspaceInstall.Run(example, parent);

        string? refusal = ExampleWorkspaceInstall.Refusal(example, parent);
        Assert.NotNull(refusal);
        Assert.Contains(example.Folder, refusal!, StringComparison.Ordinal);

        Assert.Throws<InvalidOperationException>(() => ExampleWorkspaceInstall.Run(example, parent));
    }

    /// <summary>
    /// <b>Every file of every example arrives.</b> Not a count and not a spot check: the copy's file
    /// set is compared to the source's, name for name, with only the exclusions
    /// <c>WorkspaceArchiveScanner.IsSkipped</c> names deliberately.
    ///
    /// <para>The gate above installs example <c>[0]</c> and asserts that a <c>.cws</c> and some cell
    /// folder exist — which is true of a copy that dropped an EM setup, a Touchstone file, a
    /// technology or a kit's symbols. What someone actually notices is one FILE missing from one
    /// example, and the four kinds this repository ships (<c>.cem</c>, <c>.s2p</c>, <c>.ctech</c>,
    /// <c>.csym</c>) each reach the workspace by a different route. So every example is installed and
    /// the whole set compared.</para>
    ///
    /// <para><b>Byte identity is deliberately NOT asserted</b> — <c>WorkspaceCopy</c> repairs the
    /// copy's stored references against its new location, which is the whole reason an example can
    /// ship a technology under <c>tech/</c> and still resolve wherever someone puts it. Any file that
    /// was rewritten is REPORTED instead, because a rewrite inside an example is worth a look: a
    /// reference that re-derives to the same spelling is not written at all.</para>
    /// </summary>
    [Fact]
    public void EveryFileOfEveryExampleArrivesInTheInstalledCopy()
    {
        string parent = Path.Combine(_tmp, "all");
        Directory.CreateDirectory(parent);

        int examples = 0;

        foreach (var example in ExampleWorkspaces.All(SourceExamplesRoot()))
        {
            var result = ExampleWorkspaceInstall.Run(example, parent);
            Assert.Empty(result.Failures);

            var expected = RelativeFilesOf(example.Directory);
            var actual   = RelativeFilesOf(result.WorkspaceDir);

            var missing = expected.Except(actual, StringComparer.Ordinal).OrderBy(x => x).ToList();
            var extra   = actual.Except(expected, StringComparer.Ordinal).OrderBy(x => x).ToList();

            Assert.True(missing.Count == 0,
                $"'{example.Title}' installed WITHOUT: {string.Join(", ", missing)}");
            Assert.True(extra.Count == 0,
                $"'{example.Title}' installed with files the example does not have: "
              + string.Join(", ", extra));

            foreach (string rel in expected.OrderBy(x => x, StringComparer.Ordinal))
            {
                string from = Path.Combine(example.Directory, rel);
                string to   = Path.Combine(result.WorkspaceDir, rel);
                if (!File.ReadAllBytes(from).SequenceEqual(File.ReadAllBytes(to)))
                    output.WriteLine($"  {example.Folder}/{rel}: references repaired on copy");
            }

            // Named rather than left to the count. An EM setup is the one artefact of an example
            // that nothing else in the workspace points AT — a schematic names its Touchstone and a
            // cell names its technology, so either going missing surfaces as a broken reference,
            // while a dropped `.cem` just looks like an example that never had one.
            foreach (string em in expected.Where(r => r.EndsWith(".cem", StringComparison.OrdinalIgnoreCase)))
            {
                Assert.True(File.Exists(Path.Combine(result.WorkspaceDir, em)),
                    $"'{example.Title}' installed without its EM setup '{em}'.");
                output.WriteLine($"  EM setup arrived: {em}");
            }

            output.WriteLine($"{example.Title}: {expected.Count} file(s) arrived");
            examples++;
        }

        Assert.True(examples >= 6, $"only {examples} example(s) were installed");
    }

    /// <summary>Every file under <paramref name="root"/> that a copy is supposed to carry, named
    /// relative to it with forward slashes — the same predicate <c>WorkspaceCopy</c> itself
    /// consults, so the test cannot disagree with the thing it is testing about what is skipped.</summary>
    private static HashSet<string> RelativeFilesOf(string root)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (string f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(root, f).Replace('\\', '/');
            if (CircuitRF.Ui.Archive.WorkspaceArchiveScanner.IsSkipped(rel)) continue;
            set.Add(rel);
        }
        return set;
    }

    // ══ 4. They still work ══════════════════════════════════════════════════

    /// <summary>
    /// Every schematic in every example still extracts to a netlist that elaborates, with the
    /// hierarchy intact.
    ///
    /// <para><b>The instance count is asserted, not just the absence of an exception.</b> A cell
    /// instance that cannot be resolved is SKIPPED by the extractor, not refused — which is how the
    /// harmonic-balance benches came to be authored around a device that was silently not in the
    /// netlist. A bench that elaborates to its bias network and nothing else raises no error
    /// anywhere.</para>
    /// </summary>
    [Fact]
    public void EveryExampleSchematicExtractsWithItsDeviceStillInIt()
    {
        int checkedDocs = 0;

        foreach (var example in ExampleWorkspaces.All(SourceExamplesRoot()))
        foreach (string csch in Directory.EnumerateFiles(example.Directory, "*.csch",
                                                         SearchOption.AllDirectories))
        {
            var (model, _, _) = SchematicPersistence.LoadFromFile(csch);
            model.SchematicDirectory = Path.GetDirectoryName(csch);

            var extracted = NetExtractor.Extract(model, Path.GetFileNameWithoutExtension(csch),
                                                 DiskCellResolver.Instance);

            Assert.True(extracted.Conflicts.Count == 0,
                $"{example.Folder}/{Path.GetFileName(csch)}: {string.Join("; ", extracted.Conflicts)}");

            int placed = model.Components.Count(c =>
                c.CellRef is null
                    ? c.Symbol is not (SymbolKind.Var or SymbolKind.Meas or SymbolKind.Ground
                                       or SymbolKind.Pin)
                    : true);

            Assert.True(extracted.TestBench.Instances.Count > 0,
                $"{example.Folder}/{Path.GetFileName(csch)} extracted to nothing.");
            Assert.True(extracted.TestBench.Instances.Count >= placed - 2,
                $"{example.Folder}/{Path.GetFileName(csch)}: {placed} placed part(s) but only "
              + $"{extracted.TestBench.Instances.Count} reached the netlist. A cell instance that "
              + "does not resolve is skipped in silence.");

            checkedDocs++;
        }

        Assert.True(checkedDocs >= 6, $"only {checkedDocs} example schematic(s) were checked");
        output.WriteLine($"{checkedDocs} example schematic(s) extracted cleanly");
    }

    /// <summary>
    /// <b>No example ships a System block that is not passive.</b>
    ///
    /// <para>The blocks in that family — switch, circulator, coupler, balun, attenuator, filter —
    /// build their S-matrix from real, in-phase amplitudes converted straight from dB, so an
    /// insertion loss, a return loss and an isolation that are each individually plausible can add
    /// coherently past unity. The System Design example shipped three of them: a 0.5 dB switch with
    /// 18 dB of return loss (σ_max = 1.10), a 0.4 dB circulator with 22 dB of isolation
    /// (σ_max = 1.13), and an in-phase 3 dB coupler, which cannot be passive at all (σ_max = 1.38 —
    /// a matched lossless reciprocal four-port must put 90° between its outputs). The first showed
    /// up as an antenna-port return loss reading +1.2 dB out of band.</para>
    ///
    /// <para>Elaboration alone, because that is where the check lives for a frequency-flat block and
    /// it is the whole cost — no analysis is run here, for the reason the class header already
    /// gives.</para>
    /// </summary>
    [Fact]
    public void NoExampleShipsASystemBlockThatIsNotPassive()
    {
        // Scoped to the schematics that carry one, which is what the claim is about — and is also
        // why this does not have to elaborate every example in the tree. (It could not: the
        // Loadpull benches' `Bias=on` is a string parameter the generic resolver refuses, which is
        // a documented shape in src/Core/CLAUDE.md and has nothing to do with passivity.)
        SymbolKind[] systemBlocks =
        [
            SymbolKind.Atten, SymbolKind.Switch, SymbolKind.SwitchD, SymbolKind.Circulator,
            SymbolKind.Coupler, SymbolKind.Hybrid90, SymbolKind.Hybrid180, SymbolKind.Balun,
            SymbolKind.Filter, SymbolKind.Duplexer,
        ];

        var reported = new List<string>();
        int checkedDocs = 0;

        foreach (var example in ExampleWorkspaces.All(SourceExamplesRoot()))
        foreach (string csch in Directory.EnumerateFiles(example.Directory, "*.csch",
                                                         SearchOption.AllDirectories))
        {
            var (model, _, _) = SchematicPersistence.LoadFromFile(csch);
            if (!model.Components.Any(c => systemBlocks.Contains(c.Symbol))) continue;
            model.SchematicDirectory = Path.GetDirectoryName(csch);

            var extracted = NetExtractor.Extract(model, Path.GetFileNameWithoutExtension(csch),
                                                 DiskCellResolver.Instance);
            using var nl = new Elaborator(extracted.Library).Elaborate(extracted.TestBench);
            checkedDocs++;

            reported.AddRange(nl.Warnings
                .Where(w => w.Contains("system.block-not-passive", StringComparison.Ordinal))
                .Select(w => $"{example.Folder}/{Path.GetFileName(csch)}: {w}"));
        }

        Assert.True(reported.Count == 0, string.Join("\n", reported));
        Assert.True(checkedDocs >= 6,
            $"only {checkedDocs} schematic(s) carried a System block — this gate checked nothing.");
        output.WriteLine($"{checkedDocs} schematic(s) with System blocks: all passive");
    }

    /// <summary>
    /// Every example carries at least one analysis somewhere, and no example carries an absolute
    /// path out of somebody's home directory.
    /// </summary>
    /// <remarks>
    /// <b>The runnable set is a list of DOCUMENT KINDS, and a new kind has to be added to it.</b>
    /// <c>.crail</c> was the first one to arrive after this gate was written, and the Power Rail
    /// example failed here with "has nothing to run" — on a workspace whose whole point is a rail
    /// to run. It is the omission brief-railrf-1 already names ("a row added to
    /// <c>DocumentKinds.Classify</c> obliges an arm in <c>check</c> and a case in <c>explain</c> in
    /// the same change"), one place further along.
    ///
    /// <para>The personal-path sweep covers <b>every</b> text document an example ships rather than
    /// only the runnable ones: a technology, a part library and a rail document all carry file
    /// references, and a leak is a leak whichever file it is in.</para>
    /// </remarks>
    [Fact]
    public void EveryExampleDeclaresAnalysesAndNamesNobodysMachine()
    {
        foreach (var example in ExampleWorkspaces.All(SourceExamplesRoot()))
        {
            bool runnable = false;
            foreach (string doc in Directory.EnumerateFiles(example.Directory, "*",
                                                            SearchOption.AllDirectories))
            {
                string ext = Path.GetExtension(doc);
                if (ext is not (".csch" or ".cem" or ".clay" or ".cws" or ".ccell"
                                      or ".crail" or ".crlib" or ".ctech")) continue;

                string text = File.ReadAllText(doc);
                Assert.DoesNotContain("/Users/", text, StringComparison.Ordinal);
                Assert.DoesNotContain("C:\\\\Users", text, StringComparison.Ordinal);
                Assert.DoesNotContain("/home/", text, StringComparison.Ordinal);

                if (ext is ".cem") runnable = true;

                // A .crail runs on its own — `circuitrf rail` needs no analysis card, because the
                // rail set IS the thing to run.
                if (ext is ".crail") runnable = true;
                if (ext is ".csch" && text.Contains("\"Analyses\"", StringComparison.Ordinal)
                                   && text.Contains("\"Type\"", StringComparison.Ordinal))
                    runnable = true;
            }
            Assert.True(runnable, $"'{example.Title}' has nothing to run.");
        }
    }

    // ══ 4b. Every named file resolves, against the base the window uses ══════

    /// <summary>
    /// Every <c>File</c> parameter an example's schematics carry — a Touchstone, a SPICE model, a
    /// Verilog-A source — names a file that is really there, resolved <b>against the workspace
    /// root</b>.
    ///
    /// <para><b>The base is the whole test.</b> Simulate writes <c>netlist.cnl</c> at the workspace
    /// root and elaborates from there, and <c>SnpPathPolicy.ToStored</c> writes a picked path
    /// relative to that root, so a stored reference means the root and nothing else. The
    /// S-Parameters example stored <c>potentially_unstable_amp.s2p</c> — right beside the schematic,
    /// and one directory level nothing resolves against. Every earlier gate passed: the file was
    /// committed, it shipped, it was copied into the installed workspace, and the schematic
    /// extracted cleanly. It failed on the one thing nothing asked, which is whether the string
    /// resolves.</para>
    /// </summary>
    [Fact]
    public void EveryFileAnExampleNamesResolvesAgainstItsWorkspaceRoot()
    {
        int checkedRefs = 0;

        foreach (var example in ExampleWorkspaces.All(SourceExamplesRoot()))
        foreach (string csch in Directory.EnumerateFiles(example.Directory, "*.csch",
                                                         SearchOption.AllDirectories))
        {
            var (model, _, _) = SchematicPersistence.LoadFromFile(csch);

            foreach (var comp in model.Components)
            foreach (var p in comp.Parameters)
            {
                if (p.Name != "File" || string.IsNullOrWhiteSpace(p.Expression)) continue;

                Assert.False(Path.IsPathRooted(p.Expression),
                    $"{example.Folder}/{Path.GetFileName(csch)}: {comp.InstanceName}.File is an "
                  + $"absolute path ('{p.Expression}') and means nothing on anyone else's machine.");

                string? resolved = SnpPathPolicy.Resolve(p.Expression, example.Directory, null);
                Assert.True(resolved is not null && File.Exists(resolved),
                    $"{example.Folder}/{Path.GetFileName(csch)}: {comp.InstanceName}.File is "
                  + $"'{p.Expression}', which resolves to '{resolved}' against the workspace root "
                  + "and is not there. That is the base Simulate uses, so this design opens and "
                  + "reports the file missing however well it runs headlessly.");

                checkedRefs++;
            }
        }

        Assert.True(checkedRefs >= 1, "no example named a file at all — this gate checked nothing.");
        output.WriteLine($"{checkedRefs} file reference(s) resolved against their workspace root");
    }

    // ══ 5. Every document an example ships is REACHABLE ═════════════════════

    /// <summary>
    /// Every document in every example has a row in the project tree.
    ///
    /// <para><b>Shipping a file and showing it are different questions, and only the first had a
    /// gate.</b> The Patch Antenna example's EM setup was committed, packaged, copied into the
    /// installed workspace and openable by path, by <c>circuitrf em</c> and by the run service — and
    /// the tree had no row for it, because <c>WorkspaceScanner.BuildCellNode</c> enumerated the
    /// three <see cref="ViewType"/> sub-folders and returned. Nothing was reported, because nothing
    /// failed: a folder the scanner never opens cannot be found to be missing.</para>
    ///
    /// <para>Asserted over the DOCUMENT kinds circuitRF opens rather than every file — a
    /// <c>.gitignore</c> is deliberately hidden (<c>IsHiddenTreeFile</c>) and a README is a loose
    /// file whose row is incidental. What is not negotiable is that a design document someone
    /// shipped can be clicked.</para>
    /// </summary>
    [Fact]
    public void EveryDocumentAnExampleShipsHasARowInTheProjectTree()
    {
        string[] documentExtensions = [".csch", ".csym", ".clay", ".cem", ".ctech", ".cdd", ".charm"];
        int rows = 0;

        foreach (var example in ExampleWorkspaces.All(SourceExamplesRoot()))
        {
            var inTree = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Collect(WorkspaceScanner.Scan(example.Directory), inTree);

            foreach (string doc in Directory.EnumerateFiles(example.Directory, "*",
                                                            SearchOption.AllDirectories))
            {
                if (!documentExtensions.Contains(Path.GetExtension(doc), StringComparer.OrdinalIgnoreCase))
                    continue;
                // The PCell cache is rebuildable and deliberately not in the tree.
                if (doc.Contains(GeneratedCellStore.ReservedFolderName, StringComparison.Ordinal)) continue;

                Assert.True(inTree.Contains(Path.GetFullPath(doc)),
                    $"'{example.Title}' ships {Path.GetRelativePath(example.Directory, doc)} and the "
                  + "project tree has no row for it, so nobody can open it from the window.");
                rows++;
            }
        }

        Assert.True(rows >= 10, $"only {rows} document(s) were checked");
        output.WriteLine($"{rows} shipped document(s) have a row in the tree");

        static void Collect(ProjectTreeNode n, HashSet<string> into)
        {
            if (!string.IsNullOrEmpty(n.AbsolutePath)) into.Add(Path.GetFullPath(n.AbsolutePath));
            foreach (var c in n.Children) Collect(c, into);
        }
    }

    // ══ 5. Both menu surfaces ═══════════════════════════════════════════════

    /// <summary>
    /// The in-window rows carry a real command and the example's own folder as the parameter.
    ///
    /// <para>The row's <c>Command</c> is assigned rather than bound, and this is what says so: a
    /// binding written in code would resolve against whatever DataContext the row inherits once it
    /// is handed to a parent through <c>ItemsSource</c>, and would simply do nothing when pressed.
    /// A menu item that is present and inert is the failure worth a test.</para>
    /// </summary>
    [Fact]
    public void TheInWindowMenuRowsAreWiredToTheCommand()
    {
        var vm = new WorkspaceViewModel();

        Assert.Equal(WorkspaceViewModel.Examples.Count, vm.ExampleMenuItems.Count);

        foreach (var (control, example) in vm.ExampleMenuItems.Zip(WorkspaceViewModel.Examples))
        {
            var item = Assert.IsType<Avalonia.Controls.MenuItem>(control);
            Assert.Equal(example.Title, item.Header);
            Assert.Equal(example.Folder, item.CommandParameter);
            Assert.Same(vm.OpenExampleCommand, item.Command);
            Assert.True(item.Command!.CanExecute(example.Folder));
        }
    }

    /// <summary>
    /// Both menu surfaces offer Examples. They are hand-mirrored — the macOS <c>NativeMenu</c> and
    /// the in-window <c>Menu</c> — and every comment in that file says the two must not drift.
    /// </summary>
    [Fact]
    public void BothMenuSurfacesCarryTheExamplesEntry()
    {
        string xaml = File.ReadAllText(RepoFile("src", "Ui", "Views", "WorkspaceWindow.axaml"));
        Assert.Contains("<NativeMenuItem Header=\"Examples\">", xaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ExampleMenuItems}\"", xaml, StringComparison.Ordinal);

        // The macOS side is filled in from code, so the item existing in XAML proves nothing on its
        // own — the rebuild has to be reachable from the window's own DataContext wiring.
        string codeBehind = File.ReadAllText(RepoFile("src", "Ui", "Views", "WorkspaceWindow.axaml.cs"));
        Assert.Contains("RebuildNativeExamplesMenu();", codeBehind, StringComparison.Ordinal);
        Assert.Contains("WorkspaceViewModel.Examples", codeBehind, StringComparison.Ordinal);
    }

    private static string RepoFile(params string[] parts)
        => Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray());
}
