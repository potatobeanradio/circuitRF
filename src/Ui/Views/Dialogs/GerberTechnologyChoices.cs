using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Design.Layout.Interchange;
using CircuitRF.Design.Workspace;
using CircuitRF.Ui.Layout;

namespace CircuitRF.Ui.Views.Dialogs;

/// <summary>What one row of the Gerber import's Technology combo stands for.</summary>
public enum GerberTechnologyChoiceKind
{
    /// <summary>A technology of the import's own, written beside the cell.</summary>
    New,

    /// <summary>A <c>.ctech</c> already somewhere in the workspace.</summary>
    Workspace,

    /// <summary>A technology from the catalog, copied into <c>tech/</c> when Continue is pressed.</summary>
    Catalog,
}

/// <summary>
/// One row of the Technology combo (brief-gerber-import-target-technology D2). <see cref="Technology"/>
/// is the loaded model the dialog re-proposes the rows against (R-gt-2) — for a catalog row, the
/// catalog's own copy, which is byte-identical to what Continue writes into <c>tech/</c>.
/// </summary>
public sealed record GerberTechnologyChoice(
    GerberTechnologyChoiceKind Kind,
    string Label,
    string? Path,
    string? CatalogId,
    int ConductorCount,
    bool IsEnabled,
    string ToolTip,
    Technology? Technology)
{
    /// <summary>R-gt-4: only a technology the import MINTS can have a layer added to it.</summary>
    public bool AllowsAddToTechnology => Kind == GerberTechnologyChoiceKind.New;

    /// <summary>Readable, and not an edited copy of a catalog technology — what D4 leaves to the count.</summary>
    public bool Usable { get; init; } = true;

    /// <summary>
    /// D4 against a copper count that has CHANGED since <see cref="GerberTechnologyChoices.Build"/>:
    /// the table's "in the stackup as" answers add copper the file names did not, and a set whose
    /// copper is only known once those are answered must be able to reach the stackup they make
    /// (designer report, round 17 — every four-conductor entry stayed disabled after the four
    /// files were marked copper).
    /// </summary>
    public bool EnabledFor(int copperCount) =>
        Kind == GerberTechnologyChoiceKind.New || Usable && ConductorCount == copperCount;

    public override string ToString() => Label;
}

/// <summary>
/// The Gerber import's Technology choices — which are offered, in what order, which are disabled and
/// which is selected — separated from <see cref="LayerMappingDialog"/> so the rules can be driven by a
/// test without a window, as <see cref="WorkspaceTechnologyChoices"/> is for Change Technology….
/// </summary>
public static class GerberTechnologyChoices
{
    public const string NewLabel = "New technology from the files";

    /// <summary>
    /// D2's list, in D2's order: <b>New</b>; every <c>.ctech</c> in the workspace
    /// (<see cref="WorkspaceTechnologyChoices.Enumerate"/>, the enumeration Change Technology… uses);
    /// then <paramref name="catalog"/>'s technologies, labelled as that dialog labels them.
    /// </summary>
    /// <remarks>
    /// <b>D4: a technology whose conductor count is not the set's copper count is listed and
    /// disabled</b>, and every technology row carries its count, so the reason is in the text and
    /// needs no prose. Six copper files against a four-conductor stackup is the wrong technology, not a
    /// near miss to guess around.
    ///
    /// <para><b>A catalog row is also disabled when <c>tech/&lt;id&gt;.ctech</c> already exists with
    /// DIFFERENT bytes.</b> <see cref="WorkspaceCreate.InstallTechnology"/> never overwrites the
    /// workspace's own file — it returns it — so offering the row would import against that edited copy
    /// while the label promised the built-in one. The edited file is still offered, as the workspace row
    /// above it. An identical copy is simply reused (D2).</para>
    /// </remarks>
    public static IReadOnlyList<GerberTechnologyChoice> Build(
        string? workspaceRoot, int copperCount, IReadOnlyList<TechnologyCatalogEntry> catalog)
    {
        var choices = new List<GerberTechnologyChoice>
        {
            new(GerberTechnologyChoiceKind.New, NewLabel, null, null, copperCount, true,
                "A technology of this import's own is written beside the cell, with the layers the files "
              + "name and a stackup read from the job file or filled in.", null),
        };

        string? techDir = workspaceRoot is null ? null : System.IO.Path.Combine(workspaceRoot, "tech");
        foreach (var choice in WorkspaceTechnologyChoices.Enumerate(workspaceRoot, techDir))
        {
            Technology? tech = TryLoad(choice.AbsolutePath);
            int conductors = tech is null ? 0 : ConductorCount(tech);
            choices.Add(new GerberTechnologyChoice(
                GerberTechnologyChoiceKind.Workspace,
                tech is null ? $"{choice.Label}  —  unreadable" : $"{choice.Label}  —  {conductors} copper",
                choice.AbsolutePath, null, conductors,
                tech is not null && conductors == copperCount,
                choice.AbsolutePath, tech) { Usable = tech is not null });
        }

        if (workspaceRoot is null) return choices;

        foreach (var entry in catalog)
        {
            var tech = TechnologyCatalog.Load(entry);
            int conductors = ConductorCount(tech);
            string copy = System.IO.Path.Combine(workspaceRoot, "tech", entry.Id + ".ctech");
            bool editedCopy = File.Exists(copy) && !SameText(copy, TechnologyCatalog.LoadRawJson(entry));

            choices.Add(new GerberTechnologyChoice(
                GerberTechnologyChoiceKind.Catalog,
                $"{entry.Name}  —  {conductors} copper  —  built in, copied into tech/",
                null, entry.Id, conductors,
                conductors == copperCount && !editedCopy,
                editedCopy
                    ? $"{System.IO.Path.Combine("tech", entry.Id + ".ctech")} is already in this workspace and "
                    + "differs from the built-in technology; it is offered above as the workspace's own."
                    : $"Built into circuitRF: a copy is written to {System.IO.Path.Combine("tech", entry.Id + ".ctech")} "
                    + "and used from there.",
                tech) { Usable = !editedCopy });
        }

        return choices;
    }

    /// <summary>
    /// D3: the workspace's default technology when its conductor count equals the set's copper count,
    /// otherwise <b>New</b> (index 0).
    /// </summary>
    public static int DefaultIndex(IReadOnlyList<GerberTechnologyChoice> choices, string? workspaceDefaultPath)
    {
        if (workspaceDefaultPath is not { Length: > 0 }) return 0;
        string target = System.IO.Path.GetFullPath(workspaceDefaultPath);
        for (int i = 0; i < choices.Count; i++)
            if (choices[i] is { Kind: GerberTechnologyChoiceKind.Workspace, IsEnabled: true, Path: { } p } &&
                string.Equals(System.IO.Path.GetFullPath(p), target, StringComparison.OrdinalIgnoreCase))
                return i;
        return 0;
    }

    /// <summary>
    /// What Continue does with <paramref name="choice"/>: New stays New, a workspace technology is used
    /// where it is, and a catalog technology is copied into <c>tech/</c> HERE — not before, so a
    /// dialog that is cancelled writes nothing — by the function New Workspace copies with.
    /// </summary>
    public static GerberTechnologyTarget Commit(GerberTechnologyChoice choice, string? workspaceRoot) => choice.Kind switch
    {
        GerberTechnologyChoiceKind.Workspace => GerberTechnologyTarget.Use(choice.Path!),
        GerberTechnologyChoiceKind.Catalog when workspaceRoot is not null =>
            GerberTechnologyTarget.Use(WorkspaceCreate.InstallTechnology(workspaceRoot, choice.CatalogId!)),
        _ => GerberTechnologyTarget.New,
    };

    private static int ConductorCount(Technology tech) =>
        tech.Stackup.Layers.Count(l => l.Kind == StackupKind.Conductor);

    private static Technology? TryLoad(string path)
    {
        try { return TechPersistence.LoadFromFile(path); }
        catch { return null; }   // listed as unreadable and disabled, never thrown out of a dialog
    }

    private static bool SameText(string path, string text)
    {
        try { return string.Equals(File.ReadAllText(path), text, StringComparison.Ordinal); }
        catch (IOException) { return false; }
    }
}
