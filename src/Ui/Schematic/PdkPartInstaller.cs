using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.External;
using CircuitRF.Core.Design;
using CircuitRF.Core.Netlist;
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
    /// <summary>Folder inside the workspace that holds cells generated from imported kits.</summary>
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
    public sealed record InstallOutcome(
        IReadOnlyList<PaletteItem> Items,
        int SymbolsInstalled,
        int IconsFound,
        IReadOnlyList<string> Diagnostics,
        int OmittedNotPlaceable = 0,
        IReadOnlyList<string>? Notes = null);

    /// <summary>
    /// Install every part the report lists. Returns one palette entry per part — including parts
    /// whose symbol could not be read, which still appear (with their icon, if any) so the user can
    /// see what the kit contains rather than silently losing it.
    /// </summary>
    /// <param name="report">The importer's own findings. Never modified.</param>
    /// <param name="workspaceRootDir">
    /// Workspace to install generated cells into. When null — no workspace is open — nothing is
    /// written and the parts are still listed, icons and all, just not placeable yet.
    /// </param>
    public static InstallOutcome Install(PdkImportReport report, string? workspaceRootDir)
    {
        var items  = new List<PaletteItem>();
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

        string? kitInstallDir = null;
        if (haveRoot && workspaceRootDir is not null)
            kitInstallDir = Path.Combine(workspaceRootDir, InstallFolderName, SanitizeFolderName(kit));

        // Read BEFORE any part is installed: a variant becomes part of each cell's own declared
        // parameter interface, so it has to be in hand while the cells are being written.
        // Read the kit's OWN netlists first: the formulations a part offers, which of them circuitRF
        // can build, and the circuit each one is, are all in there — so a kit that declares nothing
        // still yields a working part. A manifest can still name things, and wins where it does.
        var discovered = haveRoot ? DiscoverFromKitNetlists(report, diags) : new KitDiscovery();

        var kitManifest = haveRoot ? TryReadManifestIn(report.RootPath) : null;
        var fileParams  = kitManifest?.FileParameters ?? [];

        // Copied BEFORE the parts, so each cell can record its circuit definition at the workspace's
        // own copy. The workspace is then self-contained: the folder that was imported can be moved
        // or deleted and the parts still build.
        if (kitInstallDir is not null)
        {
            CopyProviderManifest(report, kitInstallDir, kit, diags);

            // A kit that ships no description of how to simulate its devices is the ORDINARY case —
            // a vendor kit is written for its own simulator and knows nothing about circuitRF. So
            // derive one rather than requiring somebody to hand-write it, which is the difference
            // between "import the kit" and "import the kit, then go and configure it".
            if (TryReadManifestIn(kitInstallDir) is null)
                SynthesiseProviderManifest(report, kitInstallDir, kit, discovered, diags, notes);

            kitManifest = TryReadManifestIn(kitInstallDir) ?? kitManifest;
        }

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

            string? cellDir = null;
            if (kitInstallDir is not null && part.SymbolArtwork is { } art)
            {
                // A manifest naming this part wins; otherwise what the kit's own netlist showed.
                var declared  = (kitManifest?.Variants ?? []).Where(v => v.AppliesTo(part.Id)).ToList();
                var variants  = declared.Count > 0 ? declared : discovered.VariantsFor(part.Id);
                var netlist   = NetlistPartFor(part.Id, kitManifest) ?? discovered.NetlistFor(part.Id);

                cellDir = TryInstallSymbol(kitInstallDir, kit, part,
                                           Resolve(report, art.RelativePath), diags, iconPath,
                                           variants, fileParams, netlist);
                if (cellDir is not null) syms++;
            }

            // Only placeable parts reach the palette. A part with no readable symbol is a kit's
            // internal building block, not something to browse for and click — and a tile that
            // cannot place anything is worse than no tile. The report still lists every part.
            if (cellDir is null) { omitted++; continue; }

            items.Add(new PaletteItem(
                Kind:            SymbolKind.Generic,
                PortCount:       0,
                DisplayName:     string.IsNullOrWhiteSpace(part.DisplayName) ? part.Id : part.DisplayName,
                Category:        ComponentCategory.Other,
                SearchTerms:     BuildSearchTerms(part, kit),
                IsCommon:        false,
                ExtraCategories: null,
                Pdk:             new PdkPartRef(kit, part.Id, iconPath, cellDir)));
        }

        return new InstallOutcome(items, syms, icons, diags, omitted, notes);
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
    private static void SynthesiseProviderManifest(
        PdkImportReport report, string kitInstallDir, string kitName,
        KitDiscovery discovery, List<string> diags, List<string> notes, int ancestorLevels = 2)
    {
        var types = discovery.NativeDeviceTypes;
        if (types.Count == 0) return;   // nothing compiled to serve — a purely schematic kit

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
            DeviceLibraryDiscovery.LibraryFormat.Elf);
        var windows = DeviceLibraryDiscovery.Find(
            types, report.RootPath, ["win32_64", "win64", "win32", ".dll"], ancestorLevels, null,
            DeviceLibraryDiscovery.LibraryFormat.Pe);

        var match = linux ?? windows;
        if (match is null)
        {
            diags.Add($"This kit's devices ({string.Join(", ", types.Take(3))}" +
                      $"{(types.Count > 3 ? ", …" : "")}) are compiled models, and the library that " +
                      $"implements them was not found near '{report.RootPath}'. It usually ships as " +
                      $"a separate package beside the kit — import the folder containing both, or " +
                      $"name the library in {DeviceWorkerManifest.FileName} in this kit's workspace folder.");
            return;
        }

        var profile = DeviceLibraryDiscovery.Profiles[0];

        // Where the worker actually is. circuitRF's tools directory is where it belongs, but a worker
        // sitting beside the kit is found too — otherwise a user holding one is blocked until a
        // release ships it.
        string? worker = DeviceLibraryDiscovery.FindWorker(profile, report.RootPath, ancestorLevels);
        if (worker is null)
        {
            diags.Add($"The program that evaluates this kit's devices ('{profile.Worker}') was not " +
                      $"found in circuitRF's tools folder or near the kit. The kit's parts still " +
                      $"build; simulating its devices needs that program.");
            return;
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
        string? aliasMap = FindAliasMap(kitInstallDir, workerDir);

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

        if (workers.Count == 0) return;

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

        try
        {
            Directory.CreateDirectory(kitInstallDir);
            File.WriteAllText(Path.Combine(kitInstallDir, DeviceWorkerManifest.FileName),
                              manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            notes.Add($"Devices in this kit will be evaluated using " +
                      $"'{Path.GetFileName(match.Path)}' ({string.Join(", ", match.Types)}), found at " +
                      $"{Path.GetDirectoryName(match.Path)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diags.Add($"Could not record how to simulate this kit's devices: {ex.Message}");
        }

        static JsonNode Launch(string platform, string command, string[] arguments) => new JsonObject
        {
            ["platform"]  = platform,
            ["command"]   = command,
            ["arguments"] = new JsonArray(arguments.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
        };
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

    /// <summary>
    /// True when circuitRF wrote this manifest AND nothing it names can be run any more. Only our own
    /// derivation is redone; a kit's file, or one a user edited, is left exactly as it is even when it
    /// is broken — that is theirs to fix, and silently replacing it would lose their work.
    /// </summary>
    private static bool IsOwnStaleManifest(string kitDir, DeviceWorkerManifest manifest)
    {
        try
        {
            string path = Path.Combine(kitDir, DeviceWorkerManifest.FileName);
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node?["generatedBy"]?.GetValue<string>() != GeneratedMarker) return false;

            // Written by an older build of this code, so redo it whether or not what it names still
            // exists. A manifest can be entirely runnable and still be missing something added
            // since — and one that runs is exactly the one nothing else would ever replace.
            int format = node["generatedFormat"]?.GetValue<int>() ?? 1;
            if (format < GeneratedFormat) return true;
        }
        catch { return false; }

        // Runnable if ANY entry's program is still there. A per-platform check would call a manifest
        // stale on a machine it was never meant to run on.
        foreach (var launch in manifest.Launches)
        {
            var (command, _) = manifest.Resolve(launch);

            // Still a bare name after resolution means it was found neither beside the kit nor in
            // circuitRF's tools folder — so naming it would fail at launch, not run.
            if (!Path.IsPathRooted(command)) continue;
            if (File.Exists(command)) return false;
        }

        return manifest.Launches.Count > 0;
    }

    /// <summary>
    /// Derives a missing manifest for a kit that is ALREADY installed, from what its cells recorded.
    ///
    /// <para>An installed cell keeps an absolute path back into the kit it came from — the netlist
    /// that defines it, or its icon — so the kit can be found again without the import report that
    /// created it. That is what makes healing at open-time possible rather than requiring a re-import,
    /// and it also recovers a kit whose library moved after it was installed.</para>
    ///
    /// <para>Returns true when a manifest was written.</para>
    /// </summary>
    private static bool TrySynthesiseForInstalledKit(string kitDir, IEnumerable<string> cellDirs)
    {
        string? kitRoot   = null;
        string? netlist   = null;
        string  kitName   = Path.GetFileName(kitDir);

        foreach (var cellDir in cellDirs)
        {
            CcellFile ccell;
            try
            {
                string p = Path.Combine(cellDir, CellFolder.CcellFileName);
                if (!File.Exists(p)) continue;
                ccell = CellPersistence.LoadFromFile(p);
            }
            catch { continue; }

            if (!string.IsNullOrWhiteSpace(ccell.ExternalProvider)) kitName = ccell.ExternalProvider!;

            // The netlist is what names the device types, so it is the one worth finding.
            if (netlist is null && ccell.ExternalNetlistPath is { Length: > 0 } n && File.Exists(n))
                netlist = n;

            kitRoot ??= NearestExistingDirectory(ccell.ExternalNetlistPath)
                     ?? NearestExistingDirectory(ccell.ExternalIconPath);
        }

        if (netlist is null || kitRoot is null) return false;

        // A KIT'S NETLISTS ARE ONE LIBRARY SPLIT ACROSS FILES — the file defining a part instantiates
        // cells declared beside it. Reading only the named one leaves those siblings looking like
        // compiled models, which is both wrong and the opposite of the answer wanted: the real
        // device type would not appear at all.
        var library = new Library("kit");
        foreach (string file in NetlistFilesBeside(netlist))
        {
            try
            {
                foreach (var cell in KitNetlistReader.ReadFile(file).Library.Cells)
                    if (library.Find(cell.Name) is null) library.Cells.Add(cell);
            }
            catch { /* a sibling that will not read must not stop the named one */ }
        }
        if (library.Cells.Count == 0) return false;

        var types = DeviceLibraryDiscovery.NativeDeviceTypes(library);
        if (types.Count == 0) return false;

        var report = new PdkImportReport { RootPath = kitRoot, KitName = kitName };
        var diags  = new List<string>();
        var found  = new KitDiscovery { NativeDeviceTypes = types };

        // A deeper walk than the import path needs. What an installed cell points at is a file well
        // INSIDE the kit — a netlist, an icon — whereas the importer was handed the kit's own root,
        // so the same delivery sits several more levels up from here. It costs nothing when the
        // library is found sooner: the search widens only after a level finds nothing.
        SynthesiseProviderManifest(report, kitDir, kitName, found, diags, notes: [], ancestorLevels: 6);
        return File.Exists(Path.Combine(kitDir, DeviceWorkerManifest.FileName));
    }

    /// <summary>The named netlist and every netlist beside it — a kit splits one library across files.</summary>
    private static IReadOnlyList<string> NetlistFilesBeside(string named)
    {
        var files = new List<string> { named };
        try
        {
            string? dir = Path.GetDirectoryName(named);
            if (dir is not null)
                foreach (var sibling in Directory.EnumerateFiles(dir).OrderBy(f => f, StringComparer.Ordinal))
                    if (!sibling.Equals(named, StringComparison.OrdinalIgnoreCase) &&
                        Path.GetExtension(sibling) is ".net" or ".inc" or ".ckt")
                        files.Add(sibling);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        return files;
    }

    /// <summary>
    /// The kit folder an absolute path recorded at install time points into — walking up until
    /// something still exists, because a kit may have been moved or partly removed since.
    /// </summary>
    private static string? NearestExistingDirectory(string? recordedPath)
    {
        if (string.IsNullOrWhiteSpace(recordedPath)) return null;

        try
        {
            var dir = new DirectoryInfo(Path.GetDirectoryName(Path.GetFullPath(recordedPath)) ?? "");
            for (int i = 0; dir is not null && i < 8; i++)
            {
                if (dir.Exists) return dir.FullName;
                dir = dir.Parent;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }

        return null;
    }

    private static void CopyProviderManifest(
        PdkImportReport report, string kitInstallDir, string kitName, List<string> diags)
    {
        string kitRootPath = report.RootPath;
        string source      = Path.Combine(kitRootPath, DeviceWorkerManifest.FileName);

        try
        {
            if (!File.Exists(source)) return;

            var manifest = DeviceWorkerManifest.TryRead(source, out string? problem);
            if (manifest is null)
            {
                diags.Add($"This kit describes how to simulate its devices, but that description " +
                          $"could not be used: {problem}");
                return;
            }

            Directory.CreateDirectory(kitInstallDir);

            var copy = new JsonObject
            {
                ["provider"]      = kitName,
                ["baseDirectory"] = string.IsNullOrEmpty(manifest.BaseDirectory)
                                        ? Path.GetFullPath(kitRootPath)
                                        : manifest.BaseDirectory,
                ["workers"]       = new JsonArray(manifest.Launches.Select(l => (JsonNode)new JsonObject
                {
                    ["platform"]  = l.Platform,
                    ["command"]   = l.Command,
                    ["arguments"] = new JsonArray(l.Arguments.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray()),
                }).ToArray()),
            };

            // Netlists defining a part come WITH the manifest, so the workspace holds everything
            // needed to build that part. They are small, circuitRF-side and part of the record of
            // what was imported — unlike the worker and the model libraries, which are the kit's own,
            // large, and stay where the kit is: baseDirectory is what keeps reaching those.
            var partEntries = new List<JsonNode>();
            foreach (var part in manifest.Parts)
            {
                string resolved = manifest.ResolveFile(part.NetlistFile);
                string relative = part.NetlistFile.Replace('\\', '/');

                if (Path.IsPathRooted(resolved) && File.Exists(resolved))
                {
                    try
                    {
                        string dst = Path.Combine(kitInstallDir,
                                                  relative.Replace('/', Path.DirectorySeparatorChar));
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        File.Copy(resolved, dst, overwrite: true);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        diags.Add($"'{part.Id}': its circuit definition could not be copied into the " +
                                  $"workspace ({ex.Message}); it will be read from the kit instead.");
                    }
                }
                else
                {
                    diags.Add($"'{part.Id}': the kit names a circuit definition ('{part.NetlistFile}') " +
                              $"that is not there, so this part cannot be simulated.");
                }

                partEntries.Add(new JsonObject
                {
                    ["id"]      = part.Id,
                    ["netlist"] = relative,
                    ["cell"]    = part.CellName,
                });
            }

            if (partEntries.Count > 0)
                copy["parts"] = new JsonArray([.. partEntries]);

            if (manifest.Variants.Count > 0)
                copy["variants"] = new JsonArray(manifest.Variants.Select(v => (JsonNode)new JsonObject
                {
                    ["parameter"]   = v.Parameter,
                    ["choices"]     = new JsonArray(v.Choices.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
                    ["default"]     = v.Default,
                    ["unsupported"] = new JsonArray(v.Unsupported.Select(u => (JsonNode)JsonValue.Create(u)!).ToArray()),
                    ["parts"]       = new JsonArray(v.Parts.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
                }).ToArray());

            File.WriteAllText(
                Path.Combine(kitInstallDir, DeviceWorkerManifest.FileName),
                copy.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diags.Add($"This kit's simulation settings could not be saved into the workspace: {ex.Message}");
        }
    }

    /// <summary>
    /// Rebuilds palette entries from the kits already installed in a workspace.
    ///
    /// <para>Called when a workspace opens. Without it a kit vanishes from the palette on reopen
    /// even though its cells are still on disk and its placed components still resolve — the parts
    /// were only ever held in session memory. The installed cells ARE the record; nothing needs to
    /// be re-imported.</para>
    /// </summary>
    public static IReadOnlyList<PaletteItem> LoadInstalled(string? workspaceRootDir)
    {
        var items = new List<PaletteItem>();
        if (string.IsNullOrEmpty(workspaceRootDir)) return items;

        string root = Path.Combine(workspaceRootDir, InstallFolderName);
        if (!Directory.Exists(root)) return items;

        IEnumerable<string> kitDirs;
        try { kitDirs = Directory.EnumerateDirectories(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return items; }

        foreach (var kitDir in kitDirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            // The kit's declarations are read from the WORKSPACE's own copy, not from wherever the
            // kit was imported from. That is what lets a user add what a kit does not itself carry —
            // a manifest, a translated netlist — by dropping files into a folder circuitRF made,
            // without touching a kit that is very often read-only.
            var manifest = TryReadManifestIn(kitDir);

            IEnumerable<string> cellDirs;
            try { cellDirs = Directory.EnumerateDirectories(kitDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            // AT EVERY OPEN, not only at import. A kit installed before circuitRF could work this out
            // — or one whose library has since moved — would otherwise stay unsimulable until it was
            // imported again, and re-importing is the step this exists to remove. Same reason the
            // declarations above are reconciled here rather than trusted from install time.
            // Redo one WE wrote whose worker is no longer where it was — a kit moved, a tool
            // installed since, an earlier answer that is simply now wrong. A manifest a kit or a
            // user supplied is never touched: only our own working-out is ours to redo.
            if (manifest is not null && IsOwnStaleManifest(kitDir, manifest))
            {
                try { File.Delete(Path.Combine(kitDir, DeviceWorkerManifest.FileName)); manifest = null; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }

            if (manifest is null && TrySynthesiseForInstalledKit(kitDir, cellDirs))
                manifest = TryReadManifestIn(kitDir);

            foreach (var cellDir in cellDirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                CcellFile ccell;
                try
                {
                    string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
                    if (!File.Exists(ccellPath)) continue;
                    ccell = CellPersistence.LoadFromFile(ccellPath);
                }
                catch { continue; }   // a cell we cannot read simply does not reappear

                if (string.IsNullOrWhiteSpace(ccell.ExternalProvider)) continue;

                ReconcileWithKitManifest(cellDir, ccell, manifest);

                string kit    = ccell.ExternalProvider!;
                string partId = ccell.ExternalType ?? Path.GetFileName(cellDir);

                items.Add(new PaletteItem(
                    Kind:            SymbolKind.Generic,
                    PortCount:       0,
                    DisplayName:     partId,
                    Category:        ComponentCategory.Other,
                    SearchTerms:     [partId, kit],
                    IsCommon:        false,
                    ExtraCategories: null,
                    Pdk:             new PdkPartRef(kit, partId, ccell.ExternalIconPath, cellDir)));
            }
        }

        return items;
    }

    // ── Symbol installation ───────────────────────────────────────────────────

    /// <summary>
    /// Reads one symbol description and writes it out as a cell. Returns the cell folder, or null
    /// when the file could not be read — in which case the reason is recorded, never swallowed.
    /// </summary>
    private static string? TryInstallSymbol(string kitInstallDir, string kitName, PdkPart part,
                                            string symbolAbsPath, List<string> diags, string? iconPath,
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

        try
        {
            Directory.CreateDirectory(kitInstallDir);

            string cellName = SanitizeFolderName(part.Id);
            string cellDir  = Path.Combine(kitInstallDir, cellName);

            if (!Directory.Exists(cellDir))
                cellDir = CellFolder.CreateCellFolder(kitInstallDir, cellName);

            string symDir = CellFolder.SubFolderPath(cellDir, ViewType.Symbol);
            Directory.CreateDirectory(symDir);

            string fileName = cellName + CellFolder.ViewExtension(ViewType.Symbol);
            SymbolPersistence.SaveToFile(Path.Combine(symDir, fileName), read.Symbol);

            // Name the symbol as the cell's primary, and record the pin count, so placement resolves
            // it the same way it resolves any hand-authored cell.
            string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
            var ccell = File.Exists(ccellPath) ? CellPersistence.LoadFromFile(ccellPath) : new CcellFile();
            ccell.PrimarySymbol = fileName;
            ccell.NumPorts      = read.Symbol.Pins.Count;

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

            CellPersistence.SaveToFile(ccellPath, ccell);

            return cellDir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diags.Add($"'{part.DisplayName}': its symbol could not be written — {ex.Message}");
            return null;
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
                DefaultExpression = p.DefaultExpression ?? "",
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
    /// The model-selection choices a kit declares, or empty. Read from the same manifest that says
    /// how to start the worker, because that is already the one file a kit uses to state what
    /// circuitRF cannot work out for itself.
    /// </summary>
    /// <summary>
    /// Brings an installed cell up to date with the kit declarations sitting beside it, writing the
    /// <c>.ccell</c> back only when something genuinely changed.
    ///
    /// <para><b>Why this runs at every workspace open rather than only at import.</b> The point of
    /// the workspace's own kit folder is that a user can put there what the kit itself does not
    /// carry — a manifest naming a model-selection parameter, a translated netlist defining a
    /// packaged part. Reading those only at import would mean re-importing a 17 MB kit to pick up a
    /// file dropped beside it, and re-importing is exactly the step this is meant to avoid.</para>
    ///
    /// <para>It also self-heals a moved kit: the netlist path is absolute, and it is re-resolved here
    /// against the workspace copy first and the kit second.</para>
    ///
    /// <para><b>A user's own edit to a declared value is kept.</b> Only the closed set of choices and
    /// where the definition lives are refreshed — a parameter the cell already declares keeps its
    /// current default, because that default may have been deliberately changed.</para>
    /// </summary>
    private static void ReconcileWithKitManifest(string cellDir, CcellFile ccell, DeviceWorkerManifest? manifest)
    {
        if (manifest is null) return;

        bool changed = false;

        string thisPart = ccell.ExternalType ?? Path.GetFileName(cellDir);

        foreach (var v in manifest.Variants)
        {
            if (!v.AppliesTo(thisPart)) continue;

            var declared = ccell.Parameters.FirstOrDefault(p => p.Name.Equals(v.Parameter, StringComparison.Ordinal));
            if (declared is null)
            {
                ccell.Parameters.Insert(0, new CcellParameter
                {
                    Name               = v.Parameter,
                    DefaultExpression  = v.Default,
                    Unit               = "",
                    ShowOnSchematic    = false,
                    Choices            = [.. v.Choices],
                    UnsupportedChoices = v.Unsupported.Count > 0 ? [.. v.Unsupported] : null,
                });
                changed = true;
                continue;
            }

            var unsupported = v.Unsupported.Count > 0 ? v.Unsupported.ToList() : null;
            if (declared.Choices is null || !declared.Choices.SequenceEqual(v.Choices, StringComparer.Ordinal))
            {
                declared.Choices = [.. v.Choices];
                changed = true;
            }
            if (!SameList(declared.UnsupportedChoices, unsupported))
            {
                declared.UnsupportedChoices = unsupported;
                changed = true;
            }
        }

        foreach (var name in manifest.FileParameters)
        {
            var declared = ccell.Parameters.FirstOrDefault(p => p.Name.Equals(name, StringComparison.Ordinal));
            if (declared is null || declared.IsFilePath == true) continue;
            declared.IsFilePath = true;
            changed = true;
        }

        foreach (var name in manifest.FileParameters)
        {
            var declared = ccell.Parameters.FirstOrDefault(p => p.Name.Equals(name, StringComparison.Ordinal));
            if (declared is null || declared.IsFilePath == true) continue;
            declared.IsFilePath = true;
            changed = true;
        }

        var netlist = NetlistPartFor(thisPart, manifest);
        if (netlist is { } n)
        {
            if (ccell.ExternalNetlistPath != n.AbsoluteNetlistPath) { ccell.ExternalNetlistPath = n.AbsoluteNetlistPath; changed = true; }
            if (ccell.ExternalNetlistCell != n.CellName)            { ccell.ExternalNetlistCell = n.CellName;            changed = true; }
        }

        if (!changed) return;

        try { CellPersistence.SaveToFile(Path.Combine(cellDir, CellFolder.CcellFileName), ccell); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cell still works for this session from the in-memory reconciliation above; only
            // the record of it is missing, and the next open will try again.
        }
    }

    private static bool SameList(List<string>? a, List<string>? b)
        => (a is null && b is null) || (a is not null && b is not null && a.SequenceEqual(b, StringComparer.Ordinal));

    /// <summary>What a kit's manifest was found to declare, in the user's terms.</summary>
    private static string DescribeSimulationSettings(DeviceWorkerManifest? manifest)
    {
        if (manifest is null)
            return "This kit names no program to evaluate its devices. Its parts are still built " +
                   "from the kit's own netlists; only devices needing an external model will say so " +
                   "at Run. That one setting goes in this workspace's own folder for the kit — the " +
                   "kit itself is left exactly as it was shipped.";

        var found = new List<string>();
        if (manifest.Launches.Count > 0) found.Add($"{manifest.Launches.Count} way(s) to evaluate its devices");
        if (manifest.Variants.Count > 0) found.Add($"{manifest.Variants.Count} model-selection parameter(s)");
        if (manifest.Parts.Count    > 0) found.Add($"{manifest.Parts.Count} part(s) defined by a circuit");
        if (manifest.FileParameters.Count > 0) found.Add($"{manifest.FileParameters.Count} file parameter(s)");

        return found.Count == 0
            ? "This kit's simulation settings declare nothing usable."
            : "Read this kit's simulation settings: " + string.Join(", ", found) + ".";
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
    /// names a file that is not there, because a definition that cannot be read is worse than none:
    /// the part still installs and still places, and the missing file is reported at Run against the
    /// instance that needs it rather than against a kit the user has finished importing.
    /// </summary>
    private static (string AbsoluteNetlistPath, string CellName)? NetlistPartFor(
        string partId, DeviceWorkerManifest? manifest)
    {
        var match = manifest?.Parts.FirstOrDefault(d => d.Id.Equals(partId, StringComparison.OrdinalIgnoreCase));
        if (match is null) return null;

        try
        {
            string abs = manifest!.ResolveFile(match.NetlistFile);
            return Path.IsPathRooted(abs) && File.Exists(abs) ? (abs, match.CellName) : null;
        }
        catch (ArgumentException) { return null; }
    }

    /// <summary>
    /// The kit's infrastructure parameters — declared as text rather than a number. Kept off the
    /// editable interface (a user pointing one instance at a different data folder is a mistake, not
    /// a design choice) but still emitted, so the provider receives what the kit specified.
    /// </summary>
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

    private static IReadOnlyList<string> BuildSearchTerms(PdkPart part, string kit)
    {
        var terms = new List<string> { part.Id, kit };
        if (!string.IsNullOrWhiteSpace(part.DisplayName)) terms.Add(part.DisplayName);
        if (!string.IsNullOrWhiteSpace(part.Category))    terms.Add(part.Category);
        return terms.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Makes a kit or part name safe to use as a folder name on every platform. Path separators are
    /// stripped on ALL platforms regardless of what the local runtime reports as invalid, so a name
    /// that is harmless here cannot become a path traversal somewhere else.
    /// </summary>
    internal static string SanitizeFolderName(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        var invalid = Path.GetInvalidFileNameChars();

        foreach (char c in name)
        {
            bool bad = c is '/' or '\\' or ':' || Array.IndexOf(invalid, c) >= 0 || char.IsControl(c);
            sb.Append(bad ? '_' : c);
        }

        string s = sb.ToString().Trim().Trim('.');
        return s.Length == 0 ? "part" : s;
    }
}
