namespace CircuitRF.Core.Devices.External;

/// <summary>
/// The one place that knows how a command line for <c>crf-vmhost</c> is shaped — the Linux VM host
/// circuitRF ships so a Linux-only device model can be evaluated on a Mac.
///
/// <para><b>Why this is a type rather than a few string literals.</b> The VM's contract has exactly
/// two halves that must agree: a host directory is offered as <c>--share TAG=PATH</c>, and the guest
/// then sees it at <c>/mnt/TAG</c>. Anything that builds one half without the other produces a
/// command that starts perfectly and then fails inside the guest with "no such file", naming a path
/// that plainly exists on the Mac — a confusing report of a mechanical mistake. Writing both halves
/// here means a caller cannot get them out of step.</para>
///
/// <para><b>A host path is meaningless inside the guest.</b> That is the rule this type exists to
/// enforce. Every file the guest program is told to open must have been mapped through a share
/// first; there is no fallback, because the guest's filesystem is not the Mac's.</para>
/// </summary>
public static class VmHostArguments
{
    /// <summary>
    /// The VM host's command name. Named rather than pathed wherever a manifest asks for it, so
    /// <see cref="DeviceWorkerManifest.ResolveCommand"/> finds circuitRF's own copy on whichever
    /// machine ends up running the design.
    /// </summary>
    public const string Command = "crf-vmhost";

    /// <summary>Where the guest mounts what it was given. Fixed by the guest's init, not by a caller.</summary>
    private const string MountRoot = "/mnt";

    /// <summary>Separates the VM host's own options from the argv it runs inside the guest.</summary>
    private const string ArgvSeparator = "--";

    /// <summary>
    /// True when this command starts the VM host — so its arguments follow the share/mount contract
    /// above rather than being a worker's own.
    /// </summary>
    /// <remarks>
    /// Matched on the file name, because a manifest may name it bare (resolved out of circuitRF's
    /// tools folder) or as a full path (a kit shipping its own build), and both are the same program.
    /// </remarks>
    public static bool IsVmHost(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;

        string name = Path.GetFileName(command.Trim());
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

        return name.Equals(Command, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Offers a host directory to the guest, mounted at <c>/mnt/TAG</c>.</summary>
    public const string ShareFlag = "--share";

    /// <summary>
    /// Offers a host directory to the guest mounted at the SAME absolute path it has here, so a path
    /// on this Mac is also a valid path in the guest.
    ///
    /// <para>This is what a kit's data files need. A device model is told which files to read
    /// through its own parameters — the kit's to write, sent long after the VM has started — so
    /// unlike the model library there is no command line left in which to rewrite them. Making the
    /// path true in the guest means nothing has to be rewritten at all.</para>
    /// </summary>
    public const string ShareAtFlag = "--share-at";

    /// <summary>A share value offering <paramref name="hostDirectory"/> to the guest as
    /// <paramref name="tag"/>. Read-only unless a caller genuinely needs the guest to write.</summary>
    public static string ShareValue(string tag, string hostDirectory, bool readOnly = true)
        => $"{tag}={hostDirectory}{(readOnly ? ReadOnlySuffix : "")}";

    /// <summary>Where the guest sees a file that arrived through the share named <paramref name="tag"/>.</summary>
    public static string GuestPath(string tag, string relativePath)
        => $"{MountRoot}/{tag}/{relativePath.Replace('\\', '/')}";

    private const string ReadOnlySuffix = ":ro";

    /// <summary>
    /// Index of the first argument the guest actually runs, i.e. just past the <c>--</c>. Returns
    /// -1 when there is no separator, which is not a command this type can reason about.
    /// </summary>
    public static int GuestArgvIndex(IReadOnlyList<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        for (int i = 0; i < arguments.Count; i++)
            if (arguments[i] == ArgvSeparator) return i + 1;

        return -1;
    }

    /// <summary>
    /// Makes the host file named at <paramref name="index"/> reachable inside the guest, and rewrites
    /// that argument to the path the guest will see.
    ///
    /// <para><b>An existing share is reused whenever it already covers the file.</b> That is the
    /// common case rather than a nicety: choosing a different revision of a library usually means a
    /// file sitting beside the kit's own, which the kit's share already carries. Reusing it also
    /// keeps the guest command short, and the kernel command line that carries it is a fixed-size
    /// buffer.</para>
    ///
    /// <para>Returns the arguments unchanged when the value is not a rooted path — a share can only
    /// be built from a real directory on this machine, and anything else is a value the manifest
    /// meant literally.</para>
    /// </summary>
    public static IReadOnlyList<string> ShareHostFile(IReadOnlyList<string> arguments, int index)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, arguments.Count);

        int argv = GuestArgvIndex(arguments);
        if (argv < 0 || index < argv) return arguments;

        string value = arguments[index];
        if (!Path.IsPathRooted(value)) return arguments;

        string hostFile;
        try { hostFile = Path.GetFullPath(value); }
        catch (Exception ex) when (ex is ArgumentException or IOException) { return arguments; }

        var shares = ParseShares(arguments, argv);

        // Already covered — say so in the guest's own terms and add nothing.
        foreach (var (tag, directory, atOwnPath) in shares)
            if (RelativeWithin(directory, hostFile) is { } relative)
            {
                // A share mounted where it lives needs no translation at all: the path this file has
                // here is the path it has in the guest. Rewriting it to /mnt/<tag>/… would name a
                // place nothing was mounted.
                if (atOwnPath) return arguments;

                var reused = arguments.ToArray();
                reused[index] = GuestPath(tag, relative);
                return reused;
            }

        string? hostDirectory = Path.GetDirectoryName(hostFile);
        if (string.IsNullOrEmpty(hostDirectory)) return arguments;

        string fresh = FreshTag(shares.Select(s => s.Tag));

        // Inserted just BEFORE the separator, so the option stays an option. Everything at or past
        // the separator shifts by the two elements added, which is why the target is rewritten at
        // its new index rather than the one passed in.
        var list = arguments.ToList();
        list.Insert(argv - 1, ShareFlag);
        list.Insert(argv,     ShareValue(fresh, hostDirectory));
        list[index + 2] = GuestPath(fresh, Path.GetFileName(hostFile));

        return list;
    }

    /// <summary>The share pairs among the VM host's own options, and how each is mounted.</summary>
    private static List<(string Tag, string Directory, bool AtOwnPath)> ParseShares(
        IReadOnlyList<string> arguments, int argv)
    {
        var shares = new List<(string, string, bool)>();

        for (int i = 0; i + 1 < argv - 1; i++)
        {
            bool atOwnPath = arguments[i] == ShareAtFlag;
            if (!atOwnPath && arguments[i] != ShareFlag) continue;

            string spec = arguments[i + 1];
            int eq = spec.IndexOf('=');
            if (eq <= 0) continue;

            string tag  = spec[..eq];
            string path = spec[(eq + 1)..];
            if (path.EndsWith(ReadOnlySuffix, StringComparison.Ordinal)) path = path[..^ReadOnlySuffix.Length];
            if (path.Length == 0) continue;

            try { shares.Add((tag, Path.GetFullPath(path), atOwnPath)); }
            catch (Exception ex) when (ex is ArgumentException or IOException) { /* not a share we can use */ }
        }

        return shares;
    }

    /// <summary>
    /// Where <paramref name="file"/> sits under <paramref name="directory"/>, or null when it does
    /// not. Compared case-insensitively because the only host this runs on is macOS, whose default
    /// filesystem is — the returned relative part is cut from the file's own text, so its spelling
    /// is preserved either way.
    /// </summary>
    private static string? RelativeWithin(string directory, string file)
    {
        string root = directory.TrimEnd('/');
        if (root.Length == 0 || file.Length <= root.Length + 1) return null;
        if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;
        if (file[root.Length] != Path.DirectorySeparatorChar && file[root.Length] != '/') return null;

        return file[(root.Length + 1)..];
    }

    /// <summary>A tag no existing share is using. Short, because it travels on the kernel command line.</summary>
    private static string FreshTag(IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);

        // "rosetta" is mounted by the guest itself and never appears among the options, so it would
        // not be seen as taken — name it here rather than colliding with it.
        used.Add("rosetta");

        if (!used.Contains("lib")) return "lib";
        for (int n = 2; ; n++)
            if (!used.Contains($"lib{n}")) return $"lib{n}";
    }
}
