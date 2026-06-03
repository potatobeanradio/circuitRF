using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;
using Xunit;
using Xunit.Abstractions;

namespace CircuitRF.Engine.Tests.HarmonicBalance;

public class ZPortDiagTest(ITestOutputHelper output)
{
    [Fact]
    public void ZPort_DiagnosticCheck_ScopeVarsPresent()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var c = System.IO.Path.Combine(dir, "testdata", "Hero2");
            if (System.IO.Directory.Exists(c)) { dir = c; break; }
            dir = System.IO.Path.GetDirectoryName(dir);
        }
        Assert.NotNull(dir);

        var (lib, tb) = CnlReader.ReadFile(System.IO.Path.Combine(dir!, "hero2.cnl"));

        output.WriteLine($"GlobalVariables count: {tb.GlobalVariables.Count}");
        foreach (var v in tb.GlobalVariables)
            output.WriteLine($"  Global: {v.Name} = {v.Expression}");

        var netlist = new Elaborator(lib).Elaborate(tb);

        output.WriteLine($"\nComponents count: {netlist.Components.Count}");
        foreach (var ec in netlist.Components)
        {
            output.WriteLine($"  [{ec.ComponentType}] {ec.InstancePath}: model={ec.Model?.GetType().Name ?? "null"}, nodes=[{string.Join(",", ec.Nodes)}], params=[{string.Join(", ", ec.Parameters.Keys)}]");
            if (ec.Model is ZPortModel)
            {
                output.WriteLine($"    *** ZPortModel found! Parameters:");
                foreach (var kv in ec.Parameters)
                    output.WriteLine($"      {kv.Key} = {kv.Value} ({kv.Value.Kind})");
            }
        }

        var zports = netlist.Components.Where(ec => ec.Model is ZPortModel).ToList();
        Assert.True(zports.Count > 0, "No ZPortModel found in netlist");

        foreach (var ec in zports)
        {
            Assert.True(ec.Parameters.ContainsKey("ZPortCount"), $"{ec.InstancePath}: ZPortCount missing");
            Assert.True(ec.Parameters.ContainsKey("Z[1,1]"), $"{ec.InstancePath}: Z[1,1] missing");
            Assert.True(ec.Parameters.ContainsKey("RFfreq"), $"{ec.InstancePath}: RFfreq missing from scope vars");
            Assert.True(ec.Parameters.ContainsKey("ZSource_0") || ec.Parameters.ContainsKey("ZLoad_0"),
                $"{ec.InstancePath}: Z impedance variable missing");
        }
    }
}
