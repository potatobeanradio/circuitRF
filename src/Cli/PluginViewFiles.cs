using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Plugins;

namespace CircuitRF.Cli;

/// <summary>One view of a reply, as <c>plugin run -o</c> wrote it: the files, or why there are none.</summary>
/// <param name="Notes">What the panel would also say of a view it draws — a layer its technology lacks.</param>
internal sealed record PluginViewFile(string Name, string Kind, IReadOnlyList<string> Files, string? Problem)
{
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// A plugin reply's views written as documents (brief-plg-5 §1, <c>plugins.md</c> §11) — each the document the panel
/// draws the view from, so every one opens or renders with what already reads it: a plot or Smith chart as its data
/// (the staged <c>.sNp</c>, or the inline data as <c>&lt;view&gt;.npy</c>) and the <c>.cdd</c> <b>Open in Data Display</b>
/// writes (<see cref="PluginPlots.WriteDataDisplay"/>), a layout as the <c>.clay</c> placement writes
/// (<see cref="PluginPlacement.PlacedLayout"/>, naming its technology), a schematic as its staged <c>.cnl</c>, a table
/// as CSV, an image as itself. Every view is read through <see cref="PluginViews.Read"/>, the panel's own reader.
/// </summary>
internal static class PluginViewFiles
{
    public const string ReadoutsFileName = "readouts.json";

    public static IReadOnlyList<PluginViewFile> Write(
        IReadOnlyList<PluginViewBody> bodies, PluginManifest manifest, string? workspaceDir, string outDir)
    {
        Directory.CreateDirectory(outDir);
        var declared = PluginLayoutReader.Read(manifest).Layout?.Views.GroupBy(v => v.Name).ToDictionary(g => g.Key, g => g.First().Kind)
                       ?? [];
        var ctx = new PluginViewContext(manifest, workspaceDir);
        var written = new List<PluginViewFile>();

        foreach (var body in bodies)
        {
            // A view's name is the plugin's and becomes a file name in -o: one that is not a plain name could write outside it.
            if (!IsFileName(body.Name))
            {
                written.Add(new(body.Name, declared.GetValueOrDefault(body.Name) ?? "view", [],
                    $"'{body.Name}' cannot name a file; a view written to a folder is named by letters, digits, '.', '-' and '_'."));
                continue;
            }
            var view = PluginViews.Read(body, declared.GetValueOrDefault(body.Name), ctx);
            if (view.Problem is { } problem) { written.Add(new(body.Name, view.Kind, [], problem)); continue; }
            try
            {
                written.Add(view switch
                {
                    PluginPlotView plot          => Plot(plot, manifest, outDir),
                    PluginTableView              => One(body.Name, "table", Path.Combine(outDir, body.Name + ".csv"),
                                                        p => File.WriteAllText(p, PluginValueFormat.Csv(body.Body))),
                    PluginLayoutView layout      => One(body.Name, "layout", Path.Combine(outDir, body.Name + ".clay"),
                                                        p => File.WriteAllText(p, LayoutPersistence.Serialize(
                                                                 PluginPlacement.PlacedLayout(layout, outDir, null)))),
                    PluginSchematicView schem    => Copy(body.Name, "schematic", schem.CnlPath, outDir),
                    PluginImageView image        => Copy(body.Name, "image", image.Path, outDir),
                    _                            => new PluginViewFile(body.Name, view.Kind, [], $"a {view.Kind} view has no file form."),
                });
                if (view.Notes.Count > 0) written[^1] = written[^1] with { Notes = view.Notes };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
            {
                written.Add(new(body.Name, view.Kind, [], e.Message));
            }
        }
        return written;
    }

    /// <summary>The reply's readouts as it sent them — base SI, every value with what the plugin gave it.</summary>
    public static string WriteReadouts(JsonObject readouts, string outDir)
    {
        string path = Path.Combine(outDir, ReadoutsFileName);
        File.WriteAllText(path, readouts.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
        return path;
    }

    private static PluginViewFile Plot(PluginPlotView view, PluginManifest manifest, string outDir)
    {
        var c = PluginPlots.Compose(view, manifest);
        if (c.Problem is { } problem) return new(view.Name, view.Kind, [], problem);

        // The data the .cdd names, beside it: the staged file as itself, inline data written fresh (the composer keeps a
        // file that is already there, which from a previous run would be the wrong one).
        string data = Path.Combine(outDir, c.SourceName);
        if (view.File is { } staged) File.Copy(staged, data, overwrite: true);
        else if (File.Exists(data)) File.Delete(data);
        string cdd = PluginPlots.WriteDataDisplay(c, outDir, view.Name);
        return new(view.Name, view.Kind, [data, cdd], null);
    }

    private static bool IsFileName(string name)
        => name.Length is > 0 and <= 100 && name[0] != '.' && name.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_');

    private static PluginViewFile One(string name, string kind, string path, Action<string> write)
    {
        write(path);
        return new(name, kind, [path], null);
    }

    private static PluginViewFile Copy(string name, string kind, string from, string outDir)
    {
        string to = Path.Combine(outDir, Path.GetFileName(from));
        File.Copy(from, to, overwrite: true);
        return new(name, kind, [to], null);
    }
}
