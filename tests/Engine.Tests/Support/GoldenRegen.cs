using System;
using System.IO;

namespace CircuitRF.Engine.Tests;

/// <summary>
/// The switch that decides whether a test may WRITE into <c>testdata/</c>.
///
/// <para><b>Why it exists.</b> The self-generated Hero goldens are produced by ordinary
/// <c>[Fact]</c>s — <c>GenerateHero2Golden</c>, <c>GenerateHero5Golden</c>,
/// <c>GenerateHero3Golden</c>, <c>GenerateHero3BGolden</c> — which used to run, and overwrite the
/// committed CSVs, on <b>every</b> <c>dotnet test</c>. xunit runs test classes in parallel, so a
/// regression test comparing against <c>hero2_self_V_n_gate.csv</c> could be comparing against the
/// file its own generator had just written, milliseconds earlier, from the very engine under test.
/// **A green unfiltered run was therefore not evidence that the goldens held** — a golden that
/// silently re-freezes itself is not a golden. Found during HB-P3 (2026-08-30), when a change to the
/// HB Newton moved the goldens' noise-floor digits and the suite stayed green either way.</para>
///
/// <para><b>What it does.</b> Nothing writes into <c>testdata/</c> unless
/// <c>CIRCUITRF_REGENERATE_GOLDENS</c> is set. The generator tests still RUN and still assert
/// everything they asserted before — several carry real physics checks (a positive Gt at every grid
/// point, a bounded Gmax spread, stop-reason accounting) that belong in the routine gate — they
/// simply write to <see cref="TextWriter.Null"/> instead of to the repo. Skipping the tests
/// outright would have been the smaller change and would have cost those assertions.</para>
///
/// <para><b>To regenerate deliberately</b> (after a change you have decided SHOULD move a golden):
/// <c>CIRCUITRF_REGENERATE_GOLDENS=1 dotnet test tests/Engine.Tests --filter "FullyQualifiedName~Generate"</c>,
/// then read <c>git diff testdata/</c> before committing it. That diff is the point of the exercise.</para>
/// </summary>
internal static class GoldenRegen
{
    /// <summary>The environment variable that opts a run into rewriting committed golden data.</summary>
    public const string EnvVar = "CIRCUITRF_REGENERATE_GOLDENS";

    /// <summary>
    /// True only when <see cref="EnvVar"/> is set to something other than empty, "0" or "false".
    /// Read per call rather than cached, so a test can set it around a scoped regeneration.
    /// </summary>
    public static bool Enabled
    {
        get
        {
            string? v = Environment.GetEnvironmentVariable(EnvVar);
            return !string.IsNullOrWhiteSpace(v)
                && !v.Equals("0",     StringComparison.OrdinalIgnoreCase)
                && !v.Equals("false", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// A writer for <paramref name="path"/> when regeneration is requested, and one that discards
    /// everything otherwise — so a golden writer's body needs no branch of its own and cannot grow
    /// one that forgets the guard.
    /// </summary>
    public static TextWriter OpenWriter(string path)
        => Enabled ? new StreamWriter(path) : TextWriter.Null;
}
