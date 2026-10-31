using System.Numerics;
using CircuitRF.Render.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore;
using RfCore.Data;
using RfCore.Export;

namespace CircuitRF.Ui.Tests.DataDisplay;

/// <summary>
/// A binary simulation dataset (.ds) as a Data Display source (brief-binary-dataset-data-display): it loads through
/// <see cref="PlotSourceFile"/> like a <c>.npy</c>, a uniform <c>PortZ</c> becomes the group's <c>Z0</c> (so a circuitRF
/// S result exported as <c>.ds</c> comes back with its <c>Z0</c>), a varying one and text variables are said as notes,
/// an unswept two-port gets a network view, and a <c>.cdd</c> holding one reopens, breaks and restores like any other
/// source. Every file is written by <see cref="BinaryDatasetWriter"/> from made-up numbers.
/// </summary>
public sealed class BinaryDatasetSourceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-dssource-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    private static readonly double[] F = [1e9, 2e9, 3e9, 4e9], Vds = [5, 28];

    private static Complex S(int i, int j, double f, double vd) =>
        i == j ? new(0.3 - 0.01 * vd, -0.05 * f / 1e9) : new Complex(2.0 + 0.05 * vd, -0.4 * f / 1e9) / (i < j ? 20 : 1);

    /// <summary>An S result shaped as circuitRF writes one: group SP1, S [sweep…, freq, i, j], Z0 [port].</summary>
    private static DataSet CircuitRfSResult(bool swept)
    {
        Axis i = new("i", [1, 2], "port"), j = new("j", [1, 2], "port"), freq = new("freq", F, "Hz");
        var sweep = swept ? new[] { new Axis("Vds", Vds, "V") } : [];
        var values = new List<Complex>();
        foreach (double vd in swept ? Vds : [0.0])
            foreach (double f in F)
                for (int a = 1; a <= 2; a++)
                    for (int b = 1; b <= 2; b++)
                        values.Add(S(a, b, f, vd));
        var ds = new DataSet();
        ds.AddToGroup("SP1", "S", new DataCube([.. sweep, freq, i, j], [.. values]));
        ds.AddToGroup("SP1", "Z0", DataSetBuilder.BuildZ0Cube([new(50, 0), new(50, 0)]));
        return ds;
    }

    private string WriteDs(string name, DataSet ds)
    {
        string path = Path.Combine(_dir, name);
        BinaryDatasetWriter.Write(path, ds);
        return path;
    }

    [Fact]
    public void ACircuitRfSResult_ExportedAsDs_LoadsBackWithItsZ0_AndTheVirtualZResolves()
    {
        var (data, _, error, notes) = PlotSourceFile.LoadWithNotes(WriteDs("swept.ds", CircuitRfSResult(swept: true)));

        Assert.Null(error);
        Assert.Empty(notes);
        Assert.Equal(["Vds", "freq", "i", "j"], data!["SP1.S"].Axes.Select(a => a.Name));
        Assert.Equal([new Complex(50, 0), new Complex(50, 0)], data["SP1.Z0"].ComplexValues);
        Assert.True(data.Contains("SP1.Z"));
        Assert.False(data.Contains("SP1.PortZ"));   // replaced by the Z0, not kept beside it
    }

    [Fact]
    public void AVaryingPortZ_AddsNoZ0_AndItAndATextVariableAreSaidAsNotes()
    {
        int n = F.Length;
        static BinaryDatasetVariable C(string name, IEnumerable<Complex> v, string q)
        {
            var z = v.ToArray();
            return new(name, q, BinaryValueKind.Complex, [.. z.Select(x => x.Real)], [.. z.Select(x => x.Imaginary)], null);
        }
        var block = new BinaryDatasetBlock("SP1.SP", "",
            [new("freq", "frequency", BinaryValueKind.Real, F, null, null)],
            [
                .. from a in new[] { 1, 2 } from b in new[] { 1, 2 } select C($"S[{a},{b}]", F.Select(f => S(a, b, f, 0)), "s-param"),
                C("PortZ[1]", F.Select(f => new Complex(50 + f / 1e9, 0)), "port-impedance"),
                C("PortZ[2]", F.Select(_ => new Complex(50, 0)), "port-impedance"),
                new("PortName[1]", "", BinaryValueKind.Text, new double[n], null, [.. Enumerable.Repeat("in", n)]),
            ]);
        string path = Path.Combine(_dir, "varying.ds");
        BinaryDatasetWriter.Write(path, new BinaryDataset([block]));

        var (data, _, error, notes) = PlotSourceFile.LoadWithNotes(path);

        Assert.Null(error);
        Assert.True(data!.Contains("SP1.S"));
        Assert.False(data.Contains("SP1.Z0"));
        Assert.False(data.Contains("SP1.Z"));
        Assert.Equal(2, notes.Count);
        Assert.Contains(notes, s => s.Contains("SP1.PortName[1]", StringComparison.Ordinal));
        Assert.Contains(notes, s => s.Contains("SP1.PortZ varies", StringComparison.Ordinal));
    }

    [Fact]
    public void AnUnsweptTwoPort_HasANetworkView_SoMaxGainResolves()
    {
        var (_, snp, error) = PlotSourceFile.Load(WriteDs("flat.ds", CircuitRfSResult(swept: false)));

        Assert.Null(error);
        Assert.NotNull(snp);
        Assert.Equal(F.Length, snp.Frequencies.Length);
        Assert.All(RFNetwork.MaxGain(snp), g => Assert.True(double.IsFinite(g)));
    }

    [Fact]
    public void AKnownDsFile_IsOfferedInTheSourceList_AsABinaryDataset()
    {
        string path = WriteDs("known.ds", CircuitRfSResult(swept: false));
        var lib = new DataSourceLibraryViewModel { KnownLoadpullProvider = () => [path] };

        lib.RefreshAvailableDataSources();

        var item = Assert.Single(lib.AvailableDataSources);
        Assert.Equal(SourceKind.BinaryDataset, item.Kind);
        Assert.Equal("known.ds", item.DisplayName);
    }

    [Fact]
    public async Task ACddWithADsSource_Reopens_ShowsBrokenWhenMoved_AndRestoresWhenOpenedAgain()
    {
        string path = WriteDs("bias.ds", CircuitRfSResult(swept: true));
        string cdd  = Path.Combine(_dir, "display.cdd");

        var window = new DisplayWindowViewModel();
        window.DataSourceLibrary.ResultsRootProvider = () => _dir;
        await window.DataSourceLibrary.LoadFileAsync(path);
        await window.SaveAllAsync(cdd);

        var reopened = new DisplayWindowViewModel();
        reopened.DataSourceLibrary.ResultsRootProvider = () => _dir;
        await reopened.LoadAllAsync(cdd);
        var live = Assert.Single(reopened.DataSourceLibrary.Entries);
        Assert.Equal(SourceKind.BinaryDataset, live.Kind);
        Assert.False(live.IsBroken);
        Assert.True(live.Data!.Contains("SP1.Z"));

        string away = path + ".moved";
        File.Move(path, away);
        var missing = new DisplayWindowViewModel();
        missing.DataSourceLibrary.ResultsRootProvider = () => _dir;
        await missing.LoadAllAsync(cdd);
        var broken = Assert.Single(missing.DataSourceLibrary.Entries);
        Assert.Equal(SourceKind.BinaryDataset, broken.Kind);
        Assert.True(broken.IsBroken);

        File.Move(away, path);
        await missing.DataSourceLibrary.LoadFileAsync(path);
        Assert.Same(broken, Assert.Single(missing.DataSourceLibrary.Entries));
        Assert.False(broken.IsBroken);
        Assert.True(broken.Data!.Contains("SP1.S"));
    }
}
