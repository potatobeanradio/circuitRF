using System.IO;
using Avalonia.Controls;
using CircuitRF.Design.Cells;
using CircuitRF.Design.VendorLibraries.ModelLibrary;
using CircuitRF.Design.VendorLibraries.ModelLibrary.Protect;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>Export to Library's figures (brief vendorlib-16): the live dialog, showing a stated plan.</summary>
public static class DocExportFixtures
{
    /// <summary>
    /// Export to Library with Compile netlist ticked, on the synthetic TSTPA200: what is compiled and what stays outside,
    /// the proof's default bias points, nothing refused. The plan is STATED, not planned: a real plan reads this machine's
    /// file system and looks for a compiler, and the figure must come out the same on every machine. The only thing read
    /// from disk is the cell's parameter list, from a cell folder written here whose path the dialog never shows.
    /// </summary>
    public static FigureScene ExportCompileNetlist()
    {
        string cell = Path.Combine(Path.GetTempPath(), "crf-docfig-export", "TSTPA200");
        Directory.CreateDirectory(cell);
        var ccell = new CcellFile();
        ccell.Parameters.Add(new CcellParameter { Name = "Lp", DefaultExpression = "1", Unit = "nH" });
        CellPersistence.SaveToFile(Path.Combine(cell, CellFolder.CcellFileName), ccell);

        string[] pins = ["in", "out"];
        var plan = new ExportPlan
        {
            CellDir = cell, PartNumber = "TSTPA200", ModelDir = "/libraries/devices/Amplifiers/TSTPA200", Pins = pins,
            DefaultKind = ModelPartManifest.KindActive, DefaultSymbol = ModelLibraryExport.SymbolBox,
            CompileUnavailable = "The cell places no Verilog-A file or ComposedFET to compile.", ShipsCompiled = true,
            ProtectLines =
            [
                "Compiled into one module: R1 (R), Q1 (ComposedFET), C1 (C), X1.L1 (L).",
                "Outside, in the wrapper: SNP1 (SnP).",
                "The module has 3 terminals: 1 of the cell's 2 pins, 1 added where outside elements meet it (n3) and ground.",
            ],
            SourcePath = "/designs/PowerAmp/protected-source/TSTPA200/TSTPA200.va",
            CheckBias = [.. ProtectBias.Defaults(pins).Select(b => b.ToString())],
        };

        var vm = new ExportToLibraryViewModel(cell, [], null) { Category = "" };
        vm.ShowForFigure("/libraries/devices", plan, compileNetlist: true);
        var dialog = new ExportToLibraryDialog(vm);
        var root = (Control)dialog.Content!;
        dialog.Content = null;
        // The bindings resolve against the window's DataContext, which the content leaves behind; the window's own
        // frame is the catalog row's chrome, so the content keeps its margin inside a plain border.
        return new FigureScene(new Border { DataContext = vm, Padding = new Avalonia.Thickness(4, 0, 4, 0), Child = root });
    }
}
