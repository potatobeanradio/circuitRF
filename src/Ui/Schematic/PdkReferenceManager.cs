using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Pdk;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// What the workspace references, what state each reference is in, and the edits a user can make to
/// that list. Framework-free on purpose: every decision here — is this kit reachable, does it still
/// hold the parts a design placed, is it safe to remove — is testable without a window.
///
/// <para>The dialog on top of this is presentation. Nothing about the rules lives there.</para>
/// </summary>
public static class PdkReferenceManager
{
    /// <summary>
    /// How a reference stands. Deliberately three states, not two: a kit that is present but no longer
    /// offers a part the design placed is a DIFFERENT problem from one that is missing, and reporting
    /// them the same way sends the user looking in the wrong place.
    /// </summary>
    public enum RefState
    {
        /// <summary>Resolved, loaded, and offering everything the design asks of it.</summary>
        Ok,

        /// <summary>The kit folder is not there. The usual case after a move or a fresh clone.</summary>
        Missing,

        /// <summary>Reachable, but what it holds no longer matches what was recorded or placed.</summary>
        Drifted,
    }

    /// <param name="Provider">The kit name — what a placed part's reference and a netlist both use.</param>
    /// <param name="StoredPath">Exactly what <c>.cws</c> holds: relative inside the workspace, absolute outside.</param>
    /// <param name="ResolvedPath">The stored path made absolute, whether or not it exists.</param>
    /// <param name="PartsLoaded">Parts currently held in memory for this kit.</param>
    /// <param name="Detail">One line naming the problem, or empty when there is none.</param>
    public sealed record RefStatus(
        string Provider,
        string StoredPath,
        string ResolvedPath,
        RefState State,
        int PartsLoaded,
        string Detail)
    {
        /// <summary>
        /// True when the stored path leaves the workspace. That decides whether sharing the workspace
        /// carries the kit: one INSIDE the tree travels with it, one outside does not — so nothing may
        /// state the sharing consequence except per reference.
        /// </summary>
        public bool IsExternal => Path.IsPathRooted(StoredPath);
    }

    /// <summary>
    /// Reads the workspace's references and reports the state of each, WITHOUT loading anything. A
    /// user asking "what does this workspace depend on" must not be made to wait for every kit to be
    /// re-read, and must not have the answer change as a side effect of asking.
    /// </summary>
    /// <summary>
    /// The referenced model-library packages, resolved. Handed to discovery so a part kit can find the
    /// models even when it no longer sits beside them.
    /// </summary>
    public static IReadOnlyList<string> LibraryRootsIn(
        string workspaceRootDir, IEnumerable<CwsPdkRef> refs)
    {
        ArgumentNullException.ThrowIfNull(refs);

        return [.. refs.Where(r => r.IsLibraryOnly)
                       .Select(r => WorkspaceRefs.Resolve(r.Path, workspaceRootDir))
                       .Where(Directory.Exists)];
    }

    public static IReadOnlyList<RefStatus> Describe(string workspaceRootDir, IEnumerable<CwsPdkRef> refs)
    {
        ArgumentNullException.ThrowIfNull(refs);

        var list = new List<RefStatus>();
        foreach (var r in refs)
        {
            string resolved = WorkspaceRefs.Resolve(r.Path, workspaceRootDir);

            // A library package holds no parts by definition, so "no parts loaded" is its healthy
            // state — reporting it as drift would make every such reference permanently red.
            if (r.IsLibraryOnly)
            {
                list.Add(Directory.Exists(resolved)
                    ? new RefStatus(r.Provider, r.Path, resolved, RefState.Ok, 0,
                        "Model libraries only — no parts of its own. Other kits' devices are evaluated " +
                        "with what this package supplies.")
                    : new RefStatus(r.Provider, r.Path, resolved, RefState.Missing, 0,
                        "The model-library folder is not there. Kits relying on it will not simulate."));
                continue;
            }
            int loaded = PdkKitRegistry.HasKit(r.Provider)
                ? PdkKitRegistry.PartsOf(r.Provider).Count
                : 0;

            if (!Directory.Exists(resolved))
            {
                list.Add(new RefStatus(r.Provider, r.Path, resolved, RefState.Missing, loaded,
                    "The kit folder is not there. Repair the reference or remove it."));
                continue;
            }

            // A reader change moves pins, and wires attached to them silently disconnect — so it is
            // refused at load and reported here rather than applied.
            if (r.TranslationVersion != 0 && r.TranslationVersion != DsnSymbolReader.TranslationVersion)
            {
                list.Add(new RefStatus(r.Provider, r.Path, resolved, RefState.Drifted, loaded,
                    $"Translated by an older reader (version {r.TranslationVersion}; this build uses " +
                    $"{DsnSymbolReader.TranslationVersion}). Re-import to update — pin positions may " +
                    $"move and disconnect wires."));
                continue;
            }

            if (loaded == 0)
            {
                list.Add(new RefStatus(r.Provider, r.Path, resolved, RefState.Drifted, loaded,
                    "The kit is reachable but no parts loaded from it. Validate to see why."));
                continue;
            }

            list.Add(new RefStatus(r.Provider, r.Path, resolved, RefState.Ok, loaded, ""));
        }

        return list;
    }

    /// <param name="Provider">The kit that was checked.</param>
    /// <param name="KitPath">Where it was read from, resolved.</param>
    /// <param name="PartsOffered">Placeable parts a fresh read of the kit produced. −1 when it could not be read.</param>
    /// <param name="PlacedChecked">Parts this workspace places from this kit, each looked for in that read.</param>
    /// <param name="Problems">What is wrong, in the user's terms. Empty is the clean answer.</param>
    /// <param name="Notes">What the check WORKED OUT — neutral status, never a problem.</param>
    public sealed record ValidationResult(
        string Provider,
        string KitPath,
        int PartsOffered,
        int PlacedChecked,
        IReadOnlyList<string> Problems,
        IReadOnlyList<string> Notes)
    {
        public bool IsClean => Problems.Count == 0;

        /// <summary>
        /// One line saying what was checked, not merely whether it passed. "No problems found" on its
        /// own is unsatisfying precisely because it does not say what was looked at — a user cannot
        /// tell a real check from one that did nothing.
        /// </summary>
        public string Summary => PartsOffered < 0
            ? $"{Provider}: could not be read."
            : $"{Provider}: {PdkPartInstaller.Plural(PartsOffered, "part", "parts")} offered, " +
              $"{PdkPartInstaller.Plural(PlacedChecked, "placed part", "placed parts")} checked" +
              (IsClean
                  ? " — no problems found."
                  : $" — {PdkPartInstaller.Plural(Problems.Count, "problem", "problems")}.");
    }

    /// <summary>
    /// What re-reading a kit finds NOW, compared against what the workspace recorded. This is the
    /// explicit action: an open is silent, so drift has to be something the user can ask about.
    ///
    /// <para>Reports drift as well as breakage — a kit that still loads but no longer offers a part a
    /// design placed is exactly the case that otherwise surfaces as an unresolved component with no
    /// explanation of when it stopped resolving.</para>
    ///
    /// <para>Returns what it CHECKED as well as what was wrong. A bare "no problems found" cannot be
    /// told apart from a check that did nothing, which is the one thing a validation must not be
    /// ambiguous about.</para>
    /// </summary>
    public static ValidationResult Validate(
        string workspaceRootDir, CwsPdkRef reference, IEnumerable<string> placedPartRefs,
        IReadOnlyList<string>? libraryRoots = null)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(placedPartRefs);

        // A library package validates on whether it is THERE. It has no parts to offer and none to
        // check, so running the part machinery over it would report its healthy state as empty.
        if (reference.IsLibraryOnly)
        {
            string dir = WorkspaceRefs.Resolve(reference.Path, workspaceRootDir);
            return Directory.Exists(dir) && DeviceLibraryDiscovery.HoldsAnyDeviceLibrary(dir)
                ? new ValidationResult(reference.Provider, dir, 0, 0, [],
                    ["Model libraries only — it supplies no parts, which is what it is for."])
                : new ValidationResult(reference.Provider, dir, -1, 0,
                    [Directory.Exists(dir)
                        ? "This folder no longer holds a model library our worker can drive."
                        : $"The model-library folder '{dir}' is not there."], []);
        }

        var problems = new List<string>();
        var notes    = new List<string>();
        string resolved = WorkspaceRefs.Resolve(reference.Path, workspaceRootDir);

        var placed = placedPartRefs
            .Distinct(StringComparer.Ordinal)
            .Where(cr => PdkKitRegistry.TryParse(cr, out string k, out _)
                      && string.Equals(k, reference.Provider, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!Directory.Exists(resolved))
            return new ValidationResult(reference.Provider, resolved, -1, placed.Count,
                [$"The kit folder '{resolved}' is not there."], []);

        PdkImportReport report;
        try { report = PdkImporter.Import(resolved); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ValidationResult(reference.Provider, resolved, -1, placed.Count,
                [$"The kit could not be read: {ex.Message}"], []);
        }

        var outcome = PdkPartInstaller.Install(report, reference.Settings, libraryRoots);
        problems.AddRange(outcome.Diagnostics);
        notes.AddRange(outcome.Notes ?? []);

        // Every part this design actually placed from this kit must still be there. Checked against
        // what a FRESH read offers, not against what happens to be loaded — the question is whether
        // the kit still holds it, not whether this session managed to load it.
        var offered = new HashSet<string>(
            (outcome.Parts ?? []).Select(p => p.PartId), StringComparer.OrdinalIgnoreCase);

        foreach (string cellRef in placed)
        {
            PdkKitRegistry.TryParse(cellRef, out _, out string part);
            if (offered.Contains(part)) continue;

            problems.Add($"'{part}' is placed in this workspace but the kit no longer offers it.");
        }

        if (reference.TranslationVersion != 0 &&
            reference.TranslationVersion != DsnSymbolReader.TranslationVersion)
            problems.Add($"Recorded translation version {reference.TranslationVersion} differs from " +
                         $"this build's {DsnSymbolReader.TranslationVersion}; re-import to update.");

        // Said whether or not anything is wrong: a kit that offers nothing to place is not an error —
        // some kits are purely supporting cells — but it is the first thing worth knowing when a part
        // will not appear, and it is invisible in a bare problem list.
        if (outcome.OmittedNotPlaceable > 0)
            notes.Add($"{PdkPartInstaller.Plural(outcome.OmittedNotPlaceable, "part", "parts")} in " +
                      $"this kit have no readable symbol and are not placeable. That is ordinary — " +
                      $"they are usually a kit's internal building blocks.");

        return new ValidationResult(reference.Provider, resolved, outcome.Items.Count, placed.Count,
                                    problems, notes);
    }

    /// <summary>
    /// Adds — or repairs — a reference to the kit at <paramref name="kitPath"/>, keyed on the kit's
    /// own name. Repairing rather than appending is what makes "the kit moved" a one-click fix that
    /// leaves every placed part resolving again: the parts reference the kit by NAME, so a repaired
    /// path under the same name reconnects them without touching a single schematic.
    /// </summary>
    /// <returns>
    /// What the install produced, or null when the kit could not be read. Returned rather than reduced
    /// to a name because the CALLER has to wire the result into the application — the palette and the
    /// provider resolver both follow what was loaded, and they cannot be rebuilt from the registry
    /// alone (a palette entry carries an icon and search terms the registry never sees).
    /// </returns>
    public static PdkPartInstaller.InstallOutcome? AddOrRepair(
        string workspaceRootDir, List<CwsPdkRef> refs, string kitPath, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(refs);
        problem = null;

        if (!Directory.Exists(kitPath))
        {
            problem = $"'{kitPath}' is not a folder.";
            return null;
        }

        PdkImportReport report;
        try { report = PdkImporter.Import(kitPath); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"The kit could not be read: {ex.Message}";
            return null;
        }

        var outcome = PdkPartInstaller.Install(report, libraryRoots: LibraryRootsIn(workspaceRootDir, refs));
        if (outcome.Items.Count == 0)
        {
            // A folder with no parts is not automatically a mistake. A delivery is several part kits
            // beside ONE shared library package, and that package is exactly this: no symbols, no
            // netlists, just the compiled models the other kits' devices need. Refusing it left the
            // user with no way to say where the models are once a kit had been referenced from
            // somewhere else — which is the whole reason this branch exists.
            if (DeviceLibraryDiscovery.HoldsAnyDeviceLibrary(kitPath, ancestorLevels: 0))
            {
                string name = Path.GetFileName(kitPath.TrimEnd(Path.DirectorySeparatorChar,
                                                               Path.AltDirectorySeparatorChar));
                if (name.Length == 0) name = "Model libraries";

                refs.RemoveAll(r => string.Equals(r.Provider, name, StringComparison.OrdinalIgnoreCase));
                refs.Add(new CwsPdkRef
                {
                    Path          = WorkspaceRefs.ToStoredRef(kitPath, workspaceRootDir),
                    Provider      = name,
                    IsLibraryOnly = true,
                });

                return outcome with { KitName = name };
            }

            problem = "That folder holds no placeable parts, and no model library our worker can " +
                      "drive. Check it is the kit's own folder, the folder that adds to it, or the " +
                      "package holding the compiled models.";
            return null;
        }

        refs.RemoveAll(r => string.Equals(r.Provider, outcome.KitName, StringComparison.OrdinalIgnoreCase));
        refs.Add(new CwsPdkRef
        {
            Path               = WorkspaceRefs.ToStoredRef(report.RootPath, workspaceRootDir),
            Provider           = outcome.KitName,
            TranslationVersion = DsnSymbolReader.TranslationVersion,
            Settings           = outcome.Settings,
        });

        PdkKitRegistry.SetKit(outcome.KitName, outcome.Parts ?? []);
        return outcome;
    }

    /// <summary>
    /// Drops a reference. Nothing is deleted from any schematic — parts placed from it keep their
    /// references and become unresolvable, which is the reported, repairable state rather than a loss:
    /// adding the kit back resolves them again.
    /// </summary>
    /// <returns>How many placed parts this will leave unresolved.</returns>
    public static int Remove(List<CwsPdkRef> refs, string provider, IEnumerable<string> placedPartRefs)
    {
        ArgumentNullException.ThrowIfNull(refs);
        ArgumentNullException.ThrowIfNull(placedPartRefs);

        refs.RemoveAll(r => string.Equals(r.Provider, provider, StringComparison.OrdinalIgnoreCase));
        PdkKitRegistry.RemoveKit(provider);

        return placedPartRefs.Count(cr => PdkKitRegistry.TryParse(cr, out string kit, out _)
                                       && string.Equals(kit, provider, StringComparison.OrdinalIgnoreCase));
    }
}
