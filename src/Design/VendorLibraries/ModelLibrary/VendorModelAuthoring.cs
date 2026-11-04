using System.Text.Json;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.VendorLibraries.ModelLibrary;

/// <summary>
/// Changing a placed library part (brief vendorlib-12): it cannot be pushed into, and there are two commands that say
/// what they do instead — <b>Edit Vendor Model…</b>, which opens the model folder's own schematic and refreshes the model
/// when it is saved (<see cref="ModelLibraryExport.Refresh"/>), and <b>Copy to Workspace…</b>, which makes it an ordinary
/// cell of this workspace (<see cref="CopyToWorkspace"/>). Every rule both shells read is here, so the GUI's menu, the
/// CLI and a test ask the same question and get the same sentence.
/// </summary>
public static class VendorModelAuthoring
{
    /// <summary>R-vl12-1: why Push In is not offered on a library part — the toolbar's tooltip and the key's status line.</summary>
    public const string PushInRefusal = "A library part: use Edit Vendor Model… or Copy to Workspace…";

    /// <summary>R-vl12-2b / 3e: a SPICE-text vendor part.</summary>
    public const string SpiceTextReason = "A SPICE subcircuit; there is no schematic to edit.";

    /// <summary>R-vl12-2b: the library's <c>library.clib</c> says <c>ReadOnly</c>.</summary>
    public const string ReadOnlyReason = "The publisher marks this library read-only; Copy to Workspace makes an editable copy.";

    /// <summary>R-vl12-2b: the model folder is not writable — the probe's answer, never a guess.</summary>
    public static string UnwritableReason(string modelDir)
        => $"The model folder cannot be written ({modelDir}); Copy to Workspace makes an editable copy.";

    /// <summary>A placed library part of either kind: a model (<c>vendormodel://</c>) or a SPICE-text vendor part.</summary>
    public static bool IsLibraryPart(EditableComponent comp) => IsModelPart(comp) || IsSpiceTextPart(comp);

    public static bool IsModelPart(EditableComponent comp) => VendorModelRefs.IsRef(comp.CellRef);

    public static bool IsSpiceTextPart(EditableComponent comp)
        => comp.Symbol == SymbolKind.SpiceModel
           && VendorLibraryRefs.IsVendorRef(comp.Parameters.FirstOrDefault(p => p.Name == SpiceModelSymbolProvider.FileParameter)?.Expression?.Trim());

    /// <summary>A placed model, resolved: its folder, its primary schematic, its library and its manifest.</summary>
    public sealed record Target(string ModelDir, string Schematic, string Library, string LibraryRoot, ModelPartManifest Manifest);

    /// <summary>The model a placed part names, or null with the reason it cannot be reached.</summary>
    public static Target? Resolve(EditableComponent comp, string? schematicDirectory, string? workspaceRoot, out string? reason)
    {
        reason = null;
        if (IsSpiceTextPart(comp)) { reason = SpiceTextReason; return null; }
        if (!IsModelPart(comp)) { reason = "Not a library part."; return null; }
        if (VendorModelRefs.Resolve(comp.CellRef, schematicDirectory, workspaceRoot, out string? refusal) is not { } r)
        { reason = refusal; return null; }
        var (manifest, why) = ModelPartManifest.Read(Path.Combine(r.ModelDir, ModelFolder.PartFileName));
        if (manifest is null) { reason = why; return null; }
        if (ModelLibraryExport.PrimaryPath(r.ModelDir, ViewType.Schematic) is not { } sch)
        { reason = $"{manifest.Part} ships no schematic; there is nothing to edit or copy."; return null; }
        return new Target(r.ModelDir, sch, r.Library, r.LibraryRoot, manifest);
    }

    /// <summary>R-vl12-2b: null when Edit Vendor Model… is offered, else the reason it is greyed.</summary>
    public static string? EditRefusal(EditableComponent comp, string? schematicDirectory, string? workspaceRoot)
    {
        if (Resolve(comp, schematicDirectory, workspaceRoot, out string? reason) is not { } t) return reason;
        if (LibraryManifestOf(t.ModelDir).Manifest is { ReadOnly: true }) return ReadOnlyReason;
        if (!WorkspaceWritability.IsWritable(t.ModelDir)) return UnwritableReason(t.ModelDir);
        return null;
    }

    /// <summary>R-vl12-3e: null when Copy to Workspace… is offered, else the reason it is greyed.</summary>
    public static string? CopyRefusal(EditableComponent comp, string? schematicDirectory, string? workspaceRoot)
        => Resolve(comp, schematicDirectory, workspaceRoot, out string? reason) is null ? reason : null;

    /// <summary>The <c>library.clib</c> above a model folder — the nearest one — read; nulls when there is none.</summary>
    public static (string? Root, ModelLibraryManifest? Manifest) LibraryManifestOf(string modelDir)
    {
        for (string? d = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelDir))); d is not null; d = Path.GetDirectoryName(d))
        {
            string clib = Path.Combine(d, ModelFolder.LibraryFileName);
            if (File.Exists(clib)) return (d, ModelLibraryManifest.Read(clib).Manifest);
        }
        return (null, null);
    }

    /// <summary>A library's name as a person reads it: its <c>library.clib</c> <c>Name</c>, else its folder's.</summary>
    public static string LibraryNameOf(string modelDir)
    {
        var (root, m) = LibraryManifestOf(modelDir);
        if (m is { Name.Length: > 0 }) return m.Name;
        return Path.GetFileName(root ?? Path.GetDirectoryName(Path.GetFullPath(modelDir)) ?? modelDir);
    }

    /// <summary>
    /// R-vl12-2c: how many components in <paramref name="workspaceRoot"/>'s schematics place the model at
    /// <paramref name="modelDir"/>, by what each reference RESOLVES to rather than how it is spelt. Other workspaces
    /// cannot be counted, and nothing here pretends to.
    /// </summary>
    public static int CountPlacements(string workspaceRoot, string modelDir)
    {
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelDir));
        var resolved = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        int count = 0;
        foreach (string sch in Schematics(workspaceRoot))
        {
            JsonDocument doc;
            try { doc = JsonDocument.Parse(File.ReadAllText(sch)); } catch { continue; }
            using (doc)
            {
                if (doc.RootElement.ValueKind != JsonValueKind.Object
                    || !doc.RootElement.TryGetProperty("Components", out var comps) || comps.ValueKind != JsonValueKind.Array) continue;
                string dir = Path.GetDirectoryName(sch)!;
                foreach (var c in comps.EnumerateArray())
                {
                    if (c.ValueKind != JsonValueKind.Object || !c.TryGetProperty("CellRef", out var r) || r.ValueKind != JsonValueKind.String) continue;
                    string? cellRef = r.GetString();
                    if (!VendorModelRefs.IsRef(cellRef)) continue;
                    string key = VendorModelRefs.Bare(cellRef!);
                    if (!resolved.TryGetValue(key, out bool hit))
                        resolved[key] = hit = VendorModelRefs.Resolve(key, dir, workspaceRoot, out _) is { } m
                                              && DocumentFileRefs.PathComparer.Equals(Path.TrimEndingDirectorySeparator(m.ModelDir), target);
                    if (hit) count++;
                }
            }
        }
        return count;
    }

    /// <summary>Every <c>.csch</c> under a workspace, skipping hidden folders and its results.</summary>
    private static IEnumerable<string> Schematics(string root)
    {
        var stack = new Stack<string>([Path.GetFullPath(root)]);
        while (stack.TryPop(out string? dir))
        {
            string[] files, subs;
            try { files = Directory.GetFiles(dir, "*.csch"); subs = Directory.GetDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }
            foreach (string f in files) yield return f;
            foreach (string s in subs)
            {
                string leaf = Path.GetFileName(s);
                if (leaf.StartsWith('.') || leaf.Equals(DocumentFileRefs.ResultsFolder, StringComparison.OrdinalIgnoreCase)) continue;
                stack.Push(s);
            }
        }
    }

    /// <summary>R-vl12-2c's confirmation, asked once per model per session before the first edit is saved.</summary>
    public static string ConfirmSentence(string part, string library, int placements)
        => $"Saving changes {part} in {library} for every design that places it. "
           + placements switch
           {
               0 => "This workspace does not place it.",
               1 => "This workspace places it once.",
               _ => $"This workspace places it {placements} times.",
           };

    // ── Copy to Workspace (R-vl12-3) ─────────────────────────────────────────────────────────────

    /// <summary>What Copy to Workspace did.</summary>
    public sealed record CopyResult(string? CellDir, string? Refusal, string Message)
    {
        public bool Succeeded => CellDir is not null;
    }

    /// <summary>
    /// R-vl12-3a: the model folder at <paramref name="modelDir"/> copied into the workspace folder
    /// <paramref name="destParentDir"/> as an ordinary cell, named after the part number made unique by Export's suffix
    /// rule (<c>TSTFET100_2</c>). The copy is <see cref="CrossWorkspaceCellCopy"/> — Add Cell to Workspace's step —
    /// so <c>models/</c>, <c>cells/</c> and <c>docs/</c> come with it and placed cells are re-pointed; then every file
    /// reference is rewritten from model-relative to workspace-root-relative (<see cref="SnpPathPolicy.ToStored"/>) while
    /// the copy is still a model folder for them to resolve against, <c>part.cpart</c> and the <c>.cnl</c> are removed
    /// (a workspace cell is extracted from its schematic), and the <c>.ccell</c> records <see cref="CcellFile.CopiedFrom"/>.
    /// </summary>
    public static CopyResult CopyToWorkspace(string modelDir, string destParentDir, string? libraryName = null)
    {
        string src = Path.TrimEndingDirectorySeparator(Path.GetFullPath(modelDir));
        var (manifest, why) = ModelPartManifest.Read(Path.Combine(src, ModelFolder.PartFileName));
        if (manifest is null) return new CopyResult(null, why, why ?? "");
        string part = manifest.Part;
        CopyResult Refuse(string reason) => new(null, reason, $"{part} was not copied: {reason}");

        if (ModelLibraryExport.PrimaryPath(src, ViewType.Schematic) is null)
            return Refuse($"{part} ships no schematic, and a workspace cell is extracted from its schematic.");
        string parent = Path.GetFullPath(destParentDir);
        if (!Directory.Exists(parent)) return Refuse($"'{parent}' does not exist.");
        if (WorkspaceRootFinder.WorkspaceDirOf(parent) is not { } destRoot)
            return Refuse($"'{parent}' is in no workspace.");
        if (ModelFolder.OwningModelFolder(parent) is { } inside)
            return Refuse($"'{parent}' is inside the model folder '{inside}'.");
        if (WorkspaceCreate.UnwritableParentRefusal(parent, $"{part} was not copied") is { } unwritable)
            return new CopyResult(null, unwritable, unwritable);

        string name = part;
        for (int n = 2; Directory.Exists(Path.Combine(parent, name)) || File.Exists(Path.Combine(parent, name)); n++)
            name = $"{part}_{n}";
        string library = libraryName ?? LibraryNameOf(src);

        string dest = Path.Combine(parent, name);
        try
        {
            var plan = CrossWorkspaceCellCopy.Plan(src, parent, destRoot, SubCellMode.Copy, topName: name, cache: new Layout.TechnologyCache());
            dest = CrossWorkspaceCellCopy.Execute(plan);

            // While part.cpart is still there the copy is a model folder, and its model-relative references resolve
            // against it; each becomes the form a workspace document stores.
            InvalidateResolution();
            foreach (string doc in Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories)
                                            .Where(DocumentFileRefs.IsDocument)
                                            .Where(f => !f.EndsWith(ModelFolder.NetlistExtension, StringComparison.OrdinalIgnoreCase))
                                            .Order(StringComparer.Ordinal).ToList())
            {
                string? text = DocumentFileRefs.Rewrite(doc, OwningRoot.Of(Path.GetDirectoryName(doc)),
                    (abs, refBase) => refBase == RefBase.Document ? null
                        : SnpPathPolicy.ToStored(CircuitRF.Core.Devices.External.OsdiBuilds.NeutralPath(abs), destRoot));
                if (text is not null) AtomicFile.WriteAllText(doc, text);
            }

            File.Delete(Path.Combine(dest, ModelFolder.PartFileName));
            string cnl = ModelFolder.NetlistPath(dest, part);
            if (File.Exists(cnl)) File.Delete(cnl);

            string ccellPath = Path.Combine(dest, CellFolder.CcellFileName);
            var ccell = File.Exists(ccellPath) ? CellPersistence.LoadFromFile(ccellPath) : new CcellFile();
            ccell.CopiedFrom = new CcellCopyProvenance
            {
                Library = library, Part = part, Revision = manifest.Revision, PublishedHash = manifest.PublishedHash,
            };
            CellPersistence.SaveToFile(ccellPath, ccell);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
                                       or System.Text.Json.JsonException)
        {
            // The name was free when chosen, so whatever is there now is this copy's, half made — still a model folder
            // with its references half rewritten. Leave nothing behind.
            try { if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            InvalidateResolution();
            return Refuse($"the copy could not be written ({ex.Message}).");
        }
        InvalidateResolution();
        return new CopyResult(dest, null, $"Copied {part} from {library} into the workspace as {name}.");
    }

    private static void InvalidateResolution()
    {
        WorkspaceRootFinder.InvalidateCache();
        OwningRoot.InvalidateCache();
        ExternalCellRef.InvalidateCache();
        CellStat.InvalidateCache();
        CellSymbolResolver.InvalidateAll();
    }
}
