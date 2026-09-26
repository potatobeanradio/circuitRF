using System.Globalization;

namespace CircuitRF.Design.Em3d.Wsl;

/// <summary>
/// brief-em3d-26 R-em3d26-2d — <b>killing <c>wsl.exe</c> does not kill the Linux processes it started.</b>
/// Palace's wrapper starts <c>mpirun</c>, which starts the ranks; a Spack install step starts compilers.
/// Stopping the Windows-side process would leave every one of them running inside the distribution with
/// nobody to report to. So each long-running program is started as the LEADER of its own process group,
/// through a constant wrapper that writes its pid first, and cancelling signals that whole group.
///
/// <para>The wrapper is a constant string: the pid file arrives as <c>$0</c> and the program and its
/// arguments as <c>"$@"</c>, never spliced into the script. <c>setsid -w</c> puts the shell in a new
/// session (so its pid IS the group id) and waits for it, so <c>wsl.exe</c> ends when the program does
/// and carries its exit code.</para>
/// </summary>
public static class WslProcessGroup
{
    /// <summary>The wrapper's script.</summary>
    public const string WrapperScript = "echo $$ > \"$0\"; exec \"$@\"";

    /// <summary>How long the group has to end after TERM before it is sent KILL.</summary>
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    /// <summary>The argument list that runs <paramref name="argv"/> as a process-group leader, writing its pid
    /// (= its group id) to <paramref name="pidFile"/>.</summary>
    public static IReadOnlyList<string> Wrap(string pidFile, IReadOnlyList<string> argv)
        => ["setsid", "-w", "sh", "-c", WrapperScript, pidFile, .. argv];

    /// <summary>The group id the wrapper wrote, or null when it has written none yet.</summary>
    public static int? ReadGroup(WslSession session, string pidFile)
    {
        try
        {
            string text = File.ReadAllText(session.ToWindows(pidFile)).Trim();
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int pgid) && pgid > 1 ? pgid : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    /// <summary>
    /// Stops the group the wrapper at <paramref name="pidFile"/> leads: <c>kill -TERM -- -&lt;pgid&gt;</c>, then,
    /// if anything in the group is still alive after <paramref name="grace"/>, <c>kill -KILL -- -&lt;pgid&gt;</c>.
    /// (<c>--</c> so a negative number is read as a group, not an option, by either Linux <c>kill</c>.)
    /// Returns what it did, for the log.
    /// </summary>
    /// <param name="sleep">A seam, so the gate does not wait out the grace period.</param>
    public static string Stop(WslSession session, string pidFile, TimeSpan? grace = null, Action<TimeSpan>? sleep = null)
    {
        sleep ??= Thread.Sleep;
        // The wrapper writes its pid as its first act; a cancellation in the moment before is waited out briefly.
        int? pgid = ReadGroup(session, pidFile);
        for (int i = 0; pgid is null && i < 8; i++)
        {
            sleep(TimeSpan.FromMilliseconds(250));
            pgid = ReadGroup(session, pidFile);
        }
        if (pgid is not { } g) return $"no process group to stop: {pidFile} was never written";

        string group = "-" + g.ToString(CultureInfo.InvariantCulture);
        session.Exec(["kill", "-TERM", "--", group]);
        var limit = grace ?? Grace;
        var step = TimeSpan.FromMilliseconds(250);
        for (var waited = TimeSpan.Zero; waited < limit; waited += step)
        {
            if (!session.Exec(["kill", "-0", "--", group]).Ok) return $"process group {g} ended on TERM";
            sleep(step);
        }
        session.Exec(["kill", "-KILL", "--", group]);
        return $"process group {g} did not end within {limit.TotalSeconds:0} s of TERM and was sent KILL";
    }
}
