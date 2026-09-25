// ================================================================
//  InstallationGuard.cs — did the installation change under a running `serve`?
//
//  `serve` can run for hours out of an installation that the GUI then updates
//  (brief-automation-13-installed-cli.md R-aut13-4). MEASURED on macOS, not assumed: a single-file
//  circuitRF whose bundle is exchanged under it keeps answering every tool whose code it has already
//  loaded, and fails every tool whose code it has not —
//
//      run      "Could not load file or assembly 'NumFlat, Version=1.3.0.0 …'"
//      history  "Could not load file or assembly 'System.Diagnostics.Process …'"
//      check    ok   (called before the exchange, so everything it needs was already loaded)
//
//  — because the runtime reads a not-yet-loaded assembly out of its executable BY PATH, and after
//  the exchange that path is a different file (src/Ui/RESOLVED.md records the same finding for the
//  GUI's own update hand-over). A server in that state is worse than a dead one: it works for some
//  calls and fails others with a message about a file nobody deleted, so a client concludes the
//  TOOL is broken. The same family reaches Linux if an app-<ver> directory a server runs from is
//  reclaimed, and Windows never, because a running executable's folder cannot be deleted there.
//
//  So the server remembers what its executable WAS, and before every call asks whether it still is.
//  If not, that call is answered with a JSON-RPC error saying so and the server exits — loudly,
//  once, and the client's next launch of the same command runs the new version, because the path it
//  launches is exactly the one the update replaced.
//
//  EVERYTHING HERE IS CORELIB — FileInfo, DateTime, strings — because by the time it matters, loading
//  anything else is precisely what no longer works. Changed() is called once at construction so the
//  method is prepared while its whole closure is certainly loaded.
// ================================================================

using System.Diagnostics.CodeAnalysis;

namespace CircuitRF.Cli.Serve;

internal sealed class InstallationGuard
{
    private readonly string?  _path;
    private readonly long     _length;
    private readonly DateTime _written;

    /// <summary>The executable this process was started from — circuitRF inside an installed
    /// bundle or app folder, or <c>dotnet</c> for the development CLI, whose own file never changes
    /// under it and so never trips this.</summary>
    public static InstallationGuard ForThisProcess() => new(Environment.ProcessPath);

    internal InstallationGuard(string? executable)
    {
        try
        {
            var file = executable is null ? null : new FileInfo(executable);
            if (file is { Exists: true })
            {
                _path    = file.FullName;
                _length  = file.Length;
                _written = file.LastWriteTimeUtc;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _path = null;   // nothing to guard; a server that cannot stat itself simply runs
        }

        _ = Changed(out _);
    }

    /// <summary>
    /// True, with the reason, when the executable this server started from has been replaced or
    /// removed since. A replacement is told by size and write time: two builds that agree on both to
    /// the tick are not a thing an update produces.
    /// </summary>
    public bool Changed([NotNullWhen(true)] out string? why)
    {
        why = null;
        if (_path is null) return false;

        try
        {
            var now = new FileInfo(_path);
            if (!now.Exists)
                why = $"'{_path}' no longer exists";
            else if (now.Length != _length || now.LastWriteTimeUtc != _written)
                why = $"'{_path}' has been replaced";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;   // cannot tell; do not invent an update
        }

        return why is not null;
    }

    /// <summary>The sentence a client and a person both read, on stderr and in the error frame.</summary>
    public static string Sentence(string why) =>
        $"circuitRF was updated or removed while this server was running ({why}). A running server "
      + "cannot load code it has not already loaded out of an installation that has changed, so it "
      + "is exiting rather than answering some calls and failing others. Start the server again: the "
      + "same command runs the installed version.";
}
