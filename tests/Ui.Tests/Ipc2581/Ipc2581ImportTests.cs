using System.Text.RegularExpressions;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Engine;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// The one function behind File ▸ Import ▸ IPC-2581 and <c>convert</c>
/// (docs/briefs/brief-ipx-4-surfaces-and-acceptance.md §6). The CLI half is in <c>ConvertCliVerbTests</c>.
/// </summary>
public sealed class Ipc2581ImportTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("ipc2581-import-").FullName;
    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private const int Dbu = 1000;

    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "testdata", "ipc2581"))) dir = Path.GetDirectoryName(dir);
        return dir ?? throw new DirectoryNotFoundException("testdata/ipc2581");
    }

    private static string Fixture(string name) => Path.Combine(RepoRoot(), "testdata", "ipc2581", name);

    private string Parent() => Directory.CreateDirectory(Path.Combine(_dir, Guid.NewGuid().ToString("N")[..8])).FullName;

    private static string Code(string relative) =>
        Regex.Replace(Regex.Replace(File.ReadAllText(Path.Combine(RepoRoot(), relative)), @"/\*.*?\*/", "", RegexOptions.Singleline), @"//[^\n]*", "");

    [Theory]
    [InlineData("src/Ui/ViewModels/WorkspaceViewModel.cs")]
    [InlineData("src/Cli/LayoutConvert.cs")]
    public void Both_surfaces_call_the_one_function_and_keep_no_second_copy(string file)
    {
        string code = Code(file);
        Assert.Contains("Ipc2581Import.Import(", code);
        Assert.DoesNotContain("Ipc2581Reader.Read(", code);
        Assert.DoesNotContain("PcbImport.ImportBoard(", code);
    }

    [Theory]
    [InlineData("src/Ui/Views/WorkspaceWindow.axaml", 2)]
    [InlineData("src/Ui/Views/Shared/TornOffFileMenuView.axaml", 1)]
    [InlineData("src/Ui/Views/ProjectTree/ProjectTreeView.BlankMenu.cs", 1)]
    public void The_row_appears_wherever_the_board_row_does(string file, int rows)
    {
        string text = File.ReadAllText(Path.Combine(RepoRoot(), file));
        Assert.Equal(rows, Regex.Count(text, "ImportIpc2581Command"));
    }

    [Fact]
    public void Several_steps_ask_and_a_cancelled_choice_creates_nothing_and_one_step_asks_nothing()
    {
        IReadOnlyList<Ipc2581Step>? offered = null;
        string parent = Parent();
        var cancelled = Ipc2581Import.Import(Fixture("two-steps.xml"), parent, "b", null, Dbu,
                                             chooseStep: steps => { offered = steps; return null; });
        Assert.True(cancelled.Cancelled);
        Assert.Null(cancelled.Refusal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(parent));

        Assert.Collection(offered!,
            s => Assert.Equal(("board", 1, 2, false), (s.Name, s.LayerCount, s.NetCount, s.IsPanel)),
            s => { Assert.Equal("panel", s.Name); Assert.True(s.IsPanel); Assert.Equal(["board"], s.Places); });

        var chosen = Ipc2581Import.Import(Fixture("two-steps.xml"), Parent(), "b", null, Dbu, chooseStep: _ => "board");
        Assert.False(chosen.Cancelled, string.Join("\n", chosen.Messages));

        var single = Ipc2581Import.Import(Fixture("board-plain.xml"), Parent(), "b", null, Dbu,
                                          chooseStep: _ => throw new InvalidOperationException("one step asks nothing"));
        Assert.False(single.Cancelled, string.Join("\n", single.Messages));
    }

    [Fact]
    public void A_cancelled_read_leaves_nothing_behind()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        string parent = Parent();

        var result = Ipc2581Import.Import(Fixture("board-plain.xml"), parent, "b", null, Dbu,
                                          control: new RunControl { Token = cts.Token });

        Assert.True(result.Cancelled);
        Assert.Null(result.Refusal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
    }

    [Fact]
    public void A_file_of_another_root_is_refused_with_the_readers_sentence()
    {
        string other = Path.Combine(_dir, "other.xml");
        File.WriteAllText(other, "<svg/>");
        string parent = Parent();

        var result = Ipc2581Import.Import(other, parent, "b", null, Dbu, chooseStep: _ => "x");

        Assert.Contains("root element is <svg>", result.Refusal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(parent));
        Assert.False(Ipc2581Reader.LooksLikeIpc2581(other));
        Assert.True(Ipc2581Reader.LooksLikeIpc2581(Fixture("board-plain.xml")));
    }

    [Fact]
    public void The_report_opens_with_the_stackup_and_marks_what_the_file_did_not_state()
    {
        var result = Ipc2581Import.Import(Fixture("stackup-values.xml"), Parent(), "b", new Technology { Name = "b" }, Dbu);
        Assert.False(result.Cancelled, string.Join("\n", result.Messages));

        Assert.StartsWith("Stackup, top first.", result.Messages[0]);
        var rows = result.Messages.Skip(1).Take(result.Technology!.Stackup.Layers.Count).ToList();
        foreach (var layer in result.Technology.Stackup.Layers)
            Assert.Contains(rows, r => r.StartsWith(layer.Name + " · " + layer.Kind.ToString().ToLowerInvariant(), StringComparison.Ordinal));

        // The file states this core's εr and tanδ, and no conductivity at all on a foil with an unusable unit.
        Assert.Contains(rows, r => r.Contains("dielectric") && r.Contains("εr 4.1") && !r.Contains("εr 4.1 *") && r.Contains("tanδ 0.009"));
        Assert.Contains(rows, r => r.Contains("conductor") && r.Contains("S/m *"));
    }
}
