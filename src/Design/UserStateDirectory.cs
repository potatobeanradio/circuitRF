namespace CircuitRF.Design;

/// <summary>
/// The one directory circuitRF keeps its per-user state in — preferences, the recovery sessions, and
/// anything else that belongs to the installation rather than to a workspace.
///
/// <para><b>Why it is HERE and not only in <c>src/Ui</c>.</b> It began as <c>CircuitRF.Ui.AppDataRoot</c>,
/// which is still the type the application talks to. RC-3 (<c>docs/design/revision-control.md</c> §4.4,
/// rev 5) needs the commit identity readable from <c>src/Cli</c> as well as from the GUI: §1.2's agent
/// is out of process, and on the fresh Windows machine §4.4 describes, git's own resolution names
/// nobody — so the AI-batch checkpoint would be refused for exactly the population it exists to
/// protect. Reading a JSON file in the per-user directory crosses no firewall; only the type that owns
/// the settings dialog does. So the DIRECTORY moved down and the dialog did not.</para>
///
/// <para><b>There is still exactly one lever.</b> <c>AppDataRoot.RedirectTo</c> delegates here and then
/// drops the caches that were resolved against the old location. Two independently-computed copies of
/// <c>LocalApplicationData/circuitRF</c> is the state this type was created to end — the docs factory
/// needs one place to point at so a generated figure does not depend on whose machine generated it.
/// Redirect through <c>AppDataRoot</c> from inside the application; this entry point exists for a
/// headless process that has no <c>src/Ui</c> to call.</para>
///
/// <para>The environment cannot do this job: on macOS .NET resolves
/// <see cref="Environment.SpecialFolder.LocalApplicationData"/> to <c>~/Library/Application Support</c>
/// from the platform, not from <c>XDG_DATA_HOME</c> or <c>HOME</c>, so setting either in-process
/// changes nothing (measured, not assumed).</para>
/// </summary>
public static class UserStateDirectory
{
    /// <summary>
    /// Points every per-user file somewhere else for ONE PROCESS — the out-of-process equivalent of
    /// <see cref="RedirectTo"/>, and the same arrangement <c>CRF_VERILOGA_COMPILER</c> already has.
    ///
    /// <para><b>Why a variable and not a flag.</b> RC-3's identity reader must work in a process
    /// nobody configured interactively (<c>revision-control.md</c> §4.4): an agent's container, a CI
    /// job, a test driving the real CLI. In-process redirection is unreachable from there, and on
    /// macOS the platform variables do not help — .NET resolves
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> from the platform, not from
    /// <c>XDG_DATA_HOME</c> or <c>HOME</c> (measured, not assumed), which is the whole reason
    /// <c>AppDataRoot</c> exists.</para>
    /// </summary>
    public const string EnvironmentVariable = "CRF_STATE_DIR";

    private static string? _override;

    /// <summary>
    /// Redirect every per-user file to <paramref name="directory"/>, or null to go back to the
    /// platform location.
    ///
    /// <para><b>Call <c>CircuitRF.Ui.AppDataRoot.RedirectTo</c> instead from anywhere inside the
    /// application</b> — it calls this and then invalidates the three caches that hold a path derived
    /// from the old directory. Calling this one directly from a process that has those caches loaded
    /// leaves them answering from the previous location.</para>
    /// </summary>
    public static void RedirectTo(string? directory)
        => _override = directory is null ? null : Path.GetFullPath(directory);

    /// <summary>True when <see cref="RedirectTo"/> has moved the state directory somewhere else.</summary>
    public static bool IsRedirected => _override is not null;

    /// <summary>The directory itself. Not created here — each caller creates what it writes.
    /// <para><b><c>DoNotVerify</c> is load-bearing</b> (brief-em3d-24, found in a clean Ubuntu 24.04
    /// container): without it .NET returns an EMPTY string for a special folder that does not exist yet,
    /// and on a fresh Linux account <c>~/.local/share</c> does not — so this became the RELATIVE path
    /// <c>circuitRF</c>, and every per-user file landed under whatever the working directory was.</para></summary>
    public static string Dir => _override
        ?? (Environment.GetEnvironmentVariable(EnvironmentVariable)?.Trim() is { Length: > 0 } fromEnv
                ? fromEnv
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
                                                         Environment.SpecialFolderOption.DoNotVerify), "circuitRF"));

    /// <summary>A named sub-directory of it, e.g. <c>recovery</c>.</summary>
    public static string SubDir(string name) => Path.Combine(Dir, name);

    /// <summary>
    /// The per-user preferences file. Named here rather than in <c>src/Ui</c> because two projects now
    /// read it: the settings dialog that writes it, and RC-3's identity reader that must see what the
    /// dialog wrote from a process with no UI in it (§4.4).
    /// </summary>
    public static string PreferencesPath => Path.Combine(Dir, "preferences.json");
}
