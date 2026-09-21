// ================================================================
//  LvsCliVerbTests.cs — the gate for brief-lvs-11-cli-verb.md §6.
//
//  ── WHAT THE WHOLE FILE IS ABOUT ──────────────────────────────────────────────────────────────
//
//  One claim, stated fourteen ways: `circuitrf lvs` is a COMMAND LINE onto `LvsRun.Run` and
//  nothing else. Gate 1 is the one the brief exists to pass — the verb run as a PROCESS and the
//  in-process call agree finding for finding, in one order — and the rest say that the argument
//  surface around it refuses what it cannot answer, writes only when asked, and puts the
//  reduction mode and the technology on the face of every answer.
//
//  ── WHY SO FEW PROCESS LAUNCHES ───────────────────────────────────────────────────────────────
//
//  Each `dotnet CircuitRF.Cli.dll` costs ~1 s of start-up, so the gates that are about the VERB's
//  answer rather than about the process boundary run in process through `CliEntry.Run`. The three
//  that genuinely need a second process — gate 1's identity claim, `serve`, and the exit codes a
//  caller actually observes — launch one. That split is deliberate: a same-process call cannot
//  show a difference only a second process can have, and it also cannot hide one.
//
//  ── THE FIXTURES ──────────────────────────────────────────────────────────────────────────────
//
//  `examples/LVS` (brief 5) and TWO bounded mutations of a throwaway copy of it, each made here
//  because the committed fixture has no case for it:
//    · a floating island of top copper  → warnings and no errors, for gates 3 and 4;
//    · R3's value re-pointed at a global that is not declared → gate 12's `--set`.
//  Nothing is ever written inside the repository: every mutation is made on a copy.
// ================================================================

using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using CircuitRF.Cli;
using CircuitRF.Design.Layout.Lvs;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Lvs;

public sealed class LvsCliVerbTests(ITestOutputHelper output) : IDisposable
{
    private const string Correct = "Attenuator";
    private const string Broken  = "Attenuator broken";
    private const string Mmic    = "Bias tee";

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "crf-lvs11-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a temp folder that outlives the run is not a failure */ }
    }

    // ══ gate 1 — the verb run as a PROCESS equals the in-process call ════════════════════════════
    //
    // R-lvs11-1d, and it is the gate the whole brief exists to pass. The GUI panel (brief 12) calls
    // the same function with the same arguments, so what this pins is that the command line adds
    // nothing and drops nothing on the way through: same findings, same ids, same objects, same
    // ORDER. An answer that agreed on the set and not the order would be one two surfaces present
    // differently, which is the same defect in a quieter costume.

    [Fact]
    public void TheVerbRunAsAProcess_ReportsExactlyWhatLvsRunReports()
    {
        string cell = Lvs(Broken);

        var direct = LvsRun.Run(cell);
        var (exit, stdout, stderr) = RunCli("lvs", cell, "--json");
        output.WriteLine(stderr);

        Assert.Equal(1, exit);
        var cells = Payload(stdout).GetProperty("cells");
        var one   = Assert.Single(cells.EnumerateArray().ToList());

        var viaProcess = one.GetProperty("findings").EnumerateArray()
            .Select(f => (Id: f.GetProperty("id").GetString(),
                          Objects: string.Join("|", f.GetProperty("objects").EnumerateArray()
                                                     .Select(o => o.GetString()))))
            .ToList();

        var inProcess = direct.Findings
            .Select(f => (Id: (string?)f.Id, Objects: string.Join("|", f.Objects)))
            .ToList();

        Assert.Equal(inProcess, viaProcess);

        // Not vacuous: the six-fault board has something to say, and the run-level lines are in
        // there too — a comparison of two empty lists would pass every assertion above.
        Assert.True(inProcess.Count >= 9, $"only {inProcess.Count} findings were compared");

        // The counts and the technology travel with them (R-lvs8-1a/1c) — the half a findings list
        // cannot carry, and the half that says WHICH process the artwork was read against.
        Assert.Equal(direct.TechnologyName, one.GetProperty("technology").GetString());
        Assert.Equal(direct.ErrorCount,     one.GetProperty("errors").GetInt32());
        Assert.Equal(direct.Counts.Layout.NetsAfter,
                     one.GetProperty("layout").GetProperty("netsAfter").GetInt32());
    }

    // ══ gate 2 — the correct board is clean; the six-fault board names all six ═══════════════════

    [Fact]
    public void TheCorrectBoardExitsZeroWithNothingAboveInfo()
    {
        Assert.Equal(0, InProcess("lvs", Lvs(Correct), "--json"));

        var lvs = Payload(LastDocument);
        Assert.True(lvs.GetProperty("clean").GetBoolean());
        Assert.Equal(0, lvs.GetProperty("errors").GetInt32());
        Assert.Equal(0, lvs.GetProperty("warnings").GetInt32());
    }

    [Fact]
    public void TheSixFaultBoardExitsOneAndNamesAllSix()
    {
        Assert.Equal(1, InProcess("lvs", Lvs(Broken), "--json"));

        var ids = Findings(Payload(LastDocument))
            .Where(f => f.GetProperty("severity").GetString() != "info")
            .Select(f => f.GetProperty("id").GetString())
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            ["lvs.device.unmatched-layout", "lvs.device.unmatched-schematic",
             "lvs.net.open", "lvs.net.short", "lvs.property.mismatch", "lvs.terminal.wrong-net"],
            ids);
    }

    // ══ gates 3 and 4 — warnings are reported either way, and only the threshold decides ═════════
    //
    // R-lvs11-4b. A run that hid its warnings to keep the exit code clean makes the exit code
    // useless, which is `check`'s own rule for its own reason. The fixture is a single bounded
    // mutation of the CORRECT board — one rectangle of top copper nothing lands on, which is what
    // an unstitched pour reads as — so the warning is the only thing that changed.

    [Fact]
    public void AWarningOnlyRun_ExitsZeroAndStillPrintsIt()
    {
        string cell = FloatingCopperBoard();

        Assert.Equal(0, InProcess("lvs", cell, "--json"));
        var lvs = Payload(LastDocument);

        Assert.Equal(0, lvs.GetProperty("errors").GetInt32());
        Assert.Equal(1, lvs.GetProperty("warnings").GetInt32());
        Assert.False(lvs.GetProperty("clean").GetBoolean());
        Assert.Contains(Findings(lvs), f => f.GetProperty("id").GetString() == "lvs.net.floating-copper");
    }

    [Fact]
    public void SeverityWarning_FlipsTheSameRunToOneAndChangesNothingElse()
    {
        string cell = FloatingCopperBoard();

        Assert.Equal(0, InProcess("lvs", cell, "--json"));
        var lenient = LastDocument;

        Assert.Equal(1, InProcess("lvs", cell, "--severity", "warning", "--json"));
        var strict = LastDocument;

        // The findings are the same findings. What the flag changes is the threshold the exit code
        // was decided at — which is carried, because a document holding warnings and exitCode 0 is
        // only readable next to the threshold that made it so.
        Assert.Equal(Findings(Payload(lenient)).Select(Line), Findings(Payload(strict)).Select(Line));
        Assert.Equal("error",   Payload(lenient).GetProperty("severity").GetString());
        Assert.Equal("warning", Payload(strict).GetProperty("severity").GetString());

        static string Line(JsonElement f)
            => f.GetProperty("id").GetString() + " " + f.GetProperty("message").GetString();
    }

    // ══ gate 5 — every document kind this verb can answer for resolves ══════════════════════════
    //
    // R-lvs11-2a. A `.clay` and a `.csch` name the CELL FOLDER that holds them, because the unit of
    // comparison is the cell (note R-lvs-29) — so all four of these are the same one comparison
    // reached by four spellings, and asserting that is the point.

    [Theory]
    [InlineData("cell")]
    [InlineData("clay")]
    [InlineData("csch")]
    public void EachDocumentKindResolvesToTheSameComparison(string spelling)
    {
        string path = spelling switch
        {
            "clay" => Path.Combine(Lvs(Correct), "layout",    Correct + ".clay"),
            "csch" => Path.Combine(Lvs(Correct), "schematic", Correct + ".csch"),
            _      => Lvs(Correct),
        };

        Assert.Equal(0, InProcess("lvs", path, "--json"));
        var lvs = Payload(LastDocument);

        Assert.Equal(1, lvs.GetProperty("compared").GetInt32());
        Assert.True(lvs.GetProperty("clean").GetBoolean());
        Assert.Equal(Correct, lvs.GetProperty("cells")[0].GetProperty("name").GetString());
    }

    /// <summary>The workspace arm: every cell holding both views, and the ones holding one reported
    /// and skipped (R-lvs11-2b). Three designs and nine parts, which is what `examples/LVS` is.</summary>
    [Fact]
    public void AWorkspaceComparesEveryCellThatHasBothViews()
    {
        Assert.Equal(1, InProcess("lvs", Lvs(), "--json"));   // the broken board is in there
        var lvs = Payload(LastDocument);

        Assert.Equal("workspace", lvs.GetProperty("kind").GetString());
        Assert.Equal(3, lvs.GetProperty("compared").GetInt32());
        Assert.Equal(9, lvs.GetProperty("skipped").GetInt32());

        var compared = lvs.GetProperty("cells").EnumerateArray()
            .Where(c => c.GetProperty("compared").GetBoolean())
            .Select(c => c.GetProperty("name").GetString())
            .Order(StringComparer.Ordinal);
        Assert.Equal([Correct, Broken, Mmic], compared.Order(StringComparer.Ordinal));
    }

    // ══ gate 6 — one view is INFO and skipped, and a workspace of those exits 0 ══════════════════

    [Fact]
    public void ACellWithOnlyOneViewIsInfoAndSkipped_AndAWorkspaceOfThoseExitsZero()
    {
        // `examples/LVS/footprints` is exactly this: four land patterns, layout only. Copied into a
        // workspace of its own so the comparison has nothing else to find.
        string ws = Path.Combine(_root, "parts-only");
        Copy(Lvs("footprints"), ws);
        File.WriteAllText(Path.Combine(ws, ".cws"), File.ReadAllText(Path.Combine(Lvs(), ".cws")));

        Assert.Equal(0, InProcess("lvs", ws, "--json"));
        var lvs = Payload(LastDocument);

        Assert.Equal(0, lvs.GetProperty("compared").GetInt32());
        Assert.Equal(4, lvs.GetProperty("skipped").GetInt32());
        Assert.True(lvs.GetProperty("clean").GetBoolean());

        // Info, and it SAYS which view is missing — the ordinary mid-design state, not a refusal.
        foreach (var cell in lvs.GetProperty("cells").EnumerateArray())
        {
            var note = Assert.Single(cell.GetProperty("findings").EnumerateArray().ToList());
            Assert.Equal("lvs.scope.view-missing", note.GetProperty("id").GetString());
            Assert.Equal("info", note.GetProperty("severity").GetString());
            Assert.Equal("schematic", note.GetProperty("arguments").GetProperty("view").GetString());
        }
    }

    // ══ gate 7 — a document kind this verb cannot answer for is a refusal BY KIND ════════════════
    //
    // R-lvs11-2c, and it is `render`'s own rule: the refusal NAMES what the path is, because
    // "circuitRF cannot read this" and "circuitRF reads this and lvs does not compare it" are two
    // different answers and only one of them tells the caller what to do next. The GDSII arm goes
    // through `convert`'s own content classifier rather than a second table here.

    [Fact]
    public void ACemAGdsiiAndAPlainFolderAreRefusalsByKind()
    {
        string gds = Path.Combine(_root, "exported.gds");
        Assert.Equal(0, RunCli("convert", Path.Combine(Lvs(Correct), "layout", Correct + ".clay"),
                               "-o", gds).ExitCode);

        foreach (var (path, kind) in new[]
                 {
                     (Path.Combine(RepoRoot(), "testdata", "antenna", "patch", "em", "patch-5p8GHz.cem"), "em-setup"),
                     (gds,                "GDSII"),
                     (Lvs("footprints"),  "folder"),
                 })
        {
            Assert.Equal(1, InProcess("lvs", path, "--json"));
            var d = JsonDocument.Parse(LastDocument).RootElement;

            // A refusal is not a finding (R-lvs11-4c): there is no `lvs` payload at all, so a
            // caller cannot read the run as one that concluded something with a note attached.
            Assert.False(d.TryGetProperty("result", out var r) && r.TryGetProperty("lvs", out _));

            var refusal = Assert.Single(d.GetProperty("diagnostics").EnumerateArray().ToList());
            Assert.Equal("lvs.path.not-comparable", refusal.GetProperty("id").GetString());
            Assert.Equal(kind, refusal.GetProperty("arguments").GetProperty("kind").GetString());
        }
    }

    // ══ gate 8 — nothing is written with no -o ══════════════════════════════════════════════════
    //
    // R-lvs11-3d. LVS is read-only on `check`'s terms (R-aut4-6), so it runs on a read-only tree and
    // on a workspace another process has open. Asserted by MTIME over a copy of the whole workspace,
    // which catches a re-save that happened to write identical bytes.

    [Fact]
    public void WithNoOutputNothingIsWritten_AndWithOneExactlyTheReportIs()
    {
        string ws = Path.Combine(_root, "read-only");
        Copy(Lvs(), ws);

        var before = Stamps(ws);
        Assert.Equal(1, InProcess("lvs", ws));
        Assert.Equal(before, Stamps(ws));

        string report = Path.Combine(_root, "report.txt");
        Assert.Equal(1, InProcess("lvs", ws, "-o", report));
        Assert.Equal(before, Stamps(ws));

        string text = File.ReadAllText(report);
        Assert.Contains("Attenuator broken:", text, StringComparison.Ordinal);
        Assert.Contains("3 cell(s) compared, 9 skipped:", text, StringComparison.Ordinal);
    }

    // ══ gate 9 — cancellation exits 130 and writes nothing ══════════════════════════════════════
    //
    // Through the same `RunControl` `em` and `render` use, installed by `RunHost` — which is what
    // `serve` installs in production, so this is the real path rather than a stand-in for it.

    [Fact]
    public void ACancelledRun_Exits130AndWritesNothing()
    {
        string report = Path.Combine(_root, "cancelled.txt");

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using (RunHost.Install(cts.Token, observer: null))
            Assert.Equal(130, InProcess("lvs", Lvs(Correct), "-o", report));

        Assert.False(File.Exists(report), "a cancelled comparison published a report");
    }

    /// <summary>The vacuity guard: the same call without a cancelled token writes the file, so the
    /// 130 above is the cancellation and not a refusal that happens to share its shape.</summary>
    [Fact]
    public void TheSameRunWithNoCancellation_WritesIt()
    {
        string report = Path.Combine(_root, "not-cancelled.txt");
        Assert.Equal(0, InProcess("lvs", Lvs(Correct), "-o", report));
        Assert.True(File.Exists(report));
    }

    // ══ gate 10 — the typed arguments, read without touching the sentence ═══════════════════════
    //
    // R-lvs11-3c. The id is the contract and the sentence is not, so a caller reads the layer and
    // the coordinate as VALUES. The MMIC is the fixture that carries both: its spiral reads as a
    // short through a via at a measured point, and its ground comes from a stackup entry that draws
    // no layer at all.

    [Fact]
    public void JsonCarriesTypedArguments_AConductorAndACoordinate()
    {
        Assert.Equal(1, InProcess("lvs", Lvs(Mmic), "--json"));
        var findings = Findings(Payload(LastDocument)).ToList();

        var shortFinding = Assert.Single(findings, f => f.GetProperty("id").GetString() == "lvs.net.short");
        var a = shortFinding.GetProperty("arguments");
        Assert.Equal(365_000, a.GetProperty("x").GetInt64());
        Assert.Equal(465_000, a.GetProperty("y").GetInt64());
        Assert.Equal( 10_000, a.GetProperty("widthDbu").GetInt64());

        // The conductor, as a value: the stackup entry the ground reference came from, which is the
        // one layer on this technology that draws nothing and so cannot be read off the artwork.
        var ground = Assert.Single(findings,
            f => f.GetProperty("id").GetString() == "lvs.ground.reference-undrawn");
        Assert.Equal("Backside Metal", ground.GetProperty("arguments").GetProperty("stackupEntry").GetString());
        Assert.Equal(2, ground.GetProperty("arguments").GetProperty("vias").GetInt32());

        // And where to look — the marker, in the layout's own DBU, four numbers rather than a
        // sentence a panel would have to parse back apart.
        Assert.Equal(4, shortFinding.GetProperty("marker").GetArrayLength());
    }

    // ══ gate 11 — --no-reduce and --flat change what was read, never the verdict ═════════════════
    //
    // Brief 6 R-lvs6-5c and brief 9 R-lvs9-4b. The MODE is on the face of BOTH outputs — the human
    // report and the document — which is the requirement; a result whose reduction mode is not
    // stated is one two people can read differently.
    //
    // MEASURED, and the brief's own wording does not survive it: on `examples/LVS` neither flag
    // changes a COUNT, because nothing on either board collapses and the MMIC's only sub-cells are
    // leaf parts. What both flags do change is what the run says it did, and that is what is pinned
    // here rather than a count difference the fixture cannot produce.

    [Theory]
    [InlineData("--no-reduce", "OFF")]
    [InlineData("--flat",      "ON")]
    public void TheModeIsOnTheFaceOfBothOutputs_AndTheVerdictIsUnchanged(string flag, string reduction)
    {
        Assert.Equal(0, InProcess("lvs", Lvs(Correct), flag, "--json"));
        var lvs = Payload(LastDocument);

        Assert.True(lvs.GetProperty("clean").GetBoolean());
        Assert.Equal(flag == "--flat",      lvs.GetProperty("flat").GetBoolean());
        Assert.Equal(flag != "--no-reduce", lvs.GetProperty("reduce").GetBoolean());
        Assert.Equal(reduction == "ON" ? "on" : "off",
                     lvs.GetProperty("cells")[0].GetProperty("reduction").GetString());

        // Both sides say it, every run — one line per document, never one for the pair.
        Assert.Equal(2, Findings(lvs).Count(f => f.GetProperty("id").GetString() == "lvs.reduce.mode"));

        var (exit, stdout, _) = RunCli("lvs", Lvs(Correct), flag);
        Assert.Equal(0, exit);
        Assert.Contains($"reduction {(reduction == "ON" ? "on" : "off")}", stdout, StringComparison.Ordinal);
    }

    // ══ gate 12 — --set reaches elaboration ═════════════════════════════════════════════════════
    //
    // R-lvs11-2d. The fixture re-points R3's value at a global the design does not declare, so the
    // three answers are distinguishable and none of them is ambiguous: with no override the
    // elaboration FAILS and says so, with one value the board matches, with another it reports the
    // property that differs. That is the override landing in the scope rather than past it.

    [Fact]
    public void SetReachesElaboration_AndTheSameBoardComparesDifferentlyUnderTwoValues()
    {
        string cell = ParametricBoard();

        Assert.Equal(1, InProcess("lvs", cell, "--json"));
        Assert.Contains(Findings(Payload(LastDocument)),
                        f => f.GetProperty("id").GetString() == "lvs.schematic.elaboration-failed");

        Assert.Equal(0, InProcess("lvs", cell, "--set", "Rshunt=294", "--json"));
        Assert.True(Payload(LastDocument).GetProperty("clean").GetBoolean());

        Assert.Equal(1, InProcess("lvs", cell, "--set", "Rshunt=150", "--json"));
        var mismatch = Assert.Single(Findings(Payload(LastDocument)),
            f => f.GetProperty("id").GetString() == "lvs.property.mismatch");
        Assert.Equal("150 Ω", mismatch.GetProperty("arguments").GetProperty("schematic").GetString());
        Assert.Equal("294 Ω", mismatch.GetProperty("arguments").GetProperty("layout").GetString());
    }

    // ══ gate 13 — there is no second comparison in src/Cli ══════════════════════════════════════
    //
    // R-lvs11-1b, and it is the assertion the whole design rests on: a verb that re-implements a
    // capability diverges from it silently, and the first symptom is a design that passes headlessly
    // and is refused when someone opens it. COMMENTS ARE STRIPPED FIRST — this file's own prose
    // names every symbol involved, and so does `Lvs.cs`'s, so a scan of raw text would pass on a
    // comment (project-brief-harmonicarf-h8 records that trap).

    [Fact]
    public void NoSecondComparisonLivesInTheCli()
    {
        string code = string.Concat(
            Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Cli"), "*.cs",
                                     SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.Ordinal)
                     .Select(f => StripComments(File.ReadAllText(f))));

        foreach (string call in new[]
                 {
                     "LvsCompare.", "LvsReduce.Apply", "LvsReport.Build", "LvsProperties.",
                     "LayoutRead.Read", "SchematicRead.Read", "LvsShortPath.", "LvsMarker.",
                     "new LvsHierarchyContext", "CopperPieces.Build",
                 })
            Assert.DoesNotContain(call, code, StringComparison.Ordinal);

        // And the one call that IS here is exactly one. Two would be two sets of defaults.
        Assert.Equal(1, Regex.Matches(code, @"LvsRun\.Run\(").Count);
    }

    // ══ gate 14 — serve answers `lvs` with the same payload as --json ═══════════════════════════
    //
    // R-lvs11-5a/5b: the tool falls out of the verb with no second implementation. It matters more
    // here than almost anywhere else on that surface, because an agent that authored a `.clay`
    // cannot look at the screen — asking whether the artwork implements the drawing is the only way
    // it can find out, and what comes back is typed findings it can act on.

    [Fact]
    public void ServeAnswersLvs_WithTheSamePayloadAsTheFlag()
    {
        string ws = Path.Combine(_root, "served");
        Copy(Lvs(), ws);
        string cell = Path.Combine(ws, Broken);

        string viaFlag = Payload(RunCli("lvs", cell, "--json").StdOut).GetRawText();

        using var serve = new ServeDriver(CliDll(), RepoRoot(), ws);
        string viaTool = serve.Call("lvs", new JsonObject { ["path"] = cell });

        Assert.Equal(viaFlag,
                     JsonDocument.Parse(viaTool).RootElement
                         .GetProperty("result").GetProperty("lvs").GetRawText());
    }

    // ── fixtures ─────────────────────────────────────────────────────────────

    /// <summary>The correct board with ONE extra island of top copper nothing lands on — which is
    /// what a pour somebody forgot to stitch reads as, and the only warning-without-error the
    /// example set can produce.</summary>
    private string FloatingCopperBoard()
    {
        string cell = Path.Combine(_root, "floating", Correct);
        Copy(Lvs(), Path.Combine(_root, "floating"));

        string clay = Path.Combine(cell, "layout", Correct + ".clay");
        var doc = JsonNode.Parse(File.ReadAllText(clay))!.AsObject();
        doc["Shapes"]!.AsArray().Add(new JsonObject
        {
            ["$type"] = "Rect",
            ["X1"] = 16_000_000, ["Y1"] = 7_000_000,
            ["X2"] = 17_000_000, ["Y2"] = 7_500_000,
            ["Layer"] = new JsonObject { ["Layer"] = 1, ["Datatype"] = 0 },
        });
        File.WriteAllText(clay, doc.ToJsonString());
        return cell;
    }

    /// <summary>The correct board with R3's value re-pointed at an UNDECLARED global, so the three
    /// answers gate 12 wants are distinguishable.</summary>
    private string ParametricBoard()
    {
        string cell = Path.Combine(_root, "parametric", Correct);
        Copy(Lvs(), Path.Combine(_root, "parametric"));

        string csch = Path.Combine(cell, "schematic", Correct + ".csch");
        var doc = JsonNode.Parse(File.ReadAllText(csch))!.AsObject();
        foreach (var component in doc["Components"]!.AsArray())
        {
            if (component!["InstanceName"]?.GetValue<string>() != "R3") continue;
            foreach (var p in component["Parameters"]!.AsArray())
                if (p!["Name"]?.GetValue<string>() == "R") p["Expression"] = "Rshunt";
        }
        File.WriteAllText(csch, doc.ToJsonString());
        return cell;
    }

    private static string Lvs(params string[] parts)
        => Path.Combine([RepoRoot(), "examples", "LVS", .. parts]);

    private static void Copy(string from, string to)
    {
        foreach (string file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    /// <summary>Every file under a tree, with its size and last-write time — what "wrote nothing"
    /// means, stated so that a re-save of identical bytes still fails.</summary>
    private static string Stamps(string root)
        => string.Join("\n", Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)
            .Select(f => $"{Path.GetRelativePath(root, f)} {new FileInfo(f).Length} " +
                         File.GetLastWriteTimeUtc(f).Ticks));

    // ── plumbing ─────────────────────────────────────────────────────────────

    /// <summary>The document the last <see cref="InProcess"/> call wrote.</summary>
    private string LastDocument = "";

    /// <summary>
    /// One invocation through <see cref="CliEntry.Run"/>, with stdout captured.
    /// </summary>
    /// <remarks>
    /// In process because each of these gates is about the VERB's answer rather than about the
    /// process boundary, and a launch costs ~1 s of start-up apiece. The boundary itself is gate 1's
    /// and gate 14's business, and both of those launch a real one.
    /// </remarks>
    private int InProcess(params string[] args)
    {
        var real = Console.Out;
        var buffer = new StringWriter();
        try
        {
            Console.SetOut(buffer);
            JsonRun.Reset();
            int exit = CliEntry.Run(args);
            LastDocument = buffer.ToString();
            return exit;
        }
        finally { Console.SetOut(real); }
    }

    private static JsonElement Payload(string document)
        => JsonDocument.Parse(document).RootElement.GetProperty("result").GetProperty("lvs");

    private static IEnumerable<JsonElement> Findings(JsonElement lvs)
        => lvs.GetProperty("cells").EnumerateArray()
              .SelectMany(c => c.GetProperty("findings").EnumerateArray());

    /// <summary>Line and block comments removed, so a source scan cannot be satisfied by prose.</summary>
    private static string StripComments(string code)
        => Regex.Replace(Regex.Replace(code, @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    private static (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
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
        return (proc.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    private static string CliDll()
    {
        string cliDir = System.Reflection.CustomAttributeExtensions
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(LvsCliVerbTests).Assembly)
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

    /// <summary>
    /// One <c>circuitrf serve</c> process, driven over its own pipes — newline-delimited JSON-RPC,
    /// which is what the protocol's stdio transport is.
    /// </summary>
    /// <remarks>
    /// A small driver of its own rather than a share of <c>ServeProtocolAdapterTests</c>': that
    /// file's session holds two pump threads, a frame queue and a disconnect protocol because it
    /// tests the ADAPTER, and none of that is needed to ask one tool one question.
    /// </remarks>
    private sealed class ServeDriver : IDisposable
    {
        private readonly Process _proc;

        public ServeDriver(string dll, string workingDirectory, string root)
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory       = workingDirectory,
                RedirectStandardInput  = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            psi.ArgumentList.Add(dll);
            psi.ArgumentList.Add("serve");
            psi.ArgumentList.Add("--root");
            psi.ArgumentList.Add(root);
            _proc = Process.Start(psi)!;

            Request("initialize", new JsonObject());
        }

        public string Call(string tool, JsonObject arguments)
            => JsonNode.Parse(Request("tools/call",
                   new JsonObject { ["name"] = tool, ["arguments"] = arguments }))!
               ["content"]![0]!["text"]!.GetValue<string>();

        private int _id;

        private string Request(string method, JsonObject parameters)
        {
            int id = ++_id;
            _proc.StandardInput.Write(new JsonObject
            {
                ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters,
            }.ToJsonString());
            _proc.StandardInput.Write('\n');
            _proc.StandardInput.Flush();

            // Notifications — progress, in particular — arrive on the same stream, so the answer is
            // the first frame carrying THIS id and not simply the next line.
            while (_proc.StandardOutput.ReadLine() is { } line)
            {
                if (JsonNode.Parse(line) is not JsonObject frame) continue;
                if (frame["id"]?.GetValue<int>() != id) continue;
                Assert.Null(frame["error"]);
                return frame["result"]!.ToJsonString();
            }
            throw new InvalidOperationException($"serve never answered '{method}'");
        }

        public void Dispose()
        {
            try { _proc.StandardInput.Close(); _proc.WaitForExit(5_000); } catch (IOException) { }
            try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _proc.Dispose();
        }
    }
}
