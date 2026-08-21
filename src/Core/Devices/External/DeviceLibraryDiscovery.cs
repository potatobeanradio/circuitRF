using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;

namespace CircuitRF.Core.Devices.External;

/// <summary>One compiled model library, and which of the wanted device types it serves.</summary>
/// <param name="Path">Absolute path to the shared library.</param>
/// <param name="Types">The wanted types this library was found to serve, in the order asked for.</param>
public sealed record DeviceLibraryMatch(string Path, IReadOnlyList<string> Types);

/// <summary>
/// Finds the compiled model library that serves a kit's device types, in an UNMODIFIED vendor tree.
///
/// <para><b>Why this has to exist.</b> A vendor delivery is several read-only kits beside one shared
/// library package; a part kit names its device types but never says which library implements them —
/// the simulator resolves them by name across everything loaded. So the binding is not written down
/// anywhere, and the importer has to establish it, or every kit needs a hand-written manifest before
/// it can be simulated.</para>
///
/// <para><b>How.</b> A library that circuitRF's worker can drive advertises each device type it
/// serves as an exported entry point. That is a fact about OUR worker's ABI, not about any vendor —
/// which is what keeps this free of kit knowledge — and it makes the binding decidable by looking at
/// the library rather than by guessing from filenames.</para>
///
/// <para><b>The search is deliberately a plain byte scan</b> for the exported name, not an ELF/PE/
/// Mach-O parse. An exported symbol's name is present verbatim in every one of those formats, so one
/// format-agnostic scan handles the Linux, Windows and macOS builds a vendor ships side by side —
/// and a name this specific cannot collide by accident.</para>
/// </summary>
public static class DeviceLibraryDiscovery
{
    /// <summary>
    /// circuitRF's own map of internal nodes a compiled model does not drive, shipped beside the
    /// worker. Named here rather than in the installer so the file, the build step that publishes it
    /// and the manifest that points at it all agree on one spelling.
    /// </summary>
    public const string AliasMapFileName = "alias-map.json";

    /// <summary>
    /// A container format, as the file's own magic bytes report it. Used to say which platform's
    /// build a search is for — the one property a vendor's folder naming cannot get wrong.
    /// </summary>
    public enum LibraryFormat
    {
        /// <summary>Take whatever is found, whichever platform it is for.</summary>
        Any,
        /// <summary>Linux (and other System V) shared objects.</summary>
        Elf,
        /// <summary>Windows DLLs.</summary>
        Pe,
        /// <summary>macOS dylibs. No vendor has shipped one yet — see <c>src/Core/CLAUDE.md</c>.</summary>
        MachO,
    }

    /// <summary>
    /// How a worker circuitRF ships advertises the device types a library serves.
    ///
    /// <para>This describes OUR OWN worker's plugin ABI, which is why naming it here carries no
    /// knowledge of any kit: a library is recognised by the entry points our worker will call, not
    /// by anything a vendor wrote.</para>
    /// </summary>
    /// <param name="Abi">Short name, for reporting.</param>
    /// <param name="Worker">The helper circuitRF ships, named so <c>ToolsDirectory</c> resolves it.</param>
    /// <param name="ExportPrefix">Prepended to a device type to give the exported entry-point name.</param>
    /// <param name="HostCallbacks">
    /// The services a model resolves against its host — the other half of the same ABI. A Linux
    /// build leaves these UNDEFINED for whoever loaded it to supply; a Windows build IMPORTS them
    /// from a named module, which is why this list is what identifies that module (see
    /// <see cref="PeImports.ModuleSupplying"/>). The mangled <c>DeviceInstaller</c> constructor is
    /// deliberately absent: its spelling differs per platform, and the unmangled fourteen identify
    /// the descriptor unambiguously on their own.
    /// </param>
    public sealed record WorkerProfile(
        string                Abi,
        string                Worker,
        string                ExportPrefix,
        IReadOnlyList<string> HostCallbacks);

    /// <summary>Every worker circuitRF ships, in preference order.</summary>
    public static IReadOnlyList<WorkerProfile> Profiles { get; } =
    [
        new("senior", "senior_worker", "boot_senior_",
        [
            "add_lin_n", "add_lin_y", "add_nl_gc", "add_nl_iq", "add_tr_capacitor", "add_tr_gc",
            "add_tr_iq", "add_tr_lossy_inductor", "add_tr_mutual_inductor", "add_tr_resistor",
            "get_delay_v", "load_elements", "send_error_to_scn", "send_info_to_scn",
        ]),
    ];

    private static readonly string[] LibraryExtensions = [".so", ".dll", ".dylib"];

    /// <summary>Bounds a search over a vendor delivery, which can be very large.</summary>
    private const int MaxCandidates = 4000;

    /// <summary>A library big enough to be one of these is worth scanning; anything vast is not.</summary>
    private const long MaxLibraryBytes = 256L * 1024 * 1024;

    /// <summary>
    /// The device types a kit's netlists name but do not define — its compiled models.
    ///
    /// <para>A kit's cell instantiates exactly three kinds of thing: circuitRF primitives, other
    /// cells the same kit defines, and its own compiled models. The first two are recognisable, so
    /// whatever is left is the third. Nothing here knows a type name; the classification is
    /// structural, which is what makes it a kit-agnostic rule.</para>
    /// </summary>
    public static IReadOnlyList<string> NativeDeviceTypes(Library library)
    {
        ArgumentNullException.ThrowIfNull(library);

        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = new List<string>();

        foreach (var cell in library.Cells)
            foreach (var inst in cell.Instances)
            {
                if (ComponentModelFactory.IsPrimitive(inst.Reference)) continue;
                if (library.Find(inst.Reference) is not null)          continue;
                if (seen.Add(inst.Reference)) found.Add(inst.Reference);
            }

        return found;
    }

    /// <summary>
    /// Finds the library serving <paramref name="types"/>, searching <paramref name="searchRoot"/>
    /// and — because a vendor puts the shared library package BESIDE the kits rather than inside
    /// them — a bounded number of ancestor levels.
    ///
    /// <para>Returns the candidate serving the most of the wanted types. Ties are refused rather
    /// than broken: two libraries serving the same types means the delivery holds several builds,
    /// and picking one silently is how a design ends up evaluated by a model nobody chose.</para>
    /// </summary>
    /// <param name="preferPathContaining">
    /// Ranked hints, most preferred first — how the caller says which platform's build it wants
    /// (a vendor ships one per toolchain, in sibling folders). A candidate matching an earlier hint
    /// beats one matching a later hint; matching none is last.
    ///
    /// <para>These only RANK. They cannot decide which platform a file is for, because a vendor is
    /// free to name its folders anything — use <paramref name="format"/> for that.</para>
    /// </param>
    /// <param name="format">
    /// Which container format the caller can actually load. <b>This is a filter, not a hint, and it
    /// is the only thing that genuinely separates the per-platform builds.</b> Without it, a kit
    /// shipping a single library answers BOTH a "find the Linux build" and a "find the Windows
    /// build" search with the same file — so a Linux-only kit would be described as having a
    /// Windows build, and the entry naming it would fail at launch. Decided by the file's own magic
    /// bytes rather than its extension, since the extension is a convention and the magic is not.
    /// </param>
    /// <param name="extraRoots">
    /// Folders to search after the ancestor walk finds nothing — where the application has been TOLD
    /// a model library lives.
    ///
    /// <para><b>Why the walk alone is not enough.</b> A delivery is several part kits beside one
    /// shared library package, and the walk finds it because they are adjacent. Move one kit — into a
    /// workspace, say — and that adjacency is gone, with nothing on disk left to recover it from.
    /// Widening the walk is not the fix: the further out it goes, the less that territory has to do
    /// with this kit, and it would eventually match by accident. Being told is the fix.</para>
    ///
    /// <para>Searched LAST, so a library sitting with the kit still wins.</para>
    /// </param>
    public static DeviceLibraryMatch? Find(
        IEnumerable<string>  types,
        string               searchRoot,
        IReadOnlyList<string>? preferPathContaining = null,
        int                  ancestorLevels = 2,
        Action<string>?      report = null,
        LibraryFormat        format = LibraryFormat.Any,
        IEnumerable<string>? extraRoots = null)
    {
        ArgumentNullException.ThrowIfNull(types);

        var wanted = types.Where(t => !string.IsNullOrWhiteSpace(t)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wanted.Count == 0) return null;

        // WIDEN ONLY WHEN THE NARROWER SEARCH FOUND NOTHING. Looking in every level at once means a
        // library sitting next to the kit competes with anything else that happens to be further out
        // — and the further out the walk goes, the less that territory has to do with this kit. So
        // the imported folder is searched first and answers on its own if it can.
        var best = new List<(DeviceLibraryMatch Match, int Rank)>();
        int examined = 0;

        foreach (string root in AllRoots(searchRoot, ancestorLevels, extraRoots))
        {
            foreach (string file in Candidates(root))
            {
                if (++examined > MaxCandidates)
                {
                    report?.Invoke($"Stopped after examining {MaxCandidates} libraries; if the model " +
                                   "library was not found, import the folder that contains it.");
                    break;
                }

                if (!MatchesFormat(file, format)) continue;

                var served = ServedTypes(file, wanted);
                if (served.Count == 0) continue;

                best.Add((new DeviceLibraryMatch(file, served), Rank(file, preferPathContaining)));
            }

            if (best.Count > 0 || examined > MaxCandidates) break;
        }

        if (best.Count == 0) return null;

        int mostTypes = best.Max(b => b.Match.Types.Count);
        var shortlist = best.Where(b => b.Match.Types.Count == mostTypes).ToList();

        int bestRank = shortlist.Min(b => b.Rank);
        shortlist    = shortlist.Where(b => b.Rank == bestRank).ToList();

        // SAME FILE NAME MEANS THE SAME LIBRARY, BUILT SEVERAL TIMES. A vendor ships one build per
        // toolchain in sibling folders, so a dozen hits is the ordinary case and refusing them would
        // reject every real delivery. Genuinely DIFFERENT libraries — different names — is the case
        // worth refusing, because then the choice changes which model evaluates the design.
        var names = shortlist.Select(s => Path.GetFileName(s.Match.Path))
                             .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (names.Count > 1)
        {
            report?.Invoke(
                "Several different libraries serve this kit's devices — " + string.Join(", ", names) +
                ". Name the one to use in the kit's device-provider.json.");
            return null;
        }

        // Among builds of one library, prefer the MOST SPECIFICALLY NAMED build directory, then the
        // greatest name. A vendor names a build folder for everything it targets, so the longest name
        // is the most qualified one, and ordinal-descending then picks the newest of equally
        // qualified siblings (…_2025_… over …_2023_…).
        //
        // Deliberately NOT modification time, which was tried and picks wrongly: extracting an
        // archive gives every copy whatever order the extractor happened to use, and that has nothing
        // to do with which build is newest.
        var chosen = shortlist
            .OrderByDescending(s => BuildDirectory(s.Match.Path).Length)
            .ThenByDescending(s => BuildDirectory(s.Match.Path), StringComparer.Ordinal)
            .First();

        if (shortlist.Count > 1)
            report?.Invoke($"'{names[0]}' ships {shortlist.Count} builds; using " +
                           $"{Path.GetFileName(Path.GetDirectoryName(chosen.Match.Path))}.");

        return chosen.Match;
    }

    /// <summary>
    /// Finds the worker program itself: circuitRF's own tools directory first, then near the kit.
    ///
    /// <para>The tools directory is where it belongs — the worker is circuitRF's component, not the
    /// kit's. But a worker sitting beside a kit has to be found too, or a user who has one cannot use
    /// it until a release ships, which is a long time to be blocked by a file they already have.</para>
    ///
    /// <para><b><paramref name="format"/> says which platform's worker is wanted, and it is a filter
    /// on the file's own magic bytes rather than on its name.</b> The worker is a DIFFERENT program
    /// per platform — the Windows one is a launcher stub that stages the callback module the model
    /// asks for, the Linux one is the whole worker — so "the worker" is not a single file to find.
    /// Accepting whichever happened to have the right name is how a Linux ELF gets named as the
    /// Windows command, or a Windows stub gets shared into the Linux VM: both fail at Run, and both
    /// fail complaining about a program that plainly IS there, which is a bad way to spend an
    /// afternoon.</para>
    ///
    /// <para>Both spellings of the name are looked for — with and without <c>.exe</c> — because the
    /// magic decides the platform and the extension is only a convention. A Windows build named
    /// without <c>.exe</c> is still a Windows build; a name-only search would simply not see it.</para>
    /// </summary>
    public static string? FindWorker(
        WorkerProfile profile, string? searchRoot, int ancestorLevels = 2,
        LibraryFormat format = LibraryFormat.Elf)
    {
        ArgumentNullException.ThrowIfNull(profile);

        string[] names = [profile.Worker, profile.Worker + ".exe"];

        foreach (string name in names)
        {
            string shipped = SafeCombine(DeviceWorkerManifest.ToolsDirectory, name);
            if (shipped.Length > 0 && File.Exists(shipped) && MatchesFormat(shipped, format)) return shipped;
        }

        if (string.IsNullOrWhiteSpace(searchRoot)) return null;

        int examined = 0;
        foreach (string root in SearchRoots(searchRoot, ancestorLevels))
            foreach (string name in names)
            {
                IEnumerable<string> hits;
                try
                {
                    hits = Directory.EnumerateFiles(root, name, new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible    = true,
                        MaxRecursionDepth     = 8,
                    });
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

                foreach (string hit in hits)
                {
                    if (++examined > MaxCandidates) return null;
                    if (MatchesFormat(hit, format)) return hit;
                }
            }

        return null;
    }

    /// <summary>
    /// Whether a file's own magic bytes say it is the container format asked for.
    ///
    /// <para><b>Magic, not extension.</b> A vendor names its build folders for the toolchain that
    /// produced them and its files for whatever it likes; the first few bytes are the one property
    /// that cannot be a naming convention.</para>
    ///
    /// <para>A file too short to classify, or unreadable, matches nothing — refusing is right, since
    /// the caller is about to hand it to a loader.</para>
    /// </summary>
    private static bool MatchesFormat(string path, LibraryFormat format)
    {
        if (format == LibraryFormat.Any) return true;

        try
        {
            using var f = File.OpenRead(path);
            Span<byte> head = stackalloc byte[4];
            if (f.ReadAtLeast(head, 4, throwOnEndOfStream: false) != 4) return false;

            return format switch
            {
                LibraryFormat.Elf => head[0] == 0x7F && head[1] == (byte)'E'
                                  && head[2] == (byte)'L' && head[3] == (byte)'F',
                LibraryFormat.Pe  => head[0] == (byte)'M' && head[1] == (byte)'Z',
                // Mach-O, both endiannesses and the fat/universal wrapper. Listed for completeness:
                // no vendor has yet shipped one of these, which is itself the reason macOS runs the
                // Linux build in a VM.
                LibraryFormat.MachO => Be32(head) is 0xFEEDFACE or 0xFEEDFACF or 0xCAFEBABE
                                    || Le32(head) is 0xFEEDFACE or 0xFEEDFACF,
                _ => true,
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }

        static uint Be32(ReadOnlySpan<byte> b) => (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
        static uint Le32(ReadOnlySpan<byte> b) => (uint)((b[3] << 24) | (b[2] << 16) | (b[1] << 8) | b[0]);
    }

    private static string SafeCombine(string? dir, string name)
    {
        if (string.IsNullOrEmpty(dir)) return "";
        try { return Path.GetFullPath(Path.Combine(dir, name)); }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return ""; }
    }

    /// <summary>The folder a build sits in — what a vendor names for the target it was built for.</summary>
    private static string BuildDirectory(string path)
        => Path.GetFileName(Path.GetDirectoryName(path)) ?? "";

    /// <summary>Which of <paramref name="wanted"/> this file advertises, for any shipped worker.</summary>
    private static IReadOnlyList<string> ServedTypes(string file, IReadOnlyList<string> wanted)
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length == 0 || info.Length > MaxLibraryBytes) return [];
            bytes = File.ReadAllBytes(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }

        foreach (var profile in Profiles)
        {
            var served = wanted.Where(t => Contains(bytes, profile.ExportPrefix + t)).ToList();
            if (served.Count > 0) return served;
        }
        return [];
    }

    /// <summary>
    /// Plain ASCII substring search over the file's bytes. An exported name is stored verbatim in
    /// every executable format, so this needs no per-format parser and works on a Linux <c>.so</c>,
    /// a Windows <c>.dll</c> and a macOS <c>.dylib</c> alike.
    /// </summary>
    private static bool Contains(byte[] haystack, string needle)
    {
        if (needle.Length == 0 || haystack.Length < needle.Length) return false;

        byte first = (byte)needle[0];
        int  last  = haystack.Length - needle.Length;

        for (int i = 0; i <= last; i++)
        {
            if (haystack[i] != first) continue;

            int k = 1;
            while (k < needle.Length && haystack[i + k] == (byte)needle[k]) k++;
            if (k == needle.Length) return true;
        }
        return false;
    }

    /// <summary>Lower is better. Position in the caller's hint list; no match sorts last.</summary>
    private static int Rank(string path, IReadOnlyList<string>? hints)
    {
        if (hints is null || hints.Count == 0) return 0;

        for (int i = 0; i < hints.Count; i++)
            if (path.Contains(hints[i], StringComparison.OrdinalIgnoreCase)) return i;

        return hints.Count;
    }

    /// <summary>
    /// The imported folder, then each ancestor up to <paramref name="levels"/>. A vendor delivery is
    /// several kits beside one shared library package, so the library is routinely a sibling of what
    /// was imported rather than inside it — but the walk is bounded, because searching ever upward
    /// from a folder on someone's disk ends somewhere it has no business being.
    /// </summary>
    /// <summary>
    /// The ancestor walk, then whatever the caller was told about — deduped, in that order. Told-about
    /// roots come LAST so a library sitting with the kit still wins over one merely declared somewhere.
    /// </summary>
    private static IReadOnlyList<string> AllRoots(
        string searchRoot, int levels, IEnumerable<string>? extraRoots)
    {
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();

        foreach (string r in SearchRoots(searchRoot, levels))
            if (seen.Add(r)) roots.Add(r);

        foreach (string r in extraRoots ?? [])
        {
            string full;
            try { full = Path.GetFullPath(r); }
            catch (Exception ex) when (ex is ArgumentException or IOException) { continue; }

            if (Directory.Exists(full) && seen.Add(full)) roots.Add(full);
        }

        return roots;
    }

    /// <summary>
    /// True when this folder holds a library our worker could drive — ANY device family, not a
    /// particular one. This is how a package that supplies no parts is recognised as still worth
    /// referencing: it is the models, and nothing else about it says so.
    /// </summary>
    public static bool HoldsAnyDeviceLibrary(string root, int ancestorLevels = 0)
    {
        if (string.IsNullOrWhiteSpace(root)) return false;

        int examined = 0;
        foreach (string dir in SearchRoots(root, ancestorLevels))
            foreach (string file in Candidates(dir))
            {
                if (++examined > MaxCandidates) return false;
                if (ExportsAnyDeviceEntryPoint(file)) return true;
            }

        return false;
    }

    /// <summary>Whether the file exports an entry point of the shape our worker calls, for any family.</summary>
    private static bool ExportsAnyDeviceEntryPoint(string file)
    {
        byte[] bytes;
        try
        {
            var info = new FileInfo(file);
            if (!info.Exists || info.Length == 0 || info.Length > MaxLibraryBytes) return false;
            bytes = File.ReadAllBytes(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }

        // The PREFIX alone: the family name is the vendor's and is exactly what we must not need to
        // know in advance. An exported name sits verbatim in every executable format, so one scan
        // covers the Linux, Windows and macOS builds a vendor ships side by side.
        foreach (var profile in Profiles)
            if (Contains(bytes, profile.ExportPrefix)) return true;

        return false;
    }

    private static IReadOnlyList<string> SearchRoots(string searchRoot, int levels)
    {
        var roots = new List<string>();
        try
        {
            var dir = new DirectoryInfo(Path.GetFullPath(searchRoot));
            for (int i = 0; dir is not null && i <= Math.Max(0, levels); i++)
            {
                if (dir.Exists) roots.Add(dir.FullName);
                dir = dir.Parent;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException) { }

        // Deepest first: a library inside the imported kit beats one found by walking outward.
        return roots;
    }

    private static IEnumerable<string> Candidates(string root)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible    = true,
                MaxRecursionDepth     = 8,
            });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

        foreach (string f in files)
            if (LibraryExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                yield return f;
    }
}
