using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;
using CircuitRF.Engine.Loadpull;
using Xunit;

namespace CircuitRF.Engine.Tests.Loadpull;

// Layers C+D: the .gam format carries optional per-frequency blocks (freq=<v><unit> directive lines).
// Writer appends one block per freq; reader parses blocks and selects by target frequency. A freq-less
// file stays a single any-frequency block (back-compatible).
public sealed class GamMultiBlockTests
{
    private static GamWriter.GamBuilderResult Res(params Complex[] z)
        => new(z.ToList(), new System.Collections.Generic.List<string>());

    [Fact]
    public void Writer_AppendsFreqBlocks_Reader_SelectsByFreq()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gam_mb_{System.Guid.NewGuid():N}.gam");
        try
        {
            // First block truncates + writes header; second appends.
            GamWriter.WriteFile(path, Res(new Complex(80, 10), new Complex(60, -5)), freqHz: 1.8e9, append: false);
            GamWriter.WriteFile(path, Res(new Complex(85, 5),  new Complex(70, 0), new Complex(50, 0)),
                                freqHz: 2.2e9, append: true);

            var blocks = GamReader.ReadBlocks(path);
            Assert.Equal(2, blocks.Count);
            Assert.Equal(1.8e9, blocks[0].FreqHz!.Value, 3);
            Assert.Equal(2.2e9, blocks[1].FreqHz!.Value, 3);
            Assert.Equal(2, blocks[0].Grid.Points.Count);
            Assert.Equal(3, blocks[1].Grid.Points.Count);

            // Nearest-block selection.
            Assert.Equal(2, GamReader.ReadFileForFreq(path, 1.9e9).Points.Count);   // closer to 1.8
            Assert.Equal(3, GamReader.ReadFileForFreq(path, 2.1e9).Points.Count);   // closer to 2.2

            // First point of the 1.8 GHz block round-trips its impedance.
            var z0 = blocks[0].Grid.Points[0].Z;
            Assert.Equal(80, z0.Real,      3);
            Assert.Equal(10, z0.Imaginary, 3);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void FreqLessFile_IsSingleAnyFreqBlock_BackCompat()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gam_fl_{System.Guid.NewGuid():N}.gam");
        try
        {
            GamWriter.WriteFile(path, Res(new Complex(80, 10), new Complex(60, -5)));   // no freqHz

            var blocks = GamReader.ReadBlocks(path);
            Assert.Single(blocks);
            Assert.Null(blocks[0].FreqHz);                       // freq-less → any frequency
            // Selection at any tone returns the same single block.
            Assert.Equal(2, GamReader.ReadFileForFreq(path, 5e9).Points.Count);
            Assert.Equal(2, GamReader.ReadFile(path).Points.Count);
        }
        finally { File.Delete(path); }
    }

    // Layer C: a freq-swept loadpull resolves each frequency to that frequency's input grid block.
    [Fact]
    public void FreqTaggedInputGrid_LoadpullResolve_PicksPerFreqBlock()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gam_in_{System.Guid.NewGuid():N}.gam");
        try
        {
            GamWriter.WriteFile(path, Res(new Complex(30, 0)), freqHz: 1.8e9, append: false);
            GamWriter.WriteFile(path, Res(new Complex(70, 0)), freqHz: 2.2e9, append: true);

            var globals = new Dictionary<string, Value>();

            LoadpullAnalysis Lp(string toneGhz) => new("LP")
            {
                GridPath = path, ToneExpr = toneGhz, ToneUnit = "GHz",
                LoadTunerName = "L", SourceTunerName = "S",
            };

            var p18 = LoadpullEngine.Resolve(Lp("1.8"), globals, null);
            var p22 = LoadpullEngine.Resolve(Lp("2.2"), globals, null);
            Assert.Equal(30, p18.Grid.Points[0].Z.Real, 3);   // 1.8 GHz block
            Assert.Equal(70, p22.Grid.Points[0].Z.Real, 3);   // 2.2 GHz block
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("freq=2e9",    2e9)]
    [InlineData("freq=1.8GHz", 1.8e9)]
    [InlineData("freq=900 MHz", 900e6)]
    public void Reader_ParsesFreqValueAndUnit(string freqLine, double expectHz)
    {
        var text = $"# impedance Z0=50 re+j*imag\n{freqLine}\n80+j*10\n";
        var blocks = GamReader.ReadBlocksText(text);
        Assert.Single(blocks);
        Assert.Equal(expectHz, blocks[0].FreqHz!.Value, 0);
    }
}
