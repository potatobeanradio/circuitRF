using System;
using System.IO;
using Xunit;

namespace RfCore.Tests;

/// <summary>
/// Like <see cref="FactAttribute"/>, but SKIPS (never fails) with a stated, actionable reason when
/// the named fixture directory or file is absent from this checkout.
///
/// <para>Third copy of this type in the repository (the others are under
/// <c>tests/Ui.Tests/Support/</c> and <c>tests/Engine.Tests/Support/</c>) — the test projects share
/// no code, so duplication is the established convention here rather than a shared package. Keep the
/// three in sync if the skip message or the resolution rule ever changes.</para>
///
/// <para>Why it exists (brief-housekeeping-tearoff-palette-repo.md §7 / R-hk-14): the real
/// lab-measured GaN FET <c>.spl</c> / <c>.lpcwave</c> data is proprietary and deliberately not
/// committed (see the root <c>.gitignore</c>), so a fresh clone must not FAIL these tests — it must
/// report them Skipped, naming the missing path and how to obtain it. This matters specifically for
/// <c>RfCore.Tests</c> because that project's own fixture helpers (<c>SplDir()</c>, <c>LpwaveDir()</c>,
/// …) <c>throw DirectoryNotFoundException</c> when the data is absent, which reads as a broken build
/// rather than an environment gap.</para>
/// </summary>
/// <remarks>
/// <paramref name="relativePath"/> is relative to the repo root (e.g. "testdata/spl_test_data").
/// </remarks>
public sealed class FixtureFactAttribute : FactAttribute
{
    public FixtureFactAttribute(string relativePath, string howToObtain)
    {
        if (!FixturePaths.Exists(relativePath))
            Skip = $"Missing fixture '{relativePath}' — {howToObtain}";
    }
}

/// <summary>
/// Like <see cref="TheoryAttribute"/>, but SKIPS (never fails) with a stated, actionable reason when
/// the named fixture directory or file is absent. See <see cref="FixtureFactAttribute"/>.
/// </summary>
public sealed class FixtureTheoryAttribute : TheoryAttribute
{
    public FixtureTheoryAttribute(string relativePath, string howToObtain)
    {
        if (!FixturePaths.Exists(relativePath))
            Skip = $"Missing fixture '{relativePath}' — {howToObtain}";
    }
}

internal static class FixturePaths
{
    /// <summary>
    /// Walks up from AppContext.BaseDirectory looking for relativePath as either a directory or a
    /// file. Deliberately mirrors this project's own per-file <c>SplDir()</c>/<c>LpwaveDir()</c>
    /// helpers EXACTLY, including their legacy <c>circuitRF/</c>-prefixed second candidate (a
    /// pre-subtree-merge path, when RfCore was a sibling repo). If the attribute and the helper
    /// disagreed, a test would either skip when its data is actually present, or run and throw.
    /// </summary>
    public static bool Exists(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir, relativePath)) ||
                File.Exists(Path.Combine(dir, relativePath)))
                return true;

            // Legacy sibling-repo candidate, matching the file-local helpers.
            if (Directory.Exists(Path.Combine(dir, "circuitRF", relativePath)) ||
                File.Exists(Path.Combine(dir, "circuitRF", relativePath)))
                return true;

            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }
}
