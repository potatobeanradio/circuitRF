using System;
using System.IO;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// Like <see cref="FactAttribute"/>, but SKIPS (never fails) with a stated, actionable reason when
/// the named fixture directory or file is absent from this checkout.
/// brief-housekeeping-tearoff-palette-repo.md §7 / R-hk-14: the real lab-measured GaN FET .spl /
/// .lpcwave data is not committed to the repo (see root .gitignore), so a fresh clone must not fail
/// these tests — it must report them Skipped with a reason naming the missing path.
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
    /// Walks up from AppContext.BaseDirectory (mirrors each file's own SplDataDir()/LpcwaveDataDir()
    /// style helper) looking for relativePath as either a directory or a file.
    /// </summary>
    public static bool Exists(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, relativePath);
            if (Directory.Exists(cand) || File.Exists(cand)) return true;
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    /// <summary>
    /// The resolved absolute path, or null. Same walk as <see cref="Exists"/> — derived rather than
    /// a fixed number of "../", which is what keeps it working when the output layout changes.
    /// </summary>
    public static string? Find(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var cand = Path.Combine(dir, relativePath);
            if (Directory.Exists(cand) || File.Exists(cand)) return Path.GetFullPath(cand);
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>Same as <see cref="Find"/>, but asserts — for a body already gated by the attribute.</summary>
    public static string Require(string relativePath)
        => Find(relativePath) ?? throw new FileNotFoundException(
               $"'{relativePath}' was not found walking up from {AppContext.BaseDirectory}");
}
