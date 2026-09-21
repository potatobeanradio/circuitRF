// ================================================================
//  TerminalMapTests.cs — the gate for brief-lvs-1-terminal-map.md.
//
//  ── What is actually being pinned ─────────────────────────────────────────────────────────────
//
//  The brief ships ONE fact: a cell can say which of its layout pins is which of its schematic
//  ports, and where it does not, `TerminalMap` derives an answer and STATES WHICH RULE ANSWERED.
//  So every derivation test asserts the origin as well as the terminals (R-lvs1-2b) — a map that
//  does not say it was derived is indistinguishable from a declared one, and the two have very
//  different failure modes.
//
//  The `check` half is asserted by DIAGNOSTIC ID, never by prose, for R-aut1-8's reason: the id is
//  the contract a caller filters on and the sentence is not.
//
//  Fixture paths are anonymized to the SHAPE of a path — a temp folder and invented cell names.
// ================================================================

using System.Diagnostics;
using System.Text.Json;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Symbol;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using Symbol = CircuitRF.Design.Symbol.Symbol;

namespace CircuitRF.Ui.Tests.Lvs;

public sealed class TerminalMapTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-terminals-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    // ══ 1 — the block is additive, and it round-trips ═════════════════════════════════════════════

    /// <summary>
    /// Gate 1. A <c>.ccell</c> carrying a terminal block re-saves byte for byte, and one without it
    /// re-saves byte for byte with no block at all (R-lvs1-1e).
    ///
    /// <para>The second half is the one that matters: <c>WhenWritingNull</c> is what keeps every cell
    /// in every workspace unchanged, and a field that serialized as <c>"Terminals": null</c> would
    /// rewrite all of them on first open.</para>
    /// </summary>
    [Fact]
    public void TheBlockRoundTripsByteForByte_AndACellWithoutOneStaysWithoutOne()
    {
        const string withBlock = """
        {
          "FormatVersion": 1,
          "Parameters": [],
          "IsTestBench": false,
          "Terminals": [
            {
              "Port": 1,
              "Name": "G",
              "LayoutPin": "G"
            },
            {
              "Port": 3,
              "Name": "S",
              "LayoutPin": [
                "S1",
                "S2"
              ]
            }
          ],
          "NumPorts": 3
        }
        """;

        Assert.Equal(withBlock, CellPersistence.Serialize(CellPersistence.Deserialize(withBlock)));

        string bare = CellPersistence.Serialize(new CcellFile());
        Assert.DoesNotContain("Terminals", bare, StringComparison.Ordinal);
        Assert.Equal(bare, CellPersistence.Serialize(CellPersistence.Deserialize(bare)));
    }

    /// <summary>
    /// Gate 2, and the real gate on additivity: every <c>.ccell</c> circuitRF SHIPS loads and re-saves
    /// unchanged. Walks <c>examples/</c>, which is where the shipped cells are —
    /// <c>src/Design/resources/</c> holds technologies and no cells, so it is named and found empty
    /// rather than silently skipped.
    /// </summary>
    [Fact]
    public void EveryShippedCcellReSerializesUnchanged()
    {
        string root = RepoRoot();
        var files = new List<string>();
        foreach (string dir in new[] { Path.Combine(root, "examples"), Path.Combine(root, "src", "Design", "resources") })
            if (Directory.Exists(dir))
                files.AddRange(Directory.EnumerateFiles(dir, CellFolder.CcellFileName, SearchOption.AllDirectories));

        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            // A TRAILING NEWLINE is normalized away, and only that. `AtomicFile.WriteAllText` appends
            // none, so the two shipped files (of 28) that carry one got it from whatever wrote them
            // into the repo — a pre-existing fact about those files that predates this block and would
            // be just as true with it removed. Everything else is compared byte for byte, which is
            // what the additivity claim actually rests on.
            string original = File.ReadAllText(file).TrimEnd('\n', '\r');
            Assert.Equal(original, CellPersistence.Serialize(CellPersistence.Deserialize(original)));
        }
    }

    // ══ 3 — the four derivations, one test each, asserting the ORIGIN ═════════════════════════════

    /// <summary>R-lvs1-3a. Import provenance means <c>ComponentTerminals</c> decided one numbering for
    /// both views, so the two lists are read positionally — and the result SAYS so.</summary>
    [Fact]
    public void AnImportedCellWithNoBlock_DerivesFromTheImportTable()
    {
        string cell = Cell("Imported",
            symbol: new Symbol([], [Pin(1, "A"), Pin(2, "B")], 2),
            layoutPins: ["pad-1", "pad-2"],
            ccell: c => c.ImportedFrom = new CcellImportProvenance { Source = "part.lib", Definition = "P" });

        var map = TerminalMap.ResolveCell(cell);

        Assert.Equal(TerminalMapOrigin.ImportTable, map.Origin);
        Assert.Equal(["pad-1"], map.Terminals.Single(t => t.Port == 1).LayoutPins);
        Assert.Equal(["pad-2"], map.Terminals.Single(t => t.Port == 2).LayoutPins);
    }

    /// <summary>R-lvs1-3b — what <c>pcell-contract.md</c> R3 already promises, now checked.</summary>
    [Fact]
    public void MatchingNames_DeriveByName()
    {
        string cell = Cell("Mlin", new Symbol([], [Pin(1, "1"), Pin(2, "2")], 2), ["1", "2"]);

        var map = TerminalMap.ResolveCell(cell);

        Assert.Equal(TerminalMapOrigin.ByName, map.Origin);
        Assert.Equal(["2"], map.Terminals.Single(t => t.Port == 2).LayoutPins);
    }

    /// <summary>
    /// R-lvs1-3c. Everything unnamed on both sides and the counts equal — so it is read positionally,
    /// AND the warning fires. The warning is half the requirement: a guess that is never announced is
    /// the shape of a wrong answer nobody finds.
    /// </summary>
    [Fact]
    public void NothingNamedOnEitherSide_DerivesByOrder_AndAlwaysWarns()
    {
        string cell = Cell("Blank", new Symbol([], [Pin(1, null), Pin(2, null)], 2), ["", ""]);

        var map = TerminalMap.ResolveCell(cell);

        Assert.Equal(TerminalMapOrigin.ByOrder, map.Origin);
        Assert.Equal(2, map.Terminals.Count);
        Assert.Contains("check.terminals.derived-by-order", TerminalMap.ValidateCell(cell).Select(d => d.Id));
    }

    /// <summary>
    /// R-lvs1-3d/3e — the case that matters. Three of five names match, which is evidence the author
    /// meant them to match and got two wrong. Reading the whole thing positionally would produce a
    /// confident wrong answer over a visible clue, so there is NO map, and the finding names BOTH
    /// unmatched lists.
    /// </summary>
    [Fact]
    public void PartiallyMatchingNames_AreUnderivable_AndBothListsAreNamed()
    {
        string cell = Cell("Fet",
            new Symbol([], [Pin(1, "G"), Pin(2, "D"), Pin(3, "S"), Pin(4, "B"), Pin(5, "T")], 5),
            ["G", "D", "S", "SUB", "THERM"]);

        var map = TerminalMap.ResolveCell(cell);

        Assert.Equal(TerminalMapOrigin.None, map.Origin);
        Assert.Empty(map.Terminals);
        Assert.Equal(["B", "T"], map.UnmatchedSymbolPins);
        Assert.Equal(["SUB", "THERM"], map.UnmatchedLayoutPins);
    }

    // ══ 4 — bonded pins ══════════════════════════════════════════════════════════════════════════

    /// <summary>R-lvs1-1c. A two-element <c>LayoutPin</c> is ONE terminal covering two pins, and
    /// neither of them is then reported as unmapped.</summary>
    [Fact]
    public void ATerminalOverTwoPins_IsOneTerminal_AndNeitherPinIsUnmapped()
    {
        string cell = Cell("Fet",
            new Symbol([], [Pin(1, "G"), Pin(2, "D"), Pin(3, "S")], 3),
            ["G", "D", "S1", "S2"],
            c =>
            {
                c.NumPorts = 3;
                c.Terminals =
                [
                    new CcellTerminal { Port = 1, Name = "G", LayoutPin = ["G"] },
                    new CcellTerminal { Port = 2, Name = "D", LayoutPin = ["D"] },
                    new CcellTerminal { Port = 3, Name = "S", LayoutPin = ["S1", "S2"] },
                ];
            });

        var map = TerminalMap.ResolveCell(cell);

        Assert.Equal(TerminalMapOrigin.Declared, map.Origin);
        Assert.Equal(["S1", "S2"], map.Terminals.Single(t => t.Port == 3).LayoutPins);
        Assert.Empty(TerminalMap.ValidateCell(cell));
    }

    // ══ 5/6 — every `check` row fires, by id, with the right exit code ═══════════════════════════

    /// <summary>
    /// Gate 5. One purpose-built broken cell per error row, asserted by ID through the real CLI
    /// process. Exit 1, because each is an error.
    /// </summary>
    [Theory]
    [InlineData("unknown-layout-pin")]
    [InlineData("port-out-of-range")]
    [InlineData("duplicate-port")]
    [InlineData("pin-claimed-twice")]
    public void EveryErrorRowFiresByIdAndExitsOne(string row)
    {
        string cell = Cell("Broken",
            new Symbol([], [Pin(1, "G"), Pin(2, "D")], 2),
            ["G", "D"],
            c =>
            {
                c.NumPorts = 2;
                c.Terminals = row switch
                {
                    "unknown-layout-pin" =>
                    [
                        new CcellTerminal { Port = 1, Name = "G", LayoutPin = ["G"] },
                        new CcellTerminal { Port = 2, Name = "D", LayoutPin = ["nowhere"] },
                    ],
                    "port-out-of-range" =>
                    [
                        new CcellTerminal { Port = 1, Name = "G", LayoutPin = ["G"] },
                        new CcellTerminal { Port = 9, Name = "D", LayoutPin = ["D"] },
                    ],
                    "duplicate-port" =>
                    [
                        new CcellTerminal { Port = 1, Name = "G", LayoutPin = ["G"] },
                        new CcellTerminal { Port = 1, Name = "D", LayoutPin = ["D"] },
                    ],
                    _ =>
                    [
                        new CcellTerminal { Port = 1, Name = "G", LayoutPin = ["G"] },
                        new CcellTerminal { Port = 2, Name = "D", LayoutPin = ["G"] },
                    ],
                };
            });

        var run = RunCli("check", cell, "--json");
        Assert.Contains("check.terminals." + row, Ids(run));
        Assert.Equal(1, run.ExitCode);
    }

    /// <summary>
    /// Gate 5's warning half and gate 6. A cell with NO block at all is reported — that is the whole
    /// point, <c>check</c> telling a user their cell cannot be compared before they ever ask for a
    /// comparison — and a warning-only run still exits 0, because a check that hid warnings to keep
    /// the exit code clean makes the exit code useless.
    /// </summary>
    [Fact]
    public void ACellWithNoBlockAtAll_IsReported_AndTheWarningCasesStillExitZero()
    {
        string guessed = Cell("Guessed", new Symbol([], [Pin(1, null), Pin(2, null)], 2), ["", ""]);
        var guessedRun = RunCli("check", guessed, "--json");
        Assert.Contains("check.terminals.derived-by-order", Ids(guessedRun));
        Assert.Equal(0, guessedRun.ExitCode);

        string stuck = Cell("Stuck", new Symbol([], [Pin(1, "G"), Pin(2, "D")], 2), ["G", "elsewhere"]);
        var stuckRun = RunCli("check", stuck, "--json");
        Assert.Contains("check.terminals.underivable", Ids(stuckRun));
        Assert.Equal(1, stuckRun.ExitCode);
    }

    /// <summary>The two coverage warnings, which are how a partly-filled map reports itself. A
    /// mounting pad no port names is EXACTLY <c>unmapped-pin</c> and is entirely ordinary.</summary>
    [Fact]
    public void AnUnmappedPortAndAnUnmappedPin_AreWarningsAndExitZero()
    {
        string cell = Cell("Partial",
            new Symbol([], [Pin(1, "G"), Pin(2, "D")], 2),
            ["G", "D", "MOUNT"],
            c =>
            {
                c.NumPorts = 2;
                c.Terminals = [new CcellTerminal { Port = 1, Name = "G", LayoutPin = ["G"] }];
            });

        var run = RunCli("check", cell, "--json");
        Assert.Contains("check.terminals.unmapped-port", Ids(run));
        Assert.Contains("check.terminals.unmapped-pin", Ids(run));
        Assert.Equal(0, run.ExitCode);
    }

    // ══ 7 — the import writes the block, and it IS the import's own table ════════════════════════

    /// <summary>
    /// R-lvs1-5a. The block equals <c>ComponentTerminals.Build</c>'s table — asserted against THAT
    /// call, not against a second derivation, which would only prove two copies of one mistake agree.
    /// </summary>
    [Fact]
    public void ComponentImportWritesTheBlock_AndItIsTheTableItAlreadyComputed()
    {
        var part = new ComponentPart { Name = "TwoPad" };
        part.Symbol = new ComponentSymbolDrawing();
        part.Symbol.Pins.Add(new ComponentSymbolPin("A", "1", 0, 0));
        part.Symbol.Pins.Add(new ComponentSymbolPin("K", "2", 0, 100));

        var footprint = new ComponentFootprint { Name = "SOD" };
        footprint.Cell.Pins.Add(NewPad("1", 0));
        footprint.Cell.Pins.Add(NewPad("2", 1_000));
        footprint.PadNames.AddRange(["1", "2"]);
        part.Footprints.Add(footprint);

        string ws = Dir("ws");
        var result = ComponentImport.Import(part, ws, destTech: null, destDbuPerMicron: 1000);
        Assert.NotNull(result.CellDir);

        var expected = ComponentTerminals.Build(part, footprint.PadNames);
        var written = CellPersistence
            .LoadFromFile(Path.Combine(result.CellDir!, CellFolder.CcellFileName)).Terminals;

        Assert.NotNull(written);
        Assert.Equal(expected.Terminals.Count, written!.Count);
        foreach (var (terminal, row) in expected.Terminals.Zip(written))
        {
            Assert.Equal(terminal.PortIndex, row.Port);
            Assert.Equal(terminal.PinName ?? terminal.PadName ?? "", row.Name);
            Assert.Equal(terminal.PadName is null ? [] : new[] { terminal.PadName }, row.LayoutPin);
        }
    }

    // ══ 8 — a generator whose pins disagree with its symbol refuses ══════════════════════════════

    /// <summary>
    /// R-lvs1-5b. Run against EVERY registered generator that has a symbol: any existing failure here
    /// would be a real defect, not a test to waive. All six built-in microstrip cells agree today.
    ///
    /// <para>The refusal itself is asserted on the same function the store calls, naming both lists —
    /// driving a genuinely mismatched generator through <c>GeneratedCellStore</c> would mean
    /// registering a bad one process-wide, which every other test in the run would then see.</para>
    /// </summary>
    [Fact]
    public void EveryRegisteredGeneratorAgreesWithItsSymbol_AndOneThatDoesNotIsRefused()
    {
        // ENUMERATED from the registry, not transcribed: a seventh generator added without a matching
        // symbol is exactly the defect this gate exists to catch, and a hand-written list would not
        // see it.
        var withSymbols = PCellRegistry.KnownGeneratorIds
            .Where(id => LayoutToSchematicGenerator.TryGetSymbolKind(id, out _))
            .ToList();
        Assert.NotEmpty(withSymbols);

        foreach (string id in withSymbols)
        {
            LayoutToSchematicGenerator.TryGetSymbolKind(id, out var kind);
            string[] symbolPins = [.. SymbolPortDefs.For(kind).Select(p => p.Name)];
            var generated = GeneratedPinNamesOf(id, symbolPins.Length);

            Assert.NotNull(TerminalMap.FromGeneratorPins(generated, symbolPins, out string? agreed));
            Assert.Null(agreed);
        }

        Assert.Null(TerminalMap.FromGeneratorPins(["in", "out"], ["1", "2"], out string? refusal));
        Assert.NotNull(refusal);
        Assert.Contains("1", refusal);
        Assert.Contains("in", refusal);
    }

    // ══ 9 — check writes nothing ════════════════════════════════════════════════════════════════

    /// <summary>
    /// R-aut4-6, asserted here because this brief adds a reader of <c>.ccell</c> and <c>.clay</c> to
    /// the verb — and a reader that "repairs" what it reads is exactly how a read-only verb stops
    /// being one.
    /// </summary>
    [Fact]
    public void CheckWritesNothing()
    {
        string cell = Cell("Untouched", new Symbol([], [Pin(1, "G")], 1), ["G"]);
        var before = Snapshot(_root);

        RunCli("check", cell, "--json");

        Assert.Equal(before, Snapshot(_root));
    }

    // ── Fixtures ─────────────────────────────────────────────────────────────

    /// <summary>A cell folder with a primary symbol and a primary layout, and whatever the caller
    /// wants said in its <c>.ccell</c>.</summary>
    private string Cell(
        string name, Symbol? symbol, IReadOnlyList<string>? layoutPins, Action<CcellFile>? ccell = null)
    {
        string cellDir = CellFolder.CreateCellFolder(Dir("ws"), name);

        if (symbol is not null)
            SymbolPersistence.SaveToFile(
                Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Symbol), name + ".csym"), symbol);

        if (layoutPins is not null)
        {
            var view = new LayoutView { DbuPerMicron = 1000 };
            for (int i = 0; i < layoutPins.Count; i++)
                view.Pins.Add(new LayoutPin { Name = layoutPins[i], X = i * 1000, Y = 0, WidthDbu = 500 });
            LayoutPersistence.SaveToFile(
                Path.Combine(CellFolder.SubFolderPath(cellDir, ViewType.Layout), name + ".clay"), view);
        }

        string path = Path.Combine(cellDir, CellFolder.CcellFileName);
        var file = CellPersistence.LoadFromFile(path);
        file.NumPorts = symbol?.Pins.Count ?? 0;
        file.Terminals = null;          // the ordinary "this cell does not say" state
        ccell?.Invoke(file);
        CellPersistence.SaveToFile(path, file);

        return cellDir;
    }

    /// <summary>The pin names <paramref name="id"/>'s generator actually produces, at its own declared
    /// defaults — the real thing, not a stand-in, because the claim under test is about what the
    /// generator emits.</summary>
    private static IReadOnlyList<string> GeneratedPinNamesOf(string id, int expectedCount)
    {
        Assert.True(PCellRegistry.TryGet(id, out var generator), id);
        var defaults = PCellRegistry.DeclaredDefaults(id) ?? new Dictionary<string, PCellValue>();
        var pins = generator!(defaults, null, PCellLayerSelection.Default).Pins;

        Assert.Equal(expectedCount, pins.Count);
        return [.. pins.Select(p => p.Name)];
    }

    private static SymbolPin Pin(int port, string? name) => new(0, port * 100, port, name);

    private static PcbImportedPin NewPad(string name, long x)
        => new(new LayoutPin { Name = name, X = x, Y = 0, WidthDbu = 500 }, "top");

    private string Dir(string name)
    {
        string d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    private static Dictionary<string, DateTime> Snapshot(string dir)
        => Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                    .ToDictionary(f => f, File.GetLastWriteTimeUtc, StringComparer.Ordinal);

    // ── The CLI, as a process ────────────────────────────────────────────────

    private static string[] Ids(CliRun run)
        => [.. JsonDocument.Parse(run.StdOut).RootElement.GetProperty("diagnostics").EnumerateArray()
                .Select(d => d.GetProperty("id").GetString() ?? "")];

    private readonly record struct CliRun(int ExitCode, string StdOut, string StdErr);

    private static CliRun RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = RepoRoot(),
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
        };
        psi.ArgumentList.Add(CliDll());
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi)!;
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        return new CliRun(proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(
                typeof(TerminalMapTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        return Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }
}
