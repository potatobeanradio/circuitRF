using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CircuitRF.Design.Em3d.Install;

/// <summary>An operating system a recipe is for.</summary>
public enum RecipePlatform { MacOS, Linux, Windows }

/// <summary>A processor architecture a recipe is for.</summary>
public enum RecipeArchitecture { Arm64, X64 }

/// <summary>What one recipe step does.</summary>
public enum RecipeStepKind
{
    /// <summary>circuitRF fetches one of the recipe's sources into the home's <c>downloads/</c>, with
    /// progress in bytes, and checks it against upstream's checksum when upstream publishes one.</summary>
    Download,

    /// <summary>Unpacks a downloaded <c>.tgz</c>, <c>.tar.gz</c> or <c>.zip</c>.</summary>
    Extract,

    /// <summary>Starts one program with an argument LIST — never a shell, never an interpolated string
    /// (R-em3d24-1c).</summary>
    Run,

    /// <summary>Creates a symbolic link (Palace's documented Spack MFEM workaround).</summary>
    Symlink,

    /// <summary>Writes a text file from the recipe's own lines (a Spack environment).</summary>
    Write,
}

/// <summary>How a <see cref="RecipeStepKind.Run"/> step's output becomes progress.</summary>
public enum RecipeProgress
{
    /// <summary>Indeterminate: the stage shows its name and the step's latest line.</summary>
    Lines,

    /// <summary><c>spack concretize</c>: every node marked <c> - </c> will be built, which gives the
    /// install its total before anything is built (R-em3d24-2c).</summary>
    SpackConcretize,

    /// <summary><c>spack install</c>: one <c>[+]</c> line per installed package — "package k of N".</summary>
    SpackInstall,
}

/// <summary>One upstream source (R-em3d24-1a).</summary>
/// <param name="Id">The name a Download step refers to it by; null for a source a step other than
/// circuitRF fetches.</param>
/// <param name="Url">The upstream URL, shown in the consent text.</param>
/// <param name="FetchedBy"><c>circuitRF</c>, or the step that fetches it.</param>
/// <param name="File">For a circuitRF download, the file name it is saved under in <c>downloads/</c>.</param>
/// <param name="Sha256">Upstream's own published checksum, or one the recipe pins itself (then
/// <paramref name="ChecksumNote"/> says where it came from), or null.</param>
/// <param name="ChecksumNote">Required when <paramref name="Sha256"/> is null: why ("upstream publishes
/// none"), or how the source is pinned instead.</param>
public sealed record RecipeSource(string? Id, string Url, string FetchedBy, string? File, string? Sha256, string? ChecksumNote);

/// <summary>What must already be on the machine, checked before any download (R-em3d24-2b).</summary>
/// <param name="Kind"><c>program</c> (found on <see cref="Search"/>, optionally at a minimum version),
/// <c>file</c> (<see cref="Path"/> exists, or a file matching <see cref="Name"/>, which may hold a <c>*</c>,
/// exists in one of <see cref="Search"/> — a header's directory differs by distribution), or <c>command</c> (the program at <see cref="Path"/>, or found on
/// <see cref="Search"/>, runs with <see cref="Arguments"/> and exits 0 — how "a Python whose certificate
/// store works", F0's first Palace failure, is asked).</param>
/// <param name="Name">What the user reads: <c>git</c>, <c>Homebrew's gfortran</c>.</param>
/// <param name="Path">For <c>file</c> and <c>command</c>: the exact path. For <c>program</c>, null.</param>
/// <param name="Search">For <c>program</c>, and <c>file</c> without a path: the directories looked in.</param>
/// <param name="Variable">When set, the resolved path is available to the steps as <c>${name}</c>.</param>
/// <param name="Arguments">For <c>command</c>, and for a version check.</param>
/// <param name="MinVersion">For <c>program</c>: the lowest dotted version accepted.</param>
/// <param name="Remedy">The ONE command the user runs, by distribution id (<c>ubuntu</c>, <c>debian</c>,
/// <c>fedora</c>) with <c>default</c> as the fallback. circuitRF runs none of them.</param>
public sealed record RecipePrerequisite(
    string Kind, string Name, string? Path, IReadOnlyList<string> Search, string? Variable,
    IReadOnlyList<string> Arguments, string? MinVersion, IReadOnlyDictionary<string, string> Remedy)
{
    /// <summary>brief-emi-3 R-emi3-2a — an OPTIONAL prerequisite never refuses the install: it decides whether the
    /// steps that name it in <see cref="RecipeStep.When"/> run, and an unmet one leaves them for later.</summary>
    public bool Optional { get; init; }

    /// <summary>brief-emi-3 R-emi3-4 — the program and arguments that start the prerequisite's OWN installer (its
    /// vendor's), which the user may start from a button. Empty: there is none, and only the remedy is shown.</summary>
    public IReadOnlyList<string> Installer { get; init; } = [];

    /// <summary>brief-emi-9 — the file a <c>program</c> or searched <c>command</c> looks for, when it is not
    /// <see cref="Name"/> (which is what the user reads: "a Rust toolchain", looked for as <c>cargo</c>).</summary>
    public string? Program { get; init; }

    /// <summary>The file name looked for: <see cref="Program"/>, or <see cref="Name"/>.</summary>
    public string ProgramName => Program ?? Name;

    /// <summary>brief-emi-9 — other OPTIONAL prerequisites, by name, that this one's steps also need: the group is due only
    /// when all of them are met too, and a change in any of them is a change of state (a build that failed for want of a
    /// linker is tried again when the linker arrives, not only when this prerequisite changes).</summary>
    public IReadOnlyList<string> Needs { get; init; } = [];

    /// <summary>brief-emi-9 — what this group's steps add, as a word the solver's own code reads (<c>gpu</c>); the
    /// installer does nothing with it.</summary>
    public string? Enables { get; init; }

    /// <summary>brief-emi-9 R-emi9-2b — how circuitRF can supply this prerequisite for ONE build when the user asks it to:
    /// fetched into the home, used, and deleted. Null: only the user can install it.</summary>
    public RecipeBootstrap? Bootstrap { get; init; }
}

/// <summary>
/// brief-emi-9 R-emi9-2b — a temporary copy of an optional prerequisite, fetched into the home for one run of its steps
/// (<see cref="SolverInstaller.FinishOptionalWithBootstrap"/>) and deleted after it, whatever happened.
/// </summary>
/// <param name="Name">What the consent calls it: <c>Rust's minimal toolchain</c>.</param>
/// <param name="Into">The directory it is put in, under <c>${home}</c>; deleted after.</param>
/// <param name="Environment">What its steps, the prerequisite's check and the group's steps then see, added to the inherited
/// environment (where the toolchain is).</param>
/// <param name="Sources">What it fetches; not the recipe's, since an install never fetches them.</param>
/// <param name="Steps">How it is fetched and set up: the recipe's own step kinds.</param>
/// <param name="Notes">The consent's sentences about it (licences).</param>
/// <param name="DownloadMB">Measured download size.</param>
public sealed record RecipeBootstrap(
    string Name, string Into, IReadOnlyDictionary<string, string> Environment, IReadOnlyList<RecipeSource> Sources,
    IReadOnlyList<RecipeStep> Steps, IReadOnlyList<string> Notes, double? DownloadMB, RecipeMeasured Measured);

/// <summary>One step. Which fields apply depends on <see cref="Kind"/>; the loader refuses a step
/// missing what its kind needs.</summary>
public sealed record RecipeStep(
    string Name, RecipeStepKind Kind,
    string? Command, IReadOnlyList<string> Arguments, string? WorkingDirectory, string? CaptureAs, string? Expect,
    RecipeProgress Progress, string? Source, string? Into, string? Link, string? Target,
    string? Path, IReadOnlyList<string> Lines)
{
    /// <summary>brief-emi-3 R-emi3-2a — the optional prerequisite (by its name) this step needs, or null for a step
    /// every install runs. An optional step whose prerequisite is unmet is SKIPPED, not failed, and one that fails
    /// leaves the install installed; either way the install records it and finishes it later
    /// (<see cref="SolverInstaller.FinishOptional"/>). Optional steps are the recipe's tail, so finishing them later
    /// is running exactly that tail.</summary>
    public string? When { get; init; }

    public bool Optional => When is not null;

    /// <summary>brief-emi-9 — variables this step's process alone gets, over the recipe's (<c>PATH</c> holding a
    /// prerequisite's directory, <c>${cargoDir}</c>).</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();
}

/// <summary>What a recipe cost when it was run, and where that was measured. A null figure is
/// "not yet measured", and <see cref="Source"/> then says whose run will measure it.</summary>
public sealed record RecipeMeasured(double? Minutes, double? DiskGB, string Source);

/// <summary>
/// One validated version of one tool on one platform and architecture: everything the assistant does,
/// as data (brief-em3d-24 R-em3d24-1). Adopting a new validated version is a recipe edit and a
/// validation run (em-3d.md §7.2) — nothing in the installer names a version, a URL or a package.
/// </summary>
public sealed record SolverRecipe(
    string Id, SolverTool Tool, string Version, RecipePlatform Platform, RecipeArchitecture Architecture,
    bool Relocatable, string LayoutNote, bool NeedsSpaceFreePath,
    IReadOnlyList<RecipeSource> Sources, IReadOnlyList<RecipePrerequisite> Prerequisites,
    IReadOnlyDictionary<string, string> Environment, IReadOnlyList<string> ClearEnvironment,
    IReadOnlyList<RecipeStep> Steps, string? SpackSpec, string Program, string? SpackInstallTree,
    RecipeMeasured Measured, IReadOnlyList<string> KnownFailures)
{
    /// <summary>brief-emi-2 R-emi2-4 — the sentences the consent adds about what the user accepts by installing (the
    /// licences of what is fetched), one each, in order. Data, so the installer names no package.</summary>
    public IReadOnlyList<string> ConsentNotes { get; init; } = [];

    /// <summary>brief-emi-3 — each optional prerequisite with the steps that need it, in recipe order.</summary>
    public IReadOnlyList<(RecipePrerequisite When, IReadOnlyList<RecipeStep> Steps)> OptionalGroups
        => Prerequisites.Where(p => p.Optional)
                        .Select(p => (p, (IReadOnlyList<RecipeStep>)Steps.Where(s => s.When == p.Name).ToList()))
                        .Where(g => g.Item2.Count > 0).ToList();
}

/// <summary>
/// The shipped recipes: <c>recipes/*.json</c>, embedded in this assembly, read through
/// <see cref="Parse"/> exactly as a test's recipe is. <c>recipe.schema.json</c> beside them is the
/// contract, and <c>SolverInstallTests</c> validates every shipped file against it, so a typo fails the
/// build rather than a user's install (R-em3d24-1b).
/// </summary>
public static class SolverRecipes
{
    private const string ResourcePrefix = "CircuitRF.Design.Em3d.Install.recipes.";

    /// <summary>Every shipped recipe's JSON text, by resource name — what the schema gate reads.</summary>
    public static IReadOnlyDictionary<string, string> ShippedText => ShippedTextLazy.Value;

    /// <summary>Every shipped recipe, parsed. A shipped recipe that does not parse is a build defect,
    /// which the schema gate reports; here it is skipped rather than taking the installer down with it.
    /// <para>Lazy, not a static initializer: an initializer runs in TEXT order, before the serializer
    /// options declared below it exist, and every recipe then parsed as empty — silently, which is how
    /// the schema gate found it.</para></summary>
    public static IReadOnlyList<SolverRecipe> All => AllLazy.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> ShippedTextLazy = new(LoadText);

    private static readonly Lazy<IReadOnlyList<SolverRecipe>> AllLazy = new(() => ShippedText
        .Where(kv => !kv.Key.EndsWith("schema.json", StringComparison.Ordinal))
        .Select(kv => Parse(kv.Value, out _)).OfType<SolverRecipe>().ToList());

    /// <summary>This machine's platform and architecture, or null for one no recipe could name.</summary>
    public static (RecipePlatform Platform, RecipeArchitecture Architecture)? Current()
    {
        RecipePlatform? os = OperatingSystem.IsMacOS() ? RecipePlatform.MacOS
                           : OperatingSystem.IsLinux() ? RecipePlatform.Linux
                           : OperatingSystem.IsWindows() ? RecipePlatform.Windows : null;
        RecipeArchitecture? arch = RuntimeInformation.OSArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => RecipeArchitecture.Arm64,
            System.Runtime.InteropServices.Architecture.X64   => RecipeArchitecture.X64,
            _                                                 => null,
        };
        return os is { } o && arch is { } a ? (o, a) : null;
    }

    /// <summary>The recipe for <paramref name="tool"/> on this machine — the named version, or else the
    /// newest validated one that has a recipe here. Null when there is none.</summary>
    public static SolverRecipe? For(SolverTool tool, string? version = null)
        => Current() is { } here ? For(tool, version, here.Platform, here.Architecture, All) : null;

    public static SolverRecipe? For(SolverTool tool, string? version, RecipePlatform platform, RecipeArchitecture architecture,
                                    IEnumerable<SolverRecipe> recipes)
    {
        var validated = SolverDiscovery.Create(tool).ValidatedVersions.Select(v => v.Release).ToList();
        return recipes
            .Where(r => r.Tool == tool && r.Platform == platform && r.Architecture == architecture)
            .Where(r => version is null ? validated.Contains(r.Version) : r.Version == version)
            .OrderByDescending(r => validated.IndexOf(r.Version))   // the validated list is oldest first
            .FirstOrDefault();
    }

    /// <summary>Names every platform a tool has a recipe for — what a "no recipe here" refusal lists.</summary>
    public static string PlatformsFor(SolverTool tool)
    {
        var names = All.Where(r => r.Tool == tool)
                       .Select(r => $"{Describe(r.Platform)} {Describe(r.Architecture)}")
                       .Distinct().Order(StringComparer.Ordinal).ToList();
        return names.Count == 0 ? "none" : string.Join(", ", names);
    }

    /// <summary>
    /// What a "no recipe here" message adds about building <paramref name="tool"/> by hand, or null when it adds
    /// nothing. Only Gmsh has it: upstream publishes no Linux arm64 build, and finding the source archive of the
    /// exact version circuitRF validates was the hard part of a field install there, so the message names its
    /// address. The version is the newest validated one, so the address follows the validated list.
    /// </summary>
    public static string? ByHand(SolverTool tool)
    {
        if (tool != SolverTool.Gmsh) return null;
        string release = SolverDiscovery.Gmsh.ValidatedVersions[^1].Release;
        return $"Gmsh {release} builds from its source, https://gmsh.info/src/gmsh-{release}-source.tgz: " +
               $"“{SolverDiscovery.GmshFromSourceSection}” in the EM Setup reference ({SolverDiscovery.GmshFromSourcePage}) " +
               "has the steps.";
    }

    public static string Describe(RecipePlatform p) => p switch
    {
        RecipePlatform.MacOS => "macOS",
        RecipePlatform.Linux => "Linux",
        _                    => "Windows",
    };

    public static string Describe(RecipeArchitecture a) => a == RecipeArchitecture.Arm64 ? "arm64" : "x64";

    // ── parsing ──────────────────────────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling      = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = false,
    };

    /// <summary>
    /// Reads one recipe, or returns null with <paramref name="error"/> naming what is wrong. An unknown key
    /// is an error, not something skipped — a key silently ignored is a step silently not taken.
    /// </summary>
    public static SolverRecipe? Parse(string json, out string? error)
    {
        error = null;
        RecipeDto? d;
        try { d = JsonSerializer.Deserialize<RecipeDto>(json, Json); }
        catch (JsonException e) { error = e.Message; return null; }
        if (d is null) { error = "the recipe is empty"; return null; }

        var problems = new List<string>();
        void Need(bool ok, string what) { if (!ok) problems.Add(what); }

        var tool = SolverHomes.ToolFromId(d.Tool);
        Need(tool is not null, $"tool '{d.Tool}' is not palace, gmsh, openems or emerge");
        RecipePlatform? platform = d.Platform switch { "macos" => RecipePlatform.MacOS, "linux" => RecipePlatform.Linux, "windows" => RecipePlatform.Windows, _ => null };
        Need(platform is not null, $"platform '{d.Platform}' is not macos, linux or windows");
        RecipeArchitecture? arch = d.Architecture switch { "arm64" => RecipeArchitecture.Arm64, "x64" => RecipeArchitecture.X64, _ => null };
        Need(arch is not null, $"architecture '{d.Architecture}' is not arm64 or x64");
        Need(d.Id is { Length: > 0 }, "id is missing");
        Need(d.Version is { Length: > 0 }, "version is missing");
        Need(d.Layout is "relocatable" or "in-place", $"layout '{d.Layout}' is not relocatable or in-place");
        Need(d.LayoutNote is { Length: > 0 }, "layoutNote is missing");
        Need(d.Program is { Length: > 0 }, "program is missing");
        Need(d.Measured is { Source.Length: > 0 }, "measured.source is missing");

        RecipeSource Source(SourceDto s)
        {
            Need(s.Url is { Length: > 0 }, "a source has no url");
            Need(s.FetchedBy is { Length: > 0 }, $"source '{s.Url}' has no fetchedBy");
            Need(s.Sha256 is not null || s.ChecksumNote is { Length: > 0 },
                 $"source '{s.Url}' has no checksum and no checksumNote saying why");
            Need(s.FetchedBy != "circuitRF" || (s.Id is { Length: > 0 } && s.File is { Length: > 0 }),
                 $"source '{s.Url}' is fetched by circuitRF but has no id and file");
            return new RecipeSource(s.Id, s.Url ?? "", s.FetchedBy ?? "", s.File, s.Sha256, s.ChecksumNote);
        }

        // One step, checked against the sources it may name (a bootstrap's own); a bootstrap's steps are never optional.
        RecipeStep Step(StepDto s, IReadOnlyList<RecipeSource> known, bool bootstrap)
        {
            RecipeStepKind? kind = s.Kind switch
            {
                "download" => RecipeStepKind.Download, "extract" => RecipeStepKind.Extract, "run" => RecipeStepKind.Run,
                "symlink"  => RecipeStepKind.Symlink,  "write"   => RecipeStepKind.Write,   _     => null,
            };
            RecipeProgress? progress = (s.Progress ?? "lines") switch
            {
                "lines" => RecipeProgress.Lines, "spack-concretize" => RecipeProgress.SpackConcretize,
                "spack-install" => RecipeProgress.SpackInstall, _ => null,
            };
            Need(s.Name is { Length: > 0 }, "a step has no name");
            Need(kind is not null, $"step '{s.Name}': kind '{s.Kind}' is unknown");
            Need(progress is not null, $"step '{s.Name}': progress '{s.Progress}' is unknown");
            switch (kind)
            {
                case RecipeStepKind.Download:
                    Need(known.Any(x => x.Id == s.Source && x.FetchedBy == "circuitRF"),
                         $"step '{s.Name}' downloads '{s.Source}', which is not a circuitRF-fetched source");
                    break;
                case RecipeStepKind.Extract:
                    Need(known.Any(x => x.Id == s.Source && x.File is not null), $"step '{s.Name}' extracts an unknown source '{s.Source}'");
                    Need(s.Into is { Length: > 0 }, $"step '{s.Name}' says nowhere to extract into");
                    break;
                case RecipeStepKind.Run:
                    Need(s.Command is { Length: > 0 }, $"step '{s.Name}' runs no command");
                    Need(s.Command is null || !IsShell(s.Command), $"step '{s.Name}' starts a shell, which a recipe may not");
                    break;
                case RecipeStepKind.Symlink:
                    Need(s.Link is { Length: > 0 } && s.Target is { Length: > 0 }, $"step '{s.Name}' needs link and target");
                    break;
                case RecipeStepKind.Write:
                    Need(s.Path is { Length: > 0 } && s.Lines is { Count: > 0 }, $"step '{s.Name}' needs path and lines");
                    break;
            }
            Need(!bootstrap || (!s.Optional && s.When is null), $"bootstrap step '{s.Name}' is marked optional");
            return new RecipeStep(s.Name ?? "", kind ?? RecipeStepKind.Run, s.Command, s.Arguments ?? [], s.WorkingDirectory,
                                  s.CaptureAs, s.Expect, progress ?? RecipeProgress.Lines, s.Source, s.Into,
                                  s.Link, s.Target, s.Path, s.Lines ?? [])
                   { When = s.Optional ? s.When : null, Environment = s.Environment ?? new() };
        }

        var sources = (d.Sources ?? []).Select(Source).ToList();
        Need(sources.Count > 0, "a recipe fetches from somewhere, and names it");

        var prereqs = new List<RecipePrerequisite>();
        foreach (var p in d.Prerequisites ?? [])
        {
            Need(p.Kind is "program" or "file" or "command", $"prerequisite kind '{p.Kind}' is unknown");
            Need(p.Name is { Length: > 0 }, "a prerequisite has no name");
            Need(p.Remedy is { Count: > 0 }, $"prerequisite '{p.Name}' names no remedy");
            Need(p.Kind != "program" || p.Search is { Count: > 0 }, $"prerequisite '{p.Name}' says nowhere to search");
            Need(p.Kind != "file" || p.Path is { Length: > 0 } || p.Search is { Count: > 0 },
                 $"prerequisite '{p.Name}' names neither a path nor where to search");
            Need(p.Kind != "command" || p.Path is { Length: > 0 } || p.Search is { Count: > 0 },
                 $"prerequisite '{p.Name}' names neither a path nor where to search");
            Need(p.Installer is null || (p.Installer.Count > 0 && p.Optional), $"prerequisite '{p.Name}' names an installer but is not optional");
            // brief-emi-9 — needs, enables and a bootstrap belong to an optional prerequisite; what it needs comes before it.
            Need((p.Needs is null && p.Enables is null && p.Bootstrap is null) || p.Optional,
                 $"prerequisite '{p.Name}' names needs, enables or a bootstrap but is not optional");
            foreach (string need in p.Needs ?? [])
                Need(prereqs.Any(q => q.Optional && q.Name == need), $"prerequisite '{p.Name}' needs '{need}', which is not an earlier optional prerequisite");
            RecipeBootstrap? bootstrap = null;
            if (p.Bootstrap is { } b)
            {
                Need(b.Name is { Length: > 0 }, $"prerequisite '{p.Name}': its bootstrap has no name");
                Need(b.Into is { Length: > 0 } && b.Into.StartsWith("${home}/", StringComparison.Ordinal),
                     $"prerequisite '{p.Name}': its bootstrap goes somewhere other than under ${{home}}");
                Need(b.Measured is { Source.Length: > 0 }, $"prerequisite '{p.Name}': its bootstrap's measured.source is missing");
                var bootSources = (b.Sources ?? []).Select(x => Source(x)).ToList();
                var bootSteps = (b.Steps ?? []).Select(x => Step(x, bootSources, bootstrap: true)).ToList();
                Need(bootSteps.Count > 0, $"prerequisite '{p.Name}': its bootstrap has no steps");
                bootstrap = new RecipeBootstrap(b.Name ?? "", b.Into ?? "", b.Environment ?? new(), bootSources, bootSteps, b.Notes ?? [],
                                                b.DownloadMB, new RecipeMeasured(b.Measured?.Minutes, b.Measured?.DiskGB, b.Measured?.Source ?? ""));
            }
            prereqs.Add(new RecipePrerequisite(p.Kind ?? "", p.Name ?? "", p.Path, p.Search ?? [], p.Variable,
                                               p.Arguments ?? [], p.MinVersion, p.Remedy ?? new())
                        { Optional = p.Optional, Installer = p.Installer ?? [], Program = p.Program, Needs = p.Needs ?? [],
                          Enables = p.Enables, Bootstrap = bootstrap });
        }

        var steps = new List<RecipeStep>();
        foreach (var s in d.Steps ?? [])
        {
            var step = Step(s, sources, bootstrap: false);
            // R-emi3-2a — "optional" and "when" come together, "when" names an optional prerequisite, and nothing
            // required follows an optional step (finishing later runs the tail, and nothing it skipped may be needed).
            Need(s.Optional == (s.When is not null), $"step '{s.Name}': optional and when go together");
            Need(s.When is null || prereqs.Any(p => p.Optional && p.Name == s.When),
                 $"step '{s.Name}' runs when '{s.When}', which is not an optional prerequisite");
            Need(s.When is not null || steps.All(x => !x.Optional), $"step '{s.Name}' is required but follows an optional step");
            steps.Add(step);
        }
        foreach (var p in prereqs.Where(p => p.Optional))
            Need(steps.Any(s => s.When == p.Name) || prereqs.Any(q => q.Needs.Contains(p.Name)),
                 $"optional prerequisite '{p.Name}' is needed by no step and no other prerequisite");
        Need(steps.Count > 0, "a recipe has at least one step");
        foreach (string id in d.KnownFailures ?? [])
            Need(Install.KnownFailures.All.Any(k => k.Id == id), $"known failure '{id}' is not in KnownFailures");

        if (problems.Count > 0) { error = string.Join("; ", problems); return null; }

        return new SolverRecipe(
            d.Id!, tool!.Value, d.Version!, platform!.Value, arch!.Value,
            d.Layout == "relocatable", d.LayoutNote!, d.NeedsSpaceFreePath,
            sources, prereqs, d.Environment ?? new(), d.ClearEnvironment ?? [], steps, d.SpackSpec, d.Program!,
            d.SpackInstallTree, new RecipeMeasured(d.Measured!.Minutes, d.Measured.DiskGB, d.Measured.Source!),
            d.KnownFailures ?? []) { ConsentNotes = d.ConsentNotes ?? [] };
    }

    /// <summary>A command that is a shell — refused by the loader, so no recipe can smuggle a string past
    /// R-em3d24-1c. An upstream's own build SCRIPT, started by its path, is not a shell command line.</summary>
    private static bool IsShell(string command)
    {
        string name = System.IO.Path.GetFileNameWithoutExtension(command.Trim()).ToLowerInvariant();
        return name is "sh" or "bash" or "zsh" or "dash" or "ksh" or "csh" or "tcsh" or "fish" or "cmd" or "powershell" or "pwsh";
    }

    private static IReadOnlyDictionary<string, string> LoadText()
    {
        var asm = typeof(SolverRecipes).Assembly;
        var map = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (string name in asm.GetManifestResourceNames().Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)))
        {
            using var s = asm.GetManifestResourceStream(name);
            if (s is null) continue;
            using var r = new StreamReader(s);
            map[name[ResourcePrefix.Length..]] = r.ReadToEnd();
        }
        return map;
    }

    // ── the file's shape ─────────────────────────────────────────────────────────────────────────

    private sealed class RecipeDto
    {
        [JsonPropertyName("$schema")] public string? Schema { get; set; }
        public string? Id { get; set; }
        public string? Tool { get; set; }
        public string? Version { get; set; }
        public string? Platform { get; set; }
        public string? Architecture { get; set; }
        public string? Layout { get; set; }
        public string? LayoutNote { get; set; }
        public bool NeedsSpaceFreePath { get; set; }
        public List<SourceDto>? Sources { get; set; }
        public List<PrereqDto>? Prerequisites { get; set; }
        public Dictionary<string, string>? Environment { get; set; }
        public List<string>? ClearEnvironment { get; set; }
        public List<string>? ConsentNotes { get; set; }
        public List<StepDto>? Steps { get; set; }
        public string? SpackSpec { get; set; }
        public string? Program { get; set; }
        public string? SpackInstallTree { get; set; }
        public MeasuredDto? Measured { get; set; }
        public List<string>? KnownFailures { get; set; }
        public string? Comment { get; set; }
    }

    private sealed class SourceDto
    {
        public string? Id { get; set; }
        public string? Url { get; set; }
        public string? FetchedBy { get; set; }
        public string? File { get; set; }
        public string? Sha256 { get; set; }
        public string? ChecksumNote { get; set; }
    }

    private sealed class PrereqDto
    {
        public string? Kind { get; set; }
        public string? Name { get; set; }
        public string? Path { get; set; }
        public List<string>? Search { get; set; }
        public string? Variable { get; set; }
        public List<string>? Arguments { get; set; }
        public string? MinVersion { get; set; }
        public Dictionary<string, string>? Remedy { get; set; }
        public bool Optional { get; set; }
        public List<string>? Installer { get; set; }
        public string? Program { get; set; }
        public List<string>? Needs { get; set; }
        public string? Enables { get; set; }
        public BootstrapDto? Bootstrap { get; set; }
    }

    private sealed class BootstrapDto
    {
        public string? Name { get; set; }
        public string? Into { get; set; }
        public Dictionary<string, string>? Environment { get; set; }
        public List<SourceDto>? Sources { get; set; }
        public List<StepDto>? Steps { get; set; }
        public List<string>? Notes { get; set; }
        public double? DownloadMB { get; set; }
        public MeasuredDto? Measured { get; set; }
        public string? Comment { get; set; }
    }

    private sealed class StepDto
    {
        public string? Name { get; set; }
        public string? Kind { get; set; }
        public string? Command { get; set; }
        public List<string>? Arguments { get; set; }
        public string? WorkingDirectory { get; set; }
        public string? CaptureAs { get; set; }
        public string? Expect { get; set; }
        public string? Progress { get; set; }
        public string? Source { get; set; }
        public string? Into { get; set; }
        public string? Link { get; set; }
        public string? Target { get; set; }
        public string? Path { get; set; }
        public List<string>? Lines { get; set; }
        public bool Optional { get; set; }
        public string? When { get; set; }
        public Dictionary<string, string>? Environment { get; set; }
        public string? Comment { get; set; }
    }

    private sealed class MeasuredDto
    {
        public double? Minutes { get; set; }
        public double? DiskGB { get; set; }
        public string? Source { get; set; }
    }
}
