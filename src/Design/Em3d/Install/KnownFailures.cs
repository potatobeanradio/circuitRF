using System.Text.RegularExpressions;

namespace CircuitRF.Design.Em3d.Install;

/// <summary>
/// One failure circuitRF's own installs have met, recognised by a pattern over the upstream tool's
/// VERBATIM output (brief-em3d-24 R-em3d24-3b).
/// </summary>
/// <param name="Id">What a recipe lists it by.</param>
/// <param name="Pattern">Matched against the failing step's own output lines.</param>
/// <param name="Remedy">What fixed it, in a sentence.</param>
/// <param name="Source">Which install met it, when, and on what machine — a failure with no source is a
/// guess, and the table is learned, not guessed (em-3d.md §7.2).</param>
/// <param name="Transient">R-em3d24-3c: the one kind that earns "retrying often clears this".</param>
public sealed record KnownFailure(string Id, Regex Pattern, string Remedy, string Source, bool Transient = false);

/// <summary>
/// The table of failures the assistant can name (em-3d.md §7.2). Seeded from F0's macOS install log
/// (<c>testdata/em3d/f0/README.md</c> §Install) and grown by the Linux container runs logged there.
///
/// <para><b>It explains; it never acts.</b> A match adds the remedy to the report. Nothing retries,
/// switches recipe or patches anything (R-em3d24-3a) — the recipe already pre-empts every entry here that
/// a recipe can, and the table stays because a user's OWN Palace install can meet them too.</para>
/// </summary>
public static class KnownFailures
{
    private const string F0Log = "testdata/em3d/f0/README.md §Install";

    public static IReadOnlyList<KnownFailure> All { get; } =
    [
        new("spack-ssl-certificates",
            new Regex(@"CERTIFICATE_VERIFY_FAILED", RegexOptions.Compiled),
            "Spack ran on a Python whose certificate store is empty (a python.org framework Python whose " +
            "certificate step was never run). Point SPACK_PYTHON at a Python that has certificates — the " +
            "recipe uses Homebrew's.",
            $"F0, macOS 27.0 on an Apple M4, 2026-09-24 ({F0Log}, row 4)"),

        new("spack-builtin-repo-too-old",
            new Regex(@"No such variant 'gkrand'", RegexOptions.Compiled),
            "Spack's builtin package repository is older than Palace's recipe needs (it lacks spack-packages " +
            "PR 4000). Update it to a commit that has it — the recipe pins one with " +
            "'spack repo update --commit … builtin'.",
            $"F0, macOS 27.0 on an Apple M4, 2026-09-24 ({F0Log}, row 5)"),

        new("spack-target-emits-sve",
            new Regex(@"Illegal instruction|The PETSc test program compiled, but CMake could not execute it", RegexOptions.Compiled),
            "Spack detected the processor as a target whose flags let GCC emit SVE instructions, which this " +
            "processor does not execute (an Apple M4 detected as 'm4'). Cap the target — 'packages: all: " +
            "require: target=m3' — and rebuild every package.",
            $"F0, macOS 27.0 on an Apple M4, 2026-09-24 ({F0Log}, row 7)"),

        new("autotools-path-has-space",
            new Regex(@"unsafe srcdir value|unsafe absolute working directory name", RegexOptions.Compiled),
            "autotools refuses to build in a directory whose path contains a space. Install into a path with " +
            "no spaces; circuitRF's own installs use ~/.circuitRF/solvers when its state directory has one.",
            "circuitRF's spaced-path trial, macOS 27.0 on an Apple M4, 2026-09-25 (Spack v1.2.2 building " +
            "gmake under a directory named 'space test')"),

        new("spack-variants-dropped",
            new Regex(@"cannot run (?:wave ports|eigenmode solves)", RegexOptions.Compiled),
            "Spack's concretizer turns off Palace's +slepc, +gslib and +sundials variants without a message " +
            "unless the spec states them. State every variant in the spec, as the recipe does.",
            $"F0, macOS 27.0 on an Apple M4, 2026-09-24 ({F0Log}, row 6)"),

        new("mirror-checksum-transient",
            new Regex(@"sha256 checksum failed|ChecksumError", RegexOptions.Compiled),
            "A dependency's source failed its checksum while being fetched from a mirror.",
            "em-3d.md §7.2: reported upstream as a transient failure of a dependency mirror",
            Transient: true),
    ];

    /// <summary>The first entry whose pattern matches any of <paramref name="lines"/>, or null.</summary>
    public static KnownFailure? Match(IEnumerable<string> lines)
    {
        var list = lines as IReadOnlyList<string> ?? lines.ToList();
        return All.FirstOrDefault(k => list.Any(l => k.Pattern.IsMatch(l)));
    }

    /// <summary>
    /// The sentence the report ends with (R-em3d24-3b/3c): a match's remedy with its source, the transient's
    /// one allowed sentence, or — for anything else — that circuitRF has not seen it, and nothing more.
    /// </summary>
    public static string Explain(KnownFailure? match) => match switch
    {
        null                  => "This is not a failure circuitRF has seen.",
        { Transient: true }   => $"{match.Remedy} Retrying often clears this. (Source: {match.Source}.)",
        _                     => $"circuitRF's own installs have met this: {match.Remedy} (Source: {match.Source}.)",
    };
}
