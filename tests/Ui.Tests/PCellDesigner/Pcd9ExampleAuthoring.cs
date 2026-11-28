using CircuitRF.Ui.Tests.Layout.PCells;
// Owner: PCD-9 (showcase examples). The three showcase cells and their benches, BUILT by the functions the designer's
// window calls. Skipped unless CRF_AUTHOR_PCD9=1, because it WRITES examples/PCell Designer: the six cell folders are built
// in a scratch workspace and replace their namesakes there; nothing else in the example is touched.

namespace CircuitRF.Ui.Tests.PCellDesigner;

[Collection(PCellResolverCollection.Name)]
public sealed class Pcd9ExampleAuthoring
{
    public sealed class AuthorFactAttribute : FactAttribute
    {
        public AuthorFactAttribute(string variable)
        {
            if (Environment.GetEnvironmentVariable(variable) is not { Length: > 0 }) Skip = $"runs only with {variable} set";
        }
    }

    [AuthorFact("CRF_AUTHOR_PCD9")]
    public void BuildTheCells()
    {
        string example = Path.GetFullPath(Path.Combine(Pcd8PadTee.RepoTestData(), "..", "examples", "PCell Designer"));
        string root = Path.Combine(Path.GetTempPath(), "crf-pcd9-author-" + Guid.NewGuid().ToString("N")[..8]);
        string scratch = Path.Combine(root, "PCell Designer");
        try
        {
            Pcd9Cells.Workspace(scratch);
            // Every bench ships a layout with its ground pour; the circuit-only one has no EM setup.
            Assert.Null(LayoutFromSchematic.Run(Pcd9Cells.WilkinsonBench(scratch, Pcd9Cells.Design(scratch, Pcd9Cells.Wilkinson))).Refusal);
            // The two EM-only benches are laid out with their Planar setups, so their SP1 runs by EM as shipped. An example
            // keeps each setup in its cell's em/ folder (the path inside it stays relative to the workspace).
            foreach (string bench in new[] { Pcd9Cells.SpurlineBench(scratch, Pcd9Cells.Design(scratch, Pcd9Cells.Spurline)),
                                             Pcd9Cells.PatchBench(scratch, Pcd9Cells.Design(scratch, Pcd9Cells.Patch)) })
            {
                var out1 = LayoutFromSchematic.Run(bench, emSetup: true);
                Assert.Null(out1.Refusal);
                string emDir = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(bench))!, "em");
                Directory.CreateDirectory(emDir);
                foreach (string f in out1.EmSetups) File.Move(f, Path.Combine(emDir, Path.GetFileName(f)));
            }

            // The slotline's ground is drawn, and Planar's is an unbroken plane, so its bench solves in 3D: the setup made
            // for its layout becomes an EMerge one, New 3D View from Layout embeds it, and the .cem goes, leaving one EM view.
            string slot = Pcd9Cells.SlotlineBench(scratch, Pcd9Cells.Design(scratch, Pcd9Cells.Slotline));
            var laid = LayoutFromSchematic.Run(slot, emSetup: true);
            Assert.Null(laid.Refusal);
            string cem = Assert.Single(laid.EmSetups);
            var setup = EmSetupPersistence.LoadFromFile(cem);
            setup.Solver3D = Em3dSolverId.Emerge;
            setup.Emerge = new CemEmerge { Quality = PalaceQuality.Standard, SaveFieldsGHz = [] };
            EmSetupPersistence.SaveToFile(cem, setup);
            string benchDir = Path.GetDirectoryName(Path.GetDirectoryName(slot))!;
            string c3d = Path.Combine(CellFolder.SubFolderPath(benchDir, ViewType.ThreeD), "SlotlineBench" + CircuitRF.Design.ThreeD.C3dPersistence.Extension);
            var made = CircuitRF.Design.ThreeD.C3dHierarchy.NewFromLayout(benchDir, c3d, Path.Combine(scratch, ".cws"));
            Assert.True(made.Document is not null, made.Refusal);
            CellCreate.WriteThreeDView(benchDir, "SlotlineBench", made.Document!);
            File.Delete(cem);

            foreach (string cell in new[] { "Wilkinson3", "Wilkinson3Bench", "Spurline", "SpurlineBench", "DualModePatch", "PatchBench",
                                            "Slotline", "SlotlineBench" })
            {
                string from = Path.Combine(scratch, cell), to = Path.Combine(example, cell);
                if (Directory.Exists(to)) Directory.Delete(to, true);
                foreach (string f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                {
                    string target = Path.Combine(to, Path.GetRelativePath(from, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(f, target);
                }
            }

            // PCD-8's Bench (rebuilt by its own authoring) gets its layout in place, as Update Layout from Schematic makes it.
            Assert.Null(LayoutFromSchematic.Run(Path.Combine(example, "Bench", "schematic", "Bench.csch")).Refusal);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
