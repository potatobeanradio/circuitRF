// brief-em3d-53 — material libraries in the workspace: a .cmat as its own document (R-em3d53-4a), its live edits
// reaching every technology that names it through the cache (R-em3d53-1f), the technology editor's Materials tab
// (R-em3d53-4b), renames that span files (R-em3d53-4c), and the 3D editor's entry points (R-em3d53-5).

using CircuitRF.Design.ThreeD;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.ThreeD;
using CircuitRF.Ui.Views.Dialogs;
using CommunityToolkit.Mvvm.Input;
using Dock.Model.Core;

namespace CircuitRF.Ui.ViewModels;

public partial class WorkspaceViewModel
{
    // ── the .cmat document ────────────────────────────────────────────────────────────────────

    /// <summary>Opens (or focuses) a <c>.cmat</c> as its own Materials document, with <paramref name="select"/>'s row
    /// selected. Modelled on the part library's open path: one session per path, a dirty mark, Save, Save As.</summary>
    public void OpenOrActivateMaterials(string absolutePath, string? select = null)
    {
        absolutePath = Path.GetFullPath(absolutePath);
        if (ActivateIfOpen(absolutePath))
        {
            // brief-em3d-94 — open in this window or another: the row is selected there, whatever it was showing.
            if (select is not null && OpenDocumentAnywhere(absolutePath) is MaterialsDocument m)
                m.ViewModel.Table.Select(select);
            return;
        }

        try
        {
            // An unsaved edit left in the cache (a document closed while its override stood) is what every technology
            // is resolving against; open on it rather than on the file.
            var materials = _techCache.LiveLibrary(absolutePath) is { } live
                ? MaterialLibraryPersistence.Deserialize(MaterialLibraryPersistence.Serialize(live))
                : MaterialLibraryPersistence.LoadFromFile(absolutePath);
            var vm = new MaterialsEditorViewModel(absolutePath, materials);
            var doc = new MaterialsDocument(Path.GetFileName(absolutePath), vm, absolutePath);

            vm.SaveError += m => Messages.Error(m);
            vm.LibraryLiveChanged += (path, list) =>
            {
                _techCache.SetLiveLibrary(path, list);
                NotifyTechEditorsOfLibrary(path);
            };
            vm.LibrarySaved += path =>
            {
                Messages.Success("Saved", path);
                _techCache.ClearLiveLibrary(path);
                NotifyTechEditorsOfLibrary(path);
                // A wBond's Material list reads the library from disk: a metal just saved can be chosen at once.
                _factory.PropertiesTool?.EditorVm.RefreshWBondMaterials();
            };
            vm.LibrarySavedAs += (oldPath, newPath) =>
            {
                _openDocsByPath.Remove(oldPath);
                _openDocsByPath[newPath] = doc;
                _techCache.ClearLiveLibrary(oldPath);
                NotifyTechEditorsOfLibrary(oldPath);
                vm.NamedBy = LibraryUsers(newPath);
                _factory.ProjectTreeTool?.Refresh();
                Messages.Success("Saved", newPath);
            };

            vm.NamedBy = LibraryUsers(absolutePath);
            vm.Table.UsedBy = name => LibraryMaterialUses(vm.FilePath, name);
            vm.Table.RenameAcross = (oldName, newName) => RenameLibraryMaterial(doc, oldName, newName);
            vm.Table.ConfirmDelete = (name, uses) => ConfirmLibraryMaterialDelete(doc, name, uses);

            _factory.OpenDocument(doc);
            _openDocsByPath[absolutePath] = doc;
            HookMaterialsDirty(doc);

            // §1h — a library in a referenced workspace not toggled editable, or a read-only file, opens read-only
            // and says why, through the same per-document question every other document asks.
            if (IsDocumentReadOnly(doc))
            {
                vm.Table.IsReadOnly = true;
                vm.Table.ReadOnlyReason = ReadOnlyDocumentReason(doc) ?? $"'{Path.GetFileName(absolutePath)}' cannot be written.";
                vm.Table.Rebuild();
            }
            if (select is not null) vm.Table.Select(select);
            Messages.Info("Opened", absolutePath);
        }
        catch (Exception ex)
        {
            Messages.Error($"Failed to open material library '{Path.GetFileName(absolutePath)}': {ex.Message}");
        }
    }

    // ── a wBond's New Material… ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The Inspector's wBond <b>New Material…</b>: the workspace library is reused or created
    /// (<see cref="WBondMaterialLibrary.LocateOrCreate"/>), named by the workspace technology when it is not yet,
    /// and opened in the Materials editor with a new material started. Choosing that material on the wBond is a
    /// separate act, once it has a name and a σ₂₀ and is saved — the Inspector lists it then.
    /// </summary>
    private void NewWBondMaterial(SchematicViewModel vm, EditableComponent comp)
    {
        WBondMaterialLibrary.Located located;
        try { located = WBondMaterialLibrary.LocateOrCreate(comp, vm.EditModel.SchematicDirectory); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Messages.Error(ex.Message);
            return;
        }
        if (located.Created) Messages.Info("Created material library", located.Path);

        if (located.TechnologyToRegisterIn is { } ctech)
        {
            // A technology open in its editor takes the reference as its own undoable edit — writing the file under
            // it would be overwritten by its next save. Otherwise the file is written, and every reader re-resolves.
            if (OpenDocumentAnywhere(ctech) is TechDocument open) open.ViewModel.AddLibrary(located.Path);
            else
            {
                try
                {
                    WBondMaterialLibrary.RegisterInTechnology(ctech, located.Path);
                    _techCache.Invalidate(ctech);
                    Messages.Info($"Added {Path.GetFileName(located.Path)} to the technology's material libraries", ctech);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    Messages.Warning($"{Path.GetFileName(located.Path)} could not be added to the technology " +
                                     $"'{Path.GetFileName(ctech)}' ({ex.Message}); the wBond can still use it, a 3D run cannot.");
                }
            }
        }

        OpenOrActivateMaterials(located.Path);
        if (OpenDocumentAnywhere(located.Path) is MaterialsDocument doc && !doc.ViewModel.Table.IsReadOnly)
            doc.ViewModel.Table.Add();
    }

    private void HookMaterialsDirty(MaterialsDocument doc)
    {
        doc.ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not nameof(MaterialsEditorViewModel.IsDirty)) return;
            _factory.ProjectTreeTool?.SetFileDirty(doc.FilePath, doc.ViewModel.IsDirty);
            RaiseFileMenuEnablementChanged();
        };
    }

    private void SaveMaterialsByPath(string absPath)
    {
        var key = Path.GetFullPath(absPath);
        var doc = _openDocsByPath.Values.OfType<MaterialsDocument>().FirstOrDefault(d =>
            string.Equals(Path.GetFullPath(d.FilePath), key, StringComparison.OrdinalIgnoreCase));
        if (doc is { IsDirty: true }) doc.ViewModel.SaveCommand.Execute(null);
    }

    /// <summary>Don't Save: the library's live override goes, so every technology naming it re-reads the file.</summary>
    private void DiscardLiveMaterials(string absPath)
    {
        _techCache.ClearLiveLibrary(absPath);
        NotifyTechEditorsOfLibrary(absPath);
    }

    /// <summary>An open technology editor naming this library re-resolves (its Materials tab shows the new rows).</summary>
    private void NotifyTechEditorsOfLibrary(string absLibraryPath)
    {
        foreach (var td in _openDocsByPath.Values.OfType<TechDocument>())
            td.ViewModel.OnLibraryChanged(absLibraryPath);
    }

    /// <summary>The technologies in this workspace that name a library — the document header's list.</summary>
    private IReadOnlyList<string> LibraryUsers(string absLibraryPath)
        => MaterialLibraries.TechnologiesNaming(absLibraryPath, WorkspaceRootDir ?? Path.GetDirectoryName(absLibraryPath)!);

    /// <summary>A library material's uses: the stackup entries and bodies of every technology naming the library, and
    /// open 3D objects on those technologies.</summary>
    private IReadOnlyList<string> LibraryMaterialUses(string absLibraryPath, string name)
    {
        var uses = new List<string>();
        foreach (string ctech in LibraryUsers(absLibraryPath))
        {
            Technology? raw = OpenTechEditor(ctech)?.Working;
            if (raw is null) { try { raw = TechPersistence.LoadOwnFromFile(ctech); } catch { continue; } }
            if (raw.Materials.Any(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            foreach (var l in raw.Stackup.Layers.Where(l => string.Equals(l.Material, name, StringComparison.OrdinalIgnoreCase)))
                uses.Add($"stackup entry '{l.Name}' of {Path.GetFileName(ctech)}");
            foreach (var b in raw.Bodies.Where(b => string.Equals(b.Material, name, StringComparison.OrdinalIgnoreCase)))
                uses.Add($"body '{b.Name}' of {Path.GetFileName(ctech)}");
        }
        uses.AddRange(OpenC3dUsesOf(name, c3d => c3d.Elaboration?.Technology?.ResolvedLibraryPaths
                                                   .Contains(absLibraryPath, StringComparer.OrdinalIgnoreCase) == true));
        return uses;
    }

    private TechEditorViewModel? OpenTechEditor(string ctech)
        => _openDocsByPath.Values.OfType<TechDocument>()
                          .FirstOrDefault(d => string.Equals(Path.GetFullPath(d.FilePath), Path.GetFullPath(ctech), StringComparison.OrdinalIgnoreCase))?.ViewModel;

    /// <summary>Open 3D objects naming <paramref name="name"/>, in the open .c3d documents <paramref name="which"/> accepts.</summary>
    private IReadOnlyList<string> OpenC3dUsesOf(string name, Func<C3dEditorViewModel, bool> which)
        => [.. _openDocsByPath.Values.OfType<C3dEditorDocument>().Select(d => d.ViewModel).Where(which)
                              .SelectMany(vm => vm.UsesOfMaterial(name))];

    // ── the technology editor's Materials tab ─────────────────────────────────────────────────

    private void HookTechMaterials(TechEditorViewModel vm)
    {
        vm.OpenLibraryRequested += (path, material) => OpenOrActivateMaterials(path, material);
        vm.ExternalMaterialUses = name => OpenC3dUsesOf(name, c3d => SamePath(c3d.Elaboration?.TechnologyPath, vm.FilePath));
        vm.MaterialsTable.RenameAcross = (oldName, newName) => RenameTechnologyMaterial(vm, oldName, newName);
        if (vm.LibraryRefusal is { } refusal)
            Messages.Warning($"'{Path.GetFileName(vm.FilePath)}' does not load: {refusal} It is open so it can be fixed on its Materials tab.");
    }

    private static bool SamePath(string? a, string? b)
        => a is not null && b is not null && string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // ── renames across files (R-em3d53-4c, M5) ────────────────────────────────────────────────
    //
    // There is no cross-document undo, and this does not invent one: each OPEN file that names the material gets one
    // entry on its own stack, named alike. An unopened file is listed, never rewritten — `check` then reports it as an
    // unknown material, which is the truth.

    private string? RenameTechnologyMaterial(TechEditorViewModel tech, string oldName, string newName)
    {
        tech.RenameMaterialReferences(oldName, newName, renameOwn: true);
        foreach (var c3d in _openDocsByPath.Values.OfType<C3dEditorDocument>())
            if (SamePath(c3d.ViewModel.Elaboration?.TechnologyPath, tech.FilePath)) c3d.ViewModel.RenameMaterial(oldName, newName);
        return UnopenedMentions(oldName, [], includeTechnologies: false);
    }

    private string? RenameLibraryMaterial(MaterialsDocument library, string oldName, string newName)
    {
        string lib = library.FilePath;
        library.ViewModel.CommitEdit(list =>
        {
            foreach (var m in list)
                if (string.Equals(m.Name, oldName, StringComparison.OrdinalIgnoreCase)) m.Name = newName;
        }, $"Rename material {oldName} → {newName}");

        var users = LibraryUsers(lib);
        foreach (var td in _openDocsByPath.Values.OfType<TechDocument>())
            if (td.ViewModel.NamesLibrary(lib) && !td.ViewModel.Working.Materials.Any(m => string.Equals(m.Name, oldName, StringComparison.OrdinalIgnoreCase)))
                td.ViewModel.RenameMaterialReferences(oldName, newName, renameOwn: false);
        foreach (var c3d in _openDocsByPath.Values.OfType<C3dEditorDocument>())
            if (c3d.ViewModel.Elaboration?.Technology?.ResolvedLibraryPaths.Contains(lib, StringComparer.OrdinalIgnoreCase) == true)
                c3d.ViewModel.RenameMaterial(oldName, newName);
        return UnopenedMentions(oldName, users, includeTechnologies: true);
    }

    /// <summary>
    /// Deleting a material from a <c>.cmat</c>: warns, listing every use — the open files <paramref name="uses"/> names and the
    /// unopened ones read from disk — and goes ahead only on Delete Anyway. Nothing that names it is rewritten: a stackup entry,
    /// body or 3D object left naming it is reported by <c>check</c> as an unknown material, as a rename's leftovers are.
    /// A material nothing names is deleted without asking, as before.
    /// </summary>
    private Task<bool> ConfirmLibraryMaterialDelete(MaterialsDocument library, string name, IReadOnlyList<string> uses)
        => ConfirmMaterialDelete(HostWindowOf(library), name, uses,
                                 UnopenedFilesNaming(name, LibraryUsers(library.FilePath), includeTechnologies: true),
                                 "The delete can be undone until the file is saved.");

    /// <summary>
    /// The 3D view's Materials dialog deleting a material that already existed: the same warning, over the uses of the file it
    /// is in — a library's (every technology naming it, open 3D views on them) or the technology's own list's (its stackup and
    /// bodies, open 3D views on it) — and the unopened files naming it.
    /// </summary>
    private Task<bool> ConfirmPickerMaterialDelete(Avalonia.Controls.Window window, Technology tech, string techPath, string name, string? library)
    {
        IReadOnlyList<string> uses;
        List<string> unopened;
        if (library is not null)
        {
            uses = LibraryMaterialUses(library, name);
            unopened = UnopenedFilesNaming(name, LibraryUsers(library), includeTechnologies: true);
        }
        else
        {
            string file = Path.GetFileName(techPath);
            uses =
            [
                .. tech.Stackup.Layers.Where(l => string.Equals(l.Material, name, StringComparison.OrdinalIgnoreCase))
                                      .Select(l => $"stackup entry '{l.Name}' of {file}"),
                .. tech.Bodies.Where(b => string.Equals(b.Material, name, StringComparison.OrdinalIgnoreCase))
                              .Select(b => $"body '{b.Name}' of {file}"),
                .. OpenC3dUsesOf(name, c3d => SamePath(c3d.Elaboration?.TechnologyPath, techPath)),
            ];
            unopened = UnopenedFilesNaming(name, [], includeTechnologies: false);
        }
        return ConfirmMaterialDelete(window, name, uses, unopened,
                                     "Nothing is deleted until you press OK in the Materials dialog; Cancel keeps it.");
    }

    /// <summary>The delete warning both editors show: every use, open and unopened, and Delete Anyway. A material nothing
    /// names is deleted without asking.</summary>
    private static async Task<bool> ConfirmMaterialDelete(Avalonia.Controls.Window? window, string name, IReadOnlyList<string> uses,
                                                         IReadOnlyList<string> unopened, string undoNote)
    {
        if (uses.Count == 0 && unopened.Count == 0) return true;
        if (window is null) return false;
        var lines = new List<string>();
        lines.AddRange(uses.Select(u => "  " + u));
        lines.AddRange(unopened.Select(f => $"  {f} (not open)"));
        string text = $"'{name}' is used in this workspace:\n\n{string.Join("\n", lines)}\n\n"
                    + "Deleting it leaves each of these naming a material that no longer exists; `check` reports them as unknown. "
                    + undoNote;
        return await TextConfirmDialog.AskAsync(window, "Delete Material", $"Delete '{name}'?", text, "Delete Anyway");
    }

    /// <summary>The files not open that still name <paramref name="oldName"/>, as a sentence, or null.</summary>
    private string? UnopenedMentions(string oldName, IReadOnlyList<string> technologies, bool includeTechnologies)
    {
        var listed = UnopenedFilesNaming(oldName, technologies, includeTechnologies);
        return listed.Count == 0 ? null
            : $"Not rewritten, because they are not open: {string.Join(", ", listed)} still name '{oldName}', and `check` will report it as unknown there.";
    }

    /// <summary>The files not open that name <paramref name="oldName"/>: technologies among <paramref name="technologies"/>
    /// (by stackup entry or body) and every <c>.c3d</c> under the workspace.</summary>
    private List<string> UnopenedFilesNaming(string oldName, IReadOnlyList<string> technologies, bool includeTechnologies)
    {
        var listed = new List<string>();
        if (includeTechnologies)
            foreach (string ctech in technologies)
            {
                if (OpenTechEditor(ctech) is not null) continue;
                try
                {
                    var raw = TechPersistence.LoadOwnFromFile(ctech);
                    if (raw.Stackup.Layers.Any(l => string.Equals(l.Material, oldName, StringComparison.OrdinalIgnoreCase))
                        || raw.Bodies.Any(b => string.Equals(b.Material, oldName, StringComparison.OrdinalIgnoreCase)))
                        listed.Add(Path.GetFileName(ctech));
                }
                catch { /* unreadable: check reports it */ }
            }
        if (WorkspaceRootDir is { } root)
        {
            var open = _openDocsByPath.Values.OfType<C3dEditorDocument>().Select(d => Path.GetFullPath(d.FilePath)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
            foreach (string c3d in Directory.EnumerateFiles(root, "*.c3d", options))
            {
                if (open.Contains(Path.GetFullPath(c3d))) continue;
                try
                {
                    if (C3dPersistence.LoadFromFile(c3d).Objects.Any(o => string.Equals(o.Material, oldName, StringComparison.OrdinalIgnoreCase)))
                        listed.Add(Path.GetFileName(c3d));
                }
                catch { /* not a 3D view, or unreadable */ }
            }
        }
        return listed;
    }

    // ── Edit Material… from a 3D view (brief-em3d-94 R-em3d94-2) ─────────────────────────────

    /// <summary>
    /// Opens the file <paramref name="material"/> is defined in, on its row: the technology's own list → the technology editor's
    /// Materials tab; a library the technology names → that <c>.cmat</c>'s document. A library shipped inside circuitRF is no file
    /// to open, so it is read on the technology's Materials tab, where its rows stand read-only. An editor already open —
    /// here, floated, or in another workspace window — is brought forward, never opened twice, and the row is selected even when
    /// it was showing another.
    /// </summary>
    internal void EditMaterial(string material, string? technologyPath)
    {
        if (technologyPath is not null && HeldMaterialsRefusal(technologyPath) is { } held) { Messages.Info(held); return; }
        if (technologyPath is null)
        {
            Messages.Warning(C3dEditorViewModel.EditMaterialNoTechnology);
            return;
        }
        string techPath = Path.GetFullPath(technologyPath);
        // The technology as it stands now: an open editor's unsaved list, else the file.
        Technology? own = (OpenDocumentAnywhere(techPath) as TechDocument)?.ViewModel.Working;
        if (own is null) { try { own = TechPersistence.LoadOwnFromFile(techPath); } catch { /* opened below, where it says why */ } }
        bool inOwnList = own?.Materials.Any(m => string.Equals(m.Name, material, StringComparison.OrdinalIgnoreCase)) == true;
        string? library = inOwnList ? null : SafeTech(techPath)?.LibrarySourceOf(material);
        if (library is not null && !library.StartsWith(MaterialLibraries.ShippedPrefix, StringComparison.Ordinal) && File.Exists(library))
        {
            OpenOrActivateMaterials(library, material);
            return;
        }

        OpenOrActivateTech(techPath);
        if (OpenDocumentAnywhere(techPath) is not TechDocument doc) return;
        var editor = doc.ViewModel;
        editor.SelectedTabIndex = TechEditorViewModel.MaterialsTabIndex;
        editor.MaterialsTable.Select(material);
        if (editor.MaterialsTable.SelectedRow is null)
            Messages.Warning($"'{Path.GetFileName(techPath)}' does not define material '{material}', nor does any library it names.");
    }

    private Technology? SafeTech(string techPath)
    {
        try { return _techCache.Get(techPath); }
        catch { return null; }
    }

    /// <summary>The document open at <paramref name="absolutePath"/> in this workspace window or another, or null.</summary>
    private IDockable? OpenDocumentAnywhere(string absolutePath)
    {
        string wanted = Path.GetFullPath(absolutePath);
        if (FindOpenDocument(wanted) is { } here) return here;
        foreach (var window in Views.WorkspaceLocator.AllWindows())
            if (window.DataContext is WorkspaceViewModel other && !ReferenceEquals(other, this) && other.FindOpenDocument(wanted) is { } there)
                return there;
        return null;
    }

    // ── the 3D editor (R-em3d53-5) ────────────────────────────────────────────────────────────

    /// <summary>HZN-3 — the materials of a design held in a Hertzon project are the project's, held in memory; they have no
    /// technology file to edit, and saving them back into the project is not available yet.</summary>
    private string? HeldMaterialsRefusal(string technologyPath)
        => _techCache.IsHeld(technologyPath)
            ? "The materials of a design in a Hertzon project are the project's own. Editing them here arrives with saving the design back."
            : null;

    /// <summary>Connects a 3D editor's material picker requests to the picker dialog.</summary>
    private void HookC3dMaterials(C3dEditorDocument doc)
        => doc.ViewModel.MaterialPickerRequested += (indices, startNew, part) => _ = ShowMaterialPickerAsync(doc, indices, startNew, part);

    /// <summary>The Materials dialog's Built-in toggle, as it was last left in this window.</summary>
    private bool _materialPickerShowsBuiltIns;

    private async Task ShowMaterialPickerAsync(C3dEditorDocument doc, IReadOnlyList<int> indices, bool startNew, C3dMaterialPart? part = null)
    {
        var vm = doc.ViewModel;
        if (HostWindowOf(doc) is not { } window) return;
        string? techPath = vm.Elaboration?.TechnologyPath;
        if (techPath is not null && HeldMaterialsRefusal(techPath) is { } held) { vm.StatusMessage = held; Messages.Info(held); return; }
        if (vm.Elaboration?.Technology is not { } tech || techPath is null)
        {
            if (techPath is not null && File.Exists(techPath))
            {
                // A technology that fails to LOAD leaves nothing to choose from; the way out is to open it.
                string why = $"This 3D view's technology '{Path.GetFileName(techPath)}' did not load, so there are no materials to choose from. It is open to fix.";
                vm.StatusMessage = why;
                Messages.Warning(why);
                OpenOrActivateTech(techPath);
                return;
            }
            // 3D editor bugs round 2 — resolving NONE used to end here with a sentence, so New Material… did nothing
            // visible in a cell with no layout and a workspace with no default. The design is given one instead.
            if (await ChooseC3dTechnologyAsync(doc, window) is not { } chosen) return;
            (tech, techPath) = chosen;
        }

        string? current = part is not null ? part.Current : indices.Count == 1 ? vm.Document.Objects[indices[0]].Material : vm.CurrentMaterial;
        var picker = new MaterialPickerViewModel(MaterialSeeds(tech, techPath), Path.GetFileName(techPath), startNew, current,
                                                 part is not null ? 1 : indices.Count, null)
        {
            TissueLibrary = NamesLibrary(tech, MaterialLibraries.TissueFileName) ? null : () => TissueLibrarySeed(techPath),
        };
        // The warning is owned by the DIALOG, the top window: owned by the workspace window under it, closing the warning closed
        // the dialog as well on macOS (owner-reported: Cancel on the warning dismissed the Materials dialog).
        var dialog = new Views.Dialogs.MaterialPickerDialog(picker);
        picker.ConfirmDeleteExisting = (name, library) => ConfirmPickerMaterialDelete(dialog, tech, techPath, name, library);
        // brief-sar-6 (sar.md §4.4) — a setup that requests SAR evaluates its tissue at its SAR frequency: the card reads there
        if (vm.ActiveSetup is { RequestsSar: true } sarSetup && SarSettings.Resolve(sarSetup.Sar!).FrequenciesHz is [var sarHz, ..])
            picker.Table.ReadoutFrequencyHz = sarHz;
        // The Built-in toggle opens the way it was last left in this window.
        picker.Table.ShowBuiltIns = _materialPickerShowsBuiltIns;
        if (current is not null && picker.Table.SelectedRow is null) picker.Table.Select(current);
        picker.Table.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MaterialsTableViewModel.ShowBuiltIns)) _materialPickerShowsBuiltIns = picker.Table.ShowBuiltIns;
        };
        // brief-em3d-108 R-em3d108-1c — the dialog's appearance edits, live in every open 3D view on this technology
        var previewing = new HashSet<Viewer3D.Viewer3DViewModel>(ReferenceEqualityComparer.Instance);
        picker.AppearancePreview += (library, material, appearance) =>
            previewing.UnionWith(PreviewMaterialAppearance(techPath, library, material, appearance));
        bool ok = await dialog.ShowDialog<bool>(window);
        var changed = ok ? picker.ChangedLists : [];
        // Cancel (or OK with nothing to write) takes the previews away now — each view's table back byte for byte. OK with an edit
        // keeps them until the scene built from the written technology arrives, so nothing flashes back in between.
        foreach (var v in previewing)
            if (changed.Count == 0) v.EndAppearancePreview();
            else v.EndAppearancePreviewAtNextScene();
        if (!ok) return;

        // Add Tissue Materials, pressed in the dialog: the technology editor's own command names the library before any list
        // is committed to it.
        bool tissue = picker.AddedTissueLibrary is not null;
        if (tissue && AddTissueLibrary(techPath) is { } notAdded)
        {
            foreach (var v in previewing) v.EndAppearancePreview();
            Messages.Error(notAdded);
            return;
        }
        foreach (var (seed, materials) in changed)
            if (CommitMaterialList(techPath, seed, materials) is { } refused)
            {
                foreach (var v in previewing) v.EndAppearancePreview();
                Messages.Error(refused);
                return;
            }
        if (changed.Count > 0 || tissue) ActivateIfOpen(doc.Key);
        if (part is not null) vm.ApplyPickedMaterial(part, picker.ChosenName);
        else vm.ApplyPickedMaterial(indices, picker.ChosenName);
    }

    /// <summary>
    /// brief-em3d-108 R-em3d108-1c — <paramref name="material"/>'s appearance shown as <paramref name="appearance"/> in every open 3D
    /// view whose technology is <paramref name="techPath"/> or names <paramref name="library"/> (in this window and the others), and in
    /// every setup's 3D view that draws it. Returns the views that took it.
    /// </summary>
    internal IReadOnlyList<Viewer3D.Viewer3DViewModel> PreviewMaterialAppearance(string techPath, string? library, string material, TechAppearance? appearance)
    {
        var took = new List<Viewer3D.Viewer3DViewModel>();
        var workspaces = new List<WorkspaceViewModel> { this };
        foreach (var w in Views.WorkspaceLocator.AllWindows())
            if (w.DataContext is WorkspaceViewModel other && !ReferenceEquals(other, this)) workspaces.Add(other);
        foreach (var ws in workspaces)
            foreach (var doc in ws._openDocsByPath.Values)
            {
                var viewer = doc switch
                {
                    C3dEditorDocument c3d when OnTechnology(c3d.ViewModel.Elaboration) => c3d.ViewModel.Viewer,
                    Viewer3D.Viewer3DDocument setup => setup.ViewModel,
                    _ => null,
                };
                if (viewer is not null && viewer.PreviewAppearance(material, appearance)) took.Add(viewer);
            }
        return took;

        bool OnTechnology(C3dElaboration? e)
            => SamePath(e?.TechnologyPath, techPath)
            || (library is not null && e?.Technology?.ResolvedLibraryPaths.Contains(library, StringComparer.OrdinalIgnoreCase) == true);
    }

    // ── Paste into a 3D view (brief-em3d-95 R-em3d95-4) ──────────────────────────────────────

    /// <summary>Connects a 3D editor's paste to the Paste dialog, and its report (and a STEP split's) to Messages.</summary>
    private void HookC3dPaste(C3dEditorDocument doc)
    {
        doc.ViewModel.PasteDialogRequested += request => _ = ShowPasteDialogAsync(doc, request);
        // the first line is the summary, which the status line already says
        doc.ViewModel.PasteReported += lines => { foreach (string line in lines.Skip(1)) Messages.Info(line); };
        // brief-em3d-129 R-em3d129-3b — a split's notes, or every reason it was refused, at once.
        doc.ViewModel.StepReported += (lines, refused) =>
        {
            foreach (string line in lines)
                if (refused) Messages.Error(line, doc.ViewModel.FilePath);
                else Messages.Info(line, doc.ViewModel.FilePath);
        };
    }

    /// <summary>
    /// The one Paste dialog, then: the ticked materials created — whole, through the function the Materials dialog commits
    /// with (the technology's own list, else its first writable library), which leaves that document unsaved — and the paste
    /// committed as one undo entry of the 3D view. Cancel pastes nothing and creates nothing. With no technology resolved, the
    /// dialog also offers the Materials command's Choose a Technology…, and is shown again on the one chosen.
    /// </summary>
    private async Task ShowPasteDialogAsync(C3dEditorDocument doc, C3dPasteRequest request)
    {
        var vm = doc.ViewModel;
        if (HostWindowOf(doc) is not { } window) return;
        var plan = request.Plan;
        Technology? tech = vm.Elaboration?.Technology;
        string? techPath = vm.Elaboration?.TechnologyPath;
        while (true)
        {
            MaterialSourceSeed? seed = null;
            string? cannot = null, destination = null;
            if (plan.Materials.Count > 0)
            {
                if (tech is null || techPath is null)
                    cannot = "This 3D design resolves no technology, so a new material has nowhere to go: choose a technology, or paste without creating.";
                else if (MaterialSeeds(tech, techPath).FirstOrDefault(s => s.ReadOnlyReason is null) is { } writable)
                {
                    seed = writable;
                    destination = writable.LibraryPath is { } lib ? MaterialLibraries.Display(lib) : techPath;
                }
                else
                    cannot = $"'{Path.GetFileName(techPath)}' cannot take a new material: {ReadOnlyPathReason(techPath) ?? "it and its libraries are read-only"}.";
            }
            var dialog = new C3dPasteDialogViewModel(plan, vm.VariableNameTaken, request.Payload.VariableNotes.Select(n => n.Name),
                                                     destination, cannot, canChooseTechnology: tech is null);
            var result = await new Views.Dialogs.C3dPasteDialog(dialog, choices => vm.PasteRefusal(request.Payload, choices))
                                   .ShowDialog<C3dPasteDialogResult?>(window);
            if (result is null) return;
            if (result.Answer == C3dPasteDialogAnswer.ChooseTechnology)
            {
                if (await ChooseC3dTechnologyAsync(doc, window) is not { } chosen) return;
                (tech, techPath) = chosen;
                plan = vm.PlanPaste(request.Payload, tech);
                continue;
            }
            if (result.Choices.CreateMaterials.Count > 0 && seed is not null && techPath is not null)
            {
                var add = plan.Materials.Where(m => result.Choices.CreateMaterials.Contains(m.Name)).Select(m => m.Material);
                if (CommitMaterialList(techPath, seed, [.. seed.Materials, .. add]) is { } refused)
                {
                    Messages.Error(refused + " Nothing was pasted.");
                    return;
                }
                ActivateIfOpen(doc.Key);
            }
            if (vm.CommitPaste(request.Payload, result.Choices) is { } why) Messages.Error(why);
            return;
        }
    }

    /// <summary>
    /// Flatten's materials (<see cref="C3dEditorViewModel.AddMaterials"/>): appended where the Paste dialog's creation puts a
    /// new material — the technology's own list, else its first writable library — through the function the Materials dialog
    /// commits with, which leaves that document unsaved. Returns the refusal, or null.
    /// </summary>
    private string? AddC3dMaterials(C3dEditorDocument doc, IReadOnlyList<TechMaterial> materials)
    {
        var vm = doc.ViewModel;
        if (vm.Elaboration?.Technology is not { } tech || vm.Elaboration.TechnologyPath is not { } techPath)
            return "This 3D design resolves no technology, so a new material has nowhere to go.";
        if (MaterialSeeds(tech, techPath).FirstOrDefault(s => s.ReadOnlyReason is null) is not { } seed)
            return $"'{Path.GetFileName(techPath)}' cannot take a new material: {ReadOnlyPathReason(techPath) ?? "it and its libraries are read-only"}.";
        if (CommitMaterialList(techPath, seed, [.. seed.Materials, .. materials]) is { } refused) return refused;
        ActivateIfOpen(doc.Key);
        return null;
    }

    /// <summary>Whether <paramref name="tech"/> already names a library of that file name (the technology editor's test for
    /// offering Add Generic / Add Tissue Materials).</summary>
    private static bool NamesLibrary(Technology tech, string fileName)
        => tech.ResolvedLibraryPaths.Any(p => string.Equals(Path.GetFileName(p), fileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>The Materials dialog's Add Tissue Materials: the library as it will be beside the technology, or why it cannot be
    /// put there — asked when the button is pressed, so a refusal is said in the dialog rather than after OK.</summary>
    private (MaterialSourceSeed? Seed, string? Refusal) TissueLibrarySeed(string techPath)
    {
        if (ReadOnlyPathReason(techPath) is { } readOnly)
            return (null, $"'{Path.GetFileName(techPath)}' cannot name the tissue library: {readOnly}");
        if (MaterialLibraries.ShippedBesideConflict(techPath, MaterialLibraries.TissueFileName, "tissue") is { } conflict) return (null, conflict);
        string json = MaterialLibraries.ShippedRawJson(MaterialLibraries.TissueFileName);
        return (new MaterialSourceSeed(MaterialLibraries.TissueFileName, MaterialLibraries.ShippedBesidePath(techPath, MaterialLibraries.TissueFileName),
                                       MaterialLibraryPersistence.Deserialize(json), null), null);
    }

    /// <summary>The technology editor's Add Tissue Materials on <paramref name="techPath"/>, opened if it is not: the file copied
    /// beside it and named, an unsaved change there. Null on success, else why not.</summary>
    private string? AddTissueLibrary(string techPath)
    {
        OpenOrActivateTech(techPath);
        if (OpenTechEditor(techPath) is not { } editor)
            return $"'{Path.GetFileName(techPath)}' did not open, so the tissue library was not added to it.";
        if (!editor.CanAddTissueMaterials) return null;
        editor.AddTissueMaterials();
        if (editor.MaterialsRefusal is { } why) return why;
        Messages.Info($"{MaterialLibraries.TissueFileName} was copied beside {Path.GetFileName(techPath)}, which now names it and is unsaved — save it to keep it.");
        return null;
    }

    /// <summary>
    /// Materials editor redesign (2026-09-29) — the lists the 3D view's Materials dialog edits: the technology's own
    /// materials, then each library it names, each as it stands NOW — an open document's unsaved state, else the file.
    /// A library that cannot be written says why; a library shipped inside circuitRF is shown and never edited.
    /// </summary>
    private List<MaterialSourceSeed> MaterialSeeds(Technology tech, string techPath)
    {
        var seeds = new List<MaterialSourceSeed>();
        var own = OpenTechEditor(techPath)?.Working.Materials ?? tech.Materials;
        seeds.Add(new MaterialSourceSeed($"{Path.GetFileName(techPath)} (the technology's own)", null, own, ReadOnlyPathReason(techPath)));
        foreach (string lib in tech.ResolvedLibraryPaths)
        {
            string label = Path.GetFileName(MaterialLibraries.Display(lib));
            if (_openDocsByPath.TryGetValue(Path.GetFullPath(lib), out var d) && d is MaterialsDocument open)
                seeds.Add(new MaterialSourceSeed(label, lib, open.ViewModel.Working,
                                                 open.ViewModel.Table.IsReadOnly ? open.ViewModel.Table.ReadOnlyReason ?? $"'{label}' is read-only." : null));
            else if (File.Exists(lib))
                seeds.Add(new MaterialSourceSeed(label, lib,
                                                 _techCache.LiveLibrary(lib) is { } live ? [.. live] : MaterialLibraryPersistence.LoadFromFile(lib),
                                                 ReadOnlyPathReason(lib)));
            else
                seeds.Add(new MaterialSourceSeed(label, null,
                                                 [.. tech.LibraryMaterials.Where(m => string.Equals(m.SourcePath, lib, StringComparison.OrdinalIgnoreCase)).Select(m => m.Material)],
                                                 $"'{label}' is shipped inside circuitRF and is not edited: Duplicate a material from it to change a copy.",
                                                 BuiltIn: true));
        }
        return seeds;
    }

    /// <summary>
    /// Commits one list the Materials dialog changed to the file it came from, as ONE entry on that file's own document —
    /// opening it if needed. Nothing reaches disk until that document is saved; the message says which one became dirty.
    /// </summary>
    private string? CommitMaterialList(string techPath, MaterialSourceSeed seed, List<TechMaterial> materials)
    {
        string label = seed.LibraryPath is { } p ? Path.GetFileName(p) : Path.GetFileName(techPath);
        if (seed.LibraryPath is { } lib)
        {
            OpenOrActivateMaterials(lib);
            if (!_openDocsByPath.TryGetValue(Path.GetFullPath(lib), out var d) || d is not MaterialsDocument library)
                return $"'{label}' did not open, so its material changes were not made.";
            if (library.ViewModel.Table.IsReadOnly) return library.ViewModel.Table.ReadOnlyReason ?? $"'{label}' is read-only.";
            library.ViewModel.CommitEdit(list => { list.Clear(); list.AddRange(materials); }, "Edit materials from the 3D view");
        }
        else
        {
            OpenOrActivateTech(techPath);
            if (OpenTechEditor(techPath) is not { } editor) return $"'{label}' did not open, so its material changes were not made.";
            if (editor.ReplaceOwnMaterials(materials, "Edit materials from the 3D view") is { } why) return why;
        }
        Messages.Info($"The materials of {label} were changed, and it is now unsaved — save it to keep them.");
        return null;
    }

    /// <summary>
    /// 3D editor bugs round 2 — a 3D design that resolves no technology is given one: the Choose a Technology dialog (the
    /// workspace's <c>.ctech</c> files, the built-in ones — copied into <c>tech/</c> by the function New Workspace copies
    /// with — and Browse…), written as the design's own reference, one undo entry. Returns the technology loaded from it,
    /// or null when cancelled or it would not load.
    /// </summary>
    private async Task<(Technology Tech, string Path)?> ChooseC3dTechnologyAsync(C3dEditorDocument doc, Avalonia.Controls.Window window)
    {
        var vm = doc.ViewModel;
        string? cws = CircuitRF.Design.Workspace.WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(Path.GetFullPath(vm.FilePath)));
        string? root = cws is null ? null : Path.GetDirectoryName(cws);
        var catalog = TechnologyCatalog.All.Select(e => (e.Name, e.Id)).ToList();
        var dialog = new Views.Dialogs.ChangeTechnologyDialog(
            "This 3D design has no technology, and its materials come from one. Choose the technology it uses:", root, catalog);
        if (await dialog.ShowDialog<Views.Dialogs.ChangeTechnologyResult?>(window) is not { } result) return null;

        string path;
        try
        {
            if (result.CatalogId is { } id)
            {
                if (root is null) return null;
                path = CircuitRF.Design.Workspace.WorkspaceCreate.InstallTechnology(root, id);
                Messages.Info($"'{Path.GetFileName(path)}' was copied into this workspace's tech/ folder.");
            }
            else if (result.AbsoluteTechPath is { } chosen) path = chosen;
            else return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Messages.Error($"The technology could not be copied into the workspace: {ex.Message}");
            return null;
        }

        var loaded = TechnologyResolver.LoadForPath(path);
        if (loaded is null)
        {
            Messages.Error($"'{Path.GetFileName(path)}' did not load as a technology, so it was not used.");
            return null;
        }
        vm.UseTechnology(path);
        vm.StatusMessage = $"This 3D design now uses {loaded.Name} ({Path.GetFileName(path)}).";
        return (loaded, path);
    }

    /// <summary>
    /// 3D ▸ Materials… — the active 3D view's technology, opened on its Materials tab. Instance technologies are named,
    /// not edited from here (M8): editing a child's process from its parent is editing another design.
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasActiveC3dEditor))]
    private async Task ThreeDMaterials()
    {
        if (ActiveC3dEditor() is not { } c3d) return;
        if (c3d.Elaboration?.TechnologyPath is not { } techPath || !File.Exists(techPath))
        {
            // 3D editor bugs round 2 — resolving none is not a dead end: the design is given a technology first.
            if (ResolveActiveDocumentForCommands() is not C3dEditorDocument doc
                || HostWindowOf(doc) is not { } window || await ChooseC3dTechnologyAsync(doc, window) is not { } chosen)
                return;
            techPath = chosen.Path;
        }
        OpenOrActivateTech(techPath);
        if (OpenTechEditor(techPath) is { } editor) editor.SelectedTabIndex = TechEditorViewModel.MaterialsTabIndex;

        var others = c3d.Elaboration?.WalkInstances.Select(s => s.Detail).ToList() ?? [];
        if (others.Count > 0)
            Messages.Info("Placed cells resolve their own technologies, edited from those technologies, not from here: " +
                          string.Join("; ", others));
    }
}
