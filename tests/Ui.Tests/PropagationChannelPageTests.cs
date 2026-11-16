// brief PCH-12 gate 2 — the Propagation Channel's user page lists every registered model. The model table and each model's
// parameter table are generated from RfCore.Propagation.PropagationModels (DocTables), so a model added to the registry
// reaches the page with no edit; what is held here is that the page asks for them, and that its hand-written "choosing"
// table names every model, so a new one is never left out of the advice.

using System.Text.RegularExpressions;
using CircuitRF.Ui.Diagnostics;
using CircuitRF.Ui.Tests.Em3d;
using RfCore.Propagation;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class PropagationChannelPageTests
{
    private static string Page => File.ReadAllText(Path.Combine(PalaceBackendTests.RepoRoot(), "docs", "user", "src", "reference",
                                                                "propagation-channel.md"));

    [Fact]
    public void ThePage_ListsEveryRegisteredModel_FromTheRegistry()
    {
        string page = Page;
        Assert.Contains("{{table: propagation-models}}", page, StringComparison.Ordinal);
        Assert.Contains("{{table: propagation/losses}}", page, StringComparison.Ordinal);

        string table = DocTables.PropagationModels();
        var rows = Regex.Matches(table, "<tr><td class=\"nowrap\"><code>([^<]+)</code>").Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(PropagationModels.All.Select(m => m.Name), rows);

        // the choosing table, hand-written: every model's PathLoss word in it
        int start = page.IndexOf("## Choosing a model", StringComparison.Ordinal), end = page.IndexOf("## The models", StringComparison.Ordinal);
        string choosing = page[start..end];
        foreach (var m in PropagationModels.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.Validity), $"{m.Name} states no validity");
            Assert.Contains($"`{m.Name}`", choosing, StringComparison.Ordinal);
            if (m.Parameters.Count > 0)
                Assert.Contains($"{{{{table: propagation/{m.Name}}}}}", page, StringComparison.Ordinal);
        }
        foreach (var p in AddedLosses.Parameters)
            Assert.Contains($"<code>{p.Name}</code>", DocTables.PropagationParameters("losses"), StringComparison.Ordinal);
    }
}
