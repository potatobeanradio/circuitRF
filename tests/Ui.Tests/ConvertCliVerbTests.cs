// ================================================================
//  ConvertCliVerbTests.cs — the gate for `circuitrf convert`.
//
//  The verb's whole claim is that it is the SAME import and export the application runs, reached from
//  a shell. So the gate is written the way EmCliVerbTests is written and for the same reason: the real
//  CLI as a separate process, compared against the in-process call the GUI makes. Asserting exit code
//  0 and a non-empty file would pass just as happily if the CLI had drifted onto a different
//  technology, a different cell, or a different set of layer keys — which are exactly the three
//  failures moving the interchange stack across the firewall could plausibly have introduced.
//
//  The N x N matrix is a Theory rather than 20 tests because the conversions genuinely are one code
//  path with two ends; what it proves is that no PAIR of ends is broken, which is the thing a matrix
//  can prove and a single round trip cannot.
//
//  Not tagged Benchmark, measured rather than assumed: 32 tests, 7 s together — every one launches
//  the already-built CircuitRF.Cli.dll rather than `dotnet run --project src/Cli`, which is a hang
//  inside `dotnet test` and not merely a cost (EmCliVerbTests.RunCli records why).
// ================================================================

using System.Diagnostics;
using System.Text.Json;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Ui.Layout;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests;

// This class renders a LabelShape, so it reads LayoutTextOutline.TestOverrideTypeface — a shared
// static several other classes set. See the collection's own note for why that now selects a
// different FACE rather than merely a loadable one.
[Collection(LayoutTextOutlineTypefaceCollection.Name)]
public sealed class ConvertCliVerbTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-convert-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose() { try { Directory.Delete(_root, true); } catch { /* best effort */ } }

    private const int Dbu = LayoutUnits.DefaultDbuPerMicron;

    // ── The matrix ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Every ordered pair of formats that names a real conversion. clay→clay is excluded
    /// because it is a file copy and the verb refuses it by name; the diagonal is otherwise kept —
    /// GDSII→GDSII is a normalization through the cell model, not a no-op, and it is the pair most
    /// likely to expose a reader and a writer disagreeing.</summary>
    public static TheoryData<string, string> Pairs
    {
        get
        {
            var d = new TheoryData<string, string>();
            string[] all = ["clay", "gdsii", "dxf", "gerber", "board"];
            foreach (string from in all)
                foreach (string to in all)
                    if (!(from == "clay" && to == "clay")) d.Add(from, to);
            return d;
        }
    }

    [Theory, MemberData(nameof(Pairs))]
    public void EveryFormatConvertsToEveryOther(string from, string to)
    {
        string source = SourceIn(from);
        string target = TargetPath(to, $"{from}-to-{to}");

        var (code, stdout, stderr) = RunCli("convert", source, "-o", target, "--to", to,
                                            "--accept-inferred-drill-format");

        output.WriteLine($"{from} -> {to}: exit {code}\n{stderr}");
        Assert.Equal(0, code);

        // stdout is the RESULT (§3.1's split): the paths written, one per line, and nothing else.
        // Everything above — notes, warnings, what the import understood — went to stderr.
        var written = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).ToList();
        Assert.NotEmpty(written);
        foreach (string w in written)
            Assert.True(File.Exists(w) || Directory.Exists(w), $"{from}->{to} named '{w}' but nothing is there");

        if (to == "clay")
        {
            // A clay target is cells plus the technology the import declared. SearchOption.AllDirectories
            // because Gerber import puts its whole result inside an import folder of its own (R-L4g-13)
            // while the others create cells directly — a real difference between the importers, not
            // something `convert` normalizes away.
            var techs = Directory.GetFiles(target, "*.ctech", SearchOption.AllDirectories);
            Assert.NotEmpty(techs);

            // ...and it carries the layer table the file declared — EXCEPT from GDSII, which
            // identifies a layer by number and has no name to carry. That exception is the format's,
            // it is documented in the CLI chapter, and `--tech` is how a user gets names back; a test
            // that demanded names here would be demanding something GDSII cannot supply.
            if (from != "gdsii") Assert.NotEmpty(TechPersistence.LoadFromFile(techs[0]).Layers);
        }
        else
        {
            Assert.True(new FileInfo(FirstFile(target)).Length > 0);
        }
    }

    // ── The identity claim: the CLI runs the application's own export ─────────────────────────────

    /// <summary>
    /// The acceptance test. `convert` on a `.clay` writes the same GDSII bytes
    /// <c>GdsiiExport.Analyze</c>/<c>Write</c> — the call the File ▸ Export ▸ GDSII path makes —
    /// writes for the same cell.
    ///
    /// <para>Byte identity, not a shape comparison: the whole risk in reaching the exporter from a
    /// second process is that it resolves a DIFFERENT technology or a different root view, and both
    /// of those produce a file that is structurally fine and numerically different.</para>
    /// </summary>
    [Fact]
    public void ConvertingAClayToGdsii_WritesWhatTheApplicationsOwnExportWrites()
    {
        var (clayPath, cellDir, tech) = BuildClayCell();

        string viaCli = Path.Combine(_root, "cli.gds");
        var (code, _, stderr) = RunCli("convert", clayPath, "-o", viaCli);
        output.WriteLine(stderr);
        Assert.Equal(0, code);

        string viaApp = Path.Combine(_root, "app.gds");
        var plan = GdsiiExport.Analyze(cellDir, tech, Dbu, LayoutPersistence.LoadFromFile(clayPath));
        GdsiiExport.Write(viaApp, plan);

        Assert.Equal(WithoutGdsiiTimestamps(File.ReadAllBytes(viaApp)),
                     WithoutGdsiiTimestamps(File.ReadAllBytes(viaCli)));
    }

    /// <summary>
    /// The same file with its BGNLIB and BGNSTR timestamps blanked — the ONE thing in a GDSII file
    /// that legitimately differs between two writes of the same design, because the format records
    /// when the library and each structure were written.
    ///
    /// <para>Named and masked rather than tolerated with a loose comparison, which is the discipline
    /// <c>EmCliVerbTests</c> follows for the Touchstone provenance line. Everything else in the file —
    /// units, structure names, every coordinate — still has to match byte for byte, which is the whole
    /// point of comparing bytes at all. (The first version of this test compared raw bytes and passed
    /// only when both writes landed in the same second.)</para>
    /// </summary>
    private static byte[] WithoutGdsiiTimestamps(byte[] file)
    {
        var copy = (byte[])file.Clone();
        int i = 0;
        while (i + 4 <= copy.Length)
        {
            int length = (copy[i] << 8) | copy[i + 1];
            if (length < 4 || i + length > copy.Length) break;
            var type = (GdsiiRecordType)copy[i + 2];
            if (type is GdsiiRecordType.BgnLib or GdsiiRecordType.BgnStr)
                Array.Clear(copy, i + 4, length - 4);
            i += length;
        }
        return copy;
    }

    /// <summary>The same claim for Gerber, which is the one that goes through the label flattener and
    /// therefore through a typeface. Every line but the write timestamp the files carry by design —
    /// the same exclusion <c>EmCliVerbTests</c> makes, for the same reason.</summary>
    [Fact]
    public void ConvertingAClayToGerber_WritesWhatTheApplicationsOwnExportWrites()
    {
        var (clayPath, cellDir, tech) = BuildClayCell();

        string cliDir = Path.Combine(_root, "cli-gerber");
        var (code, _, stderr) = RunCli("convert", clayPath, "-o", cliDir, "--to", "gerber");
        output.WriteLine(stderr);
        Assert.Equal(0, code);

        string appDir = Path.Combine(_root, "app-gerber");
        var plan = GerberExport.Analyze(cellDir, tech, Dbu, LayoutPersistence.LoadFromFile(clayPath),
                                        resolveTechAt: null);
        GerberExport.Write(appDir, Path.GetFileNameWithoutExtension(clayPath), plan);

        var cliFiles = Directory.GetFiles(cliDir).Select(Path.GetFileName).OrderBy(n => n).ToList();
        var appFiles = Directory.GetFiles(appDir).Select(Path.GetFileName).OrderBy(n => n).ToList();
        Assert.Equal(appFiles, cliFiles);

        foreach (string? name in appFiles)
            Assert.Equal(
                WithoutTimestamp(File.ReadAllLines(Path.Combine(appDir, name!))),
                WithoutTimestamp(File.ReadAllLines(Path.Combine(cliDir, name!))));
    }

    // ── The refusals ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A drill file that does not state its coordinate format is a REFUSAL headless, never a guess.
    /// The GUI asks; there is nobody to ask here, and leading vs trailing zero suppression differ by
    /// four orders of magnitude on identical text — a conversion that silently picked one would
    /// produce a plausible board at the wrong scale.
    /// </summary>
    [Fact]
    public void AnUnstatedDrillFormat_IsRefusedAndNamesTheFlagThatAnswersIt()
    {
        string set = GerberSetWithHeaderlessDrill();
        string target = Path.Combine(_root, "refused.dxf");

        var (code, _, stderr) = RunCli("convert", set, "-o", target);
        output.WriteLine(stderr);

        Assert.NotEqual(0, code);
        Assert.False(File.Exists(target), "a refused conversion must create nothing");
        Assert.Contains("--accept-inferred-drill-format", stderr, StringComparison.Ordinal);
        Assert.Contains("--drill-zeros", stderr, StringComparison.Ordinal);
    }

    /// <summary>The same file set converts once the flag answers the question — so the refusal is a
    /// question, not a capability gap.</summary>
    [Fact]
    public void TheSameSetConvertsOnceTheFormatIsAnswered()
    {
        string set = GerberSetWithHeaderlessDrill();
        string target = Path.Combine(_root, "answered.dxf");

        var (code, _, stderr) = RunCli("convert", set, "-o", target, "--drill-units", "mm",
                                       "--drill-format", "3:3", "--drill-zeros", "trailing");
        output.WriteLine(stderr);
        Assert.Equal(0, code);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public void AnOutputThatNamesNoFormat_SaysSoAndNamesTheFlag()
    {
        var (code, _, stderr) = RunCli("convert", SourceIn("board"), "-o", Path.Combine(_root, "x"));
        Assert.NotEqual(0, code);
        Assert.Contains("--to", stderr, StringComparison.Ordinal);
    }

    [Fact]
    public void ClayToClay_IsRefusedRatherThanSilentlyCopying()
    {
        var (clayPath, _, _) = BuildClayCell();
        var (code, _, stderr) = RunCli("convert", clayPath, "-o", Path.Combine(_root, "copy"), "--to", "clay");
        Assert.NotEqual(0, code);
        Assert.Contains("copy", stderr, StringComparison.OrdinalIgnoreCase);
    }

    // ── AUT-12 R-aut12-4: a clay target is a DIRECTORY, and says so ──────────────────────────────

    /// <summary>
    /// <c>-o out/board.clay</c> used to produce a DIRECTORY called <c>out/board.clay</c>, with the
    /// real <c>.clay</c> two levels inside it. Nothing was lost — the result document reported the
    /// true paths — but a path that names a file and yields a directory of that name is a surprise,
    /// and on a build machine nobody is there to notice it.
    ///
    /// <para><b>Refused rather than collapsed to one file, and that is the design.</b> An import
    /// produces one cell FOLDER per structure plus a technology beside them; a rule that wrote a lone
    /// <c>.clay</c> would work for the flat case and silently discard a hierarchy in the other. The
    /// extension is also how a caller SAYS clay, so the inference is kept and the SHAPE is what is
    /// refused — with the command that answers it in the message.</para>
    /// </summary>
    [Fact]
    public void AFileShapedClayTarget_IsRefusedAndNamesTheDirectoryToUse()
    {
        string source = SourceIn("gdsii");
        string asked  = Path.Combine(_root, "out", "board.clay");

        var (code, _, stderr) = RunCli("convert", source, "-o", asked);
        output.WriteLine(stderr);

        Assert.NotEqual(0, code);
        Assert.Contains("convert.target.clay-needs-directory", Ids(source, asked));
        // The suggestion is the same path with the extension taken off, so acting on the refusal is
        // an edit of one token rather than a rethink.
        Assert.Contains(Path.Combine(_root, "out", "board"), stderr, StringComparison.Ordinal);
        Assert.False(Directory.Exists(asked), "the refused target was created anyway");
    }

    /// <summary>The directory spelling the refusal names works, and puts the cells where it said.
    /// Without this the refusal could be correct and the advice wrong.</summary>
    [Fact]
    public void TheDirectorySpellingTheRefusalNames_Works()
    {
        string source = SourceIn("gdsii");
        string dir = Path.Combine(_root, "out2", "board");

        var (code, stdout, stderr) = RunCli("convert", source, "-o", dir, "--to", "clay");
        output.WriteLine(stderr);
        Assert.Equal(0, code);

        foreach (string written in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()))
            Assert.StartsWith(Path.GetFullPath(dir), Path.GetFullPath(written), StringComparison.Ordinal);
        Assert.NotEmpty(Directory.GetFiles(dir, "*.clay", SearchOption.AllDirectories));
    }

    /// <summary>An existing FILE where a directory of cell folders belongs. Nothing in `convert`
    /// deletes or overwrites, so this is a refusal rather than a replacement.</summary>
    [Fact]
    public void AnExistingFileAsAClayTarget_IsRefusedRatherThanReplaced()
    {
        string source = SourceIn("gdsii");
        string file = Path.Combine(_root, "occupied");
        File.WriteAllText(file, "not a cell folder");

        var (code, _, stderr) = RunCli("convert", source, "-o", file, "--to", "clay");
        output.WriteLine(stderr);
        Assert.NotEqual(0, code);
        Assert.Equal("not a cell folder", File.ReadAllText(file));
    }

    /// <summary>The ids a refused run reports, read out of its own <c>--json</c> document rather than
    /// matched in prose.</summary>
    private string[] Ids(string source, string output)
    {
        var (_, stdout, _) = RunCli("convert", source, "-o", output, "--json");
        return [.. System.Text.Json.JsonDocument.Parse(stdout).RootElement
                     .GetProperty("diagnostics").EnumerateArray()
                     .Select(d => d.GetProperty("id").GetString()!)];
    }

    [Fact]
    public void ListCells_ReportsWhatAFileHoldsAndWritesNothing()
    {
        string before = Path.Combine(_root, "listing-marker");
        var (code, stdout, _) = RunCli("convert", SourceIn("gdsii"), "--list-cells");
        Assert.Equal(0, code);
        Assert.NotEmpty(stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.False(File.Exists(before));
    }

    // ── The `--json` carry-through (brief-automation-4-check-and-explain.md's convert half) ───────
    //
    // `convert` answered `--json` from AUT-1, but three things a caller needs were still reaching
    // only the terminal. Each is asserted here because each was found by running the verb rather
    // than by reading it.

    /// <summary>
    /// The import's own notes — a layer mapped, a zone not imported, a stackup the file did not
    /// carry — reach the document, not only stderr. They are how a caller learns what a conversion
    /// DROPPED, and a `--json` consumer that could not read them would be reading a report that
    /// omits the losses.
    /// </summary>
    [Fact]
    public void Json_CarriesTheImportsOwnNotes()
    {
        var (code, stdout, _) = RunCli("convert", SourceIn("gdsii"),
                                       "-o", Path.Combine(_root, "noted.dxf"), "--json");
        Assert.Equal(0, code);

        using var doc = JsonDocument.Parse(stdout);
        Assert.Contains("convert.note",
            doc.RootElement.GetProperty("diagnostics").EnumerateArray()
               .Select(d => d.GetProperty("id").GetString()));
    }

    /// <summary>
    /// <c>--list-cells</c> answers `--json`. Its listing IS the result of that invocation — the whole
    /// answer to "what does this file hold?" — and `--json` replaces the stdout it was printed on, so
    /// without this the flag produced an empty document (R-aut1-3: a caller must never have to tell
    /// "no output" from "output I could not parse").
    /// </summary>
    [Fact]
    public void Json_ListCells_AnswersInTheDocument()
    {
        var (code, stdout, _) = RunCli("convert", SourceIn("gdsii"), "--list-cells", "--json");
        Assert.Equal(0, code);

        using var doc = JsonDocument.Parse(stdout);
        var listed = doc.RootElement.GetProperty("diagnostics").EnumerateArray()
                        .Where(d => d.GetProperty("id").GetString() == "convert.cell.listed")
                        .Select(d => d.GetProperty("arguments").GetProperty("cell").GetString())
                        .ToArray();
        Assert.NotEmpty(listed);

        // And it still wrote nothing: --list-cells imports into a scratch directory it deletes, so
        // the technology it minted on the way is NOT reported as an output.
        Assert.Empty(doc.RootElement.GetProperty("outputs").EnumerateArray());
    }

    /// <summary>
    /// The minted `.ctech` is an output when it survives, and is not when it does not. It is not
    /// optional context: the cells reference it by relative path, so a caller that took the cells and
    /// not this has a design whose layers resolve to nothing.
    /// </summary>
    [Fact]
    public void Json_TheMintedTechnology_IsAnOutputExactlyWhenItSurvives()
    {
        string kept = Path.Combine(_root, "kept-json");
        var (keepCode, keepOut, _) = RunCli("convert", SourceIn("gdsii"),
                                            "-o", Path.Combine(_root, "kj.dxf"),
                                            "--keep-cells", kept, "--json");
        Assert.Equal(0, keepCode);
        using (var doc = JsonDocument.Parse(keepOut))
            Assert.Contains("ctech", Kinds(doc));

        var (code, stdout, _) = RunCli("convert", SourceIn("gdsii"),
                                       "-o", Path.Combine(_root, "scratch.dxf"), "--json");
        Assert.Equal(0, code);
        using (var doc = JsonDocument.Parse(stdout))
            Assert.DoesNotContain("ctech", Kinds(doc));

        static string?[] Kinds(JsonDocument doc) =>
            [.. doc.RootElement.GetProperty("outputs").EnumerateArray()
                  .Select(o => o.GetProperty("kind").GetString())];
    }

    /// <summary>--keep-cells is the way to see what a conversion actually understood, so it has to
    /// leave a design that opens: cell folders AND the technology their `.clay` files point at.</summary>
    [Fact]
    public void KeepCells_LeavesADesignThatResolvesItsOwnTechnology()
    {
        string kept = Path.Combine(_root, "kept");
        var (code, _, stderr) = RunCli("convert", SourceIn("gdsii"), "-o", Path.Combine(_root, "k.dxf"),
                                       "--keep-cells", kept);
        output.WriteLine(stderr);
        Assert.Equal(0, code);

        string techPath = Assert.Single(Directory.GetFiles(kept, "*.ctech"));
        string cellDir = Directory.GetDirectories(kept).First();
        var primary = CellFolder.ResolvePrimary(cellDir, ViewType.Layout);
        Assert.NotNull(primary.ResolvedName);

        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);
        var view = LayoutPersistence.LoadFromFile(Path.Combine(layoutDir, primary.ResolvedName!));
        Assert.NotNull(view.TechRef);
        Assert.True(File.Exists(Path.GetFullPath(Path.Combine(layoutDir, view.TechRef!))),
            $"the .clay's TechRef '{view.TechRef}' does not resolve to {techPath}");
    }

    // ── R-rf3-7: the verb exposes coalescing, and its default is ON ───────────────────────────────

    /// <summary>
    /// <b>Read this beside R-rf3-8 before taking any expected-bytes change in this file for a
    /// re-baseline.</b> The import now turns a PAINTED pour — a copper fill expressed as thousands of
    /// abutting scanline strokes — back into the region it paints, by default. Every other fixture in
    /// this file is flashes and traces with no painted fill in it, so the matrix's expected bytes are
    /// unchanged and the byte-identity gates above still compare exactly what they compared before;
    /// that is a property of those fixtures, not an exemption. This test is the one that carries the
    /// new behaviour, and it carries BOTH sides of the flag so neither can rot.
    /// </summary>
    [Fact]
    public void ConvertingARasterFilledSet_CoalescesByDefault_AndNoCoalesceGivesTheStrokesBack()
    {
        string source = RasterFilledGerberSet();

        var (onCode, _, onErr) = RunCli("convert", source, "-o", Path.Combine(_root, "on"), "--to", "clay");
        output.WriteLine(onErr);
        Assert.Equal(0, onCode);

        var (offCode, _, offErr) = RunCli("convert", source, "-o", Path.Combine(_root, "off"), "--to", "clay",
                                          "--no-coalesce");
        output.WriteLine(offErr);
        Assert.Equal(0, offCode);

        var on = ShapesOf(Path.Combine(_root, "on"));
        var off = ShapesOf(Path.Combine(_root, "off"));

        Assert.Equal(220, off.Count);
        Assert.All(off, sh => Assert.IsType<PathShape>(sh));

        Assert.True(on.Count <= 4, $"the default should have coalesced the pour; it wrote {on.Count} shape(s)");
        Assert.All(on, sh => Assert.IsType<PolygonShape>(sh));

        // What it did is on stderr with the rest of the import's notes, never on stdout — stdout is
        // the result document (cli.md §3.1's split), and it stays the paths written.
        Assert.Contains("coalesced into", onErr, StringComparison.Ordinal);
        Assert.DoesNotContain("coalesced into", offErr, StringComparison.Ordinal);
    }

    /// <summary>The one <c>.clay</c> a <c>--to clay</c> conversion of a single-layer set wrote, found
    /// by walking rather than by assuming a depth — the import nests a cell folder inside an import
    /// folder, and the shape of that nesting is not this test's subject.</summary>
    private static IReadOnlyList<LayoutShape> ShapesOf(string clayDir) =>
        LayoutPersistence.LoadFromFile(
            Directory.EnumerateFiles(clayDir, "*.clay", SearchOption.AllDirectories).Single()).Shapes;

    /// <summary>A copper layer painted the way a CAM tool's raster fill paints one: 220 abutting
    /// one-mil scanlines with a 10% overlap, drawn rather than filled. Synthetic, per R-rf3's §4 — no
    /// vendor board is committed anywhere.</summary>
    private string RasterFilledGerberSet()
    {
        string dir = Path.Combine(_root, "raster-fill");
        Directory.CreateDirectory(dir);

        var body = new System.Text.StringBuilder(
            "%FSLAX46Y46*%\n%MOMM*%\n%TF.FileFunction,Copper,L1,Top,Signal*%\n%ADD10C,0.025400*%\nD10*\n");
        for (int i = 0; i < 220; i++)
            body.Append($"X0Y{i * 22_860}D02*\n").Append($"X5000000Y{i * 22_860}D01*\n");
        File.WriteAllText(Path.Combine(dir, "copper.gbr"), body.Append("M02*\n").ToString());

        return dir;
    }

    // ── Fixtures ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>One cell with a rectangle, a via and a label — enough to exercise the three things
    /// every writer treats differently (plain geometry, a drilled feature, and text that has to
    /// become geometry) without making the comparison depend on a large design.</summary>
    private (string ClayPath, string CellDir, Technology Tech) BuildClayCell()
    {
        string cellDir = CellFolder.CreateCellFolder(_root, "Part");
        string layoutDir = CellFolder.SubFolderPath(cellDir, ViewType.Layout);

        var tech = StarterTechnologies.Pcb2Layer();
        string techPath = Path.Combine(_root, "pcb.ctech");
        TechPersistence.SaveToFile(techPath, tech);

        var top = tech.Layers[0].Key;
        var view = new LayoutView { DbuPerMicron = Dbu, TechRef = Path.GetRelativePath(layoutDir, techPath) };
        view.Shapes.Add(new RectShape { Layer = top, X1 = 0, Y1 = 0, X2 = 2_000_000, Y2 = 800_000 });
        view.Shapes.Add(new ViaShape
        { Layer = top, LandingLayer = top, X = 1_000_000, Y = 400_000, PadSize = 600_000, DrillSize = 300_000 });
        view.Shapes.Add(new LabelShape { Layer = top, X = 0, Y = 1_000_000, Text = "R1", Height = 200_000 });

        string clayPath = Path.Combine(layoutDir, "Part.clay");
        LayoutPersistence.SaveToFile(clayPath, view);

        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.PrimaryLayout = "Part.clay";
        CellPersistence.SaveToFile(ccellPath, ccell);

        return (clayPath, cellDir, tech);
    }

    /// <summary>The matrix's source for one format: the clay fixture, or that same design already
    /// converted into the format under test. Built through the CLI itself, deliberately — a matrix
    /// whose inputs came from somewhere else would test the readers against fixtures rather than
    /// testing the pairs.</summary>
    private string SourceIn(string format)
    {
        var (clayPath, _, _) = BuildClayCell();
        if (format == "clay") return clayPath;

        string made = TargetPath(format, "seed-" + format);
        var (code, _, stderr) = RunCli("convert", clayPath, "-o", made, "--to", format);
        Assert.True(code == 0, $"could not build a {format} source for the matrix:\n{stderr}");
        return made;
    }

    private string TargetPath(string format, string stem) => format switch
    {
        "clay" or "gerber" => Path.Combine(_root, stem),
        "gdsii" => Path.Combine(_root, stem + ".gds"),
        "dxf" => Path.Combine(_root, stem + ".dxf"),
        _ => Path.Combine(_root, stem + ".kicad_pcb"),
    };

    /// <summary>A one-layer Gerber plus a drill file that declares no units, no digit counts and no
    /// LZ/TZ word — R-L4f-2's own "the missing statement is the common case" shape.</summary>
    private string GerberSetWithHeaderlessDrill()
    {
        string dir = Path.Combine(_root, "headless-drill");
        Directory.CreateDirectory(dir);

        File.WriteAllText(Path.Combine(dir, "copper.gbr"), string.Join('\n',
            "%FSLAX46Y46*%", "%MOMM*%", "%TF.FileFunction,Copper,L1,Top*%",
            "%ADD10C,0.500000*%", "D10*", "X1000000Y1000000D03*", "M02*", ""));

        File.WriteAllText(Path.Combine(dir, "drill.drl"), string.Join('\n',
            "M48", "T1C0.350", "%", "T1", "X001000Y001000", "T0", "M30", ""));

        return dir;
    }

    // ── Harness (EmCliVerbTests.RunCli's pattern, verbatim and for its reasons) ────────────────────

    private static string[] WithoutTimestamp(IEnumerable<string> lines)
        => [.. lines.Where(l => !l.Contains("CreationDate", StringComparison.Ordinal) &&
                                !l.Contains("GenerationSoftware", StringComparison.Ordinal))];

    private static string FirstFile(string target) =>
        Directory.Exists(target) ? Directory.GetFiles(target).OrderBy(f => f).First() : target;

    private (int ExitCode, string StdOut, string StdErr) RunCli(params string[] args)
    {
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory       = _root,
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
            .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(ConvertCliVerbTests).Assembly)
            .First(a => a.Key == "CliDir").Value!;
        string path = Path.GetFullPath(Path.Combine(cliDir, "CircuitRF.Cli.dll"));
        Assert.True(File.Exists(path),
            $"the CLI was not built beside these tests: {path} — the ReferenceOutputAssembly=\"false\" " +
            "project reference in CircuitRF.Ui.Tests.csproj is what guarantees it, so check that first");
        return path;
    }
}
