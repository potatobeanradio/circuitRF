// A run's Touchstone with rows that are not a passive network is a warning on the run, naming each frequency and the worst
// σ_max(S); the planar solve's own NOT PASSIVE warning (de-embedded runs) is not said twice.

using CircuitRF.Design.Layout.Em;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

public sealed class RunPassivityWarningTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("em-passivity-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A 2-port of 4 rows whose last two have gain (|S21| = 1.2 and 1.28 at S11 = 0).</summary>
    private string File2(params double[] s21)
    {
        string path = Path.Combine(_dir, "run.s2p");
        File.WriteAllText(path, "# GHz S RI R 50\n" + string.Join("\n", s21.Select((g, k) => $"{k + 1} 0 0 {g} 0 {g} 0 0 0")) + "\n");
        return path;
    }

    private static EmRunResult Ok(string snp, params string[] warnings) => new(EmRunStatus.Ok, null, null, null, null, snp, null, warnings);

    [Fact]
    public void RowsThatAreNotPassive_AreAWarningNamingThem()
    {
        var run = EmRunService.WithPassivity(Ok(File2(0.9, 0.95, 1.2, 1.28)));
        var w = Assert.Single(run.Warnings);
        Assert.Equal("NOT PASSIVE: 2 of 4 row(s) of run.s2p have σ_max(S) > 1, at 3 GHz and 4 GHz; worst 1.2800 at 4 GHz. A passive " +
                     "structure cannot do that, so the excess is the solve, not the design: do not use those rows.", w);

        Assert.Empty(EmRunService.WithPassivity(Ok(File2(0.9, 0.95, 0.99, 1.0005))).Warnings);
        // The planar solve said it already, with its cause.
        var planar = EmRunService.WithPassivity(Ok(File2(0.9, 0.95, 1.2, 1.28), "NOT PASSIVE: 2 of 4 de-embedded point(s) …"));
        Assert.Single(planar.Warnings);
    }
}
