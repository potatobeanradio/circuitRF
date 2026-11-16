using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The ladder's (MLAD's) Properties readout: one line per section of its list with what it is at F — a line's or stub's
/// impedance and electrical length, a taper's two impedances, a gap's capacitances — and a list that does not parse shows
/// the refusal naming the section instead.
/// </summary>
public class LadderReadoutTests
{
    private static ParameterEditorViewModel Editor(string sections)
    {
        var model = new SchematicEditModel();
        var comp = new EditableComponent { Symbol = SymbolKind.Mlad, InstanceName = "LAD1" };
        comp.Parameters.Add(new EditableParameter { Name = "Sections", Expression = sections });
        comp.Parameters.Add(new EditableParameter { Name = "Z0", Expression = "50", Unit = "Ω" });
        comp.Parameters.Add(new EditableParameter { Name = "F", Expression = "2.4", Unit = "GHz" });
        model.Components.Add(comp);
        var editor = new ParameterEditorViewModel();
        editor.SetTargetDirect(new SchematicViewModel(model), comp, showClose: false);
        return editor;
    }

    [Fact]
    public void TheReadout_ListsEachSectionsImpedanceAndLengthAtF()
    {
        var editor = Editor("line(W=1.1mm, L=18mm); gap(S=0.2mm); taper(W1=1.1mm, W2=0.3mm, L=5mm); "
                          + "stub(W=0.6mm, L=9mm, side=both); stub(W=1.1mm, L=9mm, end=short, side=down)");
        Assert.True(editor.IsDividerReadoutTarget);
        string[] lines = editor.DividerReadoutText.Split('\n');
        Assert.Equal(7, lines.Length);
        Assert.StartsWith("At F, ports' lines W ", lines[0]);
        Assert.Matches(@"^1 line: \d+(\.\d)? Ω, \d+(\.\d)?°$", lines[1]);
        Assert.Matches(@"^2 gap at W 1\.1 mm: Cg \d+(\.\d)? fF, Cp \d+(\.\d)? fF each end$", lines[2]);
        Assert.Matches(@"^3 taper: \d+(\.\d)? Ω to \d+(\.\d)? Ω, \d+(\.\d)?°$", lines[3]);
        Assert.Matches(@"^4 stub, open, both sides: \d+(\.\d)? Ω, \d+(\.\d)?° each \(\+\d+(\.\d)?° open end\)$", lines[4]);
        Assert.Matches(@"^5 stub, short, down: \d+(\.\d)? Ω, \d+(\.\d)?°$", lines[5]);
        Assert.Equal("A via to ground on G1", lines[6]);
        Assert.Equal("", editor.DividerReadoutNote);
    }

    [Fact]
    public void AListThatDoesNotParse_ShowsTheRefusalNamingTheSection()
    {
        var editor = Editor("line(W=1.1mm, L=18mm); stub(W=1mm)");
        Assert.Equal("", editor.DividerReadoutText);
        Assert.Contains("section 2 (stub(W=1mm))", editor.DividerReadoutNote);
    }
}
