using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;

namespace CircuitRF.Design.Em3d;

/// <summary>The three programs a 3D EM run can need. circuitRF distributes none of them (em-3d.md §7.1).</summary>
public enum SolverTool { Palace, Gmsh, OpenEms }

/// <summary>Where a program was found — the column the Settings row shows.</summary>
public enum SolverHowFound { Settings, Environment, Path, DefaultDirectory, Spack }

/// <summary>
/// Something a user-built program may or may not be able to do (em-3d.md §7.1). Only what a brief
/// actually needs is listed: the wave-port series adds an eigensolver row, and the probe mechanism
/// below does not change for it (R-em3d6-3d).
/// </summary>
public enum SolverCapability
{
    /// <summary>A driven (frequency-domain) solve with lumped ports — everything F1 asks of Palace.</summary>
    DrivenLumpedPorts,

    /// <summary>
    /// brief-em3d-23 R-em3d23-1a — wave ports: the eigensolver for each port's 2D mode, and GSLIB, which
    /// Palace needs to integrate a voltage path and report the mode's impedance.
    /// </summary>
    WavePorts,

    /// <summary>brief-em3d-23 R-em3d23-1a — an eigenmode solve: the eigensolver (SLEPc or ARPACK).</summary>
    Eigenmode,
}

/// <summary>
/// One version circuitRF has run its references through and will therefore run.
/// </summary>
/// <param name="Release">The release name a person recognises, e.g. <c>0.18.1</c>.</param>
/// <param name="Identity">What the program itself prints for that release — for Palace and openEMS a
/// git hash, because neither prints a release number (F0 Q9, Q11).</param>
/// <param name="Companion">A second printed value that must match too, or null: Palace's configuration
/// schema version, openEMS's CSXCAD hash (the library that parses its XML).</param>
public sealed record SolverValidatedVersion(string Release, string Identity, string? Companion)
{
    public override string ToString()
        => Identity == Release ? Release : $"{Release} ({Identity})";
}

/// <summary>A program circuitRF can start.</summary>
/// <param name="Path">Absolute, resolved once and started from there (a bare name resolves through
/// <c>PATH</c>, which is what <c>GitInstallation</c> records the reason for not trusting).</param>
/// <param name="Version">What the program says it is — a git hash for Palace and openEMS, a dotted
/// version for Gmsh.</param>
/// <param name="Companion">Palace's schema version, openEMS's CSXCAD hash; null for Gmsh.</param>
/// <param name="Banner">What it printed, trimmed to the lines that carried the version.</param>
/// <param name="HowFoundText">The same answer in words — "set in Settings", "found at /opt/…".</param>
/// <param name="Release">The validated release this identifies, or null when it is not one.</param>
public sealed record SolverInstallation(
    SolverTool Tool, string Path, string Version, string? Companion, string Banner,
    SolverHowFound HowFound, string HowFoundText, string? Release)
{
    /// <summary>Whether this is a version circuitRF has validated (em-3d.md §5.3).</summary>
    public bool Validated => Release is not null;

    /// <summary>The version as a person reads it: <c>0.18.1 (0dc74cd, schema 1-7-0)</c>.</summary>
    public string DescribeVersion()
    {
        string detail = Tool switch
        {
            SolverTool.Palace  => Companion is null ? Version : $"{Version}, schema {Companion}",
            SolverTool.OpenEms => Companion is null ? Version : $"{Version}, CSXCAD {Companion}",
            _                  => Version,
        };
        return Release is null ? detail : Release == detail ? Release : $"{Release} ({detail})";
    }
}

/// <summary>What a capability probe answered, and whether the answer came from the cache.</summary>
public sealed record SolverCapabilityVerdict(SolverCapability Capability, bool Available, string Detail, bool FromCache);

/// <summary>
/// Everything circuitRF knows about one program before a run: what was found, why each other candidate
/// was not taken, what it can do, and the refusal — null when the run may proceed.
/// </summary>
public sealed record SolverReadiness(
    SolverTool Tool, string Name, SolverInstallation? Installation, IReadOnlyList<string> Rejected,
    IReadOnlyList<SolverCapabilityVerdict> Capabilities, string? Refusal)
{
    public bool Proceeds => Refusal is null;
}

/// <summary>
/// Finds Palace, Gmsh and openEMS, reads their versions, and asks Palace what its build can do
/// (brief-em3d-6, em-3d.md §7.1).
///
/// <para><b>One class, one instance per tool</b> (R-em3d6-1b). The walk is written once; what differs
/// per tool — the command names, the environment variable, the version question and its parser, the
/// validated list — is data on the instance. Three private copies of the walk is how three drift
/// apart.</para>
///
/// <para><b>The walk is <c>VerilogACompilerDiscovery</c>'s and <c>GitDiscovery</c>'s</b>, and
/// <c>src/Core/RESOLVED.md</c> records why each rule is there:</para>
/// <list type="bullet">
/// <item>A path named in Settings, then the environment variable, then <c>PATH</c>, then
/// <see cref="SearchDirectories"/> — because a Finder-launched application's <c>PATH</c> holds only
/// <c>/usr/bin:/bin:/usr/sbin:/sbin</c> (measured on an installed build), so a program the user runs
/// from a terminal is otherwise invisible to the application.</item>
/// <item>Last, the Spack install database (<see cref="SpackInstalls"/>), for the tool's
/// <see cref="SpackPackage"/>. Palace's own recipe installs with no view, so a Palace built exactly as
/// its documentation says is on no <c>PATH</c> and in no default directory, and without this route
/// every user who followed that recipe would have had to find a hashed prefix by hand.</item>
/// <item><b>A NAMED program that does not work is reported, never silently replaced</b> by one found
/// elsewhere: a user who named a Palace and got a different one has been overruled without being
/// told.</item>
/// <item>Every candidate is resolved to an absolute path once and started from there.</item>
/// <item><see cref="CandidateCommands"/> is the one place that names each executable; the directory
/// search derives from it, so emptying it disables every unprompted route.</item>
/// <item>The Settings preference lives above the firewall and arrives as <see cref="PreferredCommand"/>;
/// nothing in this assembly reads a UI preference.</item>
/// </list>
///
/// <para><b>Found and validated are separate answers.</b> The first program that STARTS and identifies
/// itself is the one found, whatever its version — the Settings row shows it and says it is not
/// validated, and <see cref="Check"/> refuses to run it naming the validated list (R-em3d6-2b). Walking
/// on past it to a validated copy elsewhere would run a different program from the one the user's own
/// shell runs, with nothing on screen saying so.</para>
/// </summary>
public sealed class SolverDiscovery
{
    // ── the three instances ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Palace. The candidate is Palace's own <c>palace</c> wrapper script, which finds the
    /// <c>palace-&lt;arch&gt;.bin</c> beside it. Every question is asked with <c>--serial</c>: without it
    /// the wrapper needs <c>mpirun</c> on <c>PATH</c>, which outside the environment Palace was built in
    /// it is not, and it then fails with exit 1 before Palace starts (F0 Q9). The bare binary accepts
    /// <c>--serial</c> too, so a user who names that instead is asked the same way (measured).
    /// </summary>
    public static SolverDiscovery Palace { get; } = Create(SolverTool.Palace);

    /// <summary>
    /// Gmsh. <c>gmsh --version</c> prints one line, e.g. <c>4.15.2-git</c> (F0 §6), and the numeric part
    /// is the version.
    ///
    /// <para><b>A newer minor version is REFUSED, not warned about.</b> R-em3d6-2b allows a warning only
    /// if F0 found the <c>.geo</c> constructs brief 7 uses unchanged across that minor version. F0 ran
    /// exactly one Gmsh (<c>docs/design/em-3d-f0-findings.md</c> §6) and compared it with no other, so
    /// there is no such finding to cite, and the rule is the strict one until there is.</para>
    /// </summary>
    public static SolverDiscovery Gmsh { get; } = Create(SolverTool.Gmsh);

    /// <summary>
    /// openEMS. It has no version flag: <c>--version</c> is an unknown option that aborts with SIGABRT
    /// (F0 Q11). <c>--help</c> prints the same banner and exits 0 (measured on the F0 install), so that
    /// is the question. The banner names openEMS's git hash and, below it, CSXCAD's — the library that
    /// parses the XML circuitRF will write — and both must match.
    ///
    /// <para><c>~/opt/openEMS/bin</c> is searched as well: it is the prefix upstream's own build script
    /// takes in its instructions and the one F0 installed into, and nothing puts it on <c>PATH</c>.</para>
    /// </summary>
    public static SolverDiscovery OpenEms { get; } = Create(SolverTool.OpenEms);

    /// <summary>
    /// A new instance for <paramref name="tool"/> with the shipped data and none of the shared
    /// instance's state — its own counters, no preference, the process's environment. Tests configure
    /// one of these rather than the shared instance, which every other test in the process also reads.
    ///
    /// <para><b>This is where each tool's validated list lives</b> (R-em3d6-2a), with the F0 finding
    /// that justifies it beside it.</para>
    /// </summary>
    internal static SolverDiscovery Create(SolverTool tool) => tool switch
    {
        SolverTool.Palace => new(
            SolverTool.Palace, "Palace", "CIRCUITRF_PALACE", ["palace"],
            versionArguments: ["--serial", "--version"],
            // docs/design/em-3d-f0-findings.md §6 — built from the v0.18.1 tag, which is changeset
            // 0dc74cd. Palace prints the hash and the schema, never "0.18.1" (Q9). A release tarball built
            // without its git history is not validated: it was never run, and nothing it prints could be
            // shown to identify it.
            validated: [new SolverValidatedVersion("0.18.1", "0dc74cd", "1-7-0")],
            extraDirectories: [], spackPackage: "palace"),

        SolverTool.Gmsh => new(
            SolverTool.Gmsh, "Gmsh", "CIRCUITRF_GMSH", ["gmsh"],
            versionArguments: ["--version"],
            // docs/design/em-3d-f0-findings.md §6 — the Homebrew bottle, OCC 7.9.3.
            validated: [new SolverValidatedVersion("4.15.2", "4.15.2", null)],
            extraDirectories: [], spackPackage: "gmsh"),

        _ => new(
            SolverTool.OpenEms, "openEMS", "CIRCUITRF_OPENEMS", ["openEMS"],
            versionArguments: ["--help"],
            // docs/design/em-3d-f0-findings.md §6 — 0.37.0-rc3 (git 67d3784), CSXCAD dcdb62b. A release
            // candidate: replace this entry when 0.37.0 final ships and testdata/em3d/f0 has been re-run.
            validated: [new SolverValidatedVersion("0.37.0-rc3", "67d3784", "dcdb62b")],
            extraDirectories: [Path.Combine(Home(), "opt", "openEMS", "bin")], spackPackage: "openems"),
    };

    /// <summary>The instance for <paramref name="tool"/>.</summary>
    public static SolverDiscovery For(SolverTool tool) => tool switch
    {
        SolverTool.Palace => Palace,
        SolverTool.Gmsh   => Gmsh,
        _                 => OpenEms,
    };

    /// <summary>
    /// The programs a setup's solver needs, in the order they are reported. Palace meshes through Gmsh
    /// (em-3d.md §6.1); openEMS takes circuitRF's own grid and needs nothing else.
    /// </summary>
    public static IReadOnlyList<SolverTool> ToolsFor(Em3dSolver solver) => solver switch
    {
        Em3dSolver.Palace  => [SolverTool.Palace, SolverTool.Gmsh],
        Em3dSolver.OpenEms => [SolverTool.OpenEms],
        Em3dSolver.Both    => [SolverTool.Palace, SolverTool.Gmsh, SolverTool.OpenEms],
        _                  => [],
    };

    /// <summary>What a 3D run in this build needs <paramref name="tool"/> to be able to do.</summary>
    public static IReadOnlySet<SolverCapability> CapabilitiesFor(SolverTool tool) => CapabilitiesFor(tool, null);

    /// <summary>
    /// What <paramref name="setup"/> needs <paramref name="tool"/> to be able to do: a driven solve always;
    /// brief-em3d-23 — wave ports when a port is one, the eigensolver for an eigenmode problem
    /// (R-em3d23-1c: asked here, so a build without it is refused before Gmsh starts).
    /// </summary>
    public static IReadOnlySet<SolverCapability> CapabilitiesFor(SolverTool tool, EmSetup? setup)
    {
        var set = new HashSet<SolverCapability>();
        if (tool != SolverTool.Palace) return set;
        set.Add(SolverCapability.DrivenLumpedPorts);
        if (setup is { HasWavePorts3D: true } && !setup.IsStatic3D) set.Add(SolverCapability.WavePorts);
        if (setup?.Problem3D == Engine.Em3d.Em3dProblemType.Eigenmode) set.Add(SolverCapability.Eigenmode);
        return set;
    }

    /// <summary>
    /// Every program <paramref name="solver"/> needs, checked the way a run checks it. The run, the
    /// Settings page and <c>explain</c> all call this, which is what makes them answer alike.
    /// </summary>
    public static IReadOnlyList<SolverReadiness> ReadinessFor(Em3dSolver solver) => ReadinessFor(solver, null);

    /// <inheritdoc cref="ReadinessFor(Em3dSolver)"/>
    public static IReadOnlyList<SolverReadiness> ReadinessFor(Em3dSolver solver, EmSetup? setup)
        => ToolsFor(solver).Select(t => For(t).Check(CapabilitiesFor(t, setup))).ToList();

    // ── per-instance data ────────────────────────────────────────────────────────────────────

    internal SolverDiscovery(
        SolverTool tool, string name, string environmentVariable, IReadOnlyList<string> candidates,
        IReadOnlyList<string> versionArguments, IReadOnlyList<SolverValidatedVersion> validated,
        IReadOnlyList<string> extraDirectories, string? spackPackage = null)
    {
        Tool                = tool;
        Name                = name;
        EnvironmentVariable = environmentVariable;
        CandidateCommands   = candidates;
        _versionArguments   = versionArguments;
        ValidatedVersions   = validated;
        SearchDirectories   = DefaultSearchDirectories(extraDirectories);
        SpackPackage        = spackPackage;
    }

    public SolverTool Tool { get; }

    /// <summary>The program's name as users see it. The solvers ARE named in user-facing text, unlike
    /// the Verilog-A compiler (em-3d.md §7.1, owner's call 2026-09-24).</summary>
    public string Name { get; }

    /// <summary>Names the program for one process, outranking <c>PATH</c> and beaten only by Settings.
    /// How CI, a test or a batch job points at one without writing anyone's preferences.</summary>
    public string EnvironmentVariable { get; }

    /// <summary>
    /// The bare names looked for on <c>PATH</c>. <b>The one place in circuitRF that names this tool's
    /// executable.</b> Settable so a test can point the whole mechanism at a stub; empty disables every
    /// unprompted route.
    /// </summary>
    public IReadOnlyList<string> CandidateCommands { get; set; }

    /// <summary>
    /// Where the candidates are looked for once <c>PATH</c> has failed — <c>~/.local/bin</c>,
    /// <c>/opt/homebrew/bin</c>, <c>/usr/local/bin</c> and the tool's own default prefix. Empty on
    /// Windows, where a GUI process does inherit the user's <c>PATH</c>. Settable for tests.
    /// </summary>
    public IReadOnlyList<string> SearchDirectories { get; set; }

    /// <summary>
    /// The tool's Spack package name, looked up in every Spack install database once the directories
    /// have failed; each installed prefix's <c>bin/&lt;candidate&gt;</c> is a candidate, most recently
    /// installed first. Null disables the route. Settable for tests.
    /// </summary>
    public string? SpackPackage { get; set; }

    /// <summary>The Spack install-tree roots looked in. Settable for tests.</summary>
    public IReadOnlyList<string> SpackRoots { get; set; } = SpackInstalls.DefaultRoots;

    /// <summary>The versions this tool is validated at — a short list, data in one place (R-em3d6-2a).</summary>
    public IReadOnlyList<SolverValidatedVersion> ValidatedVersions { get; }

    /// <summary>The path named in Settings, or null. Installed by <c>src/Ui</c>
    /// (<c>Em3dSolverPathInstaller</c>); read at the moment it is needed, so a changed setting is seen.</summary>
    public Func<string?>? PreferredCommand { get; set; }

    /// <summary>Reads an environment variable — <see cref="EnvironmentVariable"/>, <c>PATH</c>,
    /// <c>PATHEXT</c>. A seam so a test can give one instance its own environment without touching the
    /// process's, which every other test shares.</summary>
    internal Func<string, string?> ReadEnvironment { get; set; } = Environment.GetEnvironmentVariable;

    /// <summary>Where the capability cache lives; null means <c>UserStateDirectory/em3d</c>.</summary>
    internal string? CacheDirectory { get; set; }

    /// <summary>How many version probes this instance has started. Gate 5 reads the other one.</summary>
    internal long VersionProbesRun => Interlocked.Read(ref _versionProbes);

    /// <summary>How many capability probes this instance has started — zero on a cache hit.</summary>
    internal long CapabilityProbesRun => Interlocked.Read(ref _capabilityProbes);

    private readonly IReadOnlyList<string> _versionArguments;
    private long _versionProbes;
    private long _capabilityProbes;

    private static readonly TimeSpan VersionTimeout    = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan CapabilityTimeout = TimeSpan.FromSeconds(60);

    // ── the walk ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The program to use, or null with <paramref name="rejected"/> explaining every candidate that was
    /// tried (R-em3d6-1c). A found program may still be unvalidated — see
    /// <see cref="SolverInstallation.Validated"/>. Not memoised: the version is read before every run
    /// (R-em3d6-2b), and it costs one short process.
    /// </summary>
    public SolverInstallation? Find(out IReadOnlyList<string> rejected)
    {
        var notes = new List<string>();
        rejected  = notes;

        if (PreferredCommand?.Invoke()?.Trim() is { Length: > 0 } preferred)
        {
            if (TryProbe(preferred, SolverHowFound.Settings, "set in Settings", out var chosen, out string? why))
                return chosen;
            notes.Add($"the {Name} set in Settings ('{preferred}'): {why}");
            return null;
        }

        if (ReadEnvironment(EnvironmentVariable)?.Trim() is { Length: > 0 } fromEnv)
        {
            if (TryProbe(fromEnv, SolverHowFound.Environment, $"named by {EnvironmentVariable}", out var chosen, out string? why))
                return chosen;
            notes.Add($"the {Name} named by {EnvironmentVariable} ('{fromEnv}'): {why}");
            return null;
        }

        foreach (string command in CandidateCommands)
        {
            if (string.IsNullOrWhiteSpace(command)) continue;
            foreach (string candidate in ResolveOnPath(command.Trim()))
            {
                if (TryProbe(candidate, SolverHowFound.Path, "found on PATH", out var chosen, out string? why))
                    return chosen;
                notes.Add($"'{candidate}': {why}");
            }
        }

        foreach (string candidate in InSearchDirectories())
        {
            if (TryProbe(candidate, SolverHowFound.DefaultDirectory, $"found at {candidate}", out var chosen, out string? why))
                return chosen;
            notes.Add($"'{candidate}': {why}");
        }

        foreach (string candidate in InSpack())
        {
            if (TryProbe(candidate, SolverHowFound.Spack, $"found in a Spack installation at {candidate}", out var chosen, out string? why))
                return chosen;
            notes.Add($"'{candidate}': {why}");
        }

        return null;
    }

    /// <summary>
    /// Asks the program at <paramref name="path"/> what it is. Public so the Settings row can report a
    /// typed path without going through <see cref="Find"/>'s precedence.
    /// </summary>
    public bool TryProbe(string path, SolverHowFound howFound, string howFoundText,
                         out SolverInstallation? installation, out string? why)
    {
        installation = null;
        why          = null;

        string absolute;
        try { absolute = Path.GetFullPath(path); }
        catch (Exception e) when (e is ArgumentException or PathTooLongException or NotSupportedException)
        {
            why = "that is not a usable path";
            return false;
        }
        if (!File.Exists(absolute))
        {
            why = "there is no file there";
            return false;
        }

        Interlocked.Increment(ref _versionProbes);
        var run = RunProbe(absolute, _versionArguments, workingDirectory: null, VersionTimeout);
        if (run.Failure is { } failure) { why = failure; return false; }

        if (ParseVersion(Tool, run.Output) is not { } parsed)
        {
            string first = FirstLine(run.Output);
            why = first.Length == 0
                ? "it started but identified itself with nothing"
                : $"it identified itself as '{first}', which is not {Name}";
            return false;
        }

        string? release = ValidatedVersions
            .FirstOrDefault(v => v.Identity == parsed.Version && (v.Companion is null || v.Companion == parsed.Companion))
            ?.Release;
        installation = new SolverInstallation(Tool, absolute, parsed.Version, parsed.Companion, parsed.Banner,
                                              howFound, howFoundText, release);
        return true;
    }

    // ── the answer a run, Settings and explain share ─────────────────────────────────────────

    /// <summary>
    /// Finds the program, checks its version, and probes the capabilities a run needs — in that order,
    /// stopping at the first refusal. An unvalidated program is not probed: what its build can do is
    /// moot when it will not be run, and a probe configuration is itself only known to mean what it
    /// says at a validated version.
    /// </summary>
    public SolverReadiness Check(IReadOnlySet<SolverCapability> required)
        => Check(required, OperatingSystem.IsWindows());

    internal SolverReadiness Check(IReadOnlySet<SolverCapability> required, bool windows)
    {
        var found = Find(out var rejected);
        if (found is null)
            return new SolverReadiness(Tool, Name, null, rejected, [], DescribeFailure(rejected, windows));
        if (!found.Validated)
            return new SolverReadiness(Tool, Name, found, rejected, [], DescribeUnvalidated(found));

        var verdicts = required.Order().Select(c => Probe(found, c)).ToList();
        string? refusal = verdicts.FirstOrDefault(v => !v.Available) is { } missing
            ? DescribeMissingCapability(found, missing)
            : null;
        return new SolverReadiness(Tool, Name, found, rejected, verdicts, refusal);
    }

    /// <summary>
    /// Whether the program can do <paramref name="capability"/>, from the cache when the same binary has
    /// been asked before (R-em3d6-3b). Only Palace has anything to probe in this build.
    /// </summary>
    public SolverCapabilityVerdict Probe(SolverInstallation installation, SolverCapability capability)
    {
        ArgumentNullException.ThrowIfNull(installation);
        if (Tool != SolverTool.Palace)
            return new SolverCapabilityVerdict(capability, true, $"{Name} has no build options circuitRF depends on", false);

        string? stamp = Stamp(installation.Path);
        if (stamp is not null && CapabilityCache.Read(CacheFile, installation.Path, stamp, capability) is { } hit)
            return hit with { FromCache = true };

        Interlocked.Increment(ref _capabilityProbes);
        var (verdict, definitive) = RunPalaceProbe(installation.Path, capability);

        // Only a definite YES is kept. A probe that timed out or could not start says nothing about the
        // build, and neither does every failed dry run: exit 134 is also what an MPI, environment or
        // temp-directory problem ends in, and a cached "no" would go on telling the user to rebuild
        // Palace after they had fixed the real cause. A failing probe costs 0.15 s to ask again.
        if (definitive && verdict.Available && stamp is not null)
            CapabilityCache.Write(CacheFile, installation.Path, stamp, verdict);
        return verdict;
    }

    private string CacheFile
        => Path.Combine(CacheDirectory ?? UserStateDirectory.SubDir("em3d"), "solver-capabilities.json");

    // ── refusals ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The refusal when nothing usable was found (R-em3d6-4d). It names, in this order: the program; how
    /// to point circuitRF at one already installed; the manual-install section. On Windows, Palace's
    /// also says it does not run natively there and names openEMS, which does — overview §4's honesty
    /// rule: say what this machine can do rather than promise what is not built.
    /// </summary>
    public string DescribeFailure(IReadOnlyList<string> rejected) => DescribeFailure(rejected, OperatingSystem.IsWindows());

    /// <inheritdoc cref="DescribeFailure(IReadOnlyList{string})"/>
    /// <param name="windows">The platform, injected so the Windows text is testable anywhere.</param>
    public string DescribeFailure(IReadOnlyList<string> rejected, bool windows)
    {
        var sb = new StringBuilder();
        sb.Append($"{Name} was not found, so this 3D setup cannot run. ");
        sb.Append($"To use a {Name} that is already installed, name it in Settings ▸ 3D EM, or set the " +
                  $"environment variable {EnvironmentVariable} to its full path. ");
        sb.Append($"To install it, follow “{ManualInstallSection}” in the EM Setup reference ({ManualInstallPage}).");
        if (windows && Tool == SolverTool.Palace)
            sb.Append(" Palace does not run natively on Windows, and running it through the Linux subsystem is " +
                      "not part of this version of circuitRF. openEMS runs natively on Windows and is the 3D " +
                      "solver this machine can use: set the setup's Solver3D to OpenEms.");
        if (rejected.Count > 0)
            sb.Append(" Tried: ").Append(string.Join("; ", rejected)).Append('.');
        return sb.ToString();
    }

    /// <summary>
    /// The Settings row's status line (R-em3d6-4a): what was found, where, which version, validated or
    /// not, and how it was found — or, when nothing was, every candidate tried and why.
    /// </summary>
    public string DescribeForSettings(SolverInstallation? found, IReadOnlyList<string> rejected)
    {
        if (found is null)
            return rejected.Count == 0
                ? $"Not found: nothing named {string.Join(" or ", CandidateCommands)} on PATH, in the default directories " +
                  "or in a Spack installation."
                : $"Not found. Tried: {string.Join("; ", rejected)}.";

        string how = found.HowFound switch
        {
            SolverHowFound.Settings    => "set here",
            SolverHowFound.Environment => $"named by {EnvironmentVariable}",
            SolverHowFound.Path        => "found on PATH",
            SolverHowFound.Spack       => "found in a Spack installation",
            _                          => "found in a default directory",
        };
        string validity = found.Validated
            ? "validated"
            : $"NOT validated — a 3D run will refuse it; circuitRF runs {string.Join(", ", ValidatedVersions.Select(v => v.ToString()))}";
        return $"{found.DescribeVersion()}, {validity}. {found.Path} ({how}).";
    }

    /// <summary>The refusal for a program found at a version circuitRF has not validated (R-em3d6-2b).</summary>
    public string DescribeUnvalidated(SolverInstallation installation)
    {
        string list = string.Join(", ", ValidatedVersions.Select(v => v.ToString()));
        string why = Tool switch
        {
            SolverTool.Palace => "Palace's configuration changes meaning between versions, and a key read differently " +
                                 "produces a plausible wrong answer rather than an error.",
            SolverTool.Gmsh   => "circuitRF's geometry scripts have been verified against that version only.",
            _                 => "circuitRF writes openEMS's XML and reads its probe files as that version does, " +
                                 "and has verified no other.",
        };
        return $"{Name} at '{installation.Path}' ({installation.HowFoundText}) reports version " +
               $"{installation.DescribeVersion()}, which circuitRF has not validated, so this 3D setup will not run. " +
               $"Validated: {list}. {why} Point circuitRF at a validated {Name} in Settings ▸ 3D EM or with " +
               $"{EnvironmentVariable}; “{ManualInstallSection}” in the EM Setup reference ({ManualInstallPage}) " +
               "says how each was installed.";
    }

    /// <summary>The refusal for a build that lacks something the setup needs (R-em3d6-3c).</summary>
    public string DescribeMissingCapability(SolverInstallation installation, SolverCapabilityVerdict verdict)
    {
        var (what, option) = Describe(verdict.Capability);
        return $"{Name} at '{installation.Path}' cannot run {what}, which this 3D setup needs: {verdict.Detail} " +
               $"{option}";
    }

    /// <summary>What a capability is, in words, and the build option that provides it.</summary>
    internal static (string What, string BuildOption) Describe(SolverCapability capability) => capability switch
    {
        SolverCapability.Eigenmode => ("eigenmode solves",
            EigensolverBuildOption),
        SolverCapability.WavePorts => ("wave ports",
            "A wave port's mode comes from Palace's eigensolver, and its impedance — which the S-parameters are " +
            "renormalised from — needs GSLIB. " + EigensolverBuildOption + " GSLIB is the +gslib variant, also on by " +
            "default; a build configured ~gslib cannot report a wave port's impedance."),
        // Every Palace build can do a driven solve with lumped ports — no build option turns it on, so a
        // build that fails this probe is broken or incomplete rather than configured without it.
        _ => ("driven solves with lumped ports",
              "Every Palace build provides this — no build option enables it — so this build is incomplete or " +
              "broken. Rebuild Palace; its default build is enough."),
    };

    /// <summary>
    /// brief-em3d-23 R-em3d23-1c — the build variant that provides the eigensolver, and the route to a build
    /// that has it. At the pinned version (0.18.1) EVERY build has one: Palace's CMake refuses to configure
    /// without SLEPc or ARPACK (src/Design/RESOLVED.md §brief-em3d-23), so this refusal names a build that is
    /// broken or incomplete rather than one configured without the feature.
    /// </summary>
    public const string EigensolverBuildOption =
        "Palace's eigensolver is SLEPc (the +slepc variant, on by default) or ARPACK (+arpack); rebuild Palace with " +
        "either, as “" + ManualInstallSection + "” in the EM Setup reference (" + ManualInstallPage + ") describes.";

    /// <summary>
    /// R-em3d23-1b — how each capability is asked, in words, for <c>explain</c>: a dry run where the schema
    /// check can answer, a one-element solve where only running can.
    /// </summary>
    public static string ProbeKind(SolverCapability capability) => capability switch
    {
        SolverCapability.WavePorts =>
            "a dry run of a driven setup with a wave port, then a one-element electrostatic solve with a field probe " +
            "(GSLIB is only reached by running; a dry run of a voltage path passes without it)",
        SolverCapability.Eigenmode =>
            "a dry run of an eigenmode setup (at 0.18.1 no Palace build lacks an eigensolver, so the dry run's " +
            "schema check is the whole question)",
        _ => "a dry run of a driven setup with a lumped port",
    };

    /// <summary>The manual-install section's title and page, as the refusals cite them.</summary>
    public const string ManualInstallSection = "Installing the 3D solvers by hand";
    public const string ManualInstallPage    = "reference/em-setup.html#install-3d-solvers";

    // ── the Palace capability probe ──────────────────────────────────────────────────────────

    /// <summary>
    /// F0 Q9: Palace's <c>--version</c> says nothing about its build, and its only other flag is
    /// <c>--dry-run</c>, which validates a configuration against the schema compiled into the binary
    /// AND opens the mesh it names — so the probe is a dry run of a smallest-possible driven setup with
    /// one lumped port, on a one-tetrahedron mesh circuitRF writes itself, in a temporary directory
    /// deleted afterwards. Measured on the F0 install: 0.15 s, exit 0 and the line
    /// <c>Dry-run: No errors detected</c>; a missing mesh or an unknown key exits 134.
    /// </summary>
    private (SolverCapabilityVerdict Verdict, bool Definitive) RunPalaceProbe(string palace, SolverCapability capability)
    {
        string dir = Path.Combine(Path.GetTempPath(), "crf-palace-probe-" + Guid.NewGuid().ToString("N")[..12]);
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "probe.json"), ProbeConfig(capability));
            File.WriteAllText(Path.Combine(dir, "probe.msh"), ProbeMesh);

            var run = RunProbe(palace, ["--serial", "--dry-run", "probe.json"], dir, CapabilityTimeout);
            if (run.Failure is { } failure)
                return (new SolverCapabilityVerdict(capability, false, $"the probe did not complete ({failure}).", false), false);

            string what = capability switch
            {
                SolverCapability.WavePorts => "a driven setup with a wave port",
                SolverCapability.Eigenmode => "an eigenmode setup",
                _                          => "a driven setup with a lumped port",
            };
            bool ok = run.ExitCode == 0 && run.Output.Contains("Dry-run: No errors detected", StringComparison.Ordinal);
            if (!ok)
                return (new SolverCapabilityVerdict(capability, false,
                    $"a dry run of {what} failed (exit {run.ExitCode}): {TellingLine(run.Output)}.", false), true);
            if (capability != SolverCapability.WavePorts)
                return (new SolverCapabilityVerdict(capability, true, $"a dry run of {what} found no errors.", false), true);

            // R-em3d23-1b — GSLIB is reached only by running: a one-element electrostatic solve with a field
            // probe, which a ~gslib build refuses as it starts (interpolator.cpp: "InterpolationOperator class
            // requires MFEM_USE_GSLIB!"). 0.17 s on the F0 install.
            File.WriteAllText(Path.Combine(dir, "gslib.json"), GslibProbeConfig);
            var solve = RunProbe(palace, ["--serial", "gslib.json"], dir, CapabilityTimeout);
            if (solve.Failure is { } solveFailure)
                return (new SolverCapabilityVerdict(capability, false, $"the one-element solve did not complete ({solveFailure}).", false), false);
            bool gslib = solve.ExitCode == 0 && File.Exists(Path.Combine(dir, "postpro", "probe-E.csv"));
            return (new SolverCapabilityVerdict(capability, gslib, gslib
                ? $"a dry run of {what} found no errors, and a one-element solve with a field probe ran (GSLIB is present)."
                : $"a dry run of {what} passed, but a one-element solve with a field probe failed (exit {solve.ExitCode}): " +
                  $"{TellingLine(solve.Output)}.", false), true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (new SolverCapabilityVerdict(capability, false, $"the probe could not be staged ({e.Message}).", false), false);
        }
        finally
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    private static string ProbeConfig(SolverCapability capability) => capability switch
    {
        SolverCapability.WavePorts => """
             {
               "Problem": { "Type": "Driven", "Output": "postpro" },
               "Model": { "Mesh": "probe.msh", "L0": 1.0e-3 },
               "Domains": { "Materials": [ { "Attributes": [1], "Permeability": 1.0, "Permittivity": 1.0 } ] },
               "Boundaries": {
                 "PEC": { "Attributes": [3] },
                 "WavePort": [ { "Index": 1, "Attributes": [2], "Mode": 1, "Offset": 0.0, "Excitation": 1,
                                 "VoltagePath": [ [0.1, 0.1, 0.0], [0.2, 0.2, 0.0] ] } ]
               },
               "Solver": { "Order": 1, "Driven": { "Samples": [ { "Type": "Point", "Freq": [1.0] } ] } }
             }
             """,
        SolverCapability.Eigenmode => """
             {
               "Problem": { "Type": "Eigenmode", "Output": "postpro" },
               "Model": { "Mesh": "probe.msh", "L0": 1.0e-3 },
               "Domains": { "Materials": [ { "Attributes": [1], "Permeability": 1.0, "Permittivity": 1.0 } ] },
               "Boundaries": { "PEC": { "Attributes": [2, 3] } },
               "Solver": { "Order": 1, "Eigenmode": { "N": 1, "Target": 1.0 } }
             }
             """,
        _ => """
             {
               "Problem": { "Type": "Driven", "Output": "postpro" },
               "Model": { "Mesh": "probe.msh", "L0": 1.0e-3 },
               "Domains": { "Materials": [ { "Attributes": [1], "Permeability": 1.0, "Permittivity": 1.0 } ] },
               "Boundaries": {
                 "PEC": { "Attributes": [3] },
                 "LumpedPort": [ { "Index": 1, "R": 50.0, "Excitation": true, "Attributes": [2], "Direction": "+X" } ]
               },
               "Solver": { "Order": 1, "Driven": { "Samples": [ { "Type": "Point", "Freq": [1.0] } ] } }
             }
             """,
    };

    /// <summary>
    /// R-em3d23-1b — the one-element solve that reaches GSLIB: the probe tetrahedron as an electrostatic
    /// problem (face 2 a terminal, the rest the natural boundary, so one unknown is free) with a field probe
    /// inside it. A probe is interpolated through GSLIB, and Palace builds its interpolator before it solves.
    /// </summary>
    private const string GslibProbeConfig = """
        {
          "Problem": { "Type": "Electrostatic", "Output": "postpro" },
          "Model": { "Mesh": "probe.msh", "L0": 1.0e-3 },
          "Domains": { "Materials": [ { "Attributes": [1], "Permeability": 1.0, "Permittivity": 1.0 } ],
                       "Postprocessing": { "Probe": [ { "Index": 1, "Center": [0.2, 0.2, 0.2] } ] } },
          "Boundaries": { "Terminal": [ { "Index": 1, "Attributes": [2] } ] },
          "Solver": { "Order": 2, "Electrostatic": {} }
        }
        """;

    /// <summary>One tetrahedron, Gmsh MSH 2.2 ASCII: volume 1, one face as the port (2), three as PEC (3).</summary>
    private const string ProbeMesh =
        "$MeshFormat\n2.2 0 8\n$EndMeshFormat\n" +
        "$Nodes\n4\n1 0 0 0\n2 1 0 0\n3 0 1 0\n4 0 0 1\n$EndNodes\n" +
        "$Elements\n5\n" +
        "1 2 2 2 2 1 3 2\n2 2 2 3 3 1 2 4\n3 2 2 3 3 1 4 3\n4 2 2 3 3 2 3 4\n" +
        "5 4 2 1 1 1 2 3 4\n$EndElements\n";

    /// <summary>The line of a failed dry run worth quoting: Palace's own validation message when there is
    /// one, else the last non-empty line.</summary>
    private static string TellingLine(string output)
    {
        var lines = output.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        string? line = lines.FirstOrDefault(l => l.Contains("validation failed", StringComparison.OrdinalIgnoreCase)
                                              || l.Contains("error", StringComparison.OrdinalIgnoreCase));
        line ??= lines.LastOrDefault() ?? "(no output)";
        return line.Length <= 300 ? line : line[..300];
    }

    // ── version parsing ──────────────────────────────────────────────────────────────────────

    internal sealed record ParsedVersion(string Version, string? Companion, string Banner);

    private static readonly Regex PalaceVersion  = new(@"^\s*Palace version:\s*(\S+)\s*$", RegexOptions.Multiline);
    private static readonly Regex PalaceSchema   = new(@"^\s*Schema version:\s*(\S+)\s*$", RegexOptions.Multiline);
    private static readonly Regex GmshVersion    = new(@"^\s*(\d+\.\d+(?:\.\d+)?)(\S*)\s*$", RegexOptions.Multiline);
    private static readonly Regex OpenEmsVersion = new(@"^.*\bopenEMS\b.*--\s*version\s+(\S+)\s*$", RegexOptions.Multiline);
    private static readonly Regex CsxcadVersion  = new(@"^\s*CSXCAD\s*--\s*Version:\s*(\S+)\s*$", RegexOptions.Multiline);

    /// <summary>
    /// The version out of what <paramref name="tool"/> printed, or null when the output is not that
    /// tool's. Tested against the exact banners F0 recorded (<c>testdata/em3d/f0/banners/</c>).
    /// Deliberately strict about the leading words: that is what makes "something else is at this path"
    /// a detectable answer rather than a plausible one.
    /// </summary>
    internal static ParsedVersion? ParseVersion(SolverTool tool, string output)
    {
        output = output.Replace("\r", "");
        switch (tool)
        {
            case SolverTool.Palace:
            {
                var v = PalaceVersion.Match(output);
                if (!v.Success) return null;
                var s = PalaceSchema.Match(output);
                return new ParsedVersion(v.Groups[1].Value, s.Success ? s.Groups[1].Value : null,
                                         s.Success ? $"{v.Value.Trim()}, {s.Value.Trim()}" : v.Value.Trim());
            }
            case SolverTool.Gmsh:
            {
                // Gmsh prints its version and nothing else; the first non-empty line must BE it.
                string first = FirstLine(output);
                var v = GmshVersion.Match(first);
                return v.Success ? new ParsedVersion(v.Groups[1].Value, null, first) : null;
            }
            default:
            {
                var v = OpenEmsVersion.Match(output);
                if (!v.Success) return null;
                var c = CsxcadVersion.Match(output);
                string banner = v.Value.Trim().TrimStart('|').Trim();
                return new ParsedVersion(v.Groups[1].Value, c.Success ? c.Groups[1].Value : null,
                                         c.Success ? $"{banner}; {c.Value.Trim()}" : banner);
            }
        }
    }

    // ── process, PATH, directories ───────────────────────────────────────────────────────────

    private sealed record ProbeRun(int ExitCode, string Output, string? Failure);

    /// <summary>Runs one probe through <see cref="Em3dProcessLauncher"/>, as a PROBE, reading both streams
    /// concurrently so a chatty program cannot fill a pipe and hang.</summary>
    private static ProbeRun RunProbe(string path, IReadOnlyList<string> arguments, string? workingDirectory, TimeSpan timeout)
    {
        try
        {
            var psi = new ProcessStartInfo(path)
            {
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
            };
            foreach (string a in arguments) psi.ArgumentList.Add(a);
            if (workingDirectory is not null) psi.WorkingDirectory = workingDirectory;

            using var p = Em3dProcessLauncher.Start(psi, Em3dProcessKind.Probe);
            if (p is null) return new ProbeRun(-1, "", "it could not be started");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeout))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
                return new ProbeRun(-1, "", $"it did not answer within {timeout.TotalSeconds:0} s");
            }
            p.WaitForExit();
            return new ProbeRun(p.ExitCode, stdout.GetAwaiter().GetResult() + "\n" + stderr.GetAwaiter().GetResult(), null);
        }
        catch (Exception e)
        {
            return new ProbeRun(-1, "", $"it could not be started ({e.Message})");
        }
    }

    /// <summary>Every absolute path a bare command resolves to on <c>PATH</c>, in order.</summary>
    private IEnumerable<string> ResolveOnPath(string command)
    {
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
        {
            yield return Path.GetFullPath(command);
            yield break;
        }

        string[] exts = OperatingSystem.IsWindows()
            ? (ReadEnvironment("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';',
                  StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [""];

        foreach (string dir in (ReadEnvironment("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (string ext in exts)
            {
                string candidate;
                try { candidate = Path.Combine(dir.Trim(), command + ext); }
                catch (ArgumentException) { continue; }   // a malformed PATH entry is not fatal
                if (Exists(candidate)) yield return candidate;
            }
        }
    }

    private IEnumerable<string> InSearchDirectories()
    {
        string[] exts = OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat"] : [""];
        foreach (string command in CandidateCommands)
        {
            if (string.IsNullOrWhiteSpace(command)) continue;
            if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar)) continue;
            foreach (string directory in SearchDirectories)
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                foreach (string ext in exts)
                {
                    string candidate;
                    try { candidate = Path.Combine(directory, command.Trim() + ext); }
                    catch (ArgumentException) { continue; }
                    if (Exists(candidate)) yield return candidate;
                }
            }
        }
    }

    /// <summary>Each installed Spack prefix's <c>bin/&lt;candidate&gt;</c> for <see cref="SpackPackage"/>.
    /// Derived from <see cref="CandidateCommands"/>, so emptying that disables this route too.</summary>
    private IEnumerable<string> InSpack()
    {
        if (SpackPackage is not { Length: > 0 } package) yield break;
        var commands = CandidateCommands
            .Where(c => !string.IsNullOrWhiteSpace(c)
                        && !c.Contains(Path.DirectorySeparatorChar) && !c.Contains(Path.AltDirectorySeparatorChar))
            .Select(c => c.Trim()).ToList();
        if (commands.Count == 0) yield break;
        foreach (var install in SpackInstalls.Find(package, SpackRoots))
            foreach (string command in commands)
            {
                string candidate;
                try { candidate = Path.Combine(install.Prefix, "bin", command); }
                catch (ArgumentException) { continue; }
                if (Exists(candidate)) yield return candidate;
            }
    }

    private static bool Exists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    private static IReadOnlyList<string> DefaultSearchDirectories(IReadOnlyList<string> extra)
    {
        if (OperatingSystem.IsWindows()) return [];
        string home = Home();
        var dirs = new List<string>();
        if (home.Length > 0) dirs.Add(Path.Combine(home, ".local", "bin"));
        dirs.Add("/opt/homebrew/bin");
        dirs.Add("/usr/local/bin");
        dirs.AddRange(extra.Where(d => d.Length > 0 && Path.IsPathRooted(d)));
        return dirs;
    }

    private static string Home() => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// What identifies this binary for the cache: size and write time of the file and of what it resolves
    /// to, and for Palace also of every <c>palace-*.bin</c> beside it — the wrapper script that is the
    /// candidate does not change when Palace is rebuilt in place, the binary it launches does. Null when
    /// the file cannot be read, which means "do not cache".
    /// </summary>
    private string? Stamp(string path)
    {
        try
        {
            var parts = new List<string>();
            void Add(string p)
            {
                var fi = new FileInfo(p);
                parts.Add($"{fi.FullName}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}");
                if (fi.LinkTarget is not null && fi.ResolveLinkTarget(returnFinalTarget: true) is FileInfo target)
                    parts.Add($"{target.FullName}|{target.Length}|{target.LastWriteTimeUtc.Ticks}");
            }
            Add(path);
            if (Tool == SolverTool.Palace)
            {
                // Beside the named file AND beside what it links to: a ~/.local/bin/palace symlink's own
                // folder holds no .bin, and the wrapper it reaches keeps a normalised timestamp.
                var dirs = new SortedSet<string>(StringComparer.Ordinal);
                if (Path.GetDirectoryName(path) is { } dir) dirs.Add(dir);
                var fi = new FileInfo(path);
                if (fi.LinkTarget is not null && fi.ResolveLinkTarget(returnFinalTarget: true) is FileInfo target
                    && target.DirectoryName is { } targetDir)
                    dirs.Add(targetDir);
                foreach (string d in dirs)
                    foreach (string bin in Directory.EnumerateFiles(d, "palace-*.bin").Order(StringComparer.Ordinal))
                        Add(bin);
            }
            return string.Join(";", parts);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    private static string FirstLine(string s)
    {
        string line = s.Split('\n', '\r').FirstOrDefault(l => l.Trim().Length > 0)?.Trim() ?? "";
        return line.Length <= 200 ? line : line[..200];
    }

    // ── the capability cache ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>solver-capabilities.json</c> under the per-user state directory: one entry per binary and
    /// capability, keyed on the binary's stamp so a changed binary is asked again (R-em3d6-3b). A cache
    /// that cannot be read is treated as empty, and one that cannot be written costs a re-probe — never
    /// a refusal.
    /// </summary>
    private static class CapabilityCache
    {
        private static readonly Lock Gate = new();
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

        private sealed class Entry
        {
            public string Path       { get; set; } = "";
            public string Stamp      { get; set; } = "";
            public string Capability { get; set; } = "";
            public bool   Available  { get; set; }
            public string Detail     { get; set; } = "";
        }

        private sealed class FileShape
        {
            public List<Entry> Entries { get; set; } = [];
        }

        public static SolverCapabilityVerdict? Read(string file, string path, string stamp, SolverCapability capability)
        {
            lock (Gate)
            {
                var e = Load(file).Entries.FirstOrDefault(x => x.Path == path && x.Capability == capability.ToString());
                return e is not null && e.Stamp == stamp
                    ? new SolverCapabilityVerdict(capability, e.Available, e.Detail, true)
                    : null;
            }
        }

        public static void Write(string file, string path, string stamp, SolverCapabilityVerdict verdict)
        {
            lock (Gate)
            {
                var shape = Load(file);
                shape.Entries.RemoveAll(x => x.Path == path && x.Capability == verdict.Capability.ToString());
                shape.Entries.Add(new Entry
                {
                    Path = path, Stamp = stamp, Capability = verdict.Capability.ToString(),
                    Available = verdict.Available, Detail = verdict.Detail,
                });
                try
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(file)!);
                    AtomicFile.WriteAllText(file, JsonSerializer.Serialize(shape, Json));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            }
        }

        private static FileShape Load(string file)
        {
            try
            {
                return File.Exists(file)
                    ? JsonSerializer.Deserialize<FileShape>(File.ReadAllText(file), Json) ?? new FileShape()
                    : new FileShape();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                return new FileShape();
            }
        }
    }
}
