using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Design.Circuit;
using CircuitRF.Design.Optimization;
using CircuitRF.Design.Schematic;
using CircuitRF.Ui.Docking;
using CircuitRF.Ui.Tests.Optimization;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Yield;

namespace CircuitRF.Ui.Tests.Statistics;

/// <summary>
/// brief-sens-3's fixture: a Yield panel and a Sensitivity panel over ONE schematic, as the shell hosts them. The bench is
/// <paramref name="body"/> plus the schematic's tuning block as it stands, so every edit a panel makes is what the next
/// plan and run read. Thread hops play inline; a run's thread is the test's to start.
/// </summary>
internal sealed class SensitivityPanelFixture
{
    public readonly SchematicEditModel Model = new();
    public readonly SchematicViewModel Top;
    public readonly YieldPanelViewModel Yield = new();
    public readonly YieldPanelViewModel Sens = new() { AvailableModes = YieldPanelViewModel.SensitivityModes };
    public Action? Started;
    public int Runs;
    private readonly Func<SchematicEditModel, string> _body;

    public SensitivityPanelFixture(Func<SchematicEditModel, string> body, string tuning, Func<SchematicViewModel, TunableCatalog>? discover = null,
                                   params EditableComponent[] parts)
    {
        _body = body;
        foreach (var p in parts) Model.Components.Add(p);
        var (_, tb) = new CnlReader().Read(body(Model) + "\n" + tuning);
        Model.Tuning = tb.Tuning;
        Top = new SchematicViewModel(Model);
        foreach (var panel in new[] { Yield, Sens })
        {
            panel.Discover        = discover ?? (vm => Prepare(vm) is { Lib: { } lib, Tb: { } tb } ? TunableCatalog.FromNetlist(tb, lib) : throw new InvalidOperationException());
            panel.PrepareCircuit  = Prepare;
            panel.SourcePathFor   = _ => null;
            panel.StartBackground = a => { Started = a; Runs++; return Task.CompletedTask; };
            panel.SetActiveSchematic(Top, "tb.csch");
        }
    }

    public PreparedCircuit Prepare(SchematicViewModel vm)
        => PreparedCircuit.FromText(_body(vm.EditModel) + "\n" + string.Join("\n", TuningDirectiveText.Write(vm.EditModel.Tuning ?? new TuningSetup())),
                                    null, null);

    /// <summary>Runs the sensitivity the panel prepared, to its end.</summary>
    public void RunToEnd()
    {
        Sens.RunCommand.Execute(null);
        Assert.NotNull(Started);
        Started!();
        Assert.True(Sens.IsFinished, Sens.StatusText);
    }

    /// <summary>The divider (R1 toleranced, R2 not) with a yield goal.</summary>
    public static SensitivityPanelFixture Divider() => new(_ => """
        Vdc:V1 in  0   Vdc=1
        R:R1   in  out R=1100 Ohm
        R:R2   out 0   R=1000 Ohm
        analysis DC1 type=dc
        """, """
        tune R1.R dist=gauss sd=2%
        goal Vout = DC1.V("out") analysis=DC1 in 0.45 0.51 use=yield
        """,
        vm => TunableCatalog.Discover(vm.EditModel, TuningFixture.Resolver()),
        TuningFixture.Part("R1", SymbolKind.Resistor, 0, ("R", "1100", "Ohm")),
        TuningFixture.Part("R2", SymbolKind.Resistor, 100, ("R", "1000", "Ohm")));
}

/// <summary>R-sens3-2, R-sens3-4, R-sens3-5: two panels, one design; the levels regroup one result; the blocks section.</summary>
public sealed class SensitivityPanelTests
{
    [Fact]
    public void WithNoSchematic_TheSensitivityPanelTalksAboutSensitivity_NotYield()
    {
        Assert.Equal(YieldPanelViewModel.NoSchematicText, new YieldPanelViewModel().NoSchematicMessage);
        Assert.Equal(YieldPanelViewModel.SensitivityNoSchematicText,
                     new YieldPanelViewModel { AvailableModes = YieldPanelViewModel.SensitivityModes }.NoSchematicMessage);
    }

    [Fact]
    public void AToleranceAddedInSensitivity_IsInYieldAtOnce_AndOneUndoRemovesIt()
    {
        var f = SensitivityPanelFixture.Divider();
        Assert.Equal(YieldMode.Sensitivity, f.Sens.Mode);
        Assert.False(f.Sens.ShowModeRow);
        Assert.True(f.Yield.ShowModeRow);
        Assert.Equal(["R1.R"], f.Yield.Variables.Select(v => v.Key));

        f.RunToEnd();
        var r2 = f.Sens.SensRows.Single(r => r.Keys.SequenceEqual(["R2.R"]));
        Assert.Equal("1 %", r2.Basis);
        Assert.False(r2.IsFailed);

        int steps = f.Top.UndoRedo.Entries.Count();
        r2.AddToleranceCommand.Execute(null);
        Assert.Equal(steps + 1, f.Top.UndoRedo.Entries.Count());
        Assert.Contains(f.Yield.Variables, v => v.Key == "R2.R" && v.IsStat);   // no refresh asked of the Yield panel

        f.Top.UndoRedo.Undo();
        Assert.DoesNotContain(f.Yield.Variables, v => v.Key == "R2.R");
        Assert.False(f.Sens.IsToleranced("R2.R"));
    }

    [Fact]
    public void EntryPortInstance_RegroupOneResult_WithNoSecondRun()
    {
        string body = $"""
            Port:P1 a 0 Num=1 Z=50 Ohm
            Port:P2 b 0 Num=2 Z=50 Ohm
            SnP:SNP1 a b c NumPorts=3 File="{SnpFiles.Reciprocal3}"
            R:R3 c 0 R=30 Ohm
            analysis SP1 type=sparam start=1 stop=3 npts=3 Unit=GHz
            """;
        var f = new SensitivityPanelFixture(_ => body, "goal Thru = dB(SP1.S(2,1)) analysis=SP1 ge -30");
        f.RunToEnd();
        long evaluations = f.Sens.SensResult!.Evaluations;

        Assert.Equal(7, f.Sens.SensRows.Count);                           // 6 SnP entries (tied 3-port) and R3.R
        Assert.Contains(f.Sens.SensRows, r => r.Label == "SNP1 · S31" && r.Basis == "0.1 dB · 1°");

        f.Sens.IsByPort = true;
        Assert.Equal("share (overlapping)", f.Sens.ShareHeader);
        Assert.Equal(4, f.Sens.SensRows.Count);                           // three ports and R3, which has none
        Assert.Contains(f.Sens.SensRows, r => r.Label == "SNP1 · port 1" && r.Port == 1);

        f.Sens.IsByInstance = true;
        Assert.Equal(["SNP1", "R3"], f.Sens.SensRows.Select(r => r.Label).Order(StringComparer.Ordinal).Reverse());
        var snp = f.Sens.SensRows.Single(r => r.Label == "SNP1");
        Assert.True(snp.CanExpand);
        snp.ToggleExpandCommand.Execute(null);
        Assert.Equal(1 + 6 + 1, f.Sens.SensRows.Count);                   // its six entries open under it
        var s31 = f.Sens.SensRows.Single(r => r.Label == "SNP1 · S31");
        s31.ToggleExpandCommand.Execute(null);
        Assert.Contains(f.Sens.SensRows, r => r.Depth == 2 && r.Keys.SequenceEqual(["SNP1.SdB_3_1"]) && r.Label == "dB");

        Assert.Equal(1, f.Runs);
        Assert.Equal(evaluations, f.Sens.SensResult!.Evaluations);

        // Add tolerance on an S-parameter entry: an offset is 0 at nominal, so its σ is absolute, never a percent of 0.
        s31.AddToleranceCommand.Execute(null);
        Assert.DoesNotContain("Refused", f.Sens.StatusText);
        Assert.Equal("0.1 dB", f.Model.Tuning!.Variables.Single(v => v.Key == "SNP1.SdB_3_1").Spread!.Sd);
        Assert.Equal("1 deg", f.Model.Tuning!.Variables.Single(v => v.Key == "SNP1.Sdeg_3_1").Spread!.Sd);
    }

    [Fact]
    public void UntickingABlock_WritesSnpexcludeAsOneStep_LowersTheCostByTwiceItsFactors_AndTheMasterDisablesWithoutUnticking()
    {
        string body = $"""
            Port:P1 a 0 Num=1 Z=50 Ohm
            Port:P2 b 0 Num=2 Z=50 Ohm
            SnP:SNP1 a m NumPorts=2 File="{SnpFiles.Attenuator}"
            R:R1 m 0 R=200 Ohm
            SnP:SNP2 m n NumPorts=2 File="{SnpFiles.Attenuator}"
            R:R2 n b R=10 Ohm
            analysis SP1 type=sparam start=1 stop=3 npts=3 Unit=GHz
            """;
        var f = new SensitivityPanelFixture(_ => body, "goal Thru = dB(SP1.S(2,1)) analysis=SP1 ge -30");
        Assert.Empty(f.Yield.SnpBlocks);                                  // the Yield panel's modes have no blocks section
        Assert.Equal(["SNP1", "SNP2"], f.Sens.SnpBlocks.Select(b => b.Instance));
        var snp2 = f.Sens.SnpBlocks[1];
        Assert.Equal("2 ports · tied · 6 factors", snp2.Summary);
        int cost = f.Sens.SensPlannedEvaluations;

        int steps = f.Top.UndoRedo.Entries.Count();
        snp2.IsIncluded = false;
        Assert.Equal(steps + 1, f.Top.UndoRedo.Entries.Count());
        Assert.Equal("SNP2", f.Model.Tuning!.Sensitivity!.SnpExclude);
        Assert.Equal(cost - 2 * 6, f.Sens.SensPlannedEvaluations);

        f.Sens.SensSnp = false;
        Assert.All(f.Sens.SnpBlocks, b => Assert.False(b.IsEnabled));
        Assert.True(f.Sens.SnpBlocks[0].IsIncluded);
        Assert.False(f.Sens.SnpBlocks[1].IsIncluded);
        f.Sens.SensSnp = true;
        Assert.Equal("SNP2", f.Model.Tuning!.Sensitivity!.SnpExclude);    // the choices come back with the master
    }

    [Fact]
    public void BothDefaults_OrderTuningOptimizerSensitivityYield_AndAnOldLayoutGainsItAfterTheOptimizer()
    {
        Assert.Contains(DockPanelIds.Sensitivity, DockPanelIds.All);
        string[] order = [DockPanelIds.Tuning, DockPanelIds.Optimizer, DockPanelIds.Sensitivity, DockPanelIds.Yield];
        foreach (var layout in new[] { DockLayoutDefaults.Default(), DockLayoutDefaults.ProjectTreeAndLibrary() })
        {
            var tabs = layout.Panels.Where(p => order.Contains(p.Id)).ToList();
            Assert.Single(tabs.Select(p => (p.Side, p.Group)).Distinct());
            Assert.Equal(order, tabs.OrderBy(p => p.Order).Select(p => p.Id));
        }

        static IReadOnlyList<string> Dock(CwsDockLayout l, string id)
        {
            var at = l.Panels.Single(p => p.Id == id);
            return [.. l.Panels.Where(p => p.Side == at.Side && p.Group == at.Group && p.Inboard == at.Inboard).OrderBy(p => p.Order).Select(p => p.Id)];
        }

        // Saved before the panel existed: it lands immediately before Yield (so after the Optimizer), and the input is
        // not modified.
        var old = DockLayoutDefaults.ProjectTreeAndLibrary();
        old.Panels.RemoveAll(p => p.Id == DockPanelIds.Sensitivity);
        int yieldOrder = old.Panels.Single(p => p.Id == DockPanelIds.Yield).Order;
        var filled = DockLayoutDefaults.WithMissingPanelsFilled(old);
        Assert.Equal([DockPanelIds.Properties, DockPanelIds.Analyses, DockPanelIds.Tuning, DockPanelIds.Optimizer, DockPanelIds.Sensitivity, DockPanelIds.Yield],
                     Dock(filled, DockPanelIds.Sensitivity));
        Assert.Equal(yieldOrder, old.Panels.Single(p => p.Id == DockPanelIds.Yield).Order);

        // Neither Sensitivity nor Yield saved, and the Optimizer moved to the right column, alone with the Library: both
        // follow the Optimizer there, in the default order.
        var moved = DockLayoutDefaults.ProjectTreeAndLibrary();
        moved.Panels.RemoveAll(p => p.Id is DockPanelIds.Sensitivity or DockPanelIds.Yield);
        var opt = moved.Panels.Single(p => p.Id == DockPanelIds.Optimizer);
        opt.Side = DockSide.Right;
        opt.Group = 0;
        opt.Order = 1;
        var refilled = DockLayoutDefaults.WithMissingPanelsFilled(moved);
        Assert.Equal([DockPanelIds.Palette, DockPanelIds.Optimizer, DockPanelIds.Sensitivity, DockPanelIds.Yield],
                     Dock(refilled, DockPanelIds.Sensitivity));
    }
}

/// <summary>R-sens3-6: Tie writes Reciprocal= and removes the tune lines it ends, as one undo step.</summary>
public sealed class SnpMatrixTileTests
{
    [Fact]
    public async Task TyingWithAnUpperTriangleToleranceLine_RemovesItInTheSameUndoStep()
    {
        var snp = new EditableComponent { InstanceName = "SNP1", Symbol = SymbolKind.Snp };
        snp.Parameters.Add(new EditableParameter { Name = "NumPorts", Expression = "3" });
        snp.Parameters.Add(new EditableParameter { Name = "File", Expression = SnpFiles.NonReciprocal3 });
        static string Body(SchematicEditModel m)
        {
            var c = m.Components.Single(x => x.InstanceName == "SNP1");
            string? recip = c.Parameters.FirstOrDefault(p => p.Name == "Reciprocal")?.Expression;
            return $"""
                Port:P1 a 0 Num=1 Z=50 Ohm
                Port:P2 b 0 Num=2 Z=50 Ohm
                SnP:SNP1 a b c NumPorts=3 File="{SnpFiles.NonReciprocal3}"{(recip is null ? "" : " Reciprocal=" + recip)}
                R:R3 c 0 R=50 Ohm
                analysis SP1 type=sparam start=1 stop=3 npts=3 Unit=GHz
                """;
        }
        var f = new SensitivityPanelFixture(Body, """
            tune SNP1.SdB_1_3 dist=gauss sd=0.15 dB
            tune SNP1.SdB_3_1 dist=gauss sd=0.15 dB
            goal Thru = dB(SP1.S(2,1)) analysis=SP1 ge -30
            """, null, snp);
        var asked = new List<IReadOnlyList<string>>();
        f.Sens.Confirm = (_, lines) => { asked.Add(lines); return Task.FromResult(true); };

        f.Sens.SelectedSnpBlock = f.Sens.SnpBlocks.Single();
        var tile = f.Sens.SnpTile!;
        Assert.False(tile.IsTied);                                         // amplifier-shaped: untied on its own
        Assert.Equal(9, tile.Cells.Count);
        Assert.StartsWith("auto: worst |S(i,j) − S(j,i)| = ", tile.TieTip);

        int steps = f.Top.UndoRedo.Entries.Count();
        Assert.True(await tile.SetTiedAsync(true));
        Assert.Equal(["tune SNP1.SdB_1_3"], Assert.Single(asked));
        Assert.Equal(steps + 1, f.Top.UndoRedo.Entries.Count());
        Assert.Equal("tied", snp.Parameters.Single(p => p.Name == "Reciprocal").Expression);
        Assert.Equal(["SNP1.SdB_3_1"], f.Model.Tuning!.Variables.Select(v => v.Key));
        Assert.True(f.Sens.SnpTile!.IsTied);

        f.Top.UndoRedo.Undo();
        Assert.DoesNotContain(snp.Parameters, p => p.Name == "Reciprocal");
        Assert.Equal(["SNP1.SdB_1_3", "SNP1.SdB_3_1"], f.Model.Tuning!.Variables.Select(v => v.Key));
    }
}
