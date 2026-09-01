using System.Linq;
using System.Numerics;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.ViewModels;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The <c>VTone</c> and <c>ITone</c> tiles carry a <c>Phase</c> row, and a value typed into it
/// reaches the matrix as that many DEGREES.
///
/// <para><b>Why this lives here and not beside the model tests.</b> An angle parameter arrives at
/// its model in RADIANS, because the Elaborator applies the row's own unit on the way
/// (<c>TLineModel</c>'s <c>E</c> convention). That makes the ROW's declared unit part of the
/// arithmetic, and a `.cnl` line cannot exercise it — a bare number in a netlist carries no unit and
/// passes through untouched. Only the placed tile does, which is exactly why the double conversion
/// in all four source models survived until a tile was elaborated with a non-zero angle.</para>
///
/// <para>The row is SEEDED rather than left to the user precisely so its unit is not a thing anyone
/// has to know: a hand-added <c>Phase</c> with a blank unit would mean radians.</para>
/// </summary>
public class ToneSourcePhaseTileTests
{
    private const double Deg = System.Math.PI / 180.0;

    [Theory]
    [InlineData(SymbolKind.ToneSource)]
    [InlineData(SymbolKind.CurrentToneSource)]
    public void BothToneTilesSeedAHiddenPhase_CarryingItsDegreeUnit(SymbolKind kind)
    {
        var p = Assert.Single(ComponentTypeRegistry.DefaultParameters(kind, 0), q => q.Name == "Phase");

        Assert.Equal("0", p.Expression);
        Assert.Equal("deg", p.Unit);
        Assert.Equal(UnitDimension.Angle, p.Dimension);
        Assert.False(p.ShowOnSchematic);   // secondary, and 0 on a fresh placement — not a label

        // The unit has to be one the editor's dropdown actually offers, or the seeded row could not
        // be reproduced by hand.
        Assert.Contains(p.Unit, ComponentTypeRegistry.UnitOptions(UnitDimension.Angle));
    }

    [Theory]
    [InlineData(SymbolKind.ToneSource,        45.0)]
    [InlineData(SymbolKind.ToneSource,       -90.0)]
    [InlineData(SymbolKind.CurrentToneSource, 30.0)]
    [InlineData(SymbolKind.CurrentToneSource, 120.0)]
    public void APhaseTypedIntoTheTileDrivesThatManyDegrees(SymbolKind kind, double deg)
    {
        var ec = Elaborate(kind, deg.ToString(System.Globalization.CultureInfo.InvariantCulture));

        var mna = new ExcitationMna();
        ec.Model.Stamp(mna, ec, 2.0 * System.Math.PI * 1e9);

        // A current source injects +j at its first node and −j at its second; the first is the one
        // the user's phase is stated at.
        bool    current = kind == SymbolKind.CurrentToneSource;
        Complex e = current
            ? mna.CurrentInjections[ec.Nodes[0]]
            : Assert.Single(mna.SourceValues.Values, v => v.Magnitude > 1e-15);

        // 1 V for the VTone, 1 mA for the ITone — both the tile's own seeded amplitude.
        double amp = current ? 1e-3 : 1.0;
        Assert.Equal(amp, e.Magnitude, 12);
        Assert.Equal(System.Math.Cos(deg * Deg) * amp, e.Real,      12);
        Assert.Equal(System.Math.Sin(deg * Deg) * amp, e.Imaginary, 12);
    }

    [Fact]
    public void AddingASecondTone_CarriesToneOnesPhaseIntoTheIndexedForm()
    {
        // What the "+" button does. Before Phase was migrated with the rest, a scalar Phase was left
        // stranded beside Freq[1]/V[1] and the multi-tone factory branch — which reads Phase[i] only
        // — dropped it, so adding a second tone silently zeroed the first tone's angle.
        var comp = Placed(SymbolKind.ToneSource, "45");

        var t  = ComponentTypeRegistry.UserParamTemplate(SymbolKind.ToneSource)!;
        var ps = ParameterEditorViewModel.MigrateToneSourceToIndexed(comp.Parameters.ToList());
        for (int i = 0; i < t.NameFormats.Length; i++)
            ps.Add(new EditableParameter
            {
                Name = string.Format(t.NameFormats[i], 2),
                Expression = t.DefaultExpression(i),
                Unit = t.DefaultUnits[i],
                ShowOnSchematic = t.ShowOnSchematic[i],
            });
        comp.Parameters.Clear();
        foreach (var p in ps) comp.Parameters.Add(p);

        Assert.Contains(comp.Parameters, p => p.Name == "Phase[1]" && p.Unit == "deg");

        var model = new SchematicEditModel();
        model.Components.Add(comp);
        var extracted = NetExtractor.Extract(model);
        var ec = Assert.Single(new Elaborator(extracted.Library).Elaborate(extracted.TestBench).Components);

        var mna = new ExcitationMna();
        ec.Model.Stamp(mna, ec, 2.0 * System.Math.PI * 1e9);   // tone 1, at the tile's own 1 GHz

        var e = Assert.Single(mna.SourceValues.Values, v => v.Magnitude > 1e-15);
        Assert.Equal(System.Math.Cos(45 * Deg), e.Real,      12);
        Assert.Equal(System.Math.Sin(45 * Deg), e.Imaginary, 12);
    }

    /// <summary>
    /// Records the RHS a source model writes — the branch value a voltage source pins and the node
    /// current an ideal current source injects. Everything else is a no-op: what is under test here
    /// is the excitation, not the topology around it.
    /// </summary>
    private sealed class ExcitationMna : CircuitRF.Core.IMnaContext
    {
        public System.Collections.Generic.Dictionary<int, Complex> SourceValues      { get; } = [];
        public System.Collections.Generic.Dictionary<int, Complex> CurrentInjections { get; } = [];

        private int _branches;

        public int  AddBranch() => _branches++;
        public void AddAdmittance(int nodeA, int nodeB, Complex y) { }
        public void AddBlockAdmittance(int rowNode, int colNode, Complex y) { }
        public void AddBranchCurrent(int branch, int nodeFrom, int nodeTo) { }
        public void AddConstraint(int branch, int node, Complex coeff) { }
        public void AddNodeBranchCoupling(int node, int branch, Complex coeff) { }
        public void AddBranchConstraint(int branch, int otherBranch, Complex coeff) { }
        public void AddCurrentInjection(int node, Complex j) => CurrentInjections[node] = j;
        public void AddSourceValue(int branch, Complex value) => SourceValues[branch] = value;
    }

    private static ElaboratedComponent Elaborate(SymbolKind kind, string phaseExpression)
    {
        var model = new SchematicEditModel();
        model.Components.Add(Placed(kind, phaseExpression));
        var extracted = NetExtractor.Extract(model);
        return Assert.Single(new Elaborator(extracted.Library).Elaborate(extracted.TestBench).Components);
    }

    private static EditableComponent Placed(SymbolKind kind, string phaseExpression)
    {
        var comp = new EditableComponent { InstanceName = "S1", Symbol = kind, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name,
                Expression = dp.Name == "Phase" ? phaseExpression : dp.Expression,
                Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        return comp;
    }
}
