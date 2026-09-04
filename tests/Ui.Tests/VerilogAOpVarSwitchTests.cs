using System;
using System.Linq;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Netlist;
using CircuitRF.Ui.Schematic;
using RfCore.Data;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The per-instance operating-point read-back switch, and the third axis family it puts in front of
/// the Data Display's picker.
///
/// <para><b>What can break here is quiet.</b> A default that reads as "off" publishes nothing and
/// looks exactly like a model that computes nothing; a switch spelled differently in the registry
/// from the factory silently takes its default; and an <c>opvar</c> axis the picker does not
/// recognise becomes a plottable X, drawing one line through a transconductance, a capacitance and
/// a temperature as though they shared a unit.</para>
/// </summary>
public sealed class VerilogAOpVarSwitchTests
{
    // ── The default is ON ─────────────────────────────────────────────────────

    /// <summary>
    /// Seeded true. A read-back that has to be switched on is one nobody discovers, and the whole
    /// point is that a designer can ask a compiled model what it computed without first learning
    /// that they had to enable it.
    /// </summary>
    [Fact]
    public void TheSwitchIsSeededOn_AndIsTheNameTheFactoryReads()
    {
        var seeded = ComponentTypeRegistry.DefaultParameters(SymbolKind.VerilogA, portCount: 2)
                                          .FirstOrDefault(p => p.Name == ComponentModelFactory.VerilogAOpVarsParam);

        Assert.False(seeded == default, "VerilogA does not seed the op-var switch at all");
        Assert.Equal("true", seeded.Expression);

        // It is circuitRF's own, so it must not be removable alongside the model's parameters, and
        // it must not be drawn on the schematic.
        Assert.False(ComponentTypeRegistry.IsRemovableParameter(
            SymbolKind.VerilogA, ComponentModelFactory.VerilogAOpVarsParam));
        Assert.False(seeded.ShowOnSchematic);

        // A model parameter of the user's own stays removable — the rule above is scoped, not blanket.
        Assert.True(ComponentTypeRegistry.IsRemovableParameter(SymbolKind.VerilogA, "vth0"));
    }

    /// <summary>It says what it is, where a user meets it.</summary>
    [Fact]
    public void TheSwitchDescribesItself()
    {
        string d = ComponentTypeRegistry.ParameterDescription(
            SymbolKind.VerilogA, ComponentModelFactory.VerilogAOpVarsParam);

        Assert.NotEqual("", d);
        Assert.Contains("operating-point", d, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// It is not a path, so it gets no Browse button — the same check <c>Model</c> and <c>Pins</c>
    /// already carry, because a field that grows a file dialog is a field that cannot be ticked.
    /// </summary>
    [Fact]
    public void TheSwitchIsNotAFilePath()
        => Assert.False(ComponentTypeRegistry.IsFilePathParameter(
               SymbolKind.VerilogA, ComponentModelFactory.VerilogAOpVarsParam));

    /// <summary>
    /// It survives a <c>.cnl</c> round trip, in both states.
    ///
    /// <para>The repo's standing check on anything added to a component: <b>can <c>CnlWriter</c> say
    /// it?</b> A field the writer cannot express is silently absent from every run, because a run is
    /// a write-then-read through a generated netlist — so a switch that did not round-trip would be
    /// permanently on, and the checkbox would appear to work while changing nothing.</para>
    /// </summary>
    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void TheSwitchSurvivesACnlRoundTrip(string state)
    {
        string cnl = $"VerilogA:X1  a b  File=/tmp/m.osdi Model=M Pins=2 OpVars={state}\n";

        var (lib, tb) = new CnlReader().Read(cnl);
        string once = CnlWriter.Write(tb, lib);
        Assert.Contains($"OpVars={state}", once);

        // And again, so a value that survives one pass but not the next is caught too.
        var (lib2, tb2) = new CnlReader().Read(once);
        Assert.Contains($"OpVars={state}", CnlWriter.Write(tb2, lib2));
    }

    // ── The picker's third axis family ────────────────────────────────────────

    /// <summary>
    /// <c>opvar</c> is a LABEL axis, like <c>node</c> and <c>branch</c> — which quantity, not a
    /// condition the run swept. Reading it as a sweep makes it the default X, and the plot that
    /// results draws one line through values that share no unit.
    /// </summary>
    [Fact]
    public void TheOpVarAxisIsALabelAxis_NotAPlottableOne()
    {
        var opAxis    = new Axis("opvar", [0.0, 1.0], "", ["X1.gm", "X1.cgs"]);
        var sweepAxis = new Axis("VGS",   [0.0, 1.0], "V");

        // A bare OP cube from an unswept DC run has nothing to plot ALONG: every axis is a label.
        Assert.Equal(-1, CircuitRF.Ui.DataDisplay.ViewModels.TraceRowViewModel.DefaultXAxis(
            new DataCube([opAxis], new double[2])));

        // Swept, the sweep is the X and the op-var stays pinned carrying its own name.
        Assert.Equal(0, CircuitRF.Ui.DataDisplay.ViewModels.TraceRowViewModel.DefaultXAxis(
            new DataCube([sweepAxis, opAxis], new double[4])));

        // …and the same answer for node/branch, which is the rule this joins rather than a new one.
        Assert.Equal(0, CircuitRF.Ui.DataDisplay.ViewModels.TraceRowViewModel.DefaultXAxis(
            new DataCube([sweepAxis, new Axis("node", [0.0, 1.0], "V", ["a", "b"])], new double[4])));
    }
}
