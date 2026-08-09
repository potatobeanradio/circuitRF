using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
using CircuitRF.Core.Netlist.Spice;
using CircuitRF.Core.Pdk;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Turns the parts an imported kit reports into entries the Library Palette can show and place.
///
/// <para><b>Why this installs cells rather than inventing a new component species.</b> circuitRF
/// already has a component whose artwork lives in a file outside the schematic and is resolved at
/// render time — a cell reference. A kit part is exactly that shape, so installing each readable
/// symbol as an ordinary cell means placement, rendering, pin geometry, hit-testing and the symbol
/// editor all work on kit parts with no new machinery, and the user can open and inspect the
/// generated symbol like any other. The alternative — a parallel "external part" render path —
/// would duplicate all of it and drift.</para>
///
/// <para>Nothing here knows anything about any particular kit. It reads what the importer found.</para>
/// </summary>
public static class PdkPartInstaller
{
    /// <summary>
    /// The folder an import used to write translated kit cells into, and must no longer create. Kept
    /// as a named constant because it is what the gate asserts the ABSENCE of — a kit's symbols are
    /// the vendor's, and putting them in the workspace is what made a shared workspace carry them.
    /// </summary>
    public const string InstallFolderName = "pdk";

    /// <param name="Items">Entries for the Library Palette — PLACEABLE parts only.</param>
    /// <param name="OmittedNotPlaceable">
    /// Parts the kit declares that got no readable symbol, so nothing could be placed for them.
    /// These are almost always a kit's internal building blocks — the helper subcircuits its real
    /// parts are assembled from — which a component browser should not be cluttered with. They are
    /// counted rather than hidden: the import report still lists every one of them.
    /// </param>
    /// <param name="Notes">
    /// What the import WORKED OUT, as neutral status. Kept apart from <c>Diagnostics</c> because a
    /// user importing a kit should not be told that everything going right is a warning: a wall of
    /// them undermines the one line that is a real warning.
    /// </param>
    /// <param name="Parts">
    /// The kit's parts, translated and held in memory. The caller registers them; nothing is
    /// written. One per entry in <paramref name="Items"/>, in the same order.
    /// </param>
    /// <param name="Settings">
    /// What circuitRF settled about how to simulate this kit — the object a
    /// <c>device-provider.json</c> holds. Recorded by the workspace, so an open reads it back rather
    /// than re-deriving it: re-deriving is both the slow part and the part that could quietly answer
    /// differently when the machine changed. Null for a kit with nothing compiled to serve.
    /// </param>
    /// <param name="KitName">
    /// The name the kit was loaded under — the one every part reference is built from, so the caller
    /// registers under exactly this and never under its own idea of the kit's name. They differ when
    /// the report carries none, which is the case that would otherwise leave every reference pointing
    /// at a kit that was never registered.
    /// </param>
    public sealed record InstallOutcome(
        IReadOnlyList<PaletteItem> Items,
        int SymbolsInstalled,
        int IconsFound,
        IReadOnlyList<string> Diagnostics,
        int OmittedNotPlaceable = 0,
        IReadOnlyList<string>? Notes = null,
        IReadOnlyList<PdkKitPart>? Parts = null,
        JsonNode? Settings = null,
        string KitName = "",
        /// <summary>The corner choices the kit declares, for the workspace to record. Empty for the
        /// overwhelming majority of kits, which declare none.</summary>
        IReadOnlyList<PdkCornerAxis>? CornerAxes = null,

        /// <summary>
        /// The compiled Verilog-A artefacts found for this kit, and the modules each implements.
        ///
        /// <para>Handed to <see cref="PdkKitRegistry"/> rather than recorded: they are the user's own
        /// build output and a kit-relative path to one is not a thing that exists. Empty for every
        /// kit whose devices are not compiled Verilog-A, which is nearly all of them.</para>
        /// </summary>
        IReadOnlyList<OsdiModel>? OsdiModels = null);

    /// <summary>
    /// Install every part the report lists. Returns one palette entry per part — including parts
    /// whose symbol could not be read, which still appear (with their icon, if any) so the user can
    /// see what the kit contains rather than silently losing it.
    /// </summary>
    /// <param name="report">The importer's own findings. Never modified.</param>
    /// <param name="recordedSettings">
    /// Settings the workspace already recorded for this kit, if it has any. Supplied on a workspace
    /// OPEN so the library discovery and variant choices are read back rather than re-derived —
    /// which is what makes an open both fast and repeatable. Null on a fresh import, where they are
    /// derived and returned for the workspace to record.
    /// </param>
    /// <param name="libraryRoots">
    /// Folders the workspace has been told hold model libraries. A delivery is several part kits
    /// beside one shared library package; once a kit is referenced from somewhere else that adjacency
    /// is gone, and being told is the only thing that recovers it.
    /// </param>
    public static InstallOutcome Install(
        PdkImportReport report, JsonNode? recordedSettings = null,
        IReadOnlyList<string>? libraryRoots = null)
    {
        var items  = new List<PaletteItem>();
        var parts  = new List<PdkKitPart>();
        var diags  = new List<string>();
        var notes  = new List<string>();
        int syms    = 0;
        int icons   = 0;
        int omitted = 0;

        // A kit imported from an archive has no directory to resolve its own asset paths against,
        // so its artwork cannot be reached without extracting it first. Say so once, not per part.
        bool haveRoot = !string.IsNullOrEmpty(report.RootPath) && Directory.Exists(report.RootPath);
        if (!haveRoot && report.Parts.Count > 0)
            diags.Add("This kit was read from an archive, so its artwork could not be opened. " +
                      "Extract it to a folder and import that to get symbols and palette icons.");

        string kit = string.IsNullOrWhiteSpace(report.KitName) ? "Kit" : report.KitName;

        // Read BEFORE any part is built: a variant becomes part of each cell's own declared
        // parameter interface, so it has to be in hand while the parts are being built.
        // Read the kit's OWN netlists first: the formulations a part offers, which of them circuitRF
        // can build, and the circuit each one is, are all in there — so a kit that declares nothing
        // still yields a working part. A manifest can still name things, and wins where it does.
        var discovered = haveRoot ? DiscoverFromKitNetlists(report, diags) : new KitDiscovery();

        // ONE scale for the whole kit, from its largest drawing-backed part. A kit draws every symbol
        // in one coordinate system, so their relative sizes are the author's choice; scaling each
        // part into the legibility band on its own throws that away and lands a ground marker bigger
        // than the transistor beside it. Zero when no part carries a drawing, and the per-part
        // fallback then applies — which is the symbol-library path throughout.
        double kitScale = KitTemplateSymbol.ChooseKitScale(
            report.Parts.Select(p => (p.Pins, p.Body)));

        // THE COMPILED VERILOG-A ARTEFACTS, FOUND BEFORE THE SETTINGS AND INDEPENDENTLY OF THEM.
        //
        // Not inside the synthesis below, because recorded settings SKIP the synthesis entirely — and
        // the index has to be in hand on a workspace open just as much as on a fresh import, since it
        // is what turns a .model card into the file implementing it every time a design is run.
        var osdiModels = haveRoot
            ? FindCompiledModels(report.RootPath, libraryRoots, notes, diags)
            : [];

        // Settled settings, in priority order. The workspace's own recorded ones win outright on an
        // open — that is the whole point of recording them — then whatever the kit itself ships,
        // and finally what can be derived. A kit shipping no description of how to simulate its
        // devices is the ORDINARY case: a vendor kit is written for its own simulator and knows
        // nothing about circuitRF, so deriving is the difference between "import the kit" and
        // "import the kit, then go and configure it".
        JsonNode? settings = KeepIfStillCurrent(recordedSettings)
                          ?? (haveRoot ? TryReadKitSettings(report.RootPath, diags) : null)
                          ?? (haveRoot ? SynthesiseProviderSettings(report, kit, discovered, diags, notes,
                                                                     libraryRoots: libraryRoots,
                                                                     osdiModels: osdiModels) : null);

        // Whatever the settings call themselves, they answer to the KIT's name. Each part records
        // Provider = the kit name and a netlist asks for that, so settings answering to anything else
        // leave every step working and only Run failing.
        if (settings is JsonObject obj) obj["provider"] = kit;

        var kitManifest = haveRoot ? ManifestFrom(settings, report.RootPath, kit) : null;
        var fileParams  = kitManifest?.FileParameters ?? [];

        // Said AFTER the manifest is settled, because one may have just been derived — reporting the
        // question before answering it would tell the user something that stopped being true two
        // lines later. Both branches are stated, so the report answers either way.
        if (haveRoot && report.Parts.Count > 0)
            notes.Add(DescribeSimulationSettings(kitManifest));

        foreach (var part in report.Parts)
        {
            string? iconPath = null;
            if (haveRoot && part.IconRelativePath is { Length: > 0 } rel)
            {
                string abs = Resolve(report, rel);
                if (File.Exists(abs)) { iconPath = abs; icons++; }
            }

            PdkKitPart? built = null;
            if (haveRoot && (part.SymbolArtwork is not null || part.Pins is { Count: > 0 }))
            {
                // A manifest naming this part wins; otherwise what the kit's own netlist showed.
                var declared  = (kitManifest?.Variants ?? []).Where(v => v.AppliesTo(part.Id)).ToList();
                var variants  = declared.Count > 0 ? declared : discovered.VariantsFor(part.Id);
                var netlist   = NetlistPartFor(part.Id, kitManifest, diags)
                                ?? discovered.NetlistFor(part.Id)
                                ?? DiscoveredDefinitionFor(report, part);

                if (part.SymbolArtwork is { } art)
                    built = TryBuildPart(kit, part, Resolve(report, art.RelativePath), diags, notes,
                                         iconPath, variants, fileParams, netlist);

                // Terminals the importer already resolved and attached. WHICH builder they go to is
                // decided by whether the part carries a DRAWING: one that does states its own artwork
                // and its own axis sense; one that does not came from a symbol library, which states
                // positions only. Reading a drawing under the library's convention mirrors every
                // symbol vertically — it still places, still connects, and is upside down.
                //
                // Tried after a per-part drawing file so a part that has one keeps it.
                if (built is null)
                {
                    var fromTemplate = part.Body is not null
                        ? KitTemplateSymbol.BuildFromDrawing(part.Pins, part.Body, kitScale)
                        : KitTemplateSymbol.Build(part.Pins);

                    if (fromTemplate is not null)
                        built = MakeKitPart(kit, part, fromTemplate, iconPath, variants, fileParams, netlist);
                }

                if (built is not null) syms++;
            }

            // Only placeable parts reach the palette. A part with no readable symbol is a kit's
            // internal building block, not something to browse for and click — and a tile that
            // cannot place anything is worse than no tile. The report still lists every part.
            if (built is null) { omitted++; continue; }

            parts.Add(built);
            items.Add(new PaletteItem(
                Kind:            SymbolKind.Generic,
                PortCount:       0,
                DisplayName:     string.IsNullOrWhiteSpace(part.DisplayName) ? part.Id : part.DisplayName,
                Category:        ComponentCategory.Other,
                SearchTerms:     BuildSearchTerms(part, kit),
                IsCommon:        false,
                ExtraCategories: null,
                // The reference a placed instance carries. Virtual, not a path: the part exists in
                // memory only, so there is nothing for a relative path to be relative to.
                Pdk:             new PdkPartRef(kit, part.Id, iconPath,
                                                PdkKitRegistry.RefFor(kit, part.Id),
                                                // The kit's OWN grouping, verbatim — never mapped onto
                                                // a ComponentCategory, because translating a kit's
                                                // vocabulary into circuitRF's is guessing at something
                                                // the kit already stated. This is what the palette
                                                // filter lists indented beneath the kit.
                                                Category:  part.Category,
                                                // The one identity a kit's schematic part and its
                                                // layout cell reliably share; KitPaletteMerge matches
                                                // on it when neither id rule can.
                                                ModelName: DeclaredModelName(part),
                                                // What the part ACCEPTS — the tie-break for the case
                                                // the model cannot settle, where a kit offers one
                                                // device as both an RF and a plain part and its two
                                                // layout cells name the same model. See
                                                // KitPaletteMerge.PairByParameterInterface.
                                                ParameterNames: [.. built.Ccell.Parameters.Select(p => p.Name)])));
        }

        return new InstallOutcome(items, syms, icons, diags, omitted, notes, parts, settings, kit,
                                  report.CornerAxes, osdiModels);
    }

    /// <summary>
    /// The compiled Verilog-A artefacts this kit's devices can be evaluated by — the kit's own tree
    /// first, widening outward only if that finds nothing, and finally the folders the workspace has
    /// been TOLD hold model libraries.
    ///
    /// <para><b>Nothing here resolves an artefact by a kit-relative path, and that is the load-bearing
    /// rule.</b> A kit of this shape ships Verilog-A SOURCES and expects them compiled; where the
    /// output lands is a property of whoever ran the compiler. One user's build happens to sit inside
    /// the kit tree, a second user of the same kit has none at all until they build, and a third may
    /// put theirs anywhere — so adjacency to the kit is a coincidence to exploit when it holds, never
    /// a fact to depend on. Being told is what covers the rest, which is the same bargain
    /// <see cref="DeviceLibraryDiscovery"/> already strikes for the other worker's libraries.</para>
    ///
    /// <para><b>Widening only when the narrower search found nothing</b> is that class's rule too, and
    /// for its reason: the further out the walk goes the less that territory has to do with this kit,
    /// and eventually it matches by accident.</para>
    /// </summary>
    private static IReadOnlyList<OsdiModel> FindCompiledModels(
        string kitRoot, IReadOnlyList<string>? libraryRoots,
        List<string> notes, List<string> diags, int ancestorLevels = 2)
    {
        string? worker = ShippedOsdiWorker();
        if (worker is null) return [];   // this build ships no OSDI worker: nothing to ask.

        foreach (string root in SearchRoots(kitRoot, ancestorLevels).Concat(libraryRoots ?? []))
        {
            var problems = new List<string>();
            var found    = OsdiModelDiscovery.Find([root], worker, problems);

            // A file that would not load is reported even when others in the same folder did: a model
            // the user believes they compiled, silently absent, is the worst outcome available here.
            foreach (string p in problems) diags.Add(p);

            if (found.Count == 0) continue;

            notes.Add($"Found {Plural(found.Count, "compiled model", "compiled models")} for this kit " +
                      $"in {root} — " +
                      string.Join(", ", found.Select(m => $"{Path.GetFileName(m.FilePath)} " +
                                                          $"({string.Join(", ", m.TypeIds)})")) + ".");
            return found;
        }

        return [];
    }

    /// <summary>A folder, then each ancestor up to <paramref name="levels"/>; deepest first.</summary>
    private static IEnumerable<string> SearchRoots(string root, int levels)
    {
        DirectoryInfo? dir;
        try { dir = new DirectoryInfo(Path.GetFullPath(root)); }
        catch (Exception ex) when (ex is ArgumentException or IOException) { yield break; }

        for (int i = 0; dir is not null && i <= Math.Max(0, levels); i++)
        {
            if (dir.Exists) yield return dir.FullName;
            dir = dir.Parent;
        }
    }

    /// <summary>
    /// The worker circuitRF ships for the openly-specified compiled-model ABI, named so
    /// <c>DeviceWorkerManifest.ResolveCommand</c> finds it in circuitRF's own tools folder wherever
    /// the design is eventually run — never as a path, which would record this machine's install.
    /// </summary>
    private const string OsdiWorkerCommand = "osdi-worker";

    /// <summary>
    /// Where that worker actually is on THIS machine, or null when this build does not ship it.
    ///
    /// <para>Only circuitRF's own tools folder is searched, unlike the proprietary worker's rule that
    /// also looks near the kit. This worker hosts an OPEN ABI and is entirely ours; a copy sitting in
    /// a kit would be someone else's build of our program, which is not a thing to run.</para>
    /// </summary>
    private static string? ShippedOsdiWorker()
    {
        foreach (string name in new[] { OsdiWorkerCommand, OsdiWorkerCommand + ".exe" })
        {
            if (string.IsNullOrEmpty(DeviceWorkerManifest.ToolsDirectory)) return null;

            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(DeviceWorkerManifest.ToolsDirectory, name)); }
            catch (Exception ex) when (ex is ArgumentException or IOException) { continue; }

            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// Recorded settings, unless circuitRF derived them itself under an older rule.
    ///
    /// <para><b>Only our own derivation is reconsidered.</b> Settings that came from the kit, or that
    /// a user edited, are theirs — replacing those silently would lose their work, even when they are
    /// broken. What is redone is what an older build of this code worked out, whether or not it still
    /// runs: a set of settings can be entirely runnable and still be missing something added since,
    /// and one that runs is exactly the one nothing else would ever replace.</para>
    /// </summary>
    private static JsonNode? KeepIfStillCurrent(JsonNode? recorded)
    {
        // Settings are an object. Anything else is not something to replay — and because JsonNode
        // converts implicitly from string, a caller passing the wrong argument entirely would
        // otherwise compile and be treated as a kit's settings.
        if (recorded is not JsonObject) return null;

        try
        {
            if (recorded["generatedBy"]?.GetValue<string>() != GeneratedMarker) return recorded;
            return (recorded["generatedFormat"]?.GetValue<int>() ?? 1) < GeneratedFormat ? null : recorded;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or JsonException)
        {
            // Not shaped the way our own generator writes them, so not ours to redo.
            return recorded;
        }
    }

    /// <summary>
    /// The kit's own settings file, as a node, or null when it ships none.
    ///
    /// <para>Shipping none is the ORDINARY case and says nothing. Shipping one that cannot be read is
    /// a different thing entirely — everything else about the kit still works, so the import must not
    /// be lost over it, but staying silent would leave the user with a kit that imports cleanly and
    /// cannot be simulated, and no line anywhere saying why.</para>
    /// </summary>
    private static JsonNode? TryReadKitSettings(string kitRoot, List<string> diags)
    {
        string p = Path.Combine(kitRoot, DeviceWorkerManifest.FileName);
        if (!File.Exists(p)) return null;

        try { return JsonNode.Parse(File.ReadAllText(p)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            diags.Add($"This kit says how to simulate its devices, but that description could not be " +
                      $"read ({ex.Message}). Everything else about the kit imported; simulating its " +
                      $"devices needs '{DeviceWorkerManifest.FileName}' to be valid.");
            return null;
        }
    }

    /// <summary>
    /// A manifest over settings held in memory. Relative paths inside them resolve against the KIT,
    /// because that is where the worker and the model files actually are — the settings themselves
    /// no longer sit in a folder of their own.
    /// </summary>
    internal static DeviceWorkerManifest? ManifestFrom(JsonNode? settings, string kitRoot, string kitName)
    {
        if (settings is null) return null;
        return DeviceWorkerManifest.TryParse(
            settings.ToJsonString(), kitRoot, $"the recorded settings for '{kitName}'", out _);
    }

    /// <summary>
    /// Carries a kit's device-provider manifest into the workspace, if it has one, so its parts can
    /// be simulated without the user configuring anything.
    ///
    /// <para><b>Why it is copied rather than read where it lies.</b> The workspace is the record of
    /// what was imported — installed cells already work this way, which is why a kit survives a
    /// reopen. Resolution then looks in one place instead of remembering where every kit came from.</para>
    ///
    /// <para>The copy declares <c>baseDirectory</c> pointing back at the kit, because the worker and
    /// the model files stay where the kit is installed and the manifest's relative paths must still
    /// reach them. A kit with no manifest is the ordinary case and is passed over in silence — its
    /// parts still place, draw and export; only simulating them needs one.</para>
    ///
    /// <para><b>The copy is always named for the kit</b>, whatever the original called itself. Each
    /// installed cell records <c>Provider = </c> the kit name, so that is the name a netlist asks
    /// for; a copy answering to anything else leaves every step working and the last one failing.</para>
    /// </summary>
    /// <summary>
    /// Writes a <c>device-provider.json</c> for a kit that ships none, so an UNMODIFIED vendor kit
    /// is simulable straight after import.
    ///
    /// <para><b>What has to be established, and why it is not simply read.</b> A vendor kit names
    /// its device types but never says which compiled library implements them — its own simulator
    /// resolves them by name across everything it has loaded, and the library is routinely a
    /// separate package BESIDE the kit rather than inside it. So the binding exists nowhere on disk
    /// and the importer has to work it out; the alternative is a file somebody hand-writes per kit,
    /// which is exactly the setup step this is here to remove.</para>
    ///
    /// <para>The result is written into the WORKSPACE, not the kit — the kit is read-only, and this
    /// is circuitRF's own record of what it worked out. It is ordinary JSON: everything chosen here
    /// is visible and one line to correct, which is what makes an automatic choice safe to make.</para>
    /// </summary>
    private static JsonNode? SynthesiseProviderSettings(
        PdkImportReport report, string kitName,
        KitDiscovery discovery, List<string> diags, List<string> notes, int ancestorLevels = 2,
        IReadOnlyList<string>? libraryRoots = null,
        IReadOnlyList<OsdiModel>? osdiModels = null)
    {
        var types = discovery.NativeDeviceTypes;

        // A KIT WHOSE DEVICES ARE COMPILED VERILOG-A IS ITS OWN SHAPE, not a variant of the one
        // below. The proprietary path binds a device TYPE to a library that exports an entry point
        // named after it; this one binds a `.model` card's MODULE to the one artefact declaring it,
        // and there is an artefact per model rather than one library serving all of them. The two
        // share no discriminator, so trying to serve both from one search would have to guess which
        // question it was answering.
        if (osdiModels is { Count: > 0 }) return OsdiProviderSettings(kitName, report, osdiModels, notes);

        if (types.Count == 0) return null;   // nothing compiled to serve — a purely schematic kit

        // One search PER TARGET, not one for the host. A vendor ships a build per platform side by
        // side, and the manifest describes all of them at once — so the Windows entry has to name the
        // Windows build even when the import happens on a Mac. On macOS the worker runs inside a
        // Linux VM (nothing on macOS can load a Linux ELF), so its target is Linux too.
        //
        // The FORMAT is what makes these two genuinely different searches; the path hints only rank
        // within a target. Without it a kit shipping one library answers both searches with the same
        // file, and a Linux-only kit would be described as having a Windows build — an entry that
        // would then fail at launch, which is precisely what naming a platform must never do.
        var linux   = DeviceLibraryDiscovery.Find(
            types, report.RootPath, ["linux_x86_64", "linux_x86", ".so"], ancestorLevels, notes.Add,
            DeviceLibraryDiscovery.LibraryFormat.Elf, libraryRoots);
        var windows = DeviceLibraryDiscovery.Find(
            types, report.RootPath, ["win32_64", "win64", "win32", ".dll"], ancestorLevels, null,
            DeviceLibraryDiscovery.LibraryFormat.Pe, libraryRoots);

        var match = linux ?? windows;
        if (match is null)
        {
            diags.Add($"This kit's devices ({string.Join(", ", types.Take(3))}" +
                      $"{(types.Count > 3 ? ", …" : "")}) are compiled models, and the library that " +
                      $"implements them was not found near '{report.RootPath}'. It usually ships as " +
                      $"a separate package beside the kit. Add that package in File ▸ Manage PDKs — " +
                      $"it needs no parts of its own — or import the folder holding both.");
            return null;
        }

        var profile = DeviceLibraryDiscovery.Profiles[0];

        // Where the worker actually is. circuitRF's tools directory is where it belongs, but a worker
        // sitting beside the kit is found too — otherwise a user holding one is blocked until a
        // release ships it.
        string? worker = DeviceLibraryDiscovery.FindWorker(profile, report.RootPath, ancestorLevels)
                      ?? libraryRoots?.Select(r => DeviceLibraryDiscovery.FindWorker(profile, r, 0))
                                      .FirstOrDefault(w => w is not null);
        if (worker is null)
        {
            diags.Add($"The program that evaluates this kit's devices ('{profile.Worker}') was not " +
                      $"found in circuitRF's tools folder or near the kit. The kit's parts still " +
                      $"build; simulating its devices needs that program.");
            return null;
        }

        string workerDir  = Path.GetDirectoryName(worker)!;
        string workerName = Path.GetFileName(worker);

        var workers = new JsonArray();

        // Native hosts run the worker directly against their own build. An entry is written only for
        // a platform whose build is actually present — naming one that is not there would turn "this
        // kit has no Windows build" into a failure to start a program.
        // The alias map, when circuitRF ships one. It names internal nodes a compiled model never
        // drives; minting an unknown for one solves an equation the model did not state, and the
        // symptom is a bias ramp that stalls rather than anything that reports itself.
        // Searched at the KIT itself first now, rather than a folder the workspace made for it —
        // which is where a kit-specific alias belongs anyway, beside the kit it describes.
        string? aliasMap = FindAliasMap(report.RootPath, workerDir);

        if (linux is not null)
            workers.Add(Launch("linux-x64", worker,
                aliasMap is not null ? [linux.Path, aliasMap] : [linux.Path]));

        // Windows runs the same worker against the library's own Windows build. The command is named
        // rather than resolved to a path here on purpose: the Windows worker is a DIFFERENT
        // executable from the Linux one, so on a Mac or a Linux box (where this import very often
        // happens) it does not exist to point at. A bare name resolves through
        // DeviceWorkerManifest.ToolsDirectory on whichever machine actually runs it.
        if (windows is not null)
            workers.Add(Launch("win-x64", WindowsWorkerCommand(profile, worker),
                aliasMap is not null ? [windows.Path, aliasMap] : [windows.Path]));

        // macOS cannot load a Linux library at all, so the worker runs in the VM circuitRF ships.
        // Two shares, because the worker and the library come from different places: ours and the
        // vendor's.
        if (linux is not null)
        {
            string dir  = Path.GetDirectoryName(linux.Path)!;
            string file = Path.GetFileName(linux.Path);
            var vmArgs = new List<string>
            {
                VmHostArguments.ShareFlag, VmHostArguments.ShareValue("crfw", workerDir),
                VmHostArguments.ShareFlag, VmHostArguments.ShareValue("kit",  dir),
            };

            // THE KIT'S OWN DATA FILES, mounted where they already live.
            //
            // A compiled model is told which data files to read through its OWN parameters — this
            // kit's FET takes four, and one of them is the path to its .mdl — and those arrive from
            // the netlist long after the VM has started. Unlike the model library there is no
            // command line left in which to rewrite them, so the tree is mounted at its own absolute
            // path and the paths simply become true.
            //
            // The tree offered is exactly the one KitDataFileResolver can anchor a file within, and
            // deliberately not a wider guess: what the reader could resolve at import is then exactly
            // what the model can open at run time. Two separate notions of "near the kit" would drift
            // apart, and the failure when they do is a path that resolves perfectly and cannot be
            // opened.
            if (KitDataFileResolver.OutermostSearchRoot(KitNetlistDirectory(report)) is { Length: > 0 } dataRoot &&
                Directory.Exists(dataRoot))
            {
                vmArgs.Add(VmHostArguments.ShareAtFlag);
                vmArgs.Add(VmHostArguments.ShareValue("kitdata", dataRoot));
            }

            vmArgs.Add("--");
            vmArgs.Add(VmHostArguments.GuestPath("crfw", workerName));
            vmArgs.Add(VmHostArguments.GuestPath("kit",  file));

            // Named by its HOST path and then put through the share mechanism, rather than assumed to
            // sit in the worker's own folder. It usually does — and ShareHostFile then reuses the
            // share already carrying the worker, producing the same short guest path — but it does
            // not have to: a worker found beside a kit leaves circuitRF's own map in circuitRF's
            // folder. Writing /mnt/crfw/… for that case would name a place nothing was mounted.
            if (aliasMap is not null)
            {
                vmArgs.Add(aliasMap);
                vmArgs = [.. VmHostArguments.ShareHostFile(vmArgs, vmArgs.Count - 1)];
            }

            workers.Add(Launch("osx", VmHostCommand, [.. vmArgs]));
        }

        if (workers.Count == 0) return null;

        // A Windows model imports its host callbacks from a NAMED MODULE, and the worker stages a
        // compatible shim under that name at run time — reading the name out of the model's own
        // import table rather than from anything remembered here. Saying which module that is, at
        // import time, is worth a line: it is the one fact that decides whether this kit's Windows
        // build is drivable at all, and finding out at Run instead is a much worse way to learn it.
        if (windows is not null) ReportWindowsHostModule(windows.Path, profile, notes, diags);

        var manifest = new JsonObject
        {
            ["provider"]      = kitName,
            ["baseDirectory"] = Path.GetFullPath(report.RootPath),
            // Marks this as circuitRF's own working-out rather than something a kit or a user wrote,
            // which is what makes it safe to redo later if what it names has moved or gone.
            ["generatedBy"]     = GeneratedMarker,
            ["generatedFormat"] = GeneratedFormat,
            ["_note"]         = "Written by circuitRF when this kit was imported. The kit itself says " +
                                "nothing about how to simulate its devices, so the model library was " +
                                "found by looking for the device types the kit's netlists name. Edit " +
                                "freely — this file is circuitRF's own, not the kit's.",
            ["workers"]       = workers,
        };

        notes.Add($"Devices in this kit will be evaluated using " +
                  $"'{Path.GetFileName(match.Path)}' ({string.Join(", ", match.Types)}), found at " +
                  $"{Path.GetDirectoryName(match.Path)}.");

        return manifest;

        static JsonNode Launch(string platform, string command, string[] arguments) => new JsonObject
        {
            ["platform"]  = platform,
            ["command"]   = command,
            ["arguments"] = new JsonArray(arguments.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
        };
    }

    /// <summary>
    /// Settings for a kit whose devices are compiled Verilog-A: <c>osdi-worker</c>, by bare command
    /// plus the artefact as its one argument — the form <c>tools/osdi-worker/README.md</c> records and
    /// its own <c>O7</c> test already gates.
    ///
    /// <para><b>One provider, several artefacts, and no new resolver concept.</b> A manifest declares
    /// one worker command, while a kit of this shape needs a different <c>.osdi</c> per model. That is
    /// already solved: <see cref="DeviceWorkerProviderResolver.ComposeOverride"/> carries a library in
    /// the PROVIDER NAME, and <c>Resolve</c> splits it back out and substitutes the argument that
    /// names a library — chosen by checking the value rather than its position. So the entry below
    /// carries one artefact as the default and every device the extractor routes replaces it with its
    /// own.</para>
    ///
    /// <para><b>Platform <c>any</c>, deliberately.</b> Unlike the proprietary worker there is no
    /// foreign binary to bridge: a user compiles these natively with their own toolchain, so the
    /// artefact and the worker are always built for the machine they are on. A per-platform entry
    /// would be describing a difference that does not exist here.</para>
    /// </summary>
    private static JsonNode? OsdiProviderSettings(
        string kitName, PdkImportReport report, IReadOnlyList<OsdiModel> models, List<string> notes)
    {
        // The default artefact, for a device that names no model card and so composes no override of
        // its own. Ordinal-first rather than "whichever the walk happened to yield" so two imports of
        // one kit settle on the same one; the routing overrides it for everything card-backed anyway.
        string first = models.Select(m => m.FilePath).OrderBy(p => p, StringComparer.Ordinal).First();

        var manifest = new JsonObject
        {
            ["provider"]        = kitName,
            ["baseDirectory"]   = Path.GetFullPath(report.RootPath),
            ["generatedBy"]     = GeneratedMarker,
            ["generatedFormat"] = GeneratedFormat,
            ["_note"]           = "Written by circuitRF when this kit was imported. Its devices are " +
                                  "compiled Verilog-A models, one file per model; the file below is " +
                                  "the default, and each device selects its own from the .model card " +
                                  "it names. Edit freely — this file is circuitRF's own, not the kit's.",
            ["workers"]         = new JsonArray(new JsonObject
            {
                ["platform"]  = "any",
                ["command"]   = OsdiWorkerCommand,
                ["arguments"] = new JsonArray(JsonValue.Create(first)!),
            }),
        };

        notes.Add($"Devices in this kit will be evaluated by circuitRF's compiled-model worker " +
                  $"('{OsdiWorkerCommand}') against the models you compiled: " +
                  string.Join(", ", models.SelectMany(m => m.TypeIds)) + ".");

        return manifest;
    }

    /// <summary>
    /// What the <c>win-x64</c> entry should name as its command: the absolute path when the worker
    /// found is genuinely the Windows one (an import performed ON Windows), otherwise the bare
    /// <c>&lt;worker&gt;.exe</c> name, which <c>DeviceWorkerManifest.ResolveCommand</c> resolves out
    /// of circuitRF's own tools folder wherever the design is eventually run.
    /// </summary>
    private static string WindowsWorkerCommand(DeviceLibraryDiscovery.WorkerProfile profile, string worker)
        => worker.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? worker
            : profile.Worker + ".exe";

    /// <summary>
    /// The map of internal nodes a compiled model does not drive. Null when there is none, which is
    /// the correct default — every node then stays an ordinary unknown, and a model with nothing to
    /// declare is unaffected.
    ///
    /// <para><b>The kit's own folder is searched FIRST, and that is the whole point.</b> Which node a
    /// degenerate node follows is definition data about ONE vendor's model — circuitRF cannot derive
    /// it, and a table carrying it would put one vendor's part numbers in circuitRF's own tree, where
    /// they serve every other kit nothing. <c>&lt;workspace&gt;/pdk/&lt;kit&gt;/</c> is already where
    /// everything else circuitRF cannot derive about a kit is declared (<c>device-provider.json</c>, a
    /// translated netlist), it is created at import, and it is re-read at every workspace open. An
    /// alias map belongs beside them.</para>
    ///
    /// <para>The other two are shared by EVERY kit, so a kit-scoped file has to win: otherwise a map
    /// sitting next to the worker would shadow the very file a user dropped for this kit, and the
    /// symptom is the bias ramp the map exists to fix — not anything that names a shadowed file.</para>
    ///
    /// <para>Beside the worker comes next: a user who ships their own worker and their own map
    /// together means the pair. circuitRF's own tools folder is last, as the shipped fallback — it
    /// carries no family entries, only the note saying where they go.</para>
    /// </summary>
    private static string? FindAliasMap(string kitInstallDir, string workerDir)
    {
        foreach (string? dir in new[] { kitInstallDir, workerDir, DeviceWorkerManifest.ToolsDirectory })
        {
            if (string.IsNullOrEmpty(dir)) continue;

            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(dir, DeviceLibraryDiscovery.AliasMapFileName)); }
            catch (Exception ex) when (ex is ArgumentException or IOException) { continue; }

            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// Reads which module the kit's Windows build imports circuitRF's worker ABI from, and says so.
    ///
    /// <para>Importing none of them is a clear report — this library is not one the worker can
    /// drive — and never a fallback guess at a name. A file that will not parse as a PE at all is
    /// passed over silently: this is a courtesy line, not a gate, and the import must not fail over
    /// it.</para>
    /// </summary>
    private static void ReportWindowsHostModule(
        string dllPath, DeviceLibraryDiscovery.WorkerProfile profile,
        List<string> notes, List<string> diags)
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(dllPath);
            if (!info.Exists || info.Length == 0 || info.Length > 256L * 1024 * 1024) return;
            bytes = File.ReadAllBytes(dllPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        string name = Path.GetFileName(dllPath);
        string? host = PeImports.ModuleSupplying(bytes, profile.HostCallbacks);

        if (host is not null)
            notes.Add($"On Windows, '{name}' resolves its host callbacks from '{host}'. circuitRF " +
                      $"supplies a compatible module under that name, staged per user at run time.");
        else
            // One message for two causes, because the outcome is the same either way: this build is
            // not one the worker can drive. Splitting "imports nothing of ours" from "could not be
            // read that far" would give the reader two things to act on where there is one.
            diags.Add($"circuitRF found none of the host callbacks its worker supplies in the " +
                      $"imports of '{name}', so that Windows build is not one the worker can drive. " +
                      $"The kit's other platforms are unaffected.");
    }

    /// <summary>
    /// The Linux VM host circuitRF ships, named so <c>DeviceWorkerManifest</c> resolves it out of
    /// circuitRF's own tools directory rather than the system path.
    /// </summary>
    private const string VmHostCommand = VmHostArguments.Command;

    /// <summary>Marks a manifest circuitRF derived itself, so it can tell its own work from a kit's.</summary>
    /// <summary>
    /// The folder a kit's netlists sit in — the anchor <see cref="KitDataFileResolver"/> resolves a
    /// data file against, and therefore the only anchor that gives the same answer here.
    ///
    /// <para>The import root is NOT interchangeable with it: for a kit imported whole it is the
    /// delivery folder, and for one healed from an already-installed workspace it is the netlist
    /// folder itself. Anchoring on the netlist keeps both cases pointing at the same tree the reader
    /// used.</para>
    /// </summary>
    private static string KitNetlistDirectory(PdkImportReport report)
    {
        var netlist = report.Assets.FirstOrDefault(a => a.Kind == PdkAssetKind.Netlist);
        if (netlist is null) return report.RootPath;

        try
        {
            string full = Path.GetFullPath(Path.Combine(report.RootPath, netlist.RelativePath));
            return Path.GetDirectoryName(full) ?? report.RootPath;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return report.RootPath; }
    }

    private const string GeneratedMarker = "circuitRF";

    /// <summary>
    /// What circuitRF's own manifests currently say. Bumped whenever a NEWLY generated manifest
    /// would differ from one written by an older build in a way that matters at run time — so the
    /// old one is redone at the next workspace open instead of quietly staying wrong.
    ///
    /// <para>Without it, "stale" meant only that a named program had gone. A manifest whose every
    /// path still existed but which was missing something added since — the kit's data share, which
    /// is the reason this exists — is perfectly runnable and perfectly broken, and re-importing the
    /// kit was the only way to pick it up.</para>
    ///
    /// <para>2 — the <c>osx</c> entry offers the kit's data tree, so a compiled model can open the
    /// data files its own parameters name.</para>
    ///
    /// <para>3 — the worker is given circuitRF's alias map, without which a model's undriven
    /// internal nodes become unknowns nobody wrote an equation for.</para>
    ///
    /// <para>4 — that alias map is looked for in the kit's own workspace folder first, so a map
    /// dropped there is the one used. A manifest written by 3 names whichever map was found under
    /// the old order and would keep naming it — runnable, and pointing at the wrong file.</para>
    /// </summary>
    private const int GeneratedFormat = 4;

    // ── Symbol installation ───────────────────────────────────────────────────

    /// <summary>
    /// Reads one symbol description and writes it out as a cell. Returns the cell folder, or null
    /// when the file could not be read — in which case the reason is recorded, never swallowed.
    /// </summary>
    private static PdkKitPart? TryBuildPart(string kitName, PdkPart part,
                                            string symbolAbsPath, List<string> diags, List<string> notes,
                                            string? iconPath,
                                            IReadOnlyList<DeviceWorkerVariant> variants,
                                            IReadOnlyList<string> fileParameters,
                                            (string AbsoluteNetlistPath, string CellName)? netlistPart)
    {
        if (!File.Exists(symbolAbsPath)) return null;

        // Only the text symbol-description format has a reader today. Anything else (a binary cell
        // view, for instance) is left alone; the importer already reports it as a known gap.
        if (!symbolAbsPath.EndsWith(".dsn", StringComparison.OrdinalIgnoreCase)) return null;

        DsnSymbolReadResult read;
        try
        {
            read = DsnSymbolReader.ReadFile(symbolAbsPath);
        }
        catch (Exception ex)
        {
            diags.Add($"'{part.DisplayName}': reading its symbol failed — {ex.Message}");
            return null;
        }

        if (!read.Success || read.Symbol is null)
        {
            string why = read.Diagnostics.Count > 0 ? read.Diagnostics[0] : "the file could not be understood";
            diags.Add($"'{part.DisplayName}': no symbol was installed — {why}");
            return null;
        }

        foreach (var d in read.Diagnostics)
            diags.Add($"'{part.DisplayName}': {d}");

        foreach (var n in read.Notes)
            notes.Add($"'{part.DisplayName}': {n}");

        return MakeKitPart(kitName, part, read.Symbol, iconPath, variants, fileParameters, netlistPart);
    }

    /// <summary>
    /// Publishes one part around a symbol, whoever produced it. Shared by both symbol sources on
    /// purpose: everything below decides what a PLACED instance is — its port count, its provider
    /// binding, its parameter interface — and two copies of that would drift the moment one changed.
    /// </summary>
    private static PdkKitPart MakeKitPart(string kitName, PdkPart part, Symbol symbol,
                                          string? iconPath,
                                          IReadOnlyList<DeviceWorkerVariant> variants,
                                          IReadOnlyList<string> fileParameters,
                                          (string AbsoluteNetlistPath, string CellName)? netlistPart)
    {
        {
            // Exactly the .ccell that used to be written beside the symbol — the same published
            // interface, built in memory instead. Nothing about a placed instance changes: it is
            // resolved through the same accessor a cell folder is.
            var ccell = new CcellFile();
            ccell.NumPorts = symbol.Pins.Count;

            // A kit part is a LEAF backed by a provider, not a hierarchy: it has a symbol and no
            // schematic on purpose, so extraction must emit one external-device instance rather
            // than trying to descend into it.
            ccell.ExternalProvider = kitName;
            ccell.ExternalType     = part.Id;
            ccell.ExternalIconPath = iconPath;

            // …unless the kit says this part is a CIRCUIT. A package is several devices plus the
            // passives connecting them — a subcircuit, not a device model — so it is emitted as an
            // ordinary cell instance and its definition read from the netlist the kit supplies.
            // The path is absolute because that file stays with the kit while the cell is installed
            // into the workspace, exactly like the worker and the model data.
            if (netlistPart is not null)
            {
                ccell.ExternalNetlistPath = netlistPart.Value.AbsoluteNetlistPath;
                ccell.ExternalNetlistCell = netlistPart.Value.CellName;
            }

            // The part's declared parameters become the cell's published interface, which is what
            // seeds a placed instance and drives the ordinary Parameter Editor — no separate
            // parameter-editing surface is needed for kit parts.
            ccell.Parameters              = BuildDeclaredParameters(part, variants, fileParameters);
            ccell.ExternalFixedParameters = BuildFixedParameters(part);

            return new PdkKitPart(part.Id, symbol, ccell, iconPath);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The part's declared parameters, as the cell's published interface — carrying the KIT's own
    /// defaults verbatim. circuitRF never invents a default: where the kit stated none, the field is
    /// left blank so whatever supplies the part's behaviour keeps ownership of it.
    /// </summary>
    private static List<CcellParameter> BuildDeclaredParameters(
        PdkPart part, IReadOnlyList<DeviceWorkerVariant> variants, IReadOnlyList<string> fileParameters)
    {
        var list = new List<CcellParameter>();

        // Variants first. A model-selection parameter is the one thing about a kit part a user
        // reasonably reaches for before anything else, and it is the only parameter here that
        // carries a real default rather than deferring to the provider.
        foreach (var v in variants)
        {
            list.Add(new CcellParameter
            {
                Name               = v.Parameter,
                DefaultExpression  = v.Default,
                Unit               = "",
                ShowOnSchematic    = false,
                Choices            = [.. v.Choices],
                UnsupportedChoices = v.Unsupported.Count > 0 ? [.. v.Unsupported] : null,
                Description        = v.Description.Length > 0 ? v.Description : null,
            });
        }

        if (part.Parameters is null) return list;

        foreach (var p in part.Parameters)
        {
            if (string.IsNullOrWhiteSpace(p.Name) || p.IsText) continue;   // text = infrastructure
            if (list.Any(c => c.Name.Equals(p.Name, StringComparison.Ordinal))) continue;  // variant wins
            list.Add(new CcellParameter
            {
                Name              = p.Name,
                DefaultExpression = InCircuitRfsOwnNotation(p.DefaultExpression),
                Unit              = "",
                ShowOnSchematic   = false,
            });
        }

        foreach (var p in list)
            if (fileParameters.Contains(p.Name, StringComparer.Ordinal))
                p.IsFilePath = true;

        // Offered on every kit part, blank. A kit cannot declare this — which model library is on
        // this machine is not something a kit knows — so it is circuitRF's own, and a picker is the
        // only sane way to set a path.
        list.Insert(0, new CcellParameter
        {
            Name              = ModelLibraryParameter,
            DefaultExpression = "",
            Unit              = "",
            ShowOnSchematic   = false,
            IsFilePath        = true,
            Description       = "Evaluate this instance with a different model library. "
                              + "Leave blank to use the kit's own.",
        });

        return list;
    }

    /// <summary>
    /// "1 thing" / "2 things". Shared across every PDK message: "1 part(s)" is a template showing
    /// through, and a user reading it has to decode a count that could simply have been written.
    /// </summary>
    internal static string Plural(int n, string singular, string plural) =>
        $"{n} {(n == 1 ? singular : plural)}";

    /// <summary>
    /// What a kit's settings were found to declare, in the user's terms.
    ///
    /// <para><b>The platform entries are reported as platforms, and by whether one is THIS machine.</b>
    /// A count on its own ("3 ways to evaluate its devices") reads as three alternative methods, when
    /// it is really one method described for three operating systems — and it buries the only thing
    /// the user actually needs, which is whether their own machine is among them. A kit built for
    /// other platforms is a completely ordinary thing to be holding and a completely useless one to
    /// press Run on, so it says so here rather than at Run.</para>
    /// </summary>
    private static string DescribeSimulationSettings(DeviceWorkerManifest? manifest)
    {
        if (manifest is null)
            return "This kit names no program to evaluate its devices. Its parts are still built " +
                   "from the kit's own netlists; only devices needing an external model will say so " +
                   "at Run. That one setting goes in this workspace's own folder for the kit — the " +
                   "kit itself is left exactly as it was shipped.";

        var found = new List<string>();

        if (manifest.Launches.Count > 0)
        {
            var platforms = manifest.Launches
                .Select(l => string.IsNullOrWhiteSpace(l.Platform) ? "any platform" : l.Platform)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            found.Add(manifest.LaunchForThisMachine() is not null
                ? $"can be simulated on this machine ({string.Join(", ", platforms)})"
                : $"can be simulated on {string.Join(", ", platforms)} — NOT on this machine " +
                  $"({DeviceWorkerManifest.CurrentRuntimeIdentifier()})");
        }

        if (manifest.Variants.Count > 0)
            found.Add(Plural(manifest.Variants.Count, "model-selection parameter", "model-selection parameters"));
        if (manifest.Parts.Count > 0)
            found.Add(Plural(manifest.Parts.Count, "part defined by a circuit", "parts defined by a circuit"));
        if (manifest.FileParameters.Count > 0)
            found.Add(Plural(manifest.FileParameters.Count, "file parameter", "file parameters"));

        return found.Count == 0
            ? "This kit's simulation settings declare nothing usable."
            : "Read this kit's simulation settings: " + string.Join("; ", found) + ".";
    }

    /// <summary>The name circuitRF gives a formulation choice it found itself. A kit's own spelling
    /// wins when it states one; circuitRF must neither invent one nor hardcode any kit's.</summary>
    private const string DiscoveredVariantParameter = "Variant";

    /// <summary>
    /// circuitRF's own name for "evaluate this one with a different model library". Blank — the
    /// normal state — means the kit's own. Set, it overrides only this instance, which is what makes
    /// two revisions of a library comparable side by side in one schematic.
    /// </summary>
    public const string ModelLibraryParameter = "ModelLibrary";

    /// <summary>What reading a kit's own netlists turned up, per part.</summary>
    private sealed class KitDiscovery
    {
        private readonly Dictionary<string, (DeviceWorkerVariant Variant, string File, string Pattern)> _byPart =
            new(StringComparer.OrdinalIgnoreCase);

        public void Add(string partId, DeviceWorkerVariant variant, string file, string pattern)
            => _byPart[partId] = (variant, file, pattern);

        public IReadOnlyList<DeviceWorkerVariant> VariantsFor(string partId)
            => _byPart.TryGetValue(partId, out var hit) ? [hit.Variant] : [];

        public (string AbsoluteNetlistPath, string CellName)? NetlistFor(string partId)
            => _byPart.TryGetValue(partId, out var hit) ? (hit.File, hit.Pattern) : null;

        /// <summary>
        /// The compiled models the kit's netlists name but do not define. Kept here because reading
        /// every netlist is what establishes them, and doing it a second time to answer the same
        /// question would be a second chance to answer it differently.
        /// </summary>
        public IReadOnlyList<string> NativeDeviceTypes { get; set; } = [];
    }

    /// <summary>
    /// Reads every netlist the kit ships and works out, for each part, which formulations it offers
    /// and which of them circuitRF can build.
    ///
    /// <para>Nothing is declared anywhere for this to work — which is the point. A kit is read-only
    /// and self-contained, so importing one has to produce a working part with no file placed
    /// afterwards by anybody.</para>
    /// </summary>
    private static KitDiscovery DiscoverFromKitNetlists(PdkImportReport report, List<string> diags)
    {
        var discovery  = new KitDiscovery();
        var library    = new Library("kit");
        var incomplete = new HashSet<string>(StringComparer.Ordinal);
        var sourceFile = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var asset in report.Assets.Where(a => a.Kind == PdkAssetKind.Netlist))
        {
            string abs = Resolve(report, asset.RelativePath);
            if (!File.Exists(abs)) continue;

            KitNetlistResult read;
            try { read = KitNetlistReader.ReadFile(abs); }
            catch (Exception ex) when (ex is KitNetlistException or IOException or UnauthorizedAccessException)
            {
                diags.Add($"'{asset.RelativePath}' could not be read: {ex.Message}");
                continue;
            }

            foreach (var cell in read.Library.Cells)
            {
                if (library.Find(cell.Name) is not null) continue;
                library.Cells.Add(cell);
                sourceFile[cell.Name] = abs;
            }
            foreach (var name in read.IncompleteCells) incomplete.Add(name);
        }

        if (library.Cells.Count == 0) return discovery;

        discovery.NativeDeviceTypes = DeviceLibraryDiscovery.NativeDeviceTypes(library);

        var families = KitVariantDiscovery.Find(library, incomplete);

        foreach (var part in report.Parts)
        {
            var family = KitVariantDiscovery.ForPart(part.Id, families);
            if (family is null) continue;

            // A part with nothing buildable offers no choice at all: a picker that cannot produce an
            // answer is worse than no picker.
            if (family.Buildable.Count == 0)
            {
                diags.Add($"'{part.Id}': the kit offers {string.Join(", ", family.Choices)}, none of " +
                          $"which circuitRF can build yet.");
                continue;
            }

            if (!sourceFile.TryGetValue(family.CellNameFor(family.Buildable[0]), out string? file)) continue;

            // Use the KIT'S own name for the choice when its symbol definition states one. A name
            // circuitRF invents appears nowhere in the kit's documentation, so a user cannot search
            // for it — a worse experience than it looks.
            var (named, description) = NameFromSymbolDefinition(part.Id, report);
            string parameter = named ?? DiscoveredVariantParameter;

            discovery.Add(part.Id,
                new DeviceWorkerVariant(parameter, family.Choices,
                                        family.Buildable[0], family.Unsupported, [part.Id], description),
                file,
                // The pattern names the SAME parameter the cell declares — they are substituted for
                // each other, so a mismatch resolves to a subcircuit that is not there.
                family.CellNameFor($"{{{parameter}}}"));
        }

        return discovery;
    }

    /// <summary>
    /// The kit's own name and description for a part's formulation choice, or null.
    ///
    /// <para>A part's symbol definition is matched by the part it NAMES, not by filename — a kit's
    /// files are not named after the parts they define, and matching on a filename convention would
    /// be reading a habit rather than a fact. Among the parameters it declares, the string-valued one
    /// is the choice: a formulation is picked by name, and every other parameter is a number.</para>
    ///
    /// <para>More than one string-valued parameter means the definition does not identify which is
    /// the choice, so nothing is claimed and circuitRF's own name stands.</para>
    /// </summary>
    private static (string? Name, string Description) NameFromSymbolDefinition(
        string partId, PdkImportReport report)
    {
        foreach (var asset in report.Assets)
        {
            if (asset.Kind is PdkAssetKind.Netlist or PdkAssetKind.SymbolArtwork
                           or PdkAssetKind.LayoutArtwork or PdkAssetKind.PaletteIcon) continue;

            var definition = KitSymbolDefinitionReader.TryReadFile(Resolve(report, asset.RelativePath));
            if (definition is null || definition.Parameters.Count == 0) continue;
            if (!definition.ReferencedNames.Contains(partId, StringComparer.Ordinal)) continue;

            var text = definition.Parameters.Where(p => p.IsText).ToList();
            if (text.Count != 1) continue;

            return (text[0].Name, text[0].Description);
        }

        return (null, "");
    }

    private static DeviceWorkerManifest? TryReadManifestIn(string directory)
    {
        try
        {
            string source = Path.Combine(directory, DeviceWorkerManifest.FileName);
            return File.Exists(source) ? DeviceWorkerManifest.TryRead(source, out _) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The circuit definition declared for this part, with its netlist made absolute against the
    /// kit. Null when the kit declares none — the ordinary single-device part — and also when it
    /// names a file that is not there: the part still installs and still places, so the import is
    /// not lost over it.
    ///
    /// <para>A named-but-absent file IS reported, though. The kit meant this part to be a circuit,
    /// and it silently becoming something else would surface much later as a device the provider does
    /// not serve — naming the type, not the file that was missing.</para>
    /// </summary>
    private static (string AbsoluteNetlistPath, string CellName)? NetlistPartFor(
        string partId, DeviceWorkerManifest? manifest, List<string> diags)
    {
        var match = manifest?.Parts.FirstOrDefault(d => d.Id.Equals(partId, StringComparison.OrdinalIgnoreCase));
        if (match is null) return null;

        string abs;
        try { abs = manifest!.ResolveFile(match.NetlistFile); }
        catch (ArgumentException) { abs = match.NetlistFile; }

        if (Path.IsPathRooted(abs) && File.Exists(abs)) return (abs, match.CellName);

        diags.Add($"'{partId}' is declared as a circuit defined in '{match.NetlistFile}', which is " +
                  $"not there. The part still places; it will not simulate as the circuit the kit " +
                  $"describes until that file can be found.");
        return null;
    }

    /// <summary>
    /// The kit's infrastructure parameters — declared as text rather than a number. Kept off the
    /// editable interface (a user pointing one instance at a different data folder is a mistake, not
    /// a design choice) but still emitted, so the provider receives what the kit specified.
    /// </summary>
    /// <summary>
    /// A value the kit wrote in ITS OWN notation, as an expression circuitRF can evaluate.
    ///
    /// <para><b>A kit spells a number the way its own simulator reads one</b> — <c>0.72u</c>,
    /// <c>600n</c>, <c>1.5p</c> — and circuitRF's expression engine reads no engineering suffixes,
    /// because a value's unit is a FIELD on the row rather than a letter on the number (measured:
    /// <c>0.72u</c> is <i>Parse error at position 2</i>). This kit is not even consistent with itself:
    /// its own symbol templates write <c>7.0e-6</c> for one part and <c>0.72u</c> for the next, so
    /// there is nothing to detect and no assumption to make — every default is simply read in the
    /// dialect it was written in.</para>
    ///
    /// <para><b>Resolved with <see cref="SpiceNumber"/>, which already exists for exactly this and
    /// already knows the trap.</b> That dialect is case-insensitive and spells milli <c>M</c> with
    /// mega as <c>MEG</c>, while circuitRF's own unit table is SI and case-sensitive — so reading a
    /// kit's suffix through the SI table turns one millifarad into one megafarad, a factor of 10⁹ in
    /// a value that still parses and still converges. Sending it through the reader's own table is
    /// what keeps the two scales from ever meeting.</para>
    ///
    /// <para><b>Anything circuitRF can already read is left EXACTLY as the kit wrote it.</b> Rewriting
    /// a value that was already fine would replace the kit's own spelling with a formatting of it, for
    /// no gain — and a word-valued default (a model name, a display mode) is not a number at all and
    /// passes straight through.</para>
    ///
    /// <para>Doing this here rather than teaching the expression engine suffixes is deliberate: the
    /// engine's <c>M</c> is mega and this dialect's is milli, and a language where the same letter
    /// means two things depending on which dialog it was typed into is a worse problem than the one
    /// being solved.</para>
    /// </summary>
    internal static string InCircuitRfsOwnNotation(string? raw)
    {
        string text = (raw ?? "").Trim();
        if (text.Length == 0) return "";

        // Already an ordinary literal — the kit and circuitRF agree, so change nothing.
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return text;

        return SpiceNumber.TryParse(text, out double value)
            ? value.ToString("R", CultureInfo.InvariantCulture)
            : text;
    }

    /// <summary>
    /// The device model this part declares, or empty.
    ///
    /// <para><b>Read from the REPORT-side part, never from the built <c>.ccell</c>.</b> The cell
    /// deliberately drops a kit's infrastructure parameters from its published interface, and the
    /// model name is one of them — reading it there returns empty for every part, silently, and the
    /// only symptom is a layout that never matches a schematic part.</para>
    ///
    /// <para>This is the one identity a kit's schematic part and its layout cell reliably share: a
    /// kit names the two independently, but both have to say what device they are, because that is
    /// what a netlist has to carry.</para>
    /// </summary>
    private static string DeclaredModelName(PdkPart part)
    {
        var declared = part.Parameters?.FirstOrDefault(
            p => p.Name.Equals("model", StringComparison.OrdinalIgnoreCase));

        return declared?.DefaultExpression?.Trim() ?? "";
    }

    private static Dictionary<string, string>? BuildFixedParameters(PdkPart part)
    {
        if (part.Parameters is null) return null;

        var fixedParams = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in part.Parameters)
            if (p.IsText && !string.IsNullOrWhiteSpace(p.Name))
                fixedParams[p.Name] = p.DefaultExpression ?? "";

        return fixedParams.Count > 0 ? fixedParams : null;
    }

    /// <summary>
    /// Finds a file the report named, in whichever of the two folders it came from. An imported
    /// "additions" folder is read together with the kit it names, so an asset's own relative path
    /// belongs to one root or the other and only trying both can find it.
    /// </summary>
    private static string Resolve(PdkImportReport report, string relative)
    {
        string rel = relative.Replace('/', Path.DirectorySeparatorChar);

        string first = Path.Combine(report.RootPath, rel);
        if (report.KitRoot is not { Length: > 0 } kitRoot || File.Exists(first)) return first;

        string second = Path.Combine(kitRoot, rel);
        return File.Exists(second) ? second : first;
    }

    /// <summary>
    /// The kit's OWN subcircuit for a part, when the kit ships no manifest saying so — which is the
    /// ordinary case for an unmodified vendor kit. The importer already worked out which subcircuit
    /// defines the part; this only turns its kit-relative path into one that survives the cell being
    /// installed into the workspace.
    ///
    /// <para><b>This is what makes a corner change a number.</b> The subcircuit names a <c>.model</c>
    /// card, the card states its value in terms of the kit's process constants, and a corner is
    /// exactly a binding of those constants. Without it the constants reach the testbench and nothing
    /// reads them — the part having no circuit to resolve them against.</para>
    ///
    /// <para>Checked LAST, so anything the kit states explicitly still wins over what was inferred.</para>
    /// </summary>
    private static (string AbsoluteNetlistPath, string CellName)? DiscoveredDefinitionFor(
        PdkImportReport report, PdkPart part)
    {
        if (part.DefinitionRelativePath is not { Length: > 0 } rel ||
            part.DefinitionCell         is not { Length: > 0 } cell) return null;

        string abs = Resolve(report, rel);
        return File.Exists(abs) ? (abs, cell) : null;
    }

    private static IReadOnlyList<string> BuildSearchTerms(PdkPart part, string kit)
    {
        var terms = new List<string> { part.Id, kit };
        if (!string.IsNullOrWhiteSpace(part.DisplayName)) terms.Add(part.DisplayName);
        if (!string.IsNullOrWhiteSpace(part.Category))    terms.Add(part.Category);
        return terms.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
