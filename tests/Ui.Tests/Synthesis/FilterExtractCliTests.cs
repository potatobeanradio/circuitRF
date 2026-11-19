using CircuitRF.Cli;
using CircuitRF.Core.Synthesis.Filters;
using CircuitRF.Core.Systems;
using CircuitRF.Design.Filters;
using CircuitRF.Ui.Tests.Lvs;
using RfCore;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Synthesis;

/// <summary><c>circuitrf filter --extract</c> (docs/design/filter-synthesis.md §6c) on a file of the ideal ladder's response.</summary>
[Collection(LvsCliConsoleCollection.Name)]
public sealed class FilterExtractCliTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "crf-filter-x-" + Guid.NewGuid().ToString("N")[..12]);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    /// <summary>The ideal 5-pole 0.1 dB Chebyshev bandpass, 2.28 to 2.52 GHz, written as a .s2p: <c>--extract</c> reads its
    /// centre as the band's geometric one, 2.397 GHz, and its return loss as the ripple's 16.43 dB, both passing; a
    /// one-port file is <c>filter.extract.unreadable</c>.</summary>
    [Fact]
    public void Extract_ReadsTheIdealChebyshevsCentreAndReturnLoss()
    {
        Directory.CreateDirectory(_dir);
        var spec = new FilterSpec(FilterResponse.Chebyshev, FilterBand.Bandpass, 5, 2.28e9, 2.52e9, 0.1);
        var f = FilterSynthesisService.Frequencies(spec, 201);
        string s2p = Path.Combine(_dir, "ideal.s2p");
        TouchstoneIO.WriteFile(FilterSynthesisService.Response(FilterSynthesisService.Ideal(spec, 201), f, spec.Z0), s2p);
        string[] args = ["--band", "bandpass", "--order", "5", "--f1", "2.28GHz", "--f2", "2.52GHz", "--ripple", "0.1"];

        var real = Console.Out;
        var buffer = new StringWriter();
        int exit;
        try
        {
            Console.SetOut(buffer);
            using (JsonRun.Isolate()) exit = FilterVerb.Run(["--extract", s2p, .. args]);
        }
        finally { Console.SetOut(real); }
        string text = buffer.ToString();
        output.WriteLine(text);
        Assert.Equal(0, exit);
        string[] lines = text.Split('\n');
        Assert.Matches(@"^\s+Centre\s+2\.39[67]\d* GHz pass\s+2\.397 GHz\s*$", Assert.Single(lines, l => l.TrimStart().StartsWith("Centre")));
        Assert.Matches(@"^\s+Return loss\s+16\.4\d? dB pass\s+16\.43 dB\s*$", Assert.Single(lines, l => l.TrimStart().StartsWith("Return loss")));

        string s1p = Path.Combine(_dir, "one.s1p");
        File.WriteAllText(s1p, "# GHz S MA R 50\n1 0.5 0\n2 0.5 0\n");
        using (JsonRun.Isolate())
        {
            Assert.Equal(1, FilterVerb.Run(["--extract", s1p, .. args]));
            Assert.Contains(JsonRun.CapturedDiagnostics, d => d.Id == "filter.extract.unreadable");
        }
    }
}
