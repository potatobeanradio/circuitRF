using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Em3d.Install;
using CircuitRF.Design.Em3d.Wsl;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-em3d-26 §5 — Palace in the Linux subsystem, gates 1-8, plus R-em3d26-3d's removal.
//
//  Everything runs on every OS against FakeWsl below: wsl.exe is abstracted behind IWsl, the fake
//  records every argument list it is handed and maps each distribution's Linux filesystem onto a
//  local directory, so a test reads back exactly what would have been asked of a real distribution.
//  Nothing here starts wsl.exe. The runs on real Windows are the owner's (§6).
// ══════════════════════════════════════════════════════════════════════════════════════════════

public sealed class WslLocationTests : IDisposable
{
    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "crf-wsl-" + Guid.NewGuid().ToString("N")[..10]);

    public WslLocationTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // ── 1. the UTF-16 listing ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate1_TheListingIsDecodedAsUtf16_AndReadAsUtf8ItFindsNothing()
    {
        byte[] bytes = File.ReadAllBytes(Fixture("wsl-l-v.utf16le.bin"));

        var list = WslDistributions.Parse(bytes);
        Assert.NotNull(list);
        Assert.Equal([("Ubuntu", "Running", 2, true), ("Debian", "Stopped", 1, false), ("Ubuntu-24.04", "Stopped", 2, false)],
                     list!.Select(d => (d.Name, d.State, d.Version, d.IsDefault)).ToList());

        // The control: the same bytes as UTF-8 are a name interleaved with NULs, and no parser finds one.
        // ORDINAL: a culture-aware comparison ignores NUL, so it "finds" Ubuntu in U\0b\0u\0n\0t\0u — xUnit's own
        // DoesNotContain(string, string) did exactly that on the first run of this gate.
        string asUtf8 = Encoding.UTF8.GetString(bytes);
        Assert.False(asUtf8.Contains("Ubuntu", StringComparison.Ordinal));
        Assert.DoesNotContain(WslDistributions.ParseText(asUtf8) ?? [], d => d.Name == "Ubuntu");
    }

    // ── 2. discovery inside ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate2_InsideADistributionSpackOutranksPath_AndTheFirstDistributionListedWins()
    {
        var wsl = Fake("  NAME STATE VERSION\r\n* Alpha Running 2\r\n");
        string spackPalace = SpackPalace(wsl, "Alpha");
        OnPath(wsl, "Alpha", "/usr/local/bin/palace");

        var found = Discovery(wsl).Find(out var rejected);
        Assert.True(found is not null, string.Join("; ", rejected));
        Assert.Equal((SolverHowFound.Spack, "Alpha", spackPalace), (found!.HowFound, found.Distribution, found.Path));
        Assert.True(found.Validated);
        Assert.Contains("'Alpha'", SolverDiscovery.Create(SolverTool.Palace).DescribeForSettings(found, []));   // the row names it

        // Two distributions, each with a validated Palace: the one `wsl -l` lists first is taken.
        wsl = Fake("  NAME STATE VERSION\r\n  Beta Stopped 2\r\n* Alpha Running 2\r\n");
        OnPath(wsl, "Alpha", "/usr/local/bin/palace");
        OnPath(wsl, "Beta", "/usr/local/bin/palace");
        Assert.Equal("Beta", Discovery(wsl).Find(out _)?.Distribution);
    }

    // ── 3. paths ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate3_PathsCrossOnceEachWay_WithSpacesNonAsciiAndAnotherDrive()
    {
        Assert.Equal(@"\\wsl.localhost\Ubuntu\home\user\My Runs\Ωmega\model.msh",
                     WslPaths.ToWindows("Ubuntu", "/home/user/My Runs/Ωmega/model.msh"));

        var wsl = Fake("* Ubuntu Running 2\r\n");
        var session = new WslSession(wsl, "Ubuntu");
        const string dDrive = @"D:\Mé projets\run 1\model.msh";
        Assert.Equal("/mnt/d/Mé projets/run 1/model.msh", session.ToLinux(dDrive, out _));
        // wslpath is handed the path as ONE argument, untouched — no quoting, no hand-built /mnt string —
        Assert.Contains(wsl.Commands, c => c.SequenceEqual(["-d", "Ubuntu", "--exec", "wslpath", "-u", dDrive]));
        // — and asked once per path per session.
        int asked = wsl.Commands.Count;
        Assert.Equal("/mnt/d/Mé projets/run 1/model.msh", session.ToLinux(dDrive, out _));
        Assert.Equal(asked, wsl.Commands.Count);

        // Linux → Windows (the share) → Linux is the identity.
        const string linux = "/home/user/My Runs/Ωmega";
        Assert.Equal(linux, session.ToLinux(WslPaths.ToWindows("Ubuntu", linux), out _));
    }

    // ── 4. the staging plan ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate4_OnlyTheMeshAndConfigGoIn_OnlyTheCsvsAndRequestedFieldsComeOut_AndNothingNamesAMountedDrive()
    {
        string runDir = Path.Combine(_tmp, "run dir");
        Directory.CreateDirectory(runDir);
        File.WriteAllText(Path.Combine(runDir, "model.msh"), "$MeshFormat");
        File.WriteAllText(Path.Combine(runDir, "model.geo"), "// stays on this side");

        var plan = WslPalaceRunner.Plan("/home/user", runDir);
        Assert.Equal(["model.msh", "config.json"], plan.CopyIn);
        Assert.StartsWith("/home/user/.circuitrf/runs/", plan.LinuxRunDirectory);
        // The fields are not among the results: they come after the results are published (Gate4c).
        Assert.Equal(["iteration1/palace.json", "palace.json", "port-S.csv", "probe-E.csv"],
                     WslPalaceRunner.CopyOut(["port-S.csv", "probe-E.csv", "palace.json", "iteration1/palace.json",
                                              "iteration1/mesh.msh", "fields/E.pvtu", "fields/E.csv", "other/E.pvtu"], ["fields"]));

        // The run itself, against the fake: what was staged when Palace started, and what came back.
        var wsl = Fake("* Ubuntu Running 2\r\n");
        string[]? stagedAtStart = null;
        wsl.OnStart = (distro, cwd, _) =>
        {
            string local = wsl.Local(distro, cwd!);
            stagedAtStart = [.. Directory.EnumerateFiles(local).Select(Path.GetFileName).Order().OfType<string>()];
            foreach (var (file, text) in new[] { ("postpro/port-S.csv", "f (GHz)"), ("postpro/palace.json", "{}"),
                                                ("postpro/iteration1/palace.json", "{}"), ("postpro/iteration1/mesh.msh", "big") })
            {
                string path = Path.Combine(local, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, text);
            }
        };
        using (var runner = new WslPalaceRunner(new WslSession(wsl, "Ubuntu"), "/home/user", null, "none"))
        {
            var step = runner.Solve(runDir, "{}", "/home/user/palace/bin/palace", 1, null, CancellationToken.None,
                                    out _, null, 0);
            Assert.True(step.Ok, step.Message);
        }
        Assert.Equal(["circuitrf.pid", "config.json", "model.msh"], stagedAtStart!.Where(f => f != null).Order());
        Assert.True(File.Exists(Path.Combine(runDir, "postpro", "port-S.csv")));
        Assert.True(File.Exists(Path.Combine(runDir, "postpro", "iteration1", "palace.json")));
        Assert.False(File.Exists(Path.Combine(runDir, "postpro", "iteration1", "mesh.msh")));
        Assert.False(Directory.Exists(wsl.Local("Ubuntu", plan.LinuxRunDirectory)));   // scratch space, removed
        Assert.DoesNotContain(wsl.Commands.Concat(wsl.Started), c => c.Any(WslPaths.IsMountedWindowsDrive));
    }

    /// <summary>Designer feedback round 11 — the runner a field-saving setup builds copies the fields back. Nothing set
    /// the runner's field directories, so every run in the subsystem left its fields behind and the Field plot said
    /// the run saved none.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gate4b_TheFieldsASetupSavesComeBack_AndNoneWhenItSavesNone(bool savesNone)
    {
        string runDir = Path.Combine(_tmp, "run dir");
        Directory.CreateDirectory(runDir);
        File.WriteAllText(Path.Combine(runDir, "model.msh"), "$MeshFormat");
        var wsl = Fake("* Ubuntu Running 2\r\n");
        wsl.OnStart = (distro, cwd, _) =>
        {
            string local = wsl.Local(distro, cwd!);
            foreach (string file in new[] { "postpro/port-S.csv", "postpro/paraview/driven/driven.pvd" })
            {
                string path = Path.Combine(local, file);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, "x");
            }
        };
        var settings = PalaceSettings.Resolve(new CircuitRF.Design.Layout.Em.CemPalace { SaveFieldsGHz = savesNone ? [] : null });
        var palace = new SolverInstallation(SolverTool.Palace, "/home/user/palace/bin/palace", "0.18.1", null, "",
                                            SolverHowFound.Path, "", null, "Ubuntu");
        using (var runner = WslPalace.Runner(wsl, palace, PalaceConfigWriter.FieldDirectories(settings), out string? refusal))
        {
            Assert.True(runner is not null, refusal);
            Assert.Equal(savesNone ? [] : ["paraview"], runner!.FieldDirectories);
            Assert.True(runner.Solve(runDir, "{}", palace.Path, 1, null, CancellationToken.None, out _, null, 0).Ok);
            Assert.Equal(!savesNone, runner.FieldsPending(runDir));
            runner.BringFields(runDir, null, CancellationToken.None);
        }
        Assert.True(File.Exists(Path.Combine(runDir, "postpro", "port-S.csv")));
        Assert.Equal(!savesNone, File.Exists(Path.Combine(runDir, "postpro", "paraview", "driven", "driven.pvd")));
    }

    /// <summary>A WSL 8-port run's Touchstone appeared 23 minutes after Palace finished: the saved fields (gigabytes) were
    /// copied file by file through the share before the results were read, under a stage still saying "solving". The solve
    /// now brings back only the results; the fields follow, after publishing, as one tar stream counted in MB, and the
    /// share copy takes over when that stream is not a well-formed archive.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Gate4c_TheFieldsComeAfterTheResults_AsOneCountedStream_OrFileByFileWhenTheStreamFails(bool tarBroken)
    {
        var (wsl, runDir) = FieldRun();
        wsl.TarBroken = tarBroken;
        var stages = new List<(string Stage, long Done, long Total, string Unit)>();
        var control = new CircuitRF.Engine.RunControl
        {
            Progress = new SyncProgress(p => stages.Add((p.Stage, p.StageCompleted, p.StageTotal, p.StageUnit))),
            MinReportIntervalMs = 0,
        };
        string field = Path.Combine(runDir, "postpro", "paraview", "driven", "Cycle000001", "proc000000.vtu");
        PalaceFieldCopy copied;
        using (var runner = new WslPalaceRunner(new WslSession(wsl, "Ubuntu"), "/home/user", null, "none") { FieldDirectories = ["paraview"] })
        {
            Assert.True(runner.Solve(runDir, "{}", "/home/user/palace/bin/palace", 1, control, CancellationToken.None, out _, null, 0).Ok);
            Assert.True(File.Exists(Path.Combine(runDir, "postpro", "port-S.csv")));
            Assert.False(File.Exists(field));                                   // the results are readable with no field here yet
            Assert.Contains(stages, s => s.Stage == WslPalaceRunner.ResultsCopyLabel);
            Assert.True(runner.FieldsPending(runDir));

            copied = runner.BringFields(runDir, control, CancellationToken.None);
            Assert.False(runner.FieldsPending(runDir));
        }
        Assert.Null(copied.Warning);
        Assert.Contains(tarBroken ? "file by file" : "as one stream", copied.Note);
        Assert.Equal(3 << 20, new FileInfo(field).Length);
        Assert.True(File.Exists(Path.Combine(runDir, "postpro", "paraview", "driven", "driven.pvd")));
        var last = stages.Last(s => s.Stage == WslPalaceRunner.FieldsCopyLabel);
        Assert.Equal(("MB", 4L, 4L), (last.Unit, last.Done, last.Total));    // 3 MB + a few bytes, rounded up
        Assert.Contains(wsl.Started, c => c.SkipWhile(a => a != "--exec").Skip(1).Take(4).SequenceEqual(["tar", "-cf", "-", "--"]));
    }

    /// <summary>The reader stops at tar's end-of-archive marker, but tar pads its output to a whole record and a Windows pipe holds
    /// about 4 KB: the rest of the stream must still be read, or wsl.exe blocks on its write and the copy never ends.</summary>
    [Fact]
    public void Gate4e_TheStreamIsReadToItsEnd_PastTheEndOfArchiveMarker()
    {
        var (wsl, runDir) = FieldRun();
        wsl.TarTrailing = true;
        using var runner = new WslPalaceRunner(new WslSession(wsl, "Ubuntu"), "/home/user", null, "none") { FieldDirectories = ["paraview"] };
        Assert.True(runner.Solve(runDir, "{}", "/home/user/palace/bin/palace", 1, null, CancellationToken.None, out _, null, 0).Ok);

        var bring = Task.Run(() => runner.BringFields(runDir, null, CancellationToken.None));
        Assert.True(bring.Wait(TimeSpan.FromSeconds(60)), "the field copy did not end");
        Assert.Null(bring.Result.Warning);
        Assert.Contains("as one stream", bring.Result.Note);
        Assert.Equal(3 << 20, new FileInfo(Path.Combine(runDir, "postpro", "paraview", "driven", "Cycle000001", "proc000000.vtu")).Length);
    }

    /// <summary>A run stopped while its fields come over keeps its results and leaves no partial field folder, which the 3D
    /// view would otherwise read as the run's fields.</summary>
    [Fact]
    public void Gate4d_AFieldCopyStoppedPartWayLeavesNoFieldFolder_AndSaysSo()
    {
        var (wsl, runDir) = FieldRun();
        using var runner = new WslPalaceRunner(new WslSession(wsl, "Ubuntu"), "/home/user", null, "none") { FieldDirectories = ["paraview"] };
        Assert.True(runner.Solve(runDir, "{}", "/home/user/palace/bin/palace", 1, null, CancellationToken.None, out _, null, 0).Ok);

        var copied = runner.BringFields(runDir, null, new CancellationToken(canceled: true));
        Assert.Contains("stopped", copied.Warning);
        Assert.False(Directory.Exists(Path.Combine(runDir, "postpro", "paraview")));
        Assert.True(File.Exists(Path.Combine(runDir, "postpro", "port-S.csv")));
    }

    /// <summary>A run directory and a fake distribution whose Palace writes port-S.csv and a 3 MB saved field.</summary>
    private (FakeWsl Wsl, string RunDir) FieldRun()
    {
        string runDir = Path.Combine(_tmp, "run dir");
        Directory.CreateDirectory(runDir);
        File.WriteAllText(Path.Combine(runDir, "model.msh"), "$MeshFormat");
        var wsl = Fake("* Ubuntu Running 2\r\n");
        wsl.OnStart = (distro, cwd, _) =>
        {
            string post = Path.Combine(wsl.Local(distro, cwd!), "postpro");
            Directory.CreateDirectory(Path.Combine(post, "paraview", "driven", "Cycle000001"));
            File.WriteAllText(Path.Combine(post, "port-S.csv"), "f (GHz)");
            File.WriteAllText(Path.Combine(post, "paraview", "driven", "driven.pvd"), "<VTKFile/>");
            var bytes = new byte[3 << 20];
            new Random(1).NextBytes(bytes);
            File.WriteAllBytes(Path.Combine(post, "paraview", "driven", "Cycle000001", "proc000000.vtu"), bytes);
        };
        return (wsl, runDir);
    }

    private sealed class SyncProgress(Action<CircuitRF.Engine.RunProgress> report) : IProgress<CircuitRF.Engine.RunProgress>
    {
        public void Report(CircuitRF.Engine.RunProgress value) => report(value);
    }

    // ── 5. memory ───────────────────────────────────────────────────────────────────────────────

    private const long GiB = 1L << 30;

    /// <summary>Designer feedback round 11 — the reporter's own figures: a 3.8 GB subsystem (WSL's default half)
    /// on an 8 GB laptop, a run needing 4.2 GB. The suggestion is worked out from the host (it was a fixed
    /// "memory=24GB", three times the memory there was), and the path is the row's LINK, not more text.</summary>
    [Fact]
    public void Gate5_TheSubsystemsMemoryTriggersTheWarning_WhichSuggestsWhatThisComputerCanGive_AndLinksDotWslconfig()
    {
        var wsl = Fake("* Ubuntu Running 2\r\n");
        wsl.MemoryBytes = 3_800_000_000;
        var scope = WslPalace.MemoryScope(new WslSession(wsl, "Ubuntu"), @"C:\Users\someone", hostBytes: 8 * GiB);
        Assert.Equal(3_800_000_000, scope!.Bytes);
        Assert.Contains(wsl.Commands, c => c.SequenceEqual(["-d", "Ubuntu", "--exec", "free", "-b"]));

        var verdict = Em3dMemoryVerdict.Evaluate(4_200_000_000, scope.Bytes, [], scope: scope);
        Assert.Equal(Em3dMemoryLevel.Warning, verdict.Level);
        Assert.Contains("the Linux subsystem's 3.8 GB", verdict.Text);
        Assert.Contains("'memory=6GB' under '[wsl2]' in .wslconfig", verdict.Text);
        Assert.Equal(@"C:\Users\someone\.wslconfig", verdict.File);
        Assert.DoesNotContain(@"C:\Users", verdict.Text);
    }

    /// <summary>The setting is offered only where it can make the run fit; otherwise the sentence says it
    /// cannot, and names no file to open.</summary>
    [Theory]
    [InlineData(4.2,    3.8, 8,  "'memory=6GB'",                 true)]
    [InlineData(20.0,   3.8, 8,  "would not be enough",          false)]   // past what the host can give
    [InlineData(6.5,    7.0, 8,  "already has most",             false)]   // memory= already raised
    [InlineData(20.0,   16,  32, "'memory=24GB'",                true)]
    public void Gate5b_TheWslconfigRemedyIsOfferedOnlyWhereItCanBeEnough(double estimateGb, double subsystemGb, int hostGib,
                                                                         string says, bool namesTheFile)
    {
        var r = WslPalace.WslConfigRemedy((long)(estimateGb * 1e9), (long)(subsystemGb * 1e9), hostGib * GiB, @"C:\Users\someone");
        Assert.Contains(says, r.Text);
        Assert.Equal(namesTheFile, r.File is not null);
    }

    // ── 6. cancellation ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate6_CancellingSignalsTheWrappersProcessGroup_TermThenKill()
    {
        const string pidFile = "/home/user/.circuitrf/runs/k/circuitrf.pid";
        Assert.Equal(["setsid", "-w", "sh", "-c", WslProcessGroup.WrapperScript, pidFile, "palace", "config.json"],
                     WslProcessGroup.Wrap(pidFile, ["palace", "config.json"]));

        var wsl = Fake("* Ubuntu Running 2\r\n");
        var session = new WslSession(wsl, "Ubuntu");
        Directory.CreateDirectory(Path.GetDirectoryName(wsl.Local("Ubuntu", pidFile))!);
        File.WriteAllText(wsl.Local("Ubuntu", pidFile), "4242\n");

        wsl.GroupAlive = true;   // ignores TERM
        WslProcessGroup.Stop(session, pidFile, TimeSpan.FromSeconds(1), _ => { });
        var kills = wsl.Commands.Where(c => c.Contains("kill")).Select(c => c.Skip(3).ToArray()).ToList();
        Assert.Equal(["kill", "-TERM", "--", "-4242"], kills[0]);
        Assert.Equal(["kill", "-KILL", "--", "-4242"], kills[^1]);

        wsl.Commands.Clear();
        wsl.GroupAlive = false;  // ends on TERM
        WslProcessGroup.Stop(session, pidFile, TimeSpan.FromSeconds(1), _ => { });
        Assert.DoesNotContain(wsl.Commands, c => c.Contains("-KILL"));
    }

    // ── 7. preconditions ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate7_EachPreconditionIsRefusedWithItsOwnStep_AndNothingIsAttempted()
    {
        var refusals = new List<string>();

        var none = Fake("");
        none.Available = false;
        refusals.Add(Refusal(none));
        Assert.Contains("wsl --install", refusals[^1]);

        var noDistro = Fake("Windows Subsystem for Linux has no installed distributions.\r\n", exit: -1);
        refusals.Add(Refusal(noDistro));
        Assert.Contains("wsl --install -d Ubuntu", refusals[^1]);

        var noVm = Fake("* Ubuntu Stopped 2\r\n");
        noVm.CannotStart["Ubuntu"] = "Please enable the Virtual Machine Platform Windows feature and ensure virtualization " +
                                     "is enabled in the BIOS.\r\nError code: Wsl/Service/CreateInstance/CreateVm/HCS/0x80370102";
        refusals.Add(Refusal(noVm));
        Assert.Contains("firmware", refusals[^1]);

        // A distribution without the build tools: the recipe's own prerequisite check, run inside, refuses
        // with the ONE apt line before any step starts.
        var bare = Fake("* Ubuntu Running 2\r\n");
        var plan = WslPalaceInstall.Plan(bare, null, null, _tmp, out string? why);
        Assert.True(plan is not null, why);
        var outcome = plan!.Installer.Install(plan.Recipe);
        Assert.Equal(InstallStatus.Refused, outcome.Status);
        refusals.Add(outcome.Report);
        Assert.Contains("sudo apt-get install build-essential", refusals[^1]);
        // Field report: the line was typed at a Windows prompt and failed with 'command not found'. The
        // refusal names WHERE it runs, and a stale package index (the 404s) is refreshed first.
        Assert.Contains("inside the Linux distribution 'Ubuntu'", refusals[^1]);
        Assert.Contains("wsl -d Ubuntu", refusals[^1]);
        Assert.Contains("sudo apt-get update && ", refusals[^1]);

        Assert.Equal(refusals.Count, refusals.Distinct().Count());
        foreach (var fake in new[] { none, noDistro, noVm, bare })
        {
            Assert.Empty(fake.Started);
            Assert.DoesNotContain(fake.Commands, c => c.Intersect(["mkdir", "ln", "mv", "rm"]).Any());
        }
    }

    // ── 8. WSL 1 ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Gate8_AWsl1DistributionIsRefusedWithTheConversionCommand()
    {
        var wsl = Fake("  NAME STATE VERSION\r\n* Legacy Running 1\r\n");
        Assert.Contains("wsl --set-version Legacy 2", Refusal(wsl));

        Assert.Null(Discovery(wsl).Find(out var rejected));
        Assert.Contains(rejected, r => r.Contains("wsl --set-version Legacy 2"));
        Assert.DoesNotContain(wsl.Commands, c => c.Contains("Legacy") && c.Contains("--exec"));   // never started
    }

    // ── R-em3d26-3d: removal goes through the distribution, and waits for it to start ──────────────

    [Fact]
    public void Uninstall_RemovesTheLinuxHomeByRenameThenDelete_ThenTheMirror_AndIsRefusedWhileTheDistributionCannotStart()
    {
        var wsl = Fake("* Ubuntu Running 2\r\n");
        const string home = "/home/user/.circuitrf/solvers/palace/0.18.1";
        Directory.CreateDirectory(wsl.Local("Ubuntu", home + "/opt/bin"));
        File.WriteAllText(wsl.Local("Ubuntu", home + "/opt/bin/palace"), "");
        var record = new InstallRecord
        {
            Tool = SolverTool.Palace, Version = "0.18.1", Recipe = "palace-0.18.1-linux-x64", Home = home,
            Program = home + "/opt/bin/palace", Distribution = "Ubuntu", SizeBytes = 123, InstalledAt = DateTimeOffset.Now,
        };
        WslSolverHomes.WriteMirror(_tmp, record);
        string mirror = WslSolverHomes.MirrorDirectory(_tmp, record);
        var uninstaller = new SolverUninstaller([_tmp]) { Subsystem = wsl };

        var plan = uninstaller.PlanOne(SolverTool.Palace);
        Assert.True(plan.CanProceed, plan.Refusal);
        Assert.False(plan.Homes.Single().Missing);
        Assert.Contains("stays", plan.Confirmation);   // the distribution is the user's

        wsl.CannotStart["Ubuntu"] = "The distribution failed to start.";
        var refused = uninstaller.Remove(plan);
        Assert.Equal(RemovalStatus.Refused, refused.Status);
        Assert.Contains("'Ubuntu'", refused.Report);
        Assert.True(Directory.Exists(wsl.Local("Ubuntu", home)) && Directory.Exists(mirror));

        wsl.CannotStart.Clear();
        var removed = uninstaller.Remove(plan);
        Assert.Equal(RemovalStatus.Removed, removed.Status);
        int mv = wsl.Commands.FindIndex(c => c.Contains("mv"));
        int rm = wsl.Commands.FindLastIndex(c => c.Contains("rm") && c.Contains(home + SolverHomes.RemovingSuffix));
        Assert.True(mv >= 0 && rm > mv);
        Assert.False(Directory.Exists(wsl.Local("Ubuntu", home)));
        Assert.False(Directory.Exists(mirror));
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private FakeWsl Fake(string listing, int exit = 0)
        => new(Path.Combine(_tmp, "linux-" + Guid.NewGuid().ToString("N")[..6])) { Listing = Encoding.Unicode.GetBytes(listing), ListingExit = exit };

    private string Refusal(FakeWsl wsl)
    {
        Assert.Null(WslPalaceInstall.Plan(wsl, null, null, _tmp, out string? refusal));
        return refusal!;
    }

    private SolverDiscovery Discovery(FakeWsl wsl)
    {
        var d = SolverDiscovery.Create(SolverTool.Palace);
        d.Subsystem         = wsl;
        d.PreferredCommand  = () => null;
        d.ReadEnvironment   = _ => null;
        d.InstallRoots      = [Path.Combine(_tmp, "no-installs")];
        d.SpackRoots        = [];
        d.CondaBases        = [];
        d.SearchDirectories = [];
        d.CacheDirectory    = Path.Combine(_tmp, "cache");
        return d;
    }

    /// <summary>A Palace in a Spack tree at the recipe's <c>~/opt/spack</c>, with the database that names it.</summary>
    private static string SpackPalace(FakeWsl wsl, string distro)
    {
        const string prefix = "/home/user/opt/spack/linux-x86_64/palace-0.18.1-abcdefg";
        wsl.Touch(distro, prefix + "/bin/palace");
        string db = wsl.Local(distro, "/home/user/opt/spack/.spack-db/index.json");
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        File.WriteAllText(db, "{\"database\":{\"version\":\"8\",\"installs\":{\"abcdefg1234\":{\"spec\":{\"name\":\"palace\"," +
                              "\"version\":\"0.18.1\",\"dependencies\":[]},\"path\":\"" + prefix + "\",\"installed\":true," +
                              "\"installation_time\":1000}}}}");
        return prefix + "/bin/palace";
    }

    private static void OnPath(FakeWsl wsl, string distro, string path)
    {
        wsl.Touch(distro, path);
        wsl.OnPath[(distro, WslPaths.Name(path))] = path;
    }

    private static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitRF.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "testdata", "em3d", "wsl", name);
    }
}

/// <summary>
/// wsl.exe, faked: each distribution's Linux filesystem is a local directory, and the handful of programs
/// circuitRF runs inside one (<c>printenv</c>, <c>free</c>, <c>test</c>, <c>mkdir</c>, <c>kill</c>, <c>wslpath</c>, …)
/// are answered against it. Every argument list handed to <see cref="Run"/> is kept in <see cref="Commands"/>,
/// and every one handed to <see cref="StartInfo"/> in <see cref="Started"/>.
/// </summary>
internal sealed class FakeWsl(string root) : IWsl
{
    public bool Available { get; set; } = true;
    public byte[] Listing { get; set; } = [];
    public int ListingExit { get; set; }
    public long MemoryBytes { get; set; } = 16_000_000_000;
    /// <summary>brief-em3d-97 — the VM's memory once <c>wsl --shutdown</c> has run (the next start reads .wslconfig).</summary>
    public long? MemoryAfterShutdown { get; set; }
    public bool GroupAlive { get; set; }
    public Dictionary<string, string> CannotStart { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<(string Distro, string Name), string> OnPath { get; } = new();
    public List<IReadOnlyList<string>> Commands { get; } = [];
    public List<IReadOnlyList<string>> Started { get; } = [];
    public Action<string, string?, IReadOnlyList<string>>? OnStart { get; set; }
    /// <summary>The field copy's <c>tar -cf -</c> sends something that is not an archive, as a stream mangled on its way out would.</summary>
    public bool TarBroken { get; set; }
    /// <summary>The field copy's <c>tar -cf -</c> sends more after its end-of-archive marker than a pipe holds (the archive twice),
    /// as a real tar's record padding does on a Windows pipe.</summary>
    public bool TarTrailing { get; set; }

    private const string Home = "/home/user";

    public string Local(string distro, string linux)
        => Path.Combine(root, distro, linux.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    public string WindowsPath(string distribution, string linuxPath) => Local(distribution, linuxPath);

    public void Touch(string distro, string linux)
    {
        string path = Local(distro, linux);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
    }

    public WslOutput Run(IReadOnlyList<string> arguments, TimeSpan timeout)
    {
        lock (Commands) Commands.Add(arguments);
        if (arguments.Count > 0 && arguments[0] == "-l") return new(ListingExit, Listing, [], null);
        if (arguments.Count > 0 && arguments[0] == "--shutdown")
        {
            if (MemoryAfterShutdown is { } after) MemoryBytes = after;
            return Out("");
        }
        var (distro, cwd, argv) = Parse(arguments);
        if (CannotStart.TryGetValue(distro, out string? why)) return new(1, [], Encoding.Unicode.GetBytes(why), null);
        return Exec(distro, cwd, argv);
    }

    public ProcessStartInfo StartInfo(IReadOnlyList<string> arguments)
    {
        lock (Started) Started.Add(arguments);
        var (distro, cwd, argv) = Parse(arguments);
        bool windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        // The field copy's tar: this machine's own tar over the distribution's directory (Windows ships bsdtar as tar.exe).
        if (argv.Count > 0 && argv[0] == "tar")
        {
            var tar = new ProcessStartInfo(TarBroken ? (windows ? "cmd.exe" : "/bin/sh") : windows ? Path.Combine(Environment.SystemDirectory, "tar.exe") : "tar")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            tar.Environment["COPYFILE_DISABLE"] = "1";   // no AppleDouble entries from macOS's tar
            string[] tarArgs = ["-cf", "-", "-C", Local(distro, cwd!), .. argv.SkipWhile(a => a != "--").Skip(1)];
            if (TarTrailing)
            {
                string exe = windows ? Path.Combine(Environment.SystemDirectory, "tar.exe") : "tar";
                string once = string.Join(' ', tarArgs.Select(a => $"\"{a}\""));
                tar.FileName = windows ? "cmd.exe" : "/bin/sh";
                foreach (string a in windows ? new[] { "/c", $"\"{exe}\" {once} && \"{exe}\" {once}" } : ["-c", $"{exe} {once} && {exe} {once}"])
                    tar.ArgumentList.Add(a);
                return tar;
            }
            var args = TarBroken
                ? windows ? new[] { "/c", "echo", "not an archive" } : ["-c", "echo not an archive"]
                : tarArgs;
            foreach (string a in args) tar.ArgumentList.Add(a);
            return tar;
        }
        // The wrapper writes its pid first, as the real one does.
        if (argv.Count > 5 && argv[0] == "setsid") File.WriteAllText(Local(distro, argv[5]), "4242\n");
        OnStart?.Invoke(distro, cwd, argv);
        var psi = new ProcessStartInfo(windows ? "cmd.exe" : "/bin/sh")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (string a in windows ? new[] { "/c", "echo", "fake Palace" } : ["-c", "echo fake Palace"]) psi.ArgumentList.Add(a);
        return psi;
    }

    private static (string Distro, string? Cwd, IReadOnlyList<string> Argv) Parse(IReadOnlyList<string> a)
    {
        int i = 0;
        string distro = "", cwd = null!;
        for (; i < a.Count && a[i] != "--exec"; i++)
        {
            if (a[i] == "-d") distro = a[++i];
            else if (a[i] == "--cd") cwd = a[++i];
        }
        return (distro, cwd, a.Skip(i + 1).ToList());
    }

    private static WslOutput Out(string text, int exit = 0) => new(exit, Encoding.UTF8.GetBytes(text), [], null);

    private WslOutput Exec(string distro, string? cwd, IReadOnlyList<string> argv)
    {
        string L(string p) => Local(distro, p);
        string last = argv.Count > 0 ? argv[^1] : "";
        switch (argv.Count > 0 ? argv[0] : "")
        {
            case "true":     return Out("");
            case "printenv": return argv[1] == "-0" ? Out($"HOME={Home}\0PATH=/usr/bin:/bin\0LANG=C.UTF-8\0") : Out(Home + "\n");
            case "nproc":    return Out("8\n");
            case "uname":    return Out("x86_64\n");
            case "free":     return Out($"               total        used\nMem:     {MemoryBytes}     1000\nSwap:  0  0\n");
            case "cat":      return File.Exists(L(last)) ? Out(File.ReadAllText(L(last))) : last == "/etc/os-release" ? Out("ID=ubuntu\nID_LIKE=debian\n") : Out("", 1);
            case "kill":     return Out("", argv[1] == "-0" && !GroupAlive ? 1 : 0);
            case "mkdir":    Directory.CreateDirectory(L(last)); return Out("");
            case "rm":
                if (Directory.Exists(L(last))) Directory.Delete(L(last), recursive: true);
                else if (File.Exists(L(last))) File.Delete(L(last));
                return Out("");
            case "mv":
                if (!Directory.Exists(L(argv[^2]))) return Out("", 1);
                Directory.Move(L(argv[^2]), L(last));
                return Out("");
            case "test":
                string t = L(argv[2]);
                bool ok = argv[1] switch { "-d" => Directory.Exists(t), "-e" => Directory.Exists(t) || File.Exists(t), _ => File.Exists(t) };
                return Out("", ok ? 0 : 1);
            case "find":
                string dir = argv[1], pattern = argv[argv.ToList().IndexOf("-name") + 1];
                return Directory.Exists(L(dir))
                    ? Out(string.Join("\n", Directory.EnumerateFileSystemEntries(L(dir), pattern).Select(e => dir.TrimEnd('/') + "/" + Path.GetFileName(e))))
                    : Out("", 1);
            case "bash":
                return OnPath.TryGetValue((distro, last), out string? onPath) ? Out(onPath + "\n") : Out("", 1);
            case "wslpath":
                string w = last;
                string share = @"\\wsl.localhost\" + distro;
                if (w.StartsWith(share, StringComparison.OrdinalIgnoreCase)) return Out(w[share.Length..].Replace('\\', '/') + "\n");
                return w.Length > 2 && w[1] == ':' ? Out($"/mnt/{char.ToLowerInvariant(w[0])}{w[2..].Replace('\\', '/')}\n") : Out("", 1);
            default:
                // A program: Palace answers its version question with the banner F0 recorded.
                if (argv.Count > 0 && argv[0].StartsWith('/') && File.Exists(L(argv[0])) && argv.Contains("--version"))
                    return Out("Palace version: 0dc74cd\nSchema version: 1-7-0\n");
                return Out("", 127);
        }
    }
}
