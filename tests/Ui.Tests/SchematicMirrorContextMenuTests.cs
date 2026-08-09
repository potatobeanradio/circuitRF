using System.IO;
using System.Xml.Linq;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Owner request: a Mirror command on the schematic component context menu, beside the existing
/// Rotate 90°. The commands themselves already existed (toolbar buttons and the M / Shift+M keys) —
/// the menu entries route to the SAME <c>OnMirrorH</c>/<c>OnMirrorV</c> handlers, so the three
/// surfaces cannot drift apart. A <c>UserControl</c> cannot be constructed headlessly here, so the
/// wiring is pinned by parsing the real AXAML; the behaviour behind it is exercised through the
/// view model.
/// </summary>
public sealed class SchematicMirrorContextMenuTests
{
    [Fact]
    public void TheContextMenuOffersBothMirrorDirections_WiredToTheToolbarsOwnHandlers()
    {
        var menu = LoadContextMenu();

        var items = menu.Elements()
            .Where(e => e.Name.LocalName == "MenuItem")
            .Select(e => (Header: (string?)e.Attribute("Header"), Click: (string?)e.Attribute("Click")))
            .ToList();

        Assert.Contains(items, i => i.Header == "Mirror Horizontal" && i.Click == "OnMirrorH");
        Assert.Contains(items, i => i.Header == "Mirror Vertical"   && i.Click == "OnMirrorV");
    }

    [Fact]
    public void MirrorSitsDirectlyBelowRotate_NotBuriedAtTheEnd()
    {
        var headers = LoadContextMenu().Elements()
            .Where(e => e.Name.LocalName == "MenuItem")
            .Select(e => (string?)e.Attribute("Header"))
            .ToList();

        int rotate = headers.IndexOf("Rotate 90°");
        Assert.True(rotate >= 0, "Rotate 90° not found — has the menu been restructured?");
        Assert.Equal("Mirror Horizontal", headers[rotate + 1]);
        Assert.Equal("Mirror Vertical",   headers[rotate + 2]);
    }

    [Fact]
    public void MirrorHorizontal_FlipsTheSelectedComponent_AndUndoRestoresIt()
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent
        {
            InstanceName = "R1",
            Symbol       = SymbolKind.Resistor,
            Rotation     = SymbolRotation.R90,
        };
        model.Components.Add(comp);

        var vm = new SchematicViewModel(model, messageSink: null);
        vm.Selection.SetAll([comp.Id]);

        vm.MirrorSelection(horizontal: true);
        Assert.True(comp.MirrorX);
        Assert.Equal(SymbolRotation.R90, comp.Rotation);   // horizontal mirror leaves rotation alone

        vm.UndoRedo.Undo();
        Assert.False(comp.MirrorX);
        Assert.Equal(SymbolRotation.R90, comp.Rotation);
    }

    [Fact]
    public void MirrorVertical_FlipsAndRotates180_AndUndoRestoresBoth()
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent
        {
            InstanceName = "R1",
            Symbol       = SymbolKind.Resistor,
            Rotation     = SymbolRotation.R0,
        };
        model.Components.Add(comp);

        var vm = new SchematicViewModel(model, messageSink: null);
        vm.Selection.SetAll([comp.Id]);

        vm.MirrorSelection(horizontal: false);
        Assert.True(comp.MirrorX);
        Assert.Equal(SymbolRotation.R180, comp.Rotation);

        vm.UndoRedo.Undo();
        Assert.False(comp.MirrorX);
        Assert.Equal(SymbolRotation.R0, comp.Rotation);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static XElement LoadContextMenu()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);

        var doc = XDocument.Load(Path.Combine(dir!.FullName, "src/Ui/Views/Content/SchematicView.axaml"));
        var menu = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "ContextMenu"
                              && (string?)e.Attribute(XName.Get("Name", "http://schemas.microsoft.com/winfx/2006/xaml"))
                                 == "ComponentContextMenu");
        Assert.NotNull(menu);
        return menu!;
    }
}
