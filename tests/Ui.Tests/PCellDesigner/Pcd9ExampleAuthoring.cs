// Owner: PCD-9 (showcase examples). The three showcase cells and their benches, BUILT by the functions the designer's
// window calls. Skipped unless CRF_AUTHOR_PCD9=1, because it WRITES examples/PCell Designer: the six cell folders are built
// in a scratch workspace and replace their namesakes there; nothing else in the example is touched. With
// CRF_PCD9_LAYOUT=<workspace> it instead lays out that workspace's benches with their Planar setups, for a by-hand EM run
// on a copy of the example (no result is committed).

namespace CircuitRF.Ui.Tests.PCellDesigner;

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
            Pcd9Cells.WilkinsonBench(scratch, Pcd9Cells.Design(scratch, Pcd9Cells.Wilkinson));
            Pcd9Cells.SpurlineBench(scratch, Pcd9Cells.Design(scratch, Pcd9Cells.Spurline));
            Pcd9Cells.PatchBench(scratch, Pcd9Cells.Design(scratch, Pcd9Cells.Patch));
            foreach (string cell in new[] { "Wilkinson3", "Wilkinson3Bench", "Spurline", "SpurlineBench", "DualModePatch", "PatchBench" })
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
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    [AuthorFact("CRF_PCD9_LAYOUT")]
    public void LayOutTheBenches()
    {
        string ws = Environment.GetEnvironmentVariable("CRF_PCD9_LAYOUT")!;
        foreach (string bench in new[] { "SpurlineBench", "PatchBench" })
        {
            var laid = LayoutFromSchematic.Run(Path.Combine(ws, bench, "schematic", bench + ".csch"), emSetup: true);
            Assert.True(laid.Refusal is null, laid.Refusal);
        }
    }
}
