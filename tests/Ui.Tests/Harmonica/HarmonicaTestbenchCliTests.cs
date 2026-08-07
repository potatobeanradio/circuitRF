// ================================================================
//  HarmonicaTestbenchCliTests.cs  —  R-h7-13's gate, END TO END
//
//  The routine tier compares the two SOLVES in process (HarmonicaInterchangeTests). This runs the
//  exported file through the real `Cli hb` verb as a separate process, because "runnable" is a claim
//  about the product path and not about a library call. Tagged Benchmark: it builds and launches the
//  CLI, which costs seconds.
// ================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Harmonica;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Harmonica;

[Collection("HarmonicaUiBenchmarks")]
[Trait("Category", "Benchmark")]
public sealed class HarmonicaTestbenchCliTests(ITestOutputHelper output)
{
    private static string RepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        while (dir is { Length: > 0 } && !File.Exists(Path.Combine(dir, "circuitRF.slnx")))
            dir = Path.GetDirectoryName(dir) ?? "";
        return dir;
    }

    [Fact]
    public void TheExportedTestbench_RunsThroughCliHb_AndConverges()
    {
        string root = RepoRoot();
        Assert.False(string.IsNullOrEmpty(root), "could not locate the repository root");

        var vm = new HarmonicaViewModel();
        vm.AddMarkerBand(TerminationSideKind.Load, 2);
        vm.SetMarkerImpedance(
            vm.Markers.Single(m => m is { Side: TerminationSideKind.Load, Band: 2 }),
            new Complex(3, -40));
        vm.SolveFrame(new HarmonicaSolver.Options { SkipContours = true });

        string path = Path.Combine(Path.GetTempPath(),
                                   $"harmonica-testbench-{Guid.NewGuid():N}.cnl");
        File.WriteAllText(path, HarmonicaInterchange.ExportTestbench(
            vm.Model, vm.Terminations, vm.OperatingPointDbm));

        try
        {
            var psi = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory       = root,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
            };
            foreach (string a in new[] { "run", "--project", "src/Cli", "--", "hb", path })
                psi.ArgumentList.Add(a);

            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            proc.WaitForExit();

            output.WriteLine(stdout.Length > 3000 ? stdout[..3000] + " …" : stdout);
            if (stderr.Length > 0) output.WriteLine("stderr: " + stderr);

            Assert.Equal(0, proc.ExitCode);
            Assert.Contains("Converged: yes", stdout, StringComparison.Ordinal);
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }
}
