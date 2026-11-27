// Owner: PCD-8 (brief-pcd-8 R-pcd8-1). The PCell Designer example, BUILT by the functions the designer's window calls rather
// than written as JSON. Skipped unless CRF_AUTHOR_PCD8=1, because it WRITES examples/PCell Designer (every file but the
// hand-written README.md and Bench.cdd is replaced). Kept so the workspace can be rebuilt after a format change.

namespace CircuitRF.Ui.Tests.PCellDesigner;

public sealed class Pcd8ExampleAuthoring
{
    public sealed class AuthorFactAttribute : FactAttribute
    {
        public AuthorFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("CRF_AUTHOR_PCD8") != "1")
                Skip = "re-builds examples/PCell Designer only with CRF_AUTHOR_PCD8=1";
        }
    }

    [AuthorFact]
    public void BuildTheWorkspace()
    {
        string ws = Path.Combine(Pcd8PadTee.RepoTestData(), "..", "examples", "PCell Designer");
        string[] kept = ["README.md", "Bench.cdd"];
        var keep = kept.Where(f => File.Exists(Path.Combine(ws, f))).ToDictionary(f => f, f => File.ReadAllBytes(Path.Combine(ws, f)));
        if (Directory.Exists(ws)) Directory.Delete(ws, true);

        Pcd8PadTee.Workspace(ws);
        string cell = Pcd8PadTee.Design(ws);
        Pcd8PadTee.Bench(ws, cell);
        foreach (var (f, bytes) in keep) File.WriteAllBytes(Path.Combine(ws, f), bytes);
    }
}
