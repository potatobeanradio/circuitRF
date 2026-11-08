using System;
using System.IO;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Ui.Schematic;

/// <summary>What installing an example produced: where it landed, and anything that went wrong.</summary>
/// <param name="WorkspaceDir">The created workspace folder.</param>
/// <param name="CwsPath">Its <c>.cws</c> — what to open.</param>
/// <param name="FileCount">Files written.</param>
/// <param name="Failures">"path: message" per file that could not be copied. A failure is REPORTED,
/// never rolled back: the files are on disk either way, and a half-copy the user cannot see is
/// worse than one they are told about.</param>
public sealed record ExampleInstallResult(
    string WorkspaceDir, string CwsPath, int FileCount, IReadOnlyList<string> Failures);

/// <summary>
/// <b>File ▸ New ▸ Examples</b> — one shipped example workspace copied to a folder the user picked, then
/// opened.
///
/// <h3>It owns no copying</h3>
/// <para>Every byte moves through <see cref="WorkspaceCopy"/>, which is what
/// <c>File ▸ Save Workspace As…</c> already calls. That matters for two reasons beyond not writing
/// the loop twice. It skips what must not travel — the rebuildable PCell cache, circuitRF's own
/// <c>.crf-</c> session bookkeeping, the advisory lock — so the copy does not arrive holding a lock
/// naming a session that has nothing to do with it. And it REPAIRS the copy's stored references
/// against its new location, which is the whole reason an example can ship with a technology under
/// <c>tech/</c> and a Touchstone file beside a schematic and still resolve wherever someone puts
/// it.</para>
///
/// <h3>An existing folder is a refusal, not an overwrite and not a rename</h3>
/// <para>Installing the same example twice into one place is a thing people do. Overwriting would
/// discard whatever they had changed in the first copy — which is the entire point of handing them
/// an editable workspace — and silently landing in <c>S-Parameters 2</c> puts a folder somewhere
/// they did not name. So it stops and says which folder is in the way, which is the same answer
/// <see cref="WorkspaceCreate"/> gives.</para>
/// </summary>
public static class ExampleWorkspaceInstall
{
    /// <summary>Where <paramref name="example"/> would land under <paramref name="parentDir"/>.</summary>
    public static string DestinationFor(ExampleWorkspace example, string parentDir)
        => ExampleWorkspaces.DestinationFor(example, parentDir);

    /// <summary>
    /// The sentence explaining why this cannot be installed here, or null when it can — the one
    /// <c>circuitrf new example</c> gives too (<see cref="ExampleWorkspaces.InstallRefusal"/>).
    /// </summary>
    public static string? Refusal(ExampleWorkspace? example, string? parentDir)
        => ExampleWorkspaces.InstallRefusal(example, parentDir);

    /// <summary>
    /// Copies <paramref name="example"/> into <paramref name="parentDir"/>. Blocking and
    /// framework-free — call it off the UI thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">Refused; see <see cref="Refusal"/>.</exception>
    public static ExampleInstallResult Run(ExampleWorkspace example, string parentDir)
    {
        if (Refusal(example, parentDir) is { } refusal)
            throw new InvalidOperationException(refusal);

        string dest   = DestinationFor(example, parentDir);
        var    result = WorkspaceCopy.Run(example.Directory, dest);

        return new ExampleInstallResult(
            dest, Path.Combine(dest, ".cws"), result.FileCount, result.Failures);
    }
}
