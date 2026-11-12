using System.Text;
using CircuitRF.Design.Interchange.Hzn;
using Xunit.Abstractions;

namespace CircuitRF.Ui.Tests.Hzn;

/// <summary>
/// HZN-2 gate — the coverage measurement: for every 3D design in the Hertzon files under <c>CIRCUITRF_HERTZON_HOME</c>,
/// whether it translates, and the refusal items that stop the rest, counted by kind. This decides the next additions to
/// the translation table. It prints the numbers (readable in the TRX); it asserts only that no file breaks the translator.
/// </summary>
public sealed class HznCoverageTests(ITestOutputHelper output)
{
    [HertzonHomeFact]
    public void EveryExampleDesign_TranslatesOrIsRefusedWithItsItems()
    {
        string home = HertzonHomeFactAttribute.Home!;
        var files = Directory.EnumerateFiles(home, "*.hzn", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}.venv{Path.DirectorySeparatorChar}"))
            .Order(StringComparer.Ordinal).ToList();

        int designs = 0, translated = 0, skippedFiles = 0;
        var byKind = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var items = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var report = new StringBuilder();
        foreach (string file in files)
        {
            var read = HznFile.Read(file);
            if (!read.Ok) { skippedFiles++; continue; }
            foreach (string name in HznToC3d.ThreeDDesigns(read.File!.Project))
            {
                designs++;
                var t = HznToC3d.Translate(read.File.Project, name);
                string label = $"{Path.GetFileName(file)} / {name}";
                if (t.Ok)
                {
                    translated++;
                    report.AppendLine($"OK      {label}");
                    continue;
                }
                Assert.NotEmpty(t.Refusals);
                report.AppendLine($"REFUSED {label}: {string.Join("; ", t.Refusals.Select(i => i.Kind + " " + i.What))}");
                foreach (var kind in t.Refusals.GroupBy(i => i.Kind))
                    byKind[kind.Key.ToString()] = byKind.GetValueOrDefault(kind.Key.ToString()) + kind.Count();
                foreach (var i in t.Refusals)
                    items[$"{i.Kind}: {i.What}"] = items.GetValueOrDefault($"{i.Kind}: {i.What}") + 1;
            }
        }
        output.WriteLine($"files read: {files.Count - skippedFiles}, files refused by the version gate: {skippedFiles}");
        output.WriteLine($"3D designs: {designs}, translated: {translated}, refused: {designs - translated}");
        foreach (var (k, v) in byKind) output.WriteLine($"  items of kind {k}: {v}");
        output.WriteLine("designs stopped by each item (a design may have several):");
        foreach (var (k, v) in items.OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal)) output.WriteLine($"  {v,3}  {k}");
        output.WriteLine(report.ToString());
        Assert.True(designs > 0);
    }
}
