using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Interchange;
using CircuitRF.Ui.Views.Dialogs;

namespace CircuitRF.Ui.Views.WBond;

/// <summary>
/// DXF in and out for the wBond editor (wbond.md §9.4) — the bridge to the assembly house.
///
/// <para><b>Two entry points, because they answer different questions.</b> <b>Export DXF…</b> writes
/// the reference layout AND the wires together — that is the file you send out. <b>Import Wires…</b>
/// reads only the 3D polylines from a DXF into the CURRENT document, leaving its layout untouched and
/// creating no new cell — that is how a bond list drawn elsewhere joins a design you already
/// have.</para>
/// </summary>
public partial class WBondEditorView
{
    private static readonly FilePickerFileType DxfFileType =
        new("DXF drawing") { Patterns = ["*.dxf"] };

    // ---------------------------------------------------------------- export

    private async void OnExportDxf(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await ExportDxfAsync();

    internal async Task ExportDxfAsync()
    {
        if (_bound is null) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export DXF",
            DefaultExtension = "dxf",
            FileTypeChoices = [DxfFileType],
            SuggestedFileName = "wirebonds.dxf",
        });

        if (file?.TryGetLocalPath() is not { } path) return;

        try
        {
            var layout = _bound.ReferenceLayout;

            // A wBond document may carry no reference geometry at all (§10's third entry point), so
            // the wires alone are a legitimate export — an empty root structure still gives the writer
            // the block it needs.
            var view = layout?.Model ?? new LayoutView();
            var structure = new InterchangeStructure("WBOND", view.Shapes, view.Instances);

            var plan = new DxfExport.ExportPlan(
                UnresolvedInstanceReferences: [],
                BlockNameByCellName: new System.Collections.Generic.Dictionary<string, string> { ["WBOND"] = "WBOND" },
                Structures: [structure],
                RootStructureName: "WBOND",
                Tech: layout?.Technology,
                DbuPerMicron: view.DbuPerMicron);

            var summary = DxfExport.Write(path, plan, new DxfExportOptions(), _bound.Editor.Design);

            ShowStatus($"Exported {summary.WiresWritten} wire(s) to {Path.GetFileName(path)}.");
        }
        catch (Exception ex)
        {
            ShowStatus("DXF export failed: " + ex.Message, isWarning: true);
        }
    }

    // ---------------------------------------------------------------- import

    private async void OnImportWires(object? sender, Avalonia.Interactivity.RoutedEventArgs e) =>
        await ImportWiresAsync();

    internal async Task ImportWiresAsync()
    {
        if (_bound is null) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Wires from DXF",
            AllowMultiple = false,
            FileTypeFilter = [DxfFileType],
        });

        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;

        try
        {
            await using var stream = File.OpenRead(path);

            // Resolve() sniffs $ACADVER/$DWGCODEPAGE and rewinds — a pre-R2007 file is not UTF-8, and
            // reading one as if it were mangles every layer name without ever throwing.
            var encoding = DxfEncoding.Resolve(stream);
            using var reader = new StreamReader(stream, encoding.Encoding);

            var dxf = DxfReader.Read(reader);

            if (dxf.WirePolylines.Count == 0)
            {
                // Said plainly rather than silently doing nothing: a DXF with no wire layers is the
                // most likely reason this appears to have failed, and the fix is a layer name.
                ShowStatus($"No wires found — this DXF has no 3D polylines on a \"{DxfWireIo.LayerPrefix}\" layer.",
                           isWarning: true);
                return;
            }

            double nmPerDrawingUnit =
                DxfUnits.NanometersPerDrawingUnit(dxf.InsUnits)
                ?? DxfUnits.NanometersPerDrawingUnit(DxfUnits.DefaultPromptUnits)!.Value;

            var incoming = DxfWireIo.BuildDesign(dxf.WirePolylines, nmPerDrawingUnit);
            int added = _bound.Editor.MergeWires(incoming);

            string units = dxf.InsUnits == 0 ? " (file stated no units; read as mm)" : "";
            ShowStatus($"Imported {added} wire(s) in {incoming.Arrays.Count} group(s){units}.");

            RepaintBoth();
        }
        catch (Exception ex)
        {
            ShowStatus("Wire import failed: " + ex.Message, isWarning: true);
        }
    }

    /// <summary>
    /// Reports into the toolbar strip rather than a modal — the same surface a refused edit uses.
    /// An import that did nothing must not be silent, but it also does not need a decision.
    /// </summary>
    internal void ShowStatus(string message, bool isWarning = false)
    {
        if (isWarning)
            QualityText.Foreground = this.FindResource("CrfWarningBrush") as Avalonia.Media.IBrush;
        else
            QualityText.ClearValue(TextBlock.ForegroundProperty);

        QualityText.Text = message;
        _refusalShowing = isWarning;
    }
}
