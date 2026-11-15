// brief-paa-3 R-paa3-3 — what a sweep may not move on a Phased Antenna Array in Elements mode: Rows and Cols set its pin count, and a
// pin count that changes between sweep points is a circuit that changes shape mid-run (D4). One function for the sweep engine and for
// `check`, so the run and the check refuse the same thing in the same words.
//
// A Rows or Cols expression depends on the swept VAR when it names it, or names a variable that does (followed through the top level's
// variables and the instance's own cell's), which is how a VAR reaches a parameter in a netlist.

using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Devices;

/// <summary>R-paa3-3 — the structural parameters a sweep may not move.</summary>
public static class PhasedArrayChecks
{
    /// <summary>The refusal for a sweep of <paramref name="variable"/> that would move an Elements-mode array's Rows or Cols, or null.</summary>
    public static string? SweptStructure(TestBench tb, Library? lib, string variable)
    {
        foreach (var (path, inst, cell) in Instances(tb, lib))
        {
            if (!inst.Reference.Equals(PhasedArrayModel.TypeName, StringComparison.OrdinalIgnoreCase)) continue;
            string mode = inst.Overrides.FirstOrDefault(o => o.Name.Equals("Mode", StringComparison.OrdinalIgnoreCase))?.Expression.Trim('"', ' ') ?? "elements";
            if (!mode.Equals("elements", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string name in (string[])["Rows", "Cols"])
            {
                var ov = inst.Overrides.FirstOrDefault(o => o.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (ov is null || !DependsOn(ov.Expression, variable, tb, cell)) continue;
                return $"{PhasedArrayModel.Describe(path)}: the sweep of {variable} moves its {name} ({name}={ov.Expression}), and in Elements mode " +
                       $"{name} sets how many pins it has, which cannot change during a run. Sweep a VAR that {name} does not depend on, or fix {name}.";
            }
        }
        return null;
    }

    private static IEnumerable<(string Path, Instance Inst, Cell? Cell)> Instances(TestBench tb, Library? lib)
    {
        foreach (var inst in tb.Instances)
        {
            yield return (inst.InstanceName, inst, null);
            if (lib?.Find(inst.Reference) is { } child)
                foreach (var x in Within(child, inst.InstanceName, lib, 0)) yield return x;
        }
    }

    private static IEnumerable<(string, Instance, Cell?)> Within(Cell cell, string prefix, Library lib, int depth)
    {
        if (depth > 32) yield break;
        foreach (var inst in cell.Instances)
        {
            string path = $"{prefix}.{inst.InstanceName}";
            yield return (path, inst, cell);
            if (lib.Find(inst.Reference) is { } child)
                foreach (var x in Within(child, path, lib, depth + 1)) yield return x;
        }
    }

    /// <summary>Whether <paramref name="expression"/> names <paramref name="variable"/>, directly or through variables.</summary>
    private static bool DependsOn(string expression, string variable, TestBench tb, Cell? cell)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        void Add(string text)
        {
            try { foreach (string r in AstWalker.CollectRefs(Parser.Parse(text))) if (seen.Add(r)) queue.Enqueue(r); }
            catch (Exception e) when (e is ExpressionException or ParseException) { }
        }
        Add(expression);
        while (queue.Count > 0)
        {
            string name = queue.Dequeue();
            if (name == variable) return true;
            if (cell?.Variables.FirstOrDefault(v => v.Name == name) is { } local) { Add(local.Expression); continue; }
            if (cell?.Parameters.FirstOrDefault(p => p.Name == name) is { } param && param.DefaultExpression is { Length: > 0 } d) Add(d);
            if (tb.GlobalVariables.FirstOrDefault(v => v.Name == name) is { } global) Add(global.Expression);
        }
        return false;
    }
}
