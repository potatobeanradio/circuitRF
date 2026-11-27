// Owner: PCD-8 (brief-pcd-8 R-pcd8-1). The PCell Designer example's PadTee and Bench, BUILT by the functions the designer's
// window calls rather than written as JSON. Skipped unless CRF_AUTHOR_PCD8=1, because it WRITES examples/PCell Designer:
// the two cell folders are built in a scratch workspace and replace PadTee/ and Bench/ there. Nothing else in the example
// (its README, displays, technology or other cells) is touched.

namespace CircuitRF.Ui.Tests.PCellDesigner;

public sealed class Pcd8ExampleAuthoring
{
    public sealed class AuthorFactAttribute : FactAttribute
    {
        public AuthorFactAttribute()
        {
            if (Environment.GetEnvironmentVariable("CRF_AUTHOR_PCD8") != "1")
                Skip = "re-builds examples/PCell Designer's PadTee and Bench only with CRF_AUTHOR_PCD8=1";
        }
    }

    [AuthorFact]
    public void BuildTheCells()
    {
        string example = Path.GetFullPath(Path.Combine(Pcd8PadTee.RepoTestData(), "..", "examples", "PCell Designer"));
        string root = Path.Combine(Path.GetTempPath(), "crf-pcd8-author-" + Guid.NewGuid().ToString("N")[..8]);
        string scratch = Path.Combine(root, "PCell Designer");
        try
        {
            Pcd8PadTee.Workspace(scratch);
            Pcd8PadTee.Bench(scratch, Pcd8PadTee.Design(scratch));
            foreach (string cell in new[] { Pcd8PadTee.Cell, "Bench" })
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
}
