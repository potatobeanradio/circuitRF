using System;
using System.IO;
using Xunit;

namespace CircuitRF.Core.Tests;

/// <summary>
/// Like <see cref="FactAttribute"/>, but SKIPS (never fails) with a stated, actionable reason when a
/// named artifact is absent from this checkout.
///
/// <para><b>Why a worker needs this and the reference worker does not.</b> `DeviceWorkerExample` is
/// an ordinary project, so `dotnet build` always produces it and a missing binary is a broken setup
/// worth failing on. A NATIVE worker needs a C compiler, and the standing rule for those is that a
/// missing cross-compiler warns and the build still succeeds — a missing worker must never be the
/// reason somebody cannot build the application. A machine without one must therefore report these
/// Skipped with a reason, not red.</para>
/// </summary>
public sealed class FixtureFactAttribute : FactAttribute
{
    public FixtureFactAttribute(string relativePath, string howToObtain)
    {
        if (FixturePaths.Find(relativePath) is null)
            Skip = $"Missing '{relativePath}' — {howToObtain}";
    }
}

/// <summary>Theory counterpart of <see cref="FixtureFactAttribute"/>.</summary>
public sealed class FixtureTheoryAttribute : TheoryAttribute
{
    public FixtureTheoryAttribute(string relativePath, string howToObtain)
    {
        if (FixturePaths.Find(relativePath) is null)
            Skip = $"Missing '{relativePath}' — {howToObtain}";
    }
}

internal static class FixturePaths
{
    /// <summary>
    /// Walks up from the test assembly's own directory looking for <paramref name="relativePath"/>
    /// as either a file or a directory, and returns the resolved absolute path (null if absent).
    ///
    /// <para>Deriving it by walking up, rather than by a fixed number of "../", is what keeps it
    /// working when the output layout changes — which it has.</para>
    /// </summary>
    public static string? Find(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            var candidate = Path.Combine(dir, relativePath);
            if (Directory.Exists(candidate) || File.Exists(candidate))
                return Path.GetFullPath(candidate);
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>Same as <see cref="Find"/>, but asserts — for use inside a body already gated by the attribute.</summary>
    public static string Require(string relativePath)
        => Find(relativePath) ?? throw new FileNotFoundException(
               $"'{relativePath}' was not found walking up from {AppContext.BaseDirectory}");
}
