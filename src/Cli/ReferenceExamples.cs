using System.Text;
using CircuitRF.Design.Workspace;
using RfCore.Export;

namespace CircuitRF.Cli;

/// <summary>
/// <c>circuitrf reference examples [name]</c> — the example workspaces that ship with circuitRF, as
/// worked answers an agent can study, copy and run.
///
/// <para><b>Why a reference topic.</b> An example is the most direct answer to "how does circuitRF
/// solve this kind of problem": a schematic, a test bench whose analyses are already right, the
/// Data Display that reads the result, and a README that says why each choice was made. A topic
/// here is also an MCP resource (<c>circuitrf://reference/examples</c>) and a <c>reference</c>
/// tool call with nothing else to add, so the surface gains no tool.</para>
///
/// <para><b>It reads the index File ▸ New ▸ Examples reads</b> (<see cref="ExampleWorkspaces.All"/>),
/// at every call. Nothing lists the examples a second time: an example added to <c>examples/</c> and
/// its <c>examples.json</c> is here the moment it is in the menu, and
/// <c>ExampleWorkspacesTests</c> already refuses a workspace folder nobody indexed. The one-example
/// view's cells and analyses are <see cref="Find"/>'s description of a workspace, for the same
/// reason.</para>
///
/// <para><b>The shipped copy is read-only.</b> It may sit inside an application bundle, and
/// running an analysis writes results beside it. So the directory is reported for reading, and
/// <c>new example</c> (<c>create what=example</c>) is how a caller gets one of its own to run and
/// change.</para>
/// </summary>
internal static partial class Reference
{
    /// <summary>The shipped example workspaces.</summary>
    public const string ExamplesTopic = "examples";

    private const string ExamplesTitle = "Example workspaces";

    private const string ExamplesSummary =
        "Generated from the index of the example workspaces that ship with circuitRF: each one's name, " +
        "what it shows and where it is. Name one for its README (what each bench shows and the order to " +
        "run them), its files, and its cells with their analyses; copy one under your root with " +
        "'create what=example' to run or change it.";

    private static int Examples(string? name)
    {
        var all = ExampleWorkspaces.All();
        if (all.Count == 0) return JsonRun.Fail(CliDiagnostics.ExamplesNotInstalled());

        if (name is null)
        {
            var rows = all.Select(ExampleRow).ToList();
            JsonRun.Reference = new ReferenceReportJson(null, null, null, Examples: rows);
            Console.Out.Write(RenderExamples(rows));
            return 0;
        }

        if (ExampleWorkspaces.Find(all, name) is not { } example)
            return JsonRun.Fail(CliDiagnostics.ReferenceUnknownExample(name, string.Join(", ", all.Select(e => e.Folder))));

        var one = ExampleDetail(example);
        JsonRun.Reference = new ReferenceReportJson(null, null, null, Examples: [one]);
        Console.Out.Write(RenderExample(one));
        return 0;
    }

    /// <summary>The list's own text — what the topic list and the resource listing measure. Reads the
    /// index only, so measuring it costs one small file.</summary>
    public static string ExamplesText()
        => RenderExamples([.. ExampleWorkspaces.All().Select(ExampleRow)]);

    private static ReferenceExampleJson ExampleRow(ExampleWorkspace e)
        => new(e.Folder, e.Title, e.Summary, e.Directory);

    private static ReferenceExampleJson ExampleDetail(ExampleWorkspace e)
    {
        string readmePath = Path.Combine(e.Directory, "README.md");
        string? readme = null;
        try { if (File.Exists(readmePath)) readme = File.ReadAllText(readmePath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* listed without it */ }

        // What a copy carries, in the order a reader scans a tree.
        var files = WorkspaceTree.EnumerateFilesSafe(e.Directory)
            .Select(f => (Rel: WorkspaceTree.Rel(e.Directory, f), Full: f))
            .Where(f => !WorkspaceTree.IsSkipped(f.Rel))
            .OrderBy(f => f.Rel, StringComparer.Ordinal)
            .Select(f => new ReferenceExampleFileJson(f.Rel, Math.Max(0, WorkspaceTree.SizeOf(f.Full))))
            .ToList();

        return new ReferenceExampleJson(e.Folder, e.Title, e.Summary, e.Directory,
            readme, files, Find.Describe(e.Directory, analyses: true));
    }

    private static string RenderExamples(IReadOnlyList<ReferenceExampleJson> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Example workspaces that ship with circuitRF — worked designs to study, copy and run.");
        sb.AppendLine("    circuitrf reference examples <name>          its README, files, cells and analyses");
        sb.AppendLine("    circuitrf new example <dir> <name>            a copy of your own at <dir>/<name>");
        sb.AppendLine("                                                  (MCP: create what=example path=<dir> name=<name>)");
        sb.AppendLine("The shipped copies are read-only; run and change a copy.");
        sb.AppendLine();
        foreach (var r in rows)
        {
            sb.AppendLine(r.Title == r.Name ? r.Name : $"{r.Name}   ({r.Title})");
            sb.AppendLine($"  {r.Summary}");
        }
        return sb.ToString();
    }

    private static string RenderExample(ReferenceExampleJson e)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{e.Name}   ({e.Title})");
        sb.AppendLine($"  {e.Summary}");
        sb.AppendLine($"  shipped at: {e.Directory}   (read-only)");
        sb.AppendLine($"  copy it:    circuitrf new example <dir> \"{e.Name}\"   (MCP: create what=example path=<dir> name=\"{e.Name}\")");
        sb.AppendLine();

        if (e.Workspace is { Cells.Count: > 0 } ws)
        {
            sb.AppendLine("Cells:");
            foreach (var c in ws.Cells)
            {
                string views = string.Join(", ", c.Views.Select(v => v.File is { } f ? $"{v.Type}: {f}" : $"{v.Type}: {v.State}"));
                sb.AppendLine($"  {c.Name,-28} {views}");
                if (c.Analyses is { Count: > 0 } a)
                    sb.AppendLine($"  {"",-28} analyses: {string.Join(", ", a)}");
            }
            sb.AppendLine();
        }

        if (e.Files is { } files)
        {
            sb.AppendLine($"Files ({files.Count}):");
            foreach (var f in files) sb.AppendLine($"  {f.Path}  ({FileSize(f.Bytes)})");
            sb.AppendLine();
        }

        if (e.Readme is { } readme)
        {
            sb.AppendLine("README.md:");
            sb.AppendLine();
            sb.Append(readme);
            if (!readme.EndsWith('\n')) sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string FileSize(long bytes)
        => bytes < 1024 ? $"{bytes} B"
         : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} kB"
         : $"{bytes / (1024.0 * 1024.0):0.#} MB";
}
