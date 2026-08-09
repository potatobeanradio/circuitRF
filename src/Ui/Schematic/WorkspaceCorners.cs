using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Design;
using CircuitRF.Core.Pdk;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// One corner choice a WORKSPACE offers: an axis a referenced kit declares, resolved to where its
/// file actually is right now.
///
/// <para><see cref="Key"/> is what a design records; everything else is re-derived from the kit on
/// every open. That split is the whole point — a recorded selection has to outlive the kit moving,
/// being re-cloned, or arriving on a different machine, and a resolved path does none of that.</para>
/// </summary>
/// <param name="Kit">The provider name — the identity every reference to this kit already uses.</param>
/// <param name="AxisId">The declaring file, kit-relative.</param>
/// <param name="DisplayName">The file's own stem, which is the only name the kit gives an axis.</param>
/// <param name="Options">Section names, verbatim and in declaration order — the kit's own vocabulary.</param>
/// <param name="AbsoluteFile">Where that file is on this machine, right now.</param>
/// <param name="Label">
/// What the panel shows. The axis's own name where that is unambiguous, qualified only where it is
/// not — see <see cref="WorkspaceCorners.From"/>.
/// </param>
public sealed record WorkspaceCornerAxis(
    string                Kit,
    string                AxisId,
    string                DisplayName,
    IReadOnlyList<string> Options,
    string                AbsoluteFile,
    string                Label)
{
    /// <summary>
    /// The identity a design records a selection against — kit and axis together, because two kits
    /// may perfectly reasonably each declare a file with the same relative path.
    /// </summary>
    public string Key => MakeKey(Kit, AxisId);

    public static string MakeKey(string kit, string axisId) => $"{kit}|{axisId}";
}

/// <summary>
/// What corners a workspace offers, and what choosing them binds.
///
/// <para>Framework-free on purpose: whether a corner exists, whether a recorded one is still offered,
/// and what it binds are all decisions a test must be able to make without a window.</para>
/// </summary>
public static class WorkspaceCorners
{
    /// <summary>
    /// The axes the workspace's referenced kits declare, from what <c>.cws</c> recorded — so this
    /// costs a dictionary walk, never a kit read.
    ///
    /// <para>An axis whose file is no longer where the reference says it is <b>is still returned</b>.
    /// Dropping it would make a moved kit look like a kit that never had corners, and would silently
    /// take a design's recorded selection with it; the panel shows it and the binding reports it,
    /// which is the repairable outcome.</para>
    ///
    /// <para><b>Names are qualified only where they are ambiguous.</b> A kit ships one corner
    /// file per device family PER SIMULATOR FLAVOUR — measured at 12 axes over 6 families — so
    /// showing the file stem alone lists "capCorners" twice with different options and no way to tell
    /// them apart. Where a name repeats, the folder that distinguishes them is prefixed; where it
    /// does not, the plain name stands, because qualifying every row would make the common case read
    /// worse to fix a case that is not there.</para>
    /// </summary>
    public static IReadOnlyList<WorkspaceCornerAxis> From(
        string workspaceRootDir, IEnumerable<CwsPdkRef>? refs)
    {
        if (refs is null) return [];

        var axes = new List<WorkspaceCornerAxis>();
        foreach (var r in refs)
        {
            if (r.Corners is null || r.Corners.Count == 0) continue;

            string kitRoot = WorkspaceRefs.Resolve(r.Path, workspaceRootDir);
            foreach (var c in r.Corners)
            {
                if (string.IsNullOrWhiteSpace(c.AxisId) || c.Options is null || c.Options.Count == 0)
                    continue;

                string full = Path.Combine(kitRoot, c.AxisId.Replace('/', Path.DirectorySeparatorChar));
                string name = string.IsNullOrWhiteSpace(c.DisplayName)
                    ? Path.GetFileNameWithoutExtension(c.AxisId)
                    : c.DisplayName;

                axes.Add(new WorkspaceCornerAxis(r.Provider, c.AxisId, name, [.. c.Options], full,
                                                 Label: name));
            }
        }

        // Stable order, so a panel does not reshuffle between opens.
        var ordered = axes.OrderBy(a => a.Kit, StringComparer.OrdinalIgnoreCase)
                          .ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
                          .ToList();

        return [.. ordered.Select(a => a with { Label = Disambiguate(a, ordered) })];
    }

    /// <summary>
    /// The label to show for one axis, given every axis on offer: its own name where that is unique,
    /// otherwise qualified by <b>the part of the path that actually differs</b>.
    ///
    /// <para><b>The folder LEAF is the wrong qualifier, and a kit proves it.</b> That kit files
    /// its corner files one directory per simulator flavour and then a <c>models</c> folder inside
    /// each — so every path ends in the same leaf, and qualifying by it produced two rows both reading
    /// "models · capCorners". A qualifier that does not distinguish is worse than none: it looks like
    /// the answer while leaving the user to guess. So the common leading AND trailing segments are
    /// dropped and what remains — the flavour folder — is what gets shown.</para>
    /// </summary>
    private static string Disambiguate(WorkspaceCornerAxis axis, IReadOnlyList<WorkspaceCornerAxis> all)
    {
        var sameName = all.Where(a => a.DisplayName.Equals(axis.DisplayName, StringComparison.OrdinalIgnoreCase))
                          .ToList();
        if (sameName.Count <= 1) return axis.DisplayName;

        string qualifier = DistinguishingSegments(axis.AxisId, sameName.Select(a => a.AxisId));
        string qualified = qualifier.Length > 0
            ? $"{axis.DisplayName} ({qualifier})"
            : axis.DisplayName;

        // Two kits shipping the same folder layout is the one case a path cannot separate.
        return sameName.Select(a => a.Kit).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
            ? $"{axis.Kit} · {qualified}"
            : qualified;
    }

    /// <summary>
    /// The directory segments of <paramref name="axisId"/> that are not shared by every path in
    /// <paramref name="group"/> — i.e. the smallest thing that tells them apart. Returns empty when
    /// the paths share every directory segment (nothing to say).
    /// </summary>
    internal static string DistinguishingSegments(string axisId, IEnumerable<string> group)
    {
        static string[] DirSegments(string id)
        {
            int slash = id.LastIndexOf('/');
            return slash <= 0
                ? []
                : id[..slash].Split('/', StringSplitOptions.RemoveEmptyEntries);
        }

        var mine   = DirSegments(axisId);
        var others = group.Select(DirSegments).ToList();
        if (mine.Length == 0 || others.Count <= 1) return "";

        int minLen = others.Min(o => o.Length);

        int prefix = 0;
        while (prefix < minLen &&
               others.All(o => o[prefix].Equals(mine[prefix], StringComparison.OrdinalIgnoreCase)))
            prefix++;

        int suffix = 0;
        while (prefix + suffix < minLen &&
               others.All(o => o[^(suffix + 1)].Equals(mine[^(suffix + 1)], StringComparison.OrdinalIgnoreCase)))
            suffix++;

        int take = mine.Length - prefix - suffix;
        return take <= 0 ? "" : string.Join('/', mine.Skip(prefix).Take(take));
    }

    /// <summary>
    /// What a design's recorded selections bind, resolved through the kit's own corner files.
    ///
    /// <para>Every way this can fail is REPORTED rather than silently binding nothing: a selection
    /// naming an axis the workspace no longer offers, a section the kit no longer declares, a kit
    /// whose file has moved. Silence here would leave the design at a corner nobody chose with every
    /// number still plausible — which is the one outcome worth going out of the way to prevent.</para>
    /// </summary>
    public static IReadOnlyList<Variable> BindingsFor(
        IReadOnlyList<WorkspaceCornerAxis>  axes,
        IReadOnlyDictionary<string, string> selections,
        List<string>                        problems)
    {
        ArgumentNullException.ThrowIfNull(axes);
        ArgumentNullException.ThrowIfNull(problems);

        var bound = new List<Variable>();
        selections ??= new Dictionary<string, string>();

        // A selection naming an axis the workspace no longer offers is reported here rather than in
        // the walk below, which is over the AXES — that walk would never reach it.
        foreach (var (key, section) in selections.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            if (!string.IsNullOrWhiteSpace(section) &&
                !axes.Any(a => string.Equals(a.Key, key, StringComparison.Ordinal)))
                problems.Add($"Corner '{section}' is set on '{key}', which this workspace no longer " +
                             $"offers. It was NOT applied — check the kit is still referenced.");

        foreach (var axis in axes.OrderBy(a => a.Key, StringComparer.Ordinal))
        {
            selections.TryGetValue(axis.Key, out string? chosen);

            // NOTHING CHOSEN IS NOT NOTHING BOUND. A kit states its process constants ONLY inside a
            // corner section — measured: the capacitor model card reads `CJ=cap_carea`, and
            // `cap_carea` is bound by cap_typ/cap_bcs/cap_wcs and by nothing else in the kit. So an
            // axis left alone must still bind the kit's own NOMINAL corner, which is the section it
            // lists first; binding nothing leaves the model referring to a name no scope defines and
            // the design fails to elaborate. That was the reported bug.
            string section = string.IsNullOrWhiteSpace(chosen) ? axis.Options[0] : chosen!;
            bool  isDefault = string.IsNullOrWhiteSpace(chosen);

            // A stale CHOICE is still refused rather than quietly replaced by the nominal one: a
            // corner nobody chose, with every number plausible, is the outcome this whole mechanism
            // exists to prevent. The nominal fallback above is not a choice, so it cannot be stale.
            if (!isDefault && !axis.Options.Any(o => o.Equals(section, StringComparison.OrdinalIgnoreCase)))
            {
                problems.Add($"'{axis.DisplayName}' no longer offers corner '{section}' " +
                             $"({string.Join(", ", axis.Options)}). It was NOT applied.");
                continue;
            }

            if (!File.Exists(axis.AbsoluteFile))
            {
                problems.Add($"'{axis.DisplayName}' corner '{section}' could not be applied — " +
                             $"'{axis.AbsoluteFile}' is not there.");
                continue;
            }

            var vars = PdkCorners.BindingsFor(axis.AbsoluteFile, section, out var readNotes);

            // WHAT THE READER NOTICED ABOUT THE KIT'S OWN FILES IS NOT A PROBLEM WITH THE CORNER,
            // and reporting it as one buries the messages that are.
            //
            // Applying a corner reads the kit's shared model library through the corner file, so
            // every honest observation the reader makes about that library — a model the library
            // itself defines twice, an `.ends` whose trailing name does not match — arrives here.
            // None of them is about this design, this axis or this section; none is actionable by
            // the person who pressed Run; and they arrive ONCE PER AXIS PER RUN because every axis
            // includes the same library. Measured: 28 of them on every single
            // simulation of one transistor, ahead of the two lines that meant anything.
            //
            // They ARE the explanation when nothing was bound, which is the one case where the
            // reader's account of what it could not read is exactly what the reader of the message
            // needs — so they surface there and nowhere else. Same rule the import report already
            // follows in keeping its Notes apart from its Diagnostics, for the same reason.
            if (vars.Count == 0)
            {
                problems.Add($"'{axis.DisplayName}' corner '{section}' bound nothing.");
                foreach (var n in readNotes) problems.Add($"{axis.DisplayName}: {n}");
                continue;
            }

            foreach (var v in vars)
            {
                // Two axes binding one name to DIFFERENT values is a genuine contradiction, not
                // something to resolve by list order — a corner set silently deciding another
                // corner's constant is exactly the failure this whole mechanism exists to make
                // visible. Binding it to the SAME value is not a contradiction at all: a kit
                // routinely repeats a shared switch in every family's corner file, and saying so on
                // every run reports agreement as a conflict.
                var existing = bound.FirstOrDefault(e => e.Name.Equals(v.Name, StringComparison.Ordinal));
                if (existing is not null)
                {
                    if (!string.Equals(existing.Expression?.Trim(), v.Expression?.Trim(), StringComparison.Ordinal))
                        problems.Add($"'{v.Name}' is bound by more than one corner axis, to " +
                                     $"'{existing.Expression}' and '{v.Expression}'; the first is used.");
                    continue;
                }
                bound.Add(v);
            }
        }

        return bound;
    }
}
