using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;

if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: circuitrf <path-to-file.cnl>");
    return 1;
}

var path = args[0];
if (!File.Exists(path))
{
    Console.Error.WriteLine($"File not found: {path}");
    return 1;
}

try
{
    var (lib, tb) = CnlReader.ReadFile(path);
    var nl = new Elaborator(lib).Elaborate(tb);

    Console.WriteLine($"Elaborated netlist: {nl.Components.Count} component(s), {nl.Nodes.Count} node(s)");
    Console.WriteLine();
    Console.WriteLine("Nodes:");
    foreach (var name in nl.Nodes.AllNames)
        Console.WriteLine($"  [{nl.Nodes.IndexOf(name):D2}]  {name}");
    Console.WriteLine();
    Console.WriteLine("Components:");
    foreach (var c in nl.Components)
    {
        var nodeStr  = string.Join(", ", c.Nodes.Select(n => $"{n}({nl.Nodes.NameOf(n)})"));
        var paramStr = string.Join("  ", c.Parameters.Select(kvp =>
            $"{kvp.Key}={kvp.Value}"));
        Console.WriteLine($"  {c.InstancePath,-24}  {c.ComponentType,-6}  nodes=[{nodeStr}]  {paramStr}");
    }

    if (tb.RawDirectives.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("Raw directives (deferred grammar):");
        foreach (var d in tb.RawDirectives)
            Console.WriteLine($"  {d.Kind}  {d.RawLine}");
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Error: {ex.Message}");
    return 1;
}

return 0;
