using CircuitRF.Ui.DataDisplay.ViewModels;
using RfCore.Data;
using RfCore.Export;
using Xunit;

namespace CircuitRF.Ui.Tests.DataDisplay;

/// <summary>Issue 68 §3: a <c>.npy</c>, <c>.spl</c> or <c>.lpcwave</c> that is there but cannot be read is a broken entry
/// that says why, as a refused binary dataset is, and never a source that silently is not there.</summary>
public sealed class UnreadableSourceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("crf-unreadable-").FullName;
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { /* best effort */ } }

    [Fact]
    public async Task AnUnreadableNpy_IsAnUnreadableEntry_AndReadsWhenItIsFixed()
    {
        string path = Path.Combine(_dir, "result.npy");
        File.WriteAllBytes(path, [0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', 1, 0, 0xFF, 0xFF, 1, 2, 3]);

        var lib = new DataSourceLibraryViewModel();
        await lib.LoadFileAsync(path);
        var entry = Assert.Single(lib.Entries);
        Assert.Equal(SourceKind.Npy, entry.Kind);
        Assert.True(entry.IsBroken);
        Assert.False(string.IsNullOrWhiteSpace(entry.LoadError));

        var ds = new DataSet();
        ds.Add("V", new DataCube([new Axis("freq", [1e9, 2e9], "Hz")], [1.0, 2.0]));
        DataSetExporter.Export(ds, path, ExportFormat.Npy);
        await lib.LoadFileAsync(path);
        Assert.Same(entry, Assert.Single(lib.Entries));
        Assert.False(entry.IsBroken);
        Assert.Null(entry.LoadError);
        Assert.True(entry.Data!.Contains("V"));
    }
}
