using System.Diagnostics;
using System.Text.Json;
using CircuitRF.Design.Em3d;
using CircuitRF.Design.Layout.Em3d;
using Xunit;

namespace CircuitRF.Ui.Tests.Em3d;

/// <summary>
/// The machine-wide record of running solver trees. Each tree's ceiling was the whole machine, as if it ran alone, so seven
/// EMerge solves started side by side each stayed under it while together they exhausted the machine.
/// </summary>
[Collection(UserStateDirectoryCollection.Name)]
public sealed class ActiveSolvesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "crf-active-" + Guid.NewGuid().ToString("N")[..12]);
    private readonly string _saved = ActiveSolves.Root;

    public ActiveSolvesTests()
    {
        Directory.CreateDirectory(_root);
        ActiveSolves.Root = Path.Combine(_root, "solves");
    }

    public void Dispose()
    {
        ActiveSolves.Root = _saved;
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    private void Older(long footprint, TimeSpan? age = null)
    {
        Directory.CreateDirectory(ActiveSolves.Root);
        string path = Path.Combine(ActiveSolves.Root, "1-2-older.json");
        File.WriteAllText(path, JsonSerializer.Serialize(
            new ActiveSolves.Entry("python3", "/runs/older", DateTimeOffset.UtcNow.AddMinutes(-5), footprint)));
        if (age is { } a) File.SetLastWriteTimeUtc(path, DateTime.UtcNow - a);
    }

    /// <summary>A running solve is warned about, by name and footprint; a file no longer rewritten is a dead host's, and is removed.</summary>
    [Fact]
    public void ARunningSolve_IsWarnedAbout_AStaleOneIsRemoved()
    {
        Assert.Null(ActiveSolves.RunningWarning(16L << 30));
        Older(6L << 30);
        string said = ActiveSolves.RunningWarning(16L << 30)!;
        Assert.StartsWith("Another EM solve is already running on this machine (python3 in /runs/older, ", said);
        Assert.Contains("Run one EM solve at a time.", said);

        Older(6L << 30, age: ActiveSolves.FreshFor + TimeSpan.FromSeconds(5));
        Assert.Null(ActiveSolves.RunningWarning(16L << 30));
        Assert.Empty(Directory.GetFiles(ActiveSolves.Root));
    }

    /// <summary>A tree started while an older solve holds all but a byte of its ceiling is stopped, the sentence naming the
    /// others; its own file is gone once it ends.</summary>
    [NonWindowsFact("the stand-in is a shell script")]
    public void ATreeStartedBesideAnOlderSolve_IsStoppedAtWhatTheOlderLeaves()
    {
        const long ceiling = 10_000_000_000;
        Older(ceiling - 1);
        string script = Path.Combine(_root, "stand-in");
        File.WriteAllText(script, "#!/bin/sh\nsleep 60\n");
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var psi = new ProcessStartInfo(script) { WorkingDirectory = _root, RedirectStandardOutput = true, RedirectStandardError = true };

        var run = PalaceRun.RunProcess(psi, Path.Combine(_root, "log.txt"), Em3dProcessKind.Solver, null, default,
                                       new ProcessWatch(null, 0) { CeilingBytes = ceiling });

        Assert.True(run.StoppedForMemory, run.StartFailure);
        Assert.Equal(ceiling - 1, run.Stopped!.SharedBytes);
        string said = run.Stopped.Sentence("EMerge", ProcessLimits.MachineOwner, [], null);
        Assert.Contains("the EM solves already running on this machine hold", said);
        Assert.Contains("Run one EM solve at a time", said);
        Assert.Equal(["1-2-older.json"], Directory.GetFiles(ActiveSolves.Root).Select(Path.GetFileName));
    }
}
