namespace CircuitRF.Core.Devices.External;

/// <summary>
/// Finds a worker-backed provider by looking for a <see cref="DeviceWorkerManifest.FileName"/> in a
/// set of folders — typically the kits installed into a workspace.
///
/// <para><b>What this buys.</b> Importing a kit is the only thing the user does. When a design
/// first asks for that kit's devices, the manifest that came with it is read and its worker is
/// started. No provider to configure, and no worker started for a kit the design never uses.</para>
/// </summary>
public sealed class DeviceWorkerProviderResolver : IExternalProviderResolver
{
    /// <summary>Starts a worker: given a provider name, a command and its arguments.</summary>
    public delegate IExternalDeviceProvider Launcher(
        string name, string command, IReadOnlyList<string> arguments);

    private readonly IReadOnlyList<string> _roots;
    private readonly Launcher              _launch;

    /// <param name="searchRoots">
    /// Folders holding kits. Each is searched for a manifest directly inside it and one level down,
    /// which is how kits are laid out — one folder per kit.
    /// </param>
    /// <param name="launcher">
    /// How to start a worker, defaulting to running it as a child process. Overridable so the
    /// command and arguments a manifest resolves to can be checked directly — which is where the
    /// mistakes are (a relative path resolved against the wrong folder, the wrong platform chosen),
    /// and testing those by starting real processes would test the operating system instead.
    /// </param>
    public DeviceWorkerProviderResolver(IEnumerable<string> searchRoots, Launcher? launcher = null)
    {
        ArgumentNullException.ThrowIfNull(searchRoots);
        _roots  = searchRoots.Where(r => !string.IsNullOrWhiteSpace(r)).ToArray();
        _launch = launcher ?? DeviceWorkerProvider.Launch;
    }

    public string Describe => _roots.Count == 0
        ? "no kit folders"
        : string.Join(", ", _roots);

    /// <summary>
    /// Separates a kit's name from a model library an instance chose instead of the kit's own. Both
    /// travel in the provider name because that is what the registry keys on: two instances naming
    /// different libraries must get two providers, or the second would silently be evaluated by the
    /// first's models.
    /// </summary>
    public const char LibraryOverrideSeparator = '|';

    /// <summary>Splits a provider name into the kit and the model library chosen for it, if any.</summary>
    public static (string Kit, string? Library) SplitOverride(string name)
    {
        int at = name.IndexOf(LibraryOverrideSeparator);
        return at < 0 ? (name, null) : (name[..at], name[(at + 1)..]);
    }

    /// <summary>Composes the two back into one provider name.</summary>
    public static string ComposeOverride(string kit, string? library)
        => string.IsNullOrWhiteSpace(library) ? kit : $"{kit}{LibraryOverrideSeparator}{library.Trim()}";

    public IExternalDeviceProvider? Resolve(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        var (kitName, library) = SplitOverride(name);
        name = kitName;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in _roots)
        {
            foreach (string dir in CandidateFolders(root))
            {
                string path = Path.Combine(dir, DeviceWorkerManifest.FileName);
                if (!seen.Add(path)) continue;

                bool folderMatches = string.Equals(
                    new DirectoryInfo(dir).Name, name, StringComparison.OrdinalIgnoreCase);

                if (!File.Exists(path)) continue;

                var manifest = DeviceWorkerManifest.TryRead(path, out string? problem);

                if (manifest is null)
                {
                    // A manifest that is present but unreadable is only this resolver's problem when
                    // it is plainly the one being asked for. Otherwise it belongs to another kit and
                    // must not fail this lookup.
                    if (folderMatches)
                        throw new ExternalDeviceException(
                            $"The kit '{name}' describes how to evaluate its devices, but that " +
                            $"description could not be used: {problem}");
                    continue;
                }

                if (!folderMatches &&
                    !string.Equals(manifest.ProviderName, name, StringComparison.OrdinalIgnoreCase))
                    continue;

                return Launch(name, manifest, _launch, library);
            }
        }

        return null;
    }

    /// <summary>
    /// A root itself, then each folder directly inside it. Kits install one folder deep, and the
    /// root is included so a single kit can also be pointed at directly.
    /// </summary>
    private static IEnumerable<string> CandidateFolders(string root)
    {
        bool exists;
        try { exists = Directory.Exists(root); }
        catch (IOException) { yield break; }

        if (!exists) yield break;

        yield return root;

        string[] children;
        try { children = Directory.GetDirectories(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }

        Array.Sort(children, StringComparer.Ordinal);   // deterministic when two kits both match
        foreach (string child in children) yield return child;
    }

    /// <summary>
    /// Starts the worker the manifest names for this machine.
    ///
    /// <para>A manifest that describes only other platforms is reported as exactly that. It is a
    /// situation a user can be in for an ordinary reason — a kit built for one operating system,
    /// opened on another — and the message says which platforms it does cover rather than leaving
    /// them to guess what is wrong.</para>
    /// </summary>
    private static IExternalDeviceProvider Launch(
        string name, DeviceWorkerManifest manifest, Launcher launcher, string? library = null)
    {
        DeviceWorkerLaunch? launch = manifest.LaunchForThisMachine();

        if (launch is null)
        {
            string offered = string.Join(", ", manifest.Launches
                .Select(l => string.IsNullOrWhiteSpace(l.Platform) ? "(unspecified)" : l.Platform)
                .Distinct(StringComparer.OrdinalIgnoreCase));

            throw new ExternalDeviceException(
                $"The kit '{name}' cannot evaluate its devices on this machine " +
                $"({DeviceWorkerManifest.CurrentRuntimeIdentifier()}). It describes how to do so " +
                $"for: {offered}.");
        }

        var (command, arguments) = manifest.Resolve(launch);

        if (library is { Length: > 0 })
            arguments = SubstituteModelLibrary(name, command, arguments, library);

        return launcher(ComposeOverride(name, library), command, arguments);
    }

    /// <summary>
    /// Puts a chosen model library in place of the kit's own in the worker's arguments.
    ///
    /// <para><b>The argument replaced is the one that names a shared library</b> — a real, checkable
    /// property of the value, not its position. A worker's arguments are the kit's to arrange, so
    /// replacing "the last one" would be reading a habit; and silently appending would hand the
    /// worker two libraries and let it decide, which is the kind of guess that produces an answer
    /// from the wrong models.</para>
    ///
    /// <para>Nothing to replace, or more than one candidate, is reported rather than resolved: the
    /// manifest does not say which argument the library is, so circuitRF does not know either.</para>
    ///
    /// <para><b>A library chosen on a Mac is put through the VM's share mechanism, not handed over
    /// as written.</b> When the worker runs inside circuitRF's Linux VM, the kit's own library
    /// reaches it as a path under <c>/mnt</c> — a path on this Mac means nothing in there. Writing
    /// the chosen one in verbatim replaces a working guest path with a host one, and the run fails
    /// deep inside the guest with "no such file" naming a file that plainly exists.</para>
    /// </summary>
    private static IReadOnlyList<string> SubstituteModelLibrary(
        string kit, string command, IReadOnlyList<string> arguments, string library)
    {
        // For the VM host, only the argv it runs INSIDE the guest can name a model library; its own
        // options describe the machine, not the work.
        bool viaVm = VmHostArguments.IsVmHost(command);
        int  first = viaVm ? VmHostArguments.GuestArgvIndex(arguments) : 0;

        var candidates = arguments
            .Select((a, i) => (Argument: a, Index: i))
            .Where(x => first >= 0 && x.Index >= first && IsSharedLibrary(x.Argument))
            .ToList();

        if (candidates.Count != 1)
            throw new ExternalDeviceException(
                $"A model library was chosen for a device from '{kit}', but the kit's own settings " +
                $"name {(candidates.Count == 0 ? "no" : candidates.Count.ToString())} model library " +
                $"to replace, so circuitRF cannot tell where it belongs in the command.");

        var replaced = arguments.ToArray();
        replaced[candidates[0].Index] = library;

        return viaVm ? VmHostArguments.ShareHostFile(replaced, candidates[0].Index) : replaced;
    }

    private static bool IsSharedLibrary(string value)
    {
        string ext = Path.GetExtension(value);
        return ext.Equals(".so",    StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".dll",   StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".dylib", StringComparison.OrdinalIgnoreCase);
    }
}
