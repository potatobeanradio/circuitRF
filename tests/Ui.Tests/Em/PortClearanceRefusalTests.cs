// PCAL2 — "a 22 dB error ships as a note; make it stop being publishable".
// `docs/sonnet-briefs/brief-portcal-2-refuse-not-warn.md`, gates 1-5.
//
// The engine half — which geometry breaches, which threshold applies, what the margin reads — is
// `Engine.Tests/Mom/PlanarFeedClearanceTests.cs`. What lives here is everything the engine cannot
// see: the two `.cem` fields and their round trip, the panel's own switches, the REFUSAL travelling
// out of `EmRunService` with no `.sNp` written, the provenance line surviving on to the file and
// back off it again, and the CLI exiting non-zero on the same decision from the same code.
//
// It runs on `testdata/portcal`, which is the series' own committed fixture pair and the geometry
// every number in PCAL1's findings was measured on.

using System.Diagnostics;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Engine.Mom;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Em;

public sealed class PortClearanceRefusalTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _results = Path.Combine(
        Path.GetTempPath(), "crf-pcal2-" + Guid.NewGuid().ToString("N")[..12], "results");

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_results)!, true); } catch { /* best effort */ }
    }

    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }

    private static string Portcal => Path.Combine(RepoRoot(), "testdata", "portcal");

    /// <summary>Loads one of the committed fixtures and resolves its layout and technology exactly
    /// as the Simulate button does.</summary>
    private static (EmSetup Setup, EmLayoutSource Source) Fixture(string name)
    {
        string cem = Path.Combine(Portcal, name, "em", name + ".cem");
        var setup  = EmSetupPersistence.LoadFromFile(cem);
        var r = EmSetupResolver.Resolve(
            cem, setup.LayoutRef, Path.Combine(Portcal, ".cws"), new TechnologyCache());
        Assert.NotNull(r.Source);
        Assert.NotNull(r.Source!.Technology);
        return (setup, r.Source!);
    }

    /// <summary>One frequency instead of seven. The clearance decision is taken at SETUP, before the
    /// first fill, so it is identical either way — and the sweep is the only part that costs.</summary>
    private static EmSetup OnePoint(EmSetup s)
    {
        var c = s.Clone();
        c.Frequency = new CircuitRF.Core.Design.FrequencySpec("1", "1", 1, CircuitRF.Core.Design.SweepKind.Linear, "GHz", "GHz");
        return c;
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 1 — a driven neighbour that cannot be CALIBRATED WITH refuses, names the port and the
    // distance, and writes no .sNp
    //
    // **The fixture moved at PCAL4 and the gate did not.** `coupled-pair` was this gate's geometry
    // until PCAL4 made it runnable — its two ports at each plane now form a CALIBRATION GROUP with
    // one modal error box, which is the whole of that brief. What still refuses, and is the
    // commoner shape on a real board, is `offset-pair`: the same two conductors 246 µm apart, with
    // the neighbour's own ports at a DIFFERENT station, so there is no one plane a shared standard
    // could be cut at. The refusal, its wording, its diagnostic id and its "writes no file" half
    // are unchanged; only the geometry that reaches it is.
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Gate1_ADrivenNeighbourThatCannotBeGroupedIsRefused_NamesPort1And246um_AndWritesNoTouchstone()
    {
        var (setup, source) = Fixture("offset-pair");
        var r = EmRunService.Run(OnePoint(setup), source, _results);

        output.WriteLine(r.Error ?? "(not refused)");
        Assert.Equal(EmRunStatus.Refused, r.Status);
        Assert.NotNull(r.Error);
        Assert.Contains("port 1", r.Error!, StringComparison.Ordinal);
        Assert.Contains("246 µm", r.Error!, StringComparison.Ordinal);
        Assert.Contains("substrate heights", r.Error!, StringComparison.Ordinal);

        // The whole finding was that the FILE outlives the note, so the file is the assertion.
        Assert.Null(r.SnpPath);
        Assert.False(Directory.Exists(_results) && Directory.GetFiles(_results, "*.s*p").Length > 0,
                     "a refused run wrote a Touchstone");

        // It is a refusal, not a crash: the diagnostic is forwarded by name, exactly as the
        // mesh-ceiling refusal's is.
        Assert.NotNull(r.Diagnostic);
        Assert.Equal("em.refused.port-clearance", r.Diagnostic!.Id);

        // PCAL4/R-pcal4-6 — the refusal carries the reason the machinery that exists to fix this
        // did not apply. A refusal discards the run's notes, so without this the user sees the
        // clearance sentence and no word about why no calibration group was formed.
        Assert.Contains("reference plane", r.Error!, StringComparison.Ordinal);
    }

    /// <summary>Gate 1's other half, and gate 5's: the CLI takes the same decision, says the same
    /// sentence and exits non-zero — from the same code, because there is no second copy of it.</summary>
    [Fact]
    public void Gate1_TheCliRefusesTheSameGeometry_AndExitsNonZero()
    {
        string repo = RepoRoot();
        string cem  = Path.Combine(Portcal, "offset-pair", "em", "offset-pair.cem");

        var (exit, stdout, stderr) = RunCli(repo, "em", cem);
        output.WriteLine($"exit {exit}\nstdout:\n{stdout}\nstderr:\n{stderr}");

        Assert.NotEqual(0, exit);
        string all = stdout + stderr;
        Assert.Contains("port 1", all, StringComparison.Ordinal);
        Assert.Contains("246 µm", all, StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 2 — the separated pair is CLEAR, and the decision is measured rather than inferred
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>R-pcal2-6: nothing that passes today changes.</b> The full proof is a byte comparison of
    /// the whole 7-point sweep against the same geometry solved by the pre-PCAL2 build, which was
    /// run by hand and recorded in `src/Engine/Mom/RESOLVED.md` — it costs ~40 s a side and there is
    /// no golden Touchstone in this repository by policy (see `testdata/portcal/README.md`).
    ///
    /// <para>What is gated here every run is the part that can regress silently: the clearance
    /// DECISION on the committed geometry, asked of the same function the solver asks, at no solve
    /// cost at all. A margin is asserted as a number so a threshold change cannot pass unnoticed.</para>
    /// </summary>
    [Fact]
    public void Gate2_TheSeparatedPairClearsBothThresholds_WithItsMarginStated()
    {
        var (setup, source) = Fixture("separated-pair");

        var x = PlanarExtractor.Extract(source.View.Shapes, source.Technology!, source.DbuPerMicron,
                                        7e9, setup.ToExtractionSettings());
        Assert.True(x.Ok, x.Refusal);

        var mesh  = SurfaceMesher.Mesh(x.Problem!, setup.PlanarMesh).Mesh;
        EmPortKindMigration.ApplyInMemory(source.View.Shapes, setup.PortKinds);
        var ports = EmPortExtraction.Extract(
            source.View.Shapes, x.Problem!, source.DbuPerMicron, setup.ResolvePortZ0,
            source.View.DisplayUnit,
            EmPortExtraction.DefaultGroundPathWidthM(source.Technology));
        Assert.True(ports.Ok, ports.Refusal);

        var cal = PlanarCalibrationSettings.Default;
        double h = x.Problem!.Slab.HeightM;
        Assert.Equal(4, ports.Ports.Count);

        var resolved = PlanarPorts.ResolveAll(mesh, ports.Ports);
        foreach (var p in resolved)
        {
            var c = PlanarPorts.MeasureFeedClearance(
                mesh, p, resolved,
                endRunM:          cal.EndRunHeights * h,
                drivenRequiredM:  cal.DrivenNeighbourClearanceHeights  * h,
                passiveRequiredM: cal.PassiveNeighbourClearanceHeights * h,
                slabHeightM:      h);
            Assert.NotNull(c);
            output.WriteLine(c!.Margin());
            Assert.False(c.Breached);

            // Its neighbour carries a port, so it is judged against the driven threshold — and it
            // clears it by ~28%, which is what makes this fixture a gate rather than a coin toss.
            Assert.Equal(PlanarNeighbourClass.Driven, c.Neighbour);
            Assert.Equal(5.0, c.RequiredHeights, 6);
            Assert.True(c.Heights > 6.0, $"port {p.Number} clears only {c.Heights:F2} h");
        }
    }

    /// <summary>Gate 2 end to end, in the opt-in tier because it is a real full-wave sweep: the
    /// committed fixture runs, publishes, and declares nothing on its file.</summary>
    [Trait("Category", "Benchmark")]
    [Fact]
    public void Gate2_TheSeparatedPairRunsCleanAndItsTouchstoneCarriesNoCaveat()
    {
        var (setup, source) = Fixture("separated-pair");
        var r = EmRunService.Run(setup, source, _results);

        foreach (string n in r.Notes!) output.WriteLine("note: " + n);
        Assert.Equal(EmRunStatus.Ok, r.Status);
        Assert.NotNull(r.SnpPath);
        Assert.Empty(EmSnpProvenance.ReadCaveats(r.SnpPath!));
        Assert.DoesNotContain(r.Notes!, n => n.Contains("OUTSIDE", StringComparison.Ordinal));
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 3 — the override publishes, and the FILE says so
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Gate3_WithTheOverrideTheFileIsWritten_AndItsProvenanceRecordsTheBreach()
    {
        var (setup, source) = Fixture("offset-pair");
        var s = OnePoint(setup);
        s.DeembedOutsideCalibrationValidity = true;

        var r = EmRunService.Run(s, source, _results);
        Assert.Equal(EmRunStatus.Ok, r.Status);
        Assert.NotNull(r.SnpPath);

        // The run says it…
        Assert.Contains(r.Notes!, n => n.Contains("OUTSIDE", StringComparison.Ordinal));

        // …and so does the artefact, which is the half that survives being opened a month later
        // somewhere else. Read back off the file, not asserted on what was written.
        // One line per fact declared, and this run declares more than one: R-pcal7-4 adds a second
        // for a row that is not a passive network, which this fixture's bottom point is. The
        // validity breach is the one this gate is about.
        var caveats = EmSnpProvenance.ReadCaveats(r.SnpPath!);
        foreach (string c in caveats) output.WriteLine(c);
        string caveat = Assert.Single(caveats, c => c.Contains("OUTSIDE", StringComparison.Ordinal));
        Assert.Contains("port 1", caveat, StringComparison.Ordinal);
        Assert.Contains("de-embedding", caveat, StringComparison.OrdinalIgnoreCase);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 4 — de-embedding is NOT optional any more, and a legacy file that asked for it says so
    //
    // This gate used to assert the opposite: that `Deembed: false` was reachable and published,
    // "and the answer says what it is". It did not say what it is. For an EDGE port the raw solve
    // is not the structure's response with a launch included, it is an OPEN CIRCUIT — the cut sits
    // one cell inside the drawn metal, so the source's outer terminal is an isolated sliver and the
    // port drives nothing but that sliver's fringing capacitance. Measured on a plain 3.8 mm x
    // 254 um microstrip: S11 = S22 = +1, S21 = -107 dB, and DOUBLING the line's length moved S11 in
    // the fourth decimal, because the raw answer carries no information about the structure at all.
    // On every other port kind de-embedding was already inert (IsDeembeddable is Edge-only), so the
    // switch's two settings were "no effect" and "an open circuit".
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// A <c>.cem</c> written when the switch existed still OPENS and still runs — refusing it would
    /// leave the user with a document they cannot open to fix — but it runs DE-EMBEDDED, and the run
    /// says so rather than quietly changing what the file asked for.
    /// </summary>
    [Fact]
    public void Gate4_ALegacyDeembedFalseFile_RunsDeembedded_AndSaysSo()
    {
        var (setup, source) = Fixture("coupled-pair");

        // The field as it appears on disk in a file written before the switch was removed.
        string json = EmSetupPersistence.Serialize(OnePoint(setup));
        json = json.TrimEnd().TrimEnd('}').TrimEnd().TrimEnd(',') + ",\n  \"Deembed\": false\n}";
        var legacy = EmSetupPersistence.Deserialize(json);
        Assert.True(legacy.LegacyRawSolveRequested);

        var r = EmRunService.Run(legacy, source, _results);
        Assert.Equal(EmRunStatus.Ok, r.Status);
        Assert.NotNull(r.SnpPath);

        string warn = Assert.Single(r.Warnings, w => w.Contains("Deembed", StringComparison.Ordinal));
        output.WriteLine(warn);
        Assert.Contains("REMOVED", warn, StringComparison.Ordinal);
        Assert.Contains("OPEN", warn, StringComparison.Ordinal);

        // It ran de-embedded, so there is no "De-embedding is OFF" note anywhere in the run…
        Assert.DoesNotContain(r.Notes!, n => n.Contains("De-embedding is OFF", StringComparison.Ordinal));

        // …and saving the setup drops the field, which is what stops it coming back.
        Assert.DoesNotContain("\"Deembed\"", EmSetupPersistence.Serialize(legacy), StringComparison.Ordinal);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The two .cem fields — omit at default, so every file on disk gains no byte
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ACemThatNeverTouchedEitherControl_GainsNoByte()
    {
        var setup = new EmSetup { Name = "hero", LayoutRef = "Amp/layout/Amp.clay" };
        string before = EmSetupPersistence.Serialize(setup);

        Assert.DoesNotContain("\"Deembed\"", before, StringComparison.Ordinal);
        Assert.DoesNotContain("DeembedOutsideCalibrationValidity", before, StringComparison.Ordinal);

        var reloaded = EmSetupPersistence.Deserialize(before);
        Assert.False(reloaded.LegacyRawSolveRequested);                // no field means nothing to warn about
        Assert.False(reloaded.DeembedOutsideCalibrationValidity);      // null means off
        Assert.Equal(before, EmSetupPersistence.Serialize(reloaded));
    }

    [Fact]
    public void TheOverrideRoundTrips_AndSurvivesClone()
    {
        var setup = new EmSetup
        {
            Name = "planar", LayoutRef = "Amp/layout/Amp.clay",
            AnalysisKind = EmAnalysisKind.Planar,
            DeembedOutsideCalibrationValidity = true,
        };

        string json = EmSetupPersistence.Serialize(setup);
        var back = EmSetupPersistence.Deserialize(json);
        Assert.True(back.DeembedOutsideCalibrationValidity);
        Assert.Equal(json, EmSetupPersistence.Serialize(back));

        // Clone drives the editor's undo snapshots; a field missing from it is silently lost on the
        // next unrelated edit. The legacy flag rides along for the same reason — an unrelated edit
        // must not be what silences the warning.
        Assert.True(setup.Clone().DeembedOutsideCalibrationValidity);
        Assert.True((new EmSetup { LegacyRawSolveRequested = true }).Clone().LegacyRawSolveRequested);
    }

    /// <summary>
    /// <b>The removed field is a deliberate, single exception to the byte-identical round trip.</b>
    /// A legacy document loads, and re-serialises WITHOUT the field — the whole point, since the
    /// field no longer has a meaning to preserve and leaving it would make the warning permanent.
    /// </summary>
    [Fact]
    public void ALegacyDeembedField_LoadsAndIsDroppedOnSave()
    {
        var setup = new EmSetup { Name = "planar", LayoutRef = "a.clay",
                                  AnalysisKind = EmAnalysisKind.Planar };
        string json = EmSetupPersistence.Serialize(setup).TrimEnd().TrimEnd('}').TrimEnd().TrimEnd(',')
                    + ",\n  \"Deembed\": false\n}";

        var back = EmSetupPersistence.Deserialize(json);
        Assert.True(back.LegacyRawSolveRequested);
        Assert.DoesNotContain("\"Deembed\"", EmSetupPersistence.Serialize(back), StringComparison.Ordinal);

        // …and `"Deembed": true` was never anything but the default, so it carries no warning.
        string on = EmSetupPersistence.Serialize(setup).TrimEnd().TrimEnd('}').TrimEnd().TrimEnd(',')
                  + ",\n  \"Deembed\": true\n}";
        Assert.False(EmSetupPersistence.Deserialize(on).LegacyRawSolveRequested);
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // The panel — both switches reach the ENGINE, which is the failure a checkbox can hide
    // ══════════════════════════════════════════════════════════════════════════════════════════

    [Fact]
    public void EmRunService_HandsBothFlagsToTheSolver()
    {
        // Asserted on the run's own behaviour rather than on the settings object, because a setting
        // the panel stores and nothing reads is the exact failure this repository has paid for
        // before (see AcceleratedSolveUiTests' header).
        var (setup, source) = Fixture("offset-pair");

        var refused = EmRunService.Run(OnePoint(setup), source, _results);
        Assert.Equal(EmRunStatus.Refused, refused.Status);

        var s1 = OnePoint(setup); s1.DeembedOutsideCalibrationValidity = true;
        Assert.Equal(EmRunStatus.Ok, EmRunService.Run(s1, source, _results).Status);

        // …and there is no second way past it any more: the de-embedding switch that used to be
        // the other escape hatch is gone, because what it escaped to was an open circuit.
    }

    [Fact]
    public void ThePanelOffersTheOverrideOnly_AndNoDeembedSwitchRemains()
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-pcal2vm-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "panel.cem");
            var setup = new EmSetup { Name = "panel", LayoutRef = "a.clay",
                                      AnalysisKind = EmAnalysisKind.Planar };
            EmSetupPersistence.SaveToFile(path, setup);

            var vm = new EmSetupEditorViewModel(path, setup);
            vm.Refresh();

            Assert.Null(vm.DeembedDisabledReason);
            Assert.True(vm.DeembedOutsideValidityEnabled);
            Assert.Null(vm.DeembedOutsideValidityDisabledReason);

            // The de-embedding checkbox is GONE, so the view model no longer carries the property
            // it bound to. Asserted structurally rather than by compiling against it, because a
            // property that comes back would compile here and silently restore the switch.
            Assert.Null(typeof(EmSetupEditorViewModel).GetProperty("Deembed"));
            Assert.Null(typeof(EmSetup).GetProperty("Deembed"));

            // …and the cross-section kernel has no calibration step at all.
            vm.AnalysisKind = EmAnalysisKind.CrossSection;
            vm.Refresh();
            Assert.NotNull(vm.DeembedDisabledReason);
            output.WriteLine(vm.DeembedDisabledReason!);
        }
        finally { try { Directory.Delete(dir, true); } catch { /* best effort */ } }
    }

    // ══════════════════════════════════════════════════════════════════════════════════════════
    // GATE 5 — ONE threshold, ONE predicate, comment-stripped, in the AuthoringCliVerbTests style
    // ══════════════════════════════════════════════════════════════════════════════════════════

    /// <summary>
    /// <b>The GUI path and the <c>circuitrf em</c> path take the same decision from the same code.</b>
    /// The structural half of that is asserted rather than described: the clearance thresholds are
    /// written down in exactly one place, the predicate is implemented in exactly one place, and
    /// neither <c>src/Cli</c> nor <c>src/Ui</c> contains a second copy of either.
    /// </summary>
    [Fact]
    public void Gate5_TheThresholdAndThePredicateExistExactlyOnce()
    {
        string src = Path.Combine(RepoRoot(), "src");

        var declares = new List<string>();
        var measures = new List<string>();
        var decides  = new List<string>();

        foreach (string f in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                           StringComparison.Ordinal)) continue;
            string code = StripComments(File.ReadAllText(f));
            string rel  = Path.GetRelativePath(src, f);

            if (code.Contains("NeighbourClearanceHeights   = 5.0", StringComparison.Ordinal) ||
                code.Contains("NeighbourClearanceHeights  = 2.0", StringComparison.Ordinal))
                declares.Add(rel);

            // The scan itself: the only place a cell's lateral gap to a port profile is computed.
            if (code.Contains("nearestDriven", StringComparison.Ordinal) ||
                code.Contains("nearestPassive", StringComparison.Ordinal))
                measures.Add(rel);

            if (code.Contains("throw new PlanarFeedClearanceRefusedException", StringComparison.Ordinal))
                decides.Add(rel);
        }

        output.WriteLine("declares: " + string.Join(", ", declares));
        output.WriteLine("measures: " + string.Join(", ", measures));
        output.WriteLine("decides : " + string.Join(", ", decides));

        Assert.Equal(["Engine/Mom/PlanarCalibration.cs"], Norm(declares));
        Assert.Equal(["Engine/Mom/PlanarPort.cs"],        Norm(measures));
        Assert.Equal(["Engine/Mom/PlanarSolve.cs"],       Norm(decides));

        // And the CLI adds nothing of its own: its `em` verb hands the `.cem` to the same
        // EmRunService the Simulate button calls, and the refusal comes back as a refusal.
        foreach (string f in Directory.EnumerateFiles(Path.Combine(src, "Cli"), "*.cs",
                                                      SearchOption.AllDirectories))
        {
            if (f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
                           StringComparison.Ordinal)) continue;
            string code = StripComments(File.ReadAllText(f));
            Assert.DoesNotContain("ClearanceHeights", code, StringComparison.Ordinal);
            Assert.DoesNotContain("MeasureFeedClearance", code, StringComparison.Ordinal);
        }
    }

    private static string[] Norm(List<string> paths)
        => [.. paths.Select(p => p.Replace(Path.DirectorySeparatorChar, '/')).Order()];

    /// <summary>Line and block comments out, string literals left alone — a threshold quoted in a
    /// message is not a second copy of it, and a threshold in a comment is not one either.</summary>
    private static string StripComments(string code)
    {
        var sb = new System.Text.StringBuilder(code.Length);
        bool inLine = false, inBlock = false, inStr = false, inChar = false, verbatim = false;
        for (int i = 0; i < code.Length; i++)
        {
            char c = code[i], n = i + 1 < code.Length ? code[i + 1] : '\0';
            if (inLine)  { if (c == '\n') { inLine = false; sb.Append(c); } continue; }
            if (inBlock) { if (c == '*' && n == '/') { inBlock = false; i++; } continue; }
            if (inStr)
            {
                sb.Append(c);
                if (verbatim) { if (c == '"' && n == '"') { sb.Append(n); i++; } else if (c == '"') inStr = false; }
                else if (c == '\\' && n != '\0') { sb.Append(n); i++; }
                else if (c == '"') inStr = false;
                continue;
            }
            if (inChar)
            {
                sb.Append(c);
                if (c == '\\' && n != '\0') { sb.Append(n); i++; }
                else if (c == '\'') inChar = false;
                continue;
            }
            if (c == '/' && n == '/') { inLine = true; continue; }
            if (c == '/' && n == '*') { inBlock = true; i++; continue; }
            if (c == '"') { inStr = true; verbatim = i > 0 && (code[i - 1] == '@' || (i > 1 && code[i - 2] == '@')); sb.Append(c); continue; }
            if (c == '\'') { inChar = true; sb.Append(c); continue; }
            sb.Append(c);
        }
        return sb.ToString();
    }

    // ── the CLI, launched the way EmCliVerbTests launches it (see its RunCli for why) ──────────
    private static (int ExitCode, string StdOut, string StdErr) RunCli(string repo, params string[] args)
    {
        string cliDir = Path.Combine(repo, "src", "Cli", "bin",
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            "net10.0");
        string dll = Path.Combine(cliDir, "CircuitRF.Cli.dll");
        Assert.True(File.Exists(dll), $"the built CLI is missing at {dll}");

        var psi = new ProcessStartInfo("dotnet") { WorkingDirectory = repo, RedirectStandardOutput = true,
                                                   RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(dll);
        foreach (string a in args) psi.ArgumentList.Add(a);

        using var p = Process.Start(psi)!;
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        p.WaitForExit(120_000);
        return (p.ExitCode, outTask.Result, errTask.Result);
    }
}
