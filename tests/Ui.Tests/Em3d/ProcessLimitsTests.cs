using System.Diagnostics;
using CircuitRF.Cli;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Theming;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

// ══════════════════════════════════════════════════════════════════════════════════════════════
//  brief-emi-10 — the memory ceiling and the free-disk floor every solver and mesher process is held to, in the one process
//  loop (PalaceRun.RunProcess). Stand-in processes are shell scripts (NonWindowsFact); the readers are injected where a reading
//  must be exact, and the two real-memory gates allocate with perl, which every macOS and Linux machine has. Nothing here needs
//  a solver installed. Counters, never timings.
// ══════════════════════════════════════════════════════════════════════════════════════════════

[Collection(UserStateDirectoryCollection.Name)]
public sealed class ProcessLimitsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-limits-" + Guid.NewGuid().ToString("N")[..12]);

    public ProcessLimitsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        AppDataRoot.RedirectTo(null);
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ── R-emi10-1: the footprint ───────────────────────────────────────────────────────────────

    [Fact]
    public void LinuxStatus_CountsSwappedPagesInTheFootprint()
    {
        const string status = "Name:\tpython3\nPPid:\t4242\nVmRSS:\t  102400 kB\nVmSwap:\t  307200 kB\nThreads:\t8\n";
        Assert.Equal(new ProcessTreeMemory.Row(77, 4242, 100L << 20, 400L << 20), ProcessTreeMemory.ParseProcStatus(77, status));
    }

    [NonWindowsFact("the allocating child is a perl one-liner")]
    public void AChildThatTouches600MB_ReadsAtLeast550MB()
    {
        using var p = Start("perl", "-e", "$x = 'a' x 600_000_000; sleep 60");
        try
        {
            var read = ProcessTreeMemory.Sampler(p.Id);
            long seen = 0;
            for (int k = 0; k < 400 && seen < 550_000_000; k++)
            {
                seen = Math.Max(seen, read()?.FootprintBytes ?? 0);
                Thread.Sleep(50);
            }
            Assert.True(seen >= 550_000_000, $"footprint read {seen:N0} bytes");
        }
        finally { p.Kill(entireProcessTree: true); }
    }

    // ── R-emi10-2: the ceiling ─────────────────────────────────────────────────────────────────

    /// <summary>A tree killed within one poll of a reading at the ceiling, grandchild included, with the reading in the result.</summary>
    [NonWindowsFact("the stand-in is a shell script")]
    public void AReadingAtTheCeiling_KillsTheTree_WithinOnePoll()
    {
        string pidFile = Path.Combine(_root, "grandchild.pid");
        var psi = Script("sleep 300 &\necho $! > grandchild.pid\nwait\n");
        int overReads = 0;
        var watch = new ProcessWatch(null, 0)
        {
            CeilingBytes = 1_000_000_000,
            ReadMemory = _ => () =>
            {
                if (!File.Exists(pidFile) || File.ReadAllText(pidFile).Trim().Length == 0) return new TreeMemory(1, 1);
                overReads++;
                return new TreeMemory(900_000_000, 1_200_000_000);
            },
        };
        var run = PalaceRun.RunProcess(psi, Path.Combine(_root, "log.txt"), Em3dProcessKind.Mesher, null, default, watch);

        Assert.True(run.StoppedForMemory);
        Assert.Equal(1, overReads);
        Assert.Equal(new ProcessStop(ProcessStopReason.Memory, 1_200_000_000, 1_000_000_000, Meshing: true), run.Stopped);
        AssertDies(int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture));
    }

    [NonWindowsFact("the stand-in is a shell script")]
    public void TheLog_SaysWhenARunEnded_AndWhetherStopWasPressed()
    {
        string finished = Path.Combine(_root, "finished.txt");
        PalaceRun.RunProcess(Script("echo hello\nexit 3\n"), finished, Em3dProcessKind.Solver, null, default, null);
        var lines = File.ReadAllLines(finished);
        Assert.Matches(@"^\[circuitRF \d{4}-\d\d-\d\d \d\d:\d\d:\d\d [-+]\d\d:\d\d\] .* started\.$", lines[0]);
        Assert.Equal("hello", lines[1]);
        Assert.Contains("ended with exit code 3 after 0 s.", lines[2]);

        // The stamp comes first, so what the program prints as it dies reads as the stop it is.
        string stopped = Path.Combine(_root, "stopped.txt");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var run = PalaceRun.RunProcess(Script("sleep 300\n"), stopped, Em3dProcessKind.Solver, null, cts.Token, null);
        Assert.True(run.Cancelled);
        Assert.Contains("Stop was pressed after 0 s. Any line after this one comes from stopping it.", File.ReadAllText(stopped));
    }

    [NonWindowsFact("the stand-in is a shell script")]
    public void AForcedRun_ReadingAboveTheCeiling_IsNotStopped()
    {
        var watch = new ProcessWatch(null, 0)
        {
            CeilingBytes = 1_000, CeilingLifted = () => true, ReadMemory = _ => () => new TreeMemory(5_000, 5_000),
        };
        var run = PalaceRun.RunProcess(Script("sleep 0.5\n"), Path.Combine(_root, "log.txt"), Em3dProcessKind.Solver, null, default, watch);
        Assert.Null(run.Stopped);
        Assert.Equal(0, run.ExitCode);
    }

    [NonWindowsFact("the stand-in is a shell script")]
    public void ARealChild_PastA300MBCeiling_IsStopped_AndTheResultSaysSo()
    {
        // perl under a shell, so the memory is a CHILD's and is found only by walking the tree
        var psi = Script("perl -e '$x = \"a\" x 600_000_000; sleep 60' &\nwait\n");
        var run = PalaceRun.RunProcess(psi, Path.Combine(_root, "log.txt"), Em3dProcessKind.Solver, null, default,
                                       new ProcessWatch(null, 0) { CeilingBytes = 300_000_000 });
        Assert.True(run.StoppedForMemory, run.StartFailure);
        Assert.True(run.Stopped!.ReadingBytes >= 300_000_000);
        string said = run.Stopped.Sentence("EMerge", ProcessLimits.MachineOwner, [new("first-order elements", null)], null);
        Assert.StartsWith("EMerge was stopped while solving: it reached ", said);
        Assert.Contains("this machine's memory limit for a solver (300 MB). Nothing was written.", said);
        Assert.Contains("in order of effect: first-order elements.", said);
    }

    [Fact]
    public void WhileMeshing_TheSentenceNamesARunaway_AndTheLog()
    {
        string said = new ProcessStop(ProcessStopReason.Memory, 15_300_000_000, 16_000_000_000, Meshing: true)
            .Sentence("EMerge", ProcessLimits.MachineOwner, [], "/runs/emerge.log");
        Assert.Equal("EMerge was stopped while meshing: it reached 15.3 GB, this machine's memory limit for a solver (16.0 GB). " +
                     "Nothing was written. A mesh this size usually means the mesher ran away on one curve; check the run's log for " +
                     "the object it was meshing (/runs/emerge.log), or coarsen the mesh.", said);
    }

    // ── R-emi10-3: the floor ───────────────────────────────────────────────────────────────────

    [NonWindowsFact("the stand-in is a shell script")]
    public void AVolumeBelowTheFloor_StopsTheTree_AndNamesThatVolume()
    {
        int started = 0;
        var watch = new ProcessWatch(null, 0)
        {
            FloorVolumes = [new("/swap", "the startup volume, where the operating system swaps"), new(_root, "the volume the run writes to")],
            // the run's volume fills once the process is running
            ReadSpace = path => path == _root && started > 0 ? new VolumeSpace(2_000_000_000, 500_000_000_000)
                                                              : new VolumeSpace(400_000_000_000, 500_000_000_000),
            OnStarted = _ => started++,
        };
        var run = PalaceRun.RunProcess(Script("sleep 300\n"), Path.Combine(_root, "log.txt"), Em3dProcessKind.Solver, null, default, watch);
        Assert.Equal(1, started);
        Assert.Equal(ProcessStopReason.Disk, run.Stopped!.Reason);
        Assert.False(run.Stopped.BeforeStart);
        Assert.Equal(("the volume the run writes to", 10_000_000_000L), (run.Stopped.VolumeRole, run.Stopped.LimitBytes));
    }

    [Fact]
    public void AVolumeAlreadyBelowTheFloor_RefusesTheRun_BeforeAnyProcessStarts()
    {
        int started = 0;
        var watch = new ProcessWatch(null, 0)
        {
            FloorVolumes = [new(_root, "the volume the run writes to")],
            ReadSpace = _ => new VolumeSpace(1_000_000_000, 64_000_000_000),     // floor: 5 % of 64 GB = 3.2 GB
            OnStarted = _ => started++,
        };
        var run = PalaceRun.RunProcess(new ProcessStartInfo("no-such-program-is-started"), Path.Combine(_root, "log.txt"),
                                       Em3dProcessKind.Solver, null, default, watch);
        Assert.Equal(0, started);
        Assert.Null(run.StartFailure);
        Assert.True(run.Stopped is { Reason: ProcessStopReason.Disk, BeforeStart: true, LimitBytes: 3_200_000_000 });
        Assert.StartsWith("Palace was not started: the volume the run writes to", run.Stopped.Sentence("Palace", "", [], null));
    }

    // ── R-emi10-4: the user's own limit ────────────────────────────────────────────────────────

    [Fact]
    public void ThePreference_RoundTrips_AndALimitAboveMemory_IsRefusedAsTheFlagRefusesIt()
    {
        AppDataRoot.RedirectTo(Path.Combine(_root, "state"));
        Assert.Null(EmMemoryLimitPreference.TrySet(4));
        AppPreferencesIo.InvalidateCache();
        Assert.Contains("\"em_memory_limit_gb\": 4", File.ReadAllText(Path.Combine(_root, "state", "preferences.json")));
        Assert.Equal(4_000_000_000, EmMemoryLimitPreference.CeilingBytes);

        string tooMuch = (MachineMemory.PhysicalBytes / 1e9 + 1).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        string? refused = EmMemoryLimitPreference.TrySet(double.Parse(tooMuch, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(Em3dMemoryLimit.Refusal(double.Parse(tooMuch, System.Globalization.CultureInfo.InvariantCulture), MachineMemory.PhysicalBytes), refused);
        Assert.Equal(4_000_000_000, EmMemoryLimitPreference.CeilingBytes);          // nothing stored

        var (code, _, stderr) = CliProcess.Run(PalaceBackendTests.RepoRoot(), [], "em", Path.Combine(_root, "x.cem"), "--memory-limit", tooMuch);
        Assert.Equal(1, code);
        Assert.Contains(refused!, stderr.Replace("\r", "").Replace("\n", " "));
    }

    [Fact]
    public void TheFlag_ReachesTheRun()
    {
        string cem = PalaceBackendTests.SmallWorkspace(_root, "flag");
        long? seen = null;
        Em3dRunService.RunStarting = (results, limit) =>
        {
            if (!results.StartsWith(_root, StringComparison.Ordinal)) return;
            seen = limit;
            throw new OperationCanceledException();      // nothing is started: the flag's arrival is the claim
        };
        try
        {
            JsonRun.Reset();
            Assert.Equal(130, CliEntry.Run(["em", cem, "--memory-limit", "2.5"]));
        }
        finally { Em3dRunService.RunStarting = null; }
        Assert.Equal(2_500_000_000, seen);
    }

    // ── helpers ────────────────────────────────────────────────────────────────────────────────

    private ProcessStartInfo Script(string body)
    {
        string path = Path.Combine(_root, "stand-in-" + Guid.NewGuid().ToString("N")[..6]);
        File.WriteAllText(path, "#!/bin/sh\n" + body);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new ProcessStartInfo(path) { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true };
    }

    private static Process Start(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) psi.ArgumentList.Add(a);
        return Process.Start(psi)!;
    }

    private static void AssertDies(int pid)
    {
        for (int k = 0; k < 250 && Alive(pid); k++) Thread.Sleep(20);
        Assert.False(Alive(pid), $"process {pid} outlived the stop");
    }

    private static bool Alive(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
