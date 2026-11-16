// brief PCH-12 gate 1, the routine half: each of the three Propagation Channel examples' benches passes `check` and is still
// the netlist it was drawn from (PropagationExamplesAuthoring) — the same parts, analyses and measures — so its README's
// numbers come from the bench that ships. Running them at their shipped settings (2 s, 3 s, 20 s and a minute in a Release
// build) was done when they were written and is recorded in each README; it is not repeated here.

using CircuitRF.Cli;
using CircuitRF.Core.Netlist;
using Xunit;

namespace CircuitRF.Ui.Tests.Examples;

public sealed class PropagationExamplesTests
{
    public static TheoryData<string, string> Benches()
    {
        var d = new TheoryData<string, string>();
        foreach (var b in PropagationExamplesAuthoring.Benches) d.Add(b.Folder, b.Cell);
        return d;
    }

    [Theory]
    [MemberData(nameof(Benches))]
    public void EachBench_PassesCheck_AndIsItsNetlist(string folder, string cell)
    {
        string sch = Path.Combine(PropagationExamplesAuthoring.Examples, folder, cell, "schematic", cell + ".csch");
        Assert.Equal(0, CliEntry.Run(["check", sch]));

        var (_, tb) = CircuitSource.FromSchematic(sch);
        var (_, want) = new CnlReader().Read(PropagationExamplesAuthoring.Benches.Single(b => b.Folder == folder && b.Cell == cell).Netlist,
                                                    cell, Path.Combine(PropagationExamplesAuthoring.Examples, folder));
        Assert.Equal(want.Analyses.Select(a => (a.Name, a.GetType())), tb.Analyses.Select(a => (a.Name, a.GetType())));
        Assert.Equal(want.Measurements.Select(m => m.Name), tb.Measurements.Select(m => m.Name));
        Assert.Equal(want.GlobalVariables.Select(v => (v.Name, v.Expression, v.Unit)).OrderBy(x => x.Name),
                     tb.GlobalVariables.Select(v => (v.Name, v.Expression, v.Unit)).OrderBy(x => x.Name));
        Assert.Equal(want.Instances.Select(i => i.InstanceName).Order(), tb.Instances.Select(i => i.InstanceName).Order());
    }

    [Fact]
    public void TheOfficeProfile_IsTheOneTheReadmeDescribes()
    {
        string csv = File.ReadAllText(Path.Combine(PropagationExamplesAuthoring.Examples, PropagationExamplesAuthoring.IotFolder,
                                                   PropagationExamplesAuthoring.IotProfile));
        Assert.Equal(PropagationExamplesAuthoring.IotProfileCsv.ReplaceLineEndings("\n") + "\n", csv);
    }
}
