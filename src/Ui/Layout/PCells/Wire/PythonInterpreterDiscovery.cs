using System.Diagnostics;

namespace CircuitRF.Ui.Layout.PCells.Wire;

/// <summary>An interpreter circuitRF can run generator scripts with.</summary>
/// <param name="Command">The executable to run.</param>
/// <param name="Arguments">Arguments that must precede the script — empty for a direct interpreter,
/// <c>-3</c> for the Windows launcher, which is a dispatcher rather than an interpreter.</param>
/// <param name="Version">What it reported when asked, e.g. "3.13.1".</param>
/// <param name="HowFound">Where it came from, in words. Appears in the message that records the
/// decision, because "circuitRF picked an interpreter" is not actionable and "it took the one on
/// PATH" is.</param>
public sealed record PythonInterpreter(
    string Command, IReadOnlyList<string> Arguments, string Version, string HowFound)
{
    /// <summary>How this is written into a workspace so the choice can be corrected by hand: the
    /// command, with any prefix arguments after it.</summary>
    public string ToRecord()
        => Arguments.Count == 0 ? Command : Command + " " + string.Join(' ', Arguments);

    /// <summary>Reads back <see cref="ToRecord"/>. A quoted first token is honoured so a path with a
    /// space in it (the common case on Windows) round-trips.</summary>
    public static (string Command, IReadOnlyList<string> Arguments)? ParseRecord(string? recorded)
    {
        if (string.IsNullOrWhiteSpace(recorded)) return null;
        recorded = recorded.Trim();

        if (recorded[0] is '"' or '\'')
        {
            char quote = recorded[0];
            int close = recorded.IndexOf(quote, 1);
            if (close < 0) return null;
            return (recorded[1..close], Split(recorded[(close + 1)..]));
        }

        var parts = Split(recorded);
        return parts.Count == 0 ? null : (parts[0], parts.Skip(1).ToList());
    }

    private static List<string> Split(string s)
        => [.. s.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}

/// <summary>
/// Finds an interpreter to run PCell generator scripts with.
///
/// <para><b>The bar is the one a kit import already sets: reference, place, run — nothing to
/// configure.</b> So this looks in the places an interpreter actually is, rather than asking the
/// user where theirs lives.</para>
///
/// <para><b>circuitRF bundles no interpreter and installs no packages.</b> A kit whose cells need
/// third-party packages declares the environment that has them, in its own manifest, and that
/// declaration outranks everything found here — it is a deliberate statement about dependencies,
/// and guessing past it would run the kit's cells against an environment missing what they import.</para>
/// </summary>
public static class PythonInterpreterDiscovery
{
    /// <summary>Matches what the Python package itself requires. An older interpreter is refused by
    /// version rather than allowed to fail later on syntax, which reads as a broken kit.</summary>
    public const int MinimumMajor = 3;
    public const int MinimumMinor = 9;

    /// <summary>How long to let a candidate answer before moving on. A probe that hangs — a stale
    /// symlink, a shim waiting on a network store — must not hold up opening a workspace.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Resolution order, most specific first. Stops at the first candidate that runs and is new
    /// enough.
    /// </summary>
    /// <param name="declaredByKit">The kit manifest's own <c>interpreter</c>, already made absolute.
    /// Outranks everything: a kit declaring an environment is stating where its dependencies are.</param>
    /// <param name="recorded">What this workspace recorded last time. Replayed rather than
    /// re-derived — the same reason a kit's settings are (probing candidates costs a process launch
    /// each, and the answer does not change between sessions).</param>
    /// <param name="rejected">Every candidate tried and why, in order. Non-empty on failure, so the
    /// message can say what was looked for rather than only that nothing was found.</param>
    /// <param name="pathVariable">The PATH to resolve bare command names against. Defaults to this
    /// process's own. Named explicitly so the case this exists for — a bundled application, whose
    /// PATH is not the user's shell's — can be exercised without mutating the environment.</param>
    public static PythonInterpreter? Find(
        string? declaredByKit, string? recorded, out IReadOnlyList<string> rejected,
        string? pathVariable = null)
    {
        var notes = new List<string>();
        rejected = notes;

        if (declaredByKit is { Length: > 0 })
        {
            if (TryProbe(declaredByKit, [], "declared by the kit", out var declared, out string? why))
                return declared;
            // Reported and NOT fallen back from silently: a kit that names an environment and does
            // not get it will fail on an import, far from here and much harder to explain.
            notes.Add($"the interpreter the kit declares ('{declaredByKit}'): {why}");
            return null;
        }

        if (PythonInterpreter.ParseRecord(recorded) is { } replay)
        {
            // A recorded choice that no longer works — or that is a bare name landing on Apple's stub,
            // for the same reason it is not offered as a candidate below — is re-derived rather than
            // treated as fatal. An interpreter can be upgraded or removed between sessions, and the
            // workspace should heal rather than need the user to know that is what happened.
            string? why = AppleStubReason;
            PythonInterpreter? kept = null;

            if (!IsImplicitlyAppleCommandLineToolsPython(replay.Command, pathVariable)
                && TryProbe(replay.Command, replay.Arguments, "recorded for this workspace", out kept, out why))
                return kept;

            notes.Add($"the interpreter recorded for this workspace ('{recorded}'): {why}");
        }

        foreach (var (command, arguments, how) in Candidates(pathVariable))
        {
            if (TryProbe(command, arguments, how, out var found, out string? why)) return found;
            notes.Add($"'{command}': {why}");
        }

        return null;
    }

    /// <summary>
    /// Where an interpreter is looked for, in order. PATH first because it is what the user's own
    /// shell would run — matching that is the least surprising answer, and it is also what a virtual
    /// environment activates.
    /// </summary>
    /// <param name="pathVariable">See <see cref="Find"/>.</param>
    public static IEnumerable<(string Command, IReadOnlyList<string> Arguments, string How)> Candidates(
        string? pathVariable = null)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return ("python.exe", [], "found on PATH");
            // The launcher is a DISPATCHER, not an interpreter — it needs to be told which version,
            // or it may hand back a Python 2 on a machine that still has one.
            yield return ("py.exe", ["-3"], "found via the Windows Python launcher");

            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (local.Length > 0)
                foreach (string path in InstalledWindowsPythons(Path.Combine(local, "Programs", "Python")))
                    yield return (path, [], "found in the per-user Python installation folder");

            foreach (string path in InstalledWindowsPythons(@"C:\Program Files\Python"))
                yield return (path, [], "found in the system Python installation folder");
            yield break;
        }

        // Skipped on macOS when the name resolves to Apple's Command Line Tools stub — it is still
        // reachable, as the LAST entry of the list below. See IsImplicitlyAppleCommandLineToolsPython.
        if (!IsImplicitlyAppleCommandLineToolsPython("python3", pathVariable)) yield return ("python3", [], "found on PATH");
        if (!IsImplicitlyAppleCommandLineToolsPython("python", pathVariable)) yield return ("python", [], "found on PATH");

        // Only where an interpreter actually installs. A wider search costs a process launch per
        // candidate and would eventually match something that is not a system Python at all.
        foreach (string path in (string[])
                 ["/opt/homebrew/bin/python3", "/usr/local/bin/python3", AppleCommandLineToolsPython])
            if (File.Exists(path)) yield return (path, [], $"found at {path}");
    }

    /// <summary>macOS ships this, and it is a Command Line Tools STUB rather than a Python
    /// installation: frozen at 3.9 and never updated.</summary>
    public const string AppleCommandLineToolsPython = "/usr/bin/python3";

    private const string AppleStubReason =
        "it resolves to macOS's Command Line Tools Python, which is frozen at 3.9; "
        + "a real installation is preferred where one exists";

    /// <summary>
    /// True when <paramref name="command"/> is a BARE NAME that PATH resolves to Apple's stub.
    ///
    /// <para><b>Why this exists.</b> The rule above is "PATH first, because that is what the user's
    /// own shell would run". In an application launched from the Finder that premise is simply
    /// false: a bundled app inherits <c>/usr/bin:/bin:/usr/sbin:/sbin</c> and nothing of the user's
    /// login shell, so <c>python3</c> resolves to Apple's 3.9.6 stub — never to the 3.13 the same
    /// machine has in <c>/usr/local/bin</c>, and never to what the user gets by typing python3. It
    /// clears the 3.9 floor (which is what circuitRF's OWN package needs) and then fails to PARSE a
    /// kit whose cells use anything newer. Measured on IHP's sg13g2: <c>match</c> statements,
    /// 3.10+, so its 34 cells came back as "invalid syntax (res_base_code.py, line 61)" — a message
    /// that reads as a broken kit and is nothing of the sort.</para>
    ///
    /// <para><b>BARE NAME, deliberately.</b> Naming <c>/usr/bin/python3</c> outright — in a kit's
    /// manifest or in a workspace's own record — is a deliberate statement and is honoured. Only the
    /// implicit choice is demoted. A virtual environment is likewise untouched: its <c>python3</c>
    /// is in the environment's own <c>bin</c>, never in <c>/usr/bin</c>.</para>
    /// </summary>
    internal static bool IsImplicitlyAppleCommandLineToolsPython(string command, string? pathVariable = null)
        => OperatingSystem.IsMacOS()
           && !Path.IsPathRooted(command)
           && string.Equals(ResolveOnPath(command, pathVariable), AppleCommandLineToolsPython,
                            StringComparison.Ordinal);

    /// <summary>The file PATH would run for a bare command name, or null when nothing on it matches.
    /// Resolution only — it does not run anything, so it costs no process launch.</summary>
    private static string? ResolveOnPath(string command, string? pathVariable)
    {
        string? path = pathVariable ?? Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path)) return null;

        foreach (string dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;
            try { candidate = Path.Combine(dir.Trim(), command); }
            catch (ArgumentException) { continue; }   // a malformed PATH entry is skipped, not fatal
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>Newest first, so a machine with several installs gets the most recent one.</summary>
    private static IEnumerable<string> InstalledWindowsPythons(string root)
    {
        string[] dirs;
        try { dirs = Directory.Exists(root) ? Directory.GetDirectories(root) : []; }
        catch { yield break; }

        foreach (string dir in dirs.OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase))
        {
            string exe = Path.Combine(dir, "python.exe");
            if (File.Exists(exe)) yield return exe;
        }
    }

    /// <summary>
    /// Runs a candidate and asks it what it is.
    ///
    /// <para>Asked with <c>-c</c> rather than <c>--version</c> deliberately: it proves the
    /// interpreter can actually EXECUTE something (a broken shim or a stub on PATH will happily
    /// print a version and then fail to run a script), and it returns a form that needs no parsing
    /// of prose.</para>
    /// </summary>
    public static bool TryProbe(
        string command, IReadOnlyList<string> arguments, string howFound,
        out PythonInterpreter? interpreter, out string? why)
    {
        interpreter = null;
        why = null;

        var info = new ProcessStartInfo(command)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        foreach (string a in arguments) info.ArgumentList.Add(a);
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add("import sys; print('%d.%d.%d' % sys.version_info[:3])");

        string output;
        try
        {
            using var probe = Process.Start(info);
            if (probe is null) { why = "it could not be started"; return false; }

            output = probe.StandardOutput.ReadToEnd().Trim();
            if (!probe.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
            {
                try { probe.Kill(entireProcessTree: true); } catch { /* already gone */ }
                why = "it did not answer";
                return false;
            }
            if (probe.ExitCode != 0) { why = $"it exited with code {probe.ExitCode}"; return false; }
        }
        catch (Exception ex)
        {
            why = ex.Message;
            return false;
        }

        if (!TryParseVersion(output, out int major, out int minor))
        {
            why = $"it answered '{Shorten(output)}', which is not a version";
            return false;
        }

        if (major < MinimumMajor || (major == MinimumMajor && minor < MinimumMinor))
        {
            why = $"it is Python {major}.{minor}; {MinimumMajor}.{MinimumMinor} or newer is needed";
            return false;
        }

        interpreter = new PythonInterpreter(command, [.. arguments], output, howFound);
        return true;
    }

    private static bool TryParseVersion(string text, out int major, out int minor)
    {
        major = minor = 0;
        var parts = text.Split('.');
        return parts.Length >= 2
            && int.TryParse(parts[0], out major)
            && int.TryParse(parts[1], out minor);
    }

    private static string Shorten(string s)
        => s.Length <= 60 ? s : s[..60] + "…";

    /// <summary>
    /// The message shown when nothing was found. Degrading is the behaviour — a design still opens
    /// and its generated cells draw as the existing Not Found placeholder — so this exists to make
    /// the degradation explicable rather than mysterious.
    /// </summary>
    public static string DescribeFailure(IReadOnlyList<string> rejected)
    {
        string tried = rejected.Count == 0 ? "" :
            Environment.NewLine + string.Join(Environment.NewLine, rejected.Select(r => "  · tried " + r));

        return "No Python interpreter was found, so cells with generated artwork will draw as " +
               $"placeholders. Install Python {MinimumMajor}.{MinimumMinor} or newer, or name one in " +
               $"the kit's own {PCellGeneratorManifest.FileName}." + tried;
    }
}
