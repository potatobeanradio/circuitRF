using System.Linq;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The system tiles that have an engine model (brief-sys-2, brief-sys-3), from the palette to the
/// elaborated netlist: a freshly placed <c>Atten</c>, <c>Switch</c>, <c>SwitchD</c>,
/// <c>Circulator</c>, <c>Balun</c>, <c>Coupler</c>, <c>Hybrid90</c>, <c>Hybrid180</c> or
/// <c>Amp</c> must elaborate into the model its glyph promises, with no edit and no hand-written
/// netlist.
///
/// <para>Three pieces of wiring meet here and nothing else exercises all three at once: the registry
/// seeds the parameter defaults, <c>NetExtractor</c> appends the ground returns that turn N pins
/// into 2N nets, and the <c>Elaborator</c> resolves the values and checks the count. A default that
/// the evaluator cannot read — <c>State = On</c>, say, or an unquoted enum name in a numeric slot —
/// is invisible until someone places the tile and presses Simulate.</para>
/// </summary>
public class SystemBlockElaborationTests
{
    private static ElaboratedComponent Elaborate(SymbolKind kind)
    {
        var model = new SchematicEditModel();
        var comp  = new EditableComponent { InstanceName = "X1", Symbol = kind, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name, Expression = dp.Expression, Unit = dp.Unit,
                ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        model.Components.Add(comp);

        var extracted = NetExtractor.Extract(model);
        var netlist   = new Elaborator(extracted.Library).Elaborate(extracted.TestBench);
        return Assert.Single(netlist.Components);
    }

    [Fact]
    public void AFreshlyPlacedAttenuatorIsATenDbPad_MatchedAtBothPorts()
    {
        var ec = Elaborate(SymbolKind.Atten);
        var m  = Assert.IsType<AttenuatorModel>(ec.Model);

        Assert.Equal(4, ec.Nodes.Length);
        Assert.Equal(50.0, m.PortZOf(0));
        Assert.Equal(50.0, m.PortZOf(1));

        var s = m.SAt(2 * System.Math.PI * 1e9);
        // 10 dB — the same default the FACTORY falls back to when the parameter is absent, which is
        // the point: a placed tile and a hand-written line with no Loss must be the same pad.
        Assert.Equal(System.Math.Pow(10.0, -10.0 / 20.0), s[1, 0].Real, 12);
        Assert.Equal(System.Numerics.Complex.Zero, s[0, 0]);   // RL = 200 means MATCHED
    }

    [Fact]
    public void AFreshlyPlacedSpstSwitchIsClosed_AndItsGlyphAgrees()
    {
        var ec = Elaborate(SymbolKind.Switch);
        var m  = Assert.IsType<SwitchModel>(ec.Model);

        Assert.Equal(2, m.PortCount);
        Assert.Equal(4, ec.Nodes.Length);

        // The default is State = 1, which is the ideal through — and the same "1" the glyph reads
        // as SwitchState.On, which is what keeps the picture and the stamp from disagreeing.
        var s = m.SAt(0.0);
        Assert.Equal(System.Numerics.Complex.One,  s[0, 1]);
        Assert.Equal(System.Numerics.Complex.Zero, s[0, 0]);
        Assert.Equal(SwitchState.On, ReadGlyphState(SymbolKind.Switch));
    }

    [Fact]
    public void AFreshlyPlacedSpdtSwitchIsThreePorts_OnThrowOne()
    {
        var ec = Elaborate(SymbolKind.SwitchD);
        var m  = Assert.IsType<SwitchModel>(ec.Model);

        Assert.Equal(3, m.PortCount);          // Throws = 2 is seeded by the TILE, not the reference
        Assert.Equal(6, ec.Nodes.Length);
        Assert.Equal(["com", "1", "2"], m.TerminalNames);

        var s = m.SAt(0.0);
        Assert.Equal(System.Numerics.Complex.One,  s[0, 1]);   // common → throw 1, ideal
        Assert.Equal(System.Numerics.Complex.Zero, s[0, 2]);   // → throw 2, nothing at all
        Assert.Equal(System.Numerics.Complex.One,  s[2, 2]);   // throw 2 is an ideal open
    }

    // ── brief-sys-3: the circulator, the coupler family and the balun ────────

    [Fact]
    public void AFreshlyPlacedCirculatorIsTheIdealPermutationMatrix_TurningTheWayItIsDrawn()
    {
        var ec = Elaborate(SymbolKind.Circulator);
        var m  = Assert.IsType<CirculatorModel>(ec.Model);

        Assert.Equal(3, m.PortCount);
        Assert.Equal(6, ec.Nodes.Length);

        // CW: 1→2, 2→3, 3→1, with no return loss and no reverse leakage stamped at all.
        var s = m.SAt(0.0);
        Assert.Equal(System.Numerics.Complex.One,  s[1, 0]);
        Assert.Equal(System.Numerics.Complex.One,  s[2, 1]);
        Assert.Equal(System.Numerics.Complex.One,  s[0, 2]);
        Assert.Equal(System.Numerics.Complex.Zero, s[0, 1]);
        Assert.Equal(System.Numerics.Complex.Zero, s[0, 0]);

        // and the ARROW the tile draws agrees with the direction the stamp turns — the picture and
        // the matrix read the same parameter, which is the whole point of Direction being one.
        Assert.Equal(Ui.Schematic.CirculatorDirection.CW, ReadGlyphDirection());
    }

    [Fact]
    public void AFreshlyPlacedBalunIsAHalfSplit_ExactlyAntiphase_AndFiftyToAHundred()
    {
        var ec = Elaborate(SymbolKind.Balun);
        var m  = Assert.IsType<BalunModel>(ec.Model);

        Assert.Equal(3, m.PortCount);
        Assert.Equal(6, ec.Nodes.Length);
        Assert.Equal(["unb", "bal+", "bal-"], m.TerminalNames);

        // Zbal is the impedance of EACH balanced port to ground, so the 50/50 defaults are the
        // ordinary 1:2 balun — 100 Ω differential presenting 50 Ω single-ended.
        Assert.Equal(50.0, m.PortZOf(0));
        Assert.Equal(50.0, m.PortZOf(1));
        Assert.Equal(50.0, m.PortZOf(2));

        var s = m.SAt(0.0);
        double half = 1.0 / System.Math.Sqrt(2.0);
        Assert.Equal(new System.Numerics.Complex(+half, 0), s[1, 0]);
        Assert.Equal(new System.Numerics.Complex(-half, 0), s[2, 0]);   // exactly antiphase
    }

    [Theory]
    [InlineData(SymbolKind.Coupler,   20.0,      90.0)]
    [InlineData(SymbolKind.Hybrid90,   3.0103,   90.0)]
    [InlineData(SymbolKind.Hybrid180,  3.0103,  180.0)]
    public void TheThreeCouplerTilesAreOneComponentSeededDifferently(
        SymbolKind kind, double couplingDb, double phaseDeg)
    {
        // ONE engine component for three tiles — the Mixer/MixerD and Switch/SwitchD arrangement.
        // What separates them is two seeded numbers and nothing else, which is exactly what this
        // asserts: same type, same port count, different Coupling and Phase.
        var ec = Elaborate(kind);
        var m  = Assert.IsType<CouplerModel>(ec.Model);

        Assert.Equal(4, m.PortCount);
        Assert.Equal(8, ec.Nodes.Length);
        Assert.Equal(["in", "thru", "cpl", "iso"], m.TerminalNames);

        var s = m.SAt(2 * System.Math.PI * 1e9);
        double c = System.Math.Pow(10.0, -couplingDb / 20.0);

        Assert.Equal(c, s[2, 0].Magnitude, 12);
        Assert.Equal(System.Math.Sqrt(1.0 - c * c), s[1, 0].Magnitude, 12);
        Assert.Equal(-phaseDeg, (s[2, 0].Phase - s[1, 0].Phase) * 180.0 / System.Math.PI, 11);

        // Directivity and RL are off, so the isolated port is not stamped at all.
        Assert.Equal(System.Numerics.Complex.Zero, s[3, 0]);
        Assert.Equal(System.Numerics.Complex.Zero, s[0, 0]);
    }

    /// <summary>
    /// A freshly placed amplifier is 20 dB, 40 dBm OIP3, matched and unilateral (brief-sys-5) — and
    /// the values it takes are the ones the FACTORY falls back to when the parameter is absent, so a
    /// placed tile and a bare hand-written <c>Amp</c> line are the same amplifier.
    ///
    /// <para><c>IP3Ref</c> is the piece nothing else exercises: it is an enum NAME sitting in a
    /// parameter slot, so a tile that seeded it as a number, or an Elaborator that let it reach the
    /// expression evaluator, would fail here and nowhere before Simulate.</para>
    /// </summary>
    [Fact]
    public void AFreshlyPlacedAmplifierIsTwentyDb_MatchedAndUnilateral_WithItsInterceptOutputReferred()
    {
        var ec = Elaborate(SymbolKind.Amp);
        var m  = Assert.IsType<AmplifierModel>(ec.Model);

        Assert.Equal(2, m.PortCount);
        Assert.Equal(4, ec.Nodes.Length);
        Assert.Equal(50.0, m.PortZOf(0));
        Assert.Equal(50.0, m.PortZOf(1));

        var s = m.SAt(2 * System.Math.PI * 1e9);
        Assert.Equal(System.Math.Pow(10.0, 20.0 / 20.0), s[1, 0].Real, 12);
        Assert.Equal(System.Numerics.Complex.Zero, s[0, 0]);   // RLin  = 200 means MATCHED
        Assert.Equal(System.Numerics.Complex.Zero, s[1, 1]);   // RLout = 200 means MATCHED
        Assert.Equal(System.Numerics.Complex.Zero, s[0, 1]);   // S12   = 200 means UNILATERAL

        // IP3 = 40 dBm OUTPUT-referred on a 20 dB amplifier is 20 dBm at the input — so the tile's
        // hidden IP3Ref really did arrive as the string "Output" and really was read.
        double vsatIfInputReferred  = 0.5 * System.Math.Sqrt(2.0 * 1e-3 * System.Math.Pow(10.0, 20.0 / 10.0) * 50.0);
        Assert.Equal(vsatIfInputReferred, m.SaturationVolts, 15);
        Assert.Equal(CircuitRF.Core.ModelKind.Nonlinear, m.Kind);
    }

    /// <summary>
    /// A freshly placed <c>Filter</c> is a 3rd-order 0.1 dB Chebyshev bandpass over 0.9–1.1 GHz,
    /// and it is the SAME filter a hand-written <c>Filter</c> line with nothing said about it is —
    /// the tile's defaults and the factory's fallbacks are the same numbers on purpose.
    ///
    /// <para>Two pieces of wiring meet here that nothing else exercises together: <c>Response</c>
    /// and <c>Form</c> are enum NAMES sitting in parameter slots, so a tile that seeded either as a
    /// number, or an Elaborator that let one reach the expression evaluator, would fail here and
    /// nowhere before Simulate.</para>
    /// </summary>
    [Fact]
    public void AFreshlyPlacedFilterIsAChebyshevBandpassOverItsStatedEdges()
    {
        var ec = Elaborate(SymbolKind.Filter);
        var m  = Assert.IsType<FilterModel>(ec.Model);

        Assert.Equal(2, m.PortCount);
        Assert.Equal(4, ec.Nodes.Length);
        Assert.Equal(50.0, m.PortZOf(0));
        Assert.Equal(50.0, m.PortZOf(1));
        Assert.Equal(CircuitRF.Core.ModelKind.Linear, m.Kind);

        Assert.Equal(CircuitRF.Core.Systems.FilterResponse.Chebyshev, m.Network.Prototype.Response);
        Assert.Equal(CircuitRF.Core.Matching.NetworkForm.Bandpass,    m.Network.Form);
        Assert.Equal(3, m.Network.Prototype.Order);

        // The band edges arrived with their GHz unit applied: the transformed variable is −1 at the
        // lower edge and +1 at the upper, which is the one check that reads both of them at once.
        Assert.Equal(-1.0, m.Network.PrototypeOmega(2 * System.Math.PI * 0.9e9), 12);
        Assert.Equal(+1.0, m.Network.PrototypeOmega(2 * System.Math.PI * 1.1e9), 12);

        // …and the ripple really is 0.1 dB: the band edge sits exactly there.
        double edgeDb = 20.0 * System.Math.Log10(m.SAt(2 * System.Math.PI * 0.9e9)[1, 0].Magnitude);
        Assert.Equal(-0.1, edgeDb, 9);
    }

    /// <summary>
    /// A freshly placed <c>Duplexer</c> is two of those, on two bands that do not overlap — so it is
    /// a working duplexer the moment it is dropped, rather than two filters fighting over the same
    /// frequency. Every one of its twenty prefixed parameters goes through the same resolution path
    /// twice over, including the four enum names.
    /// </summary>
    [Fact]
    public void AFreshlyPlacedDuplexerIsTwoNonOverlappingChebyshevArms()
    {
        var ec = Elaborate(SymbolKind.Duplexer);
        var m  = Assert.IsType<DuplexerModel>(ec.Model);

        Assert.Equal(3, m.PortCount);
        Assert.Equal(6, ec.Nodes.Length);
        Assert.Equal(["ANT", "TX", "RX"], m.TerminalNames);
        Assert.Equal(CircuitRF.Core.ModelKind.Linear, m.Kind);

        foreach (var arm in new[] { m.Tx, m.Rx })
        {
            Assert.Equal(CircuitRF.Core.Systems.FilterResponse.Chebyshev, arm.Prototype.Response);
            Assert.Equal(CircuitRF.Core.Matching.NetworkForm.Bandpass,    arm.Form);
            Assert.Equal(3, arm.Prototype.Order);
        }

        // 0.9–1.0 GHz against 1.1–1.2 GHz: the arms' own edges, and a real gap between them.
        Assert.Equal(-1.0, m.Tx.PrototypeOmega(2 * System.Math.PI * 0.90e9), 12);
        Assert.Equal(+1.0, m.Tx.PrototypeOmega(2 * System.Math.PI * 1.00e9), 12);
        Assert.Equal(-1.0, m.Rx.PrototypeOmega(2 * System.Math.PI * 1.10e9), 12);
        Assert.Equal(+1.0, m.Rx.PrototypeOmega(2 * System.Math.PI * 1.20e9), 12);
    }

    private static Ui.Schematic.CirculatorDirection ReadGlyphDirection()
    {
        var comp = Placed(SymbolKind.Circulator);
        var sym  = comp.ToRenderComponent().InstanceSymbol;
        return ReferenceEquals(sym, BuiltInSymbols.PrimitivesForCirculator(Ui.Schematic.CirculatorDirection.CW))
             ? Ui.Schematic.CirculatorDirection.CW : Ui.Schematic.CirculatorDirection.CCW;
    }

    [Theory]
    [InlineData("0", SwitchState.Off)]
    [InlineData("1", SwitchState.On)]
    [InlineData("Off", SwitchState.Off)]
    [InlineData("On", SwitchState.On)]
    public void TheSpstGlyphReadsTheEnginesOwnStateNumbering(string expression, SwitchState expected)
    {
        // Both spellings resolve, and the NUMERALS are the engine's: State names which throw is
        // closed, so 0 is open and 1 is closed. Numbering the enum in declaration order would have
        // drawn a closed switch open, silently — the failure a glyph selector cannot report.
        Assert.Equal(expected, ReadGlyphState(SymbolKind.Switch, expression));
    }

    [Theory]
    [InlineData("1", SwitchThrow.T1)]
    [InlineData("2", SwitchThrow.T2)]
    public void TheSpdtGlyphPointsAtTheThrowTheParameterNames(string expression, SwitchThrow expected)
    {
        var comp = Placed(SymbolKind.SwitchD, expression);
        Assert.Same(BuiltInSymbols.PrimitivesForSwitchD(expected),
                    comp.ToRenderComponent().InstanceSymbol);
    }

    private static SwitchState ReadGlyphState(SymbolKind kind, string? expression = null)
    {
        var comp = Placed(kind, expression);
        var sym  = comp.ToRenderComponent().InstanceSymbol;
        return ReferenceEquals(sym, BuiltInSymbols.PrimitivesForSwitch(SwitchState.On))
             ? SwitchState.On : SwitchState.Off;
    }

    private static EditableComponent Placed(SymbolKind kind, string? stateExpression = null)
    {
        var comp = new EditableComponent { InstanceName = "SW1", Symbol = kind, X = 0, Y = 0 };
        foreach (var dp in ComponentTypeRegistry.DefaultParameters(kind, 0))
            comp.Parameters.Add(new EditableParameter
            {
                Name = dp.Name,
                Expression = dp.Name == "State" && stateExpression is not null ? stateExpression : dp.Expression,
                Unit = dp.Unit, ShowOnSchematic = dp.ShowOnSchematic, Dimension = dp.Dimension,
            });
        return comp;
    }
}
