using System;
using System.IO;
using Xunit;

namespace CircuitRF.Harmonica.Tests;

/// <summary>
/// Like <see cref="FactAttribute"/>, but SKIPS (never fails) with a stated, actionable reason when
/// the named fixture directory or file is absent from this checkout. The native workers and the
/// test-only model library are build outputs that are deliberately not committed, so a fresh clone
/// with no C compiler must report these Skipped with a reason rather than red.
/// </summary>
/// <remarks><paramref name="relativePath"/> is relative to the repo root.</remarks>
public sealed class FixtureFactAttribute : FactAttribute
{
    public FixtureFactAttribute(string relativePath, string howToObtain)
    {
        if (!FixturePaths.Exists(relativePath))
            Skip = $"Missing fixture '{relativePath}' — {howToObtain}";
    }
}

internal static class FixturePaths
{
    /// <summary>Walks up from the test binary's folder looking for a repo-relative path.</summary>
    public static bool Exists(string relativePath) => Find(relativePath) is not null;

    public static string Require(string relativePath)
        => Find(relativePath) ?? throw new FileNotFoundException(
               $"fixture '{relativePath}' was not found above '{AppContext.BaseDirectory}'");

    private static string? Find(string relativePath)
    {
        string? dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            string candidate = Path.Combine(dir, relativePath);
            if (Directory.Exists(candidate) || File.Exists(candidate)) return Path.GetFullPath(candidate);
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
