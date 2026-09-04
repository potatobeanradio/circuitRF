using System;
using System.IO;

namespace CircuitRF.Ui;

/// <summary>
/// The one directory circuitRF keeps its per-user state in — preferences, the recovery sessions,
/// and anything else that belongs to the installation rather than to a workspace.
///
/// <para>It exists so that state directory can be <b>redirected</b>. Two callers computed
/// <c>LocalApplicationData/circuitRF</c> independently (<see cref="Theming.AppPreferencesIo"/> and
/// <see cref="Schematic.RecoveryManager"/>), which meant there was no single lever to move them
/// both — and the User-Docs Factory needs exactly that lever.</para>
///
/// <para><b>Why the docs factory needs it.</b> A generated figure must not depend on whose machine
/// generated it. <c>WorkspaceViewModel</c>'s constructor reads the real preferences file and
/// restores the installed PDKs from it, so a workspace capture taken on a developer's machine
/// picked up that developer's launch window layout, colour scheme and installed kits — the dock
/// arrangement in the figure visibly changed with them. <c>tools/DocGen</c> points this at a
/// throwaway directory before it starts, so every run sees a first-launch installation and
/// regenerating the docs produces the same bytes anywhere.</para>
///
/// <para>The environment cannot do this job: on macOS .NET resolves
/// <see cref="Environment.SpecialFolder.LocalApplicationData"/> to
/// <c>~/Library/Application Support</c> from the platform, not from <c>XDG_DATA_HOME</c> or
/// <c>HOME</c>, so setting either in-process changes nothing (measured, not assumed).</para>
/// </summary>
public static class AppDataRoot
{
    private static string? _override;

    /// <summary>
    /// Redirect every per-user file to <paramref name="directory"/>, or pass null to go back to the
    /// platform location. Set it before anything reads a preference — in practice, first thing in a
    /// tool's entry point.
    /// </summary>
    public static void RedirectTo(string? directory)
    {
        _override = directory is null ? null : Path.GetFullPath(directory);
        // The preferences are held as ONE in-process copy now (MW1 R-mw1-8), and that copy belongs to
        // whichever directory it was read from — so moving the directory has to drop it, or the next
        // read answers from the old location and the next write puts it in the new one.
        Theming.AppPreferencesIo.InvalidateCache();
        // The compiled-model cache is per-user state like the rest and was resolved against the OLD
        // directory. It is a path held in CircuitRF.Core rather than computed on each use, so moving
        // the root has to move it too — otherwise a redirected process writes its build output into
        // the real user's cache, which is exactly what redirecting exists to prevent.
        Schematic.VerilogACompilerInstaller.RefreshCacheDirectory();
    }

    /// <summary>True when <see cref="RedirectTo"/> has moved the state directory somewhere else.</summary>
    public static bool IsRedirected => _override is not null;

    /// <summary>The directory itself. Not created here — each caller creates what it writes.</summary>
    public static string Dir => _override
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "circuitRF");

    /// <summary>A named sub-directory of it, e.g. <c>recovery</c>.</summary>
    public static string SubDir(string name) => Path.Combine(Dir, name);
}
