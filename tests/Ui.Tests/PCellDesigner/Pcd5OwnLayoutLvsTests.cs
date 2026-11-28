// Owner: PCD-5 (brief-pcd-5 §3).

using CircuitRF.Core.Expressions;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Lvs;
using CircuitRF.Design.Layout.SchematicSync;
using CircuitRF.Design.Layout.PCells;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Diagnostics;
using CircuitRF.Ui.Tests.Layout.PCells;

namespace CircuitRF.Ui.Tests.PCellDesigner;

/// <summary>A designed cell's saved layout (the planner's output at the defaults) is LVS clean against its own schematic.</summary>
[Collection(PCellResolverCollection.Name)]
public sealed class Pcd5OwnLayoutLvsTests
{
    [Fact]
    public void PadTeeIsLvsCleanOnItsOwnLayout() => LvsCleanOnItsOwnLayout("PadTee", angleRowWithoutDimension: true);

    [Fact]
    public void Wilkinson3IsLvsCleanOnItsOwnLayout() => LvsCleanOnItsOwnLayout("Wilkinson3");

    /// <param name="angleRowWithoutDimension">SH1's PinDir[1] row saved with no Dimension, as a pin added after placement
    /// is: LVS still compares it as an angle, never as "schematic 3.1416, layout 180".</param>
    private static void LvsCleanOnItsOwnLayout(string cell, bool angleRowWithoutDimension = false)
    {
        string source = Path.Combine(Pcd5Workspace.TestData, "..", "examples", "PCell Designer");
        string ws = Path.Combine(Path.GetTempPath(), "pcd5-lvs-" + Guid.NewGuid().ToString("N")[..10]);
        try
        {
            foreach (string dir in new[] { "tech", cell })
                CopyTree(Path.Combine(source, dir), Path.Combine(ws, dir));
            File.Copy(Path.Combine(source, ".cws"), Path.Combine(ws, ".cws"));

            if (angleRowWithoutDimension)
            {
                string csch = Path.Combine(ws, cell, "schematic", cell + ".csch");
                var (model, view, name) = SchematicPersistence.Deserialize(File.ReadAllText(csch), Path.GetDirectoryName(csch));
                model.Components.Single(c => c.InstanceName == "SH1").Parameters.Single(p => p.Name == "PinDir[1]").Dimension = UnitDimension.None;
                SchematicPersistence.SaveToFile(csch, model, name);
            }

            // The layout as the designer's Save writes it: the planner at the defaults (S1).
            string cellDir = Path.Combine(ws, cell), clay = Path.Combine(cellDir, "layout", cell + ".clay");
            var tech = TechPersistence.LoadFromFile(Directory.GetFiles(Path.Combine(ws, "tech"), "*.ctech").Single());
            var at = DesignedCellLayout.At(cellDir, new Dictionary<string, Value>(), ws, tech, null);
            Assert.True(at.Layout is not null, at.Refusal);
            LayoutPersistence.SaveToFile(clay, at.Layout!);
            string planned = string.Join("\n", at.Lines.Select(l => $"{l.Severity} {l.Text}"))
                           + "\ninstances: " + string.Join(", ", at.Layout!.Instances.Select(i => $"{i.SchematicId}@{i.X},{i.Y} r{i.RotationDegrees} m{i.MirrorX} {Path.GetFileName(i.CellRef)}"))
                           + "\nshapes: " + string.Join(",", at.Layout.Shapes.Select(sh => sh.GetType().Name + ":" + sh.Generated + ":" + sh.Layer));

            // As `circuitrf lvs` does: the generated cells the layout places, rebuilt from its snapshots.
            using var generated = GeneratedCellsRun.Prepare(Path.Combine(ws, cell, "layout", cell + ".clay"));
            var lvs = LvsRun.Run(Path.Combine(ws, cell));
            Assert.True(lvs.ErrorCount == 0, planned + "\n" + string.Join("\n", lvs.Findings.Where(f => f.Severity == DiagnosticSeverity.Error)
                                                                   .Select(f => $"{f.Id}: {f.Diagnostic} [{string.Join(";", f.Objects)}] {f.Marker}")));
        }
        finally { try { Directory.Delete(ws, recursive: true); } catch (IOException) { } }
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
        foreach (string d in Directory.GetDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
    }
}
