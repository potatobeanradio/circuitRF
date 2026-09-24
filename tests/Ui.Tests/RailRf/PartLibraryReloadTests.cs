// ================================================================
//  PartLibraryReloadTests.cs — a saved part library reaches the railRF window that uses it
//
//  Field report, 2026-09-23: an ESR edited in the part library editor and saved stayed at its old
//  value in the open railRF window's parts table. The window read its library once, on open.
// ================================================================

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircuitRF.Design.RailRf;
using CircuitRF.Ui.RailRf;
using Xunit;

namespace CircuitRF.Ui.Tests.RailRf;

public sealed class PartLibraryReloadTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "crf-crlib-reload-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void SavingTheLibraryThisWindowUses_UpdatesItsPartsTable_AndAnotherLibraryDoesNot()
    {
        CopyDirectory(Path.Combine(RepoRoot(), "examples", "Power Rail"), _root);
        string crail = Path.Combine(_root, "Sensor board", "Sensor board.crail");
        string crlib = Path.Combine(_root, "parts", "decoupling.crlib");

        var vm = new RailRfViewModel(RailDocumentIo.LoadFromFile(crail), crail)
        {
            PostToUi     = a => a(),
            RunOffThread = (work, _) => Task.FromResult(work()),
        };
        vm.LoadDocumentReferences();

        const string partNumber = "CAP-0402-100N-X7R-16V";
        string EsrShown() => vm.Parts.First(p => p.PartNumber == partNumber).EsrText;
        Assert.Equal("32 mΩ", EsrShown());

        var library = PartLibraryIo.LoadFromFile(crlib);
        library.Rows.Single(r => r.PartNumber == partNumber).EsrOhms = 0.007;
        PartLibraryIo.SaveToFile(crlib, library);

        // A different file saved: nothing to do with this window.
        Assert.False(vm.ReloadPartLibrary(Path.Combine(_root, "parts", "other.crlib")));
        Assert.Equal("32 mΩ", EsrShown());

        Assert.True(vm.ReloadPartLibrary(crlib));
        Assert.Equal("7 mΩ", EsrShown());
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(dir.Replace(from, to, StringComparison.Ordinal));
        Directory.CreateDirectory(to);
        foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, file.Replace(from, to, StringComparison.Ordinal), overwrite: true);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
