namespace CircuitRF.Core.Devices.External;

/// <summary>One Verilog-A module a compiled artefact implements.</summary>
/// <param name="TypeId">The module name, exactly as the artefact declares it.</param>
/// <param name="Parameters">
/// The parameter names the module declares, in its own spelling.
///
/// <para><b>Carried because the two sides of this boundary disagree on case and one of them is
/// strict.</b> A <c>.model</c> card is written in a case-INsensitive dialect and a kit writes
/// its parameters in lower case; the compiled module declares them upper case and the worker matches
/// them with <c>strcmp</c>. Measured directly against a real artefact: <c>level</c> is refused —
/// <i>'level' is not a parameter of this device type</i> — where <c>LEVEL</c> is accepted. So a card
/// can only reach the model it was written for if its names are respelled the way the artefact
/// declares them, which needs this list.</para>
/// </param>
public sealed record OsdiModule(string TypeId, IReadOnlyList<string> Parameters);

/// <summary>One compiled OSDI artefact and the Verilog-A modules it implements.</summary>
/// <param name="FilePath">Absolute path to the <c>.osdi</c>.</param>
/// <param name="Modules">What it implements, in the order the artefact declares them.</param>
public sealed record OsdiModel(string FilePath, IReadOnlyList<OsdiModule> Modules)
{
    /// <summary>Module names, exactly as the artefact declares them.</summary>
    public IReadOnlyList<string> TypeIds => [.. Modules.Select(m => m.TypeId)];
}

/// <summary>
/// The artefact implementing one module, and everything needed to route a device to it: which file
/// to load, which module inside it, and how that module spells its parameters.
/// </summary>
public sealed record OsdiImplementor(string FilePath, string TypeId, IReadOnlyList<string> Parameters);

/// <summary>
/// Finds compiled Verilog-A models and works out which modules each one implements.
///
/// <para><b>These are BUILD OUTPUT, not kit content.</b> A kit of this shape ships Verilog-A sources
/// and expects them compiled; where the result lands is a property of whoever ran the compiler, not
/// of the kit. So nothing here may resolve a model by a kit-relative path — the search takes roots
/// from the caller, and a second user of the same kit has none of these until they build.</para>
///
/// <para><b>The module is read FROM the artefact, never inferred from its file name.</b> Measured on
/// a real build: <c>mdla.osdi</c> declares <c>MDLA_VA</c>, and <c>mdla_nqs.osdi</c> declares
/// <c>MDLANQS_VA</c> — nothing about that second file name yields that module. A name-derived
/// mapping therefore fails to resolve a model that is sitting right there, and fails silently,
/// because a model that is merely not found is indistinguishable from one that was never built.</para>
///
/// <para><b>Asked, not parsed.</b> The artefact's own descriptor table is an ABI detail; circuitRF
/// already ships a worker that hosts that ABI and answers <c>describe</c>, so the module names come
/// from the one piece of code that already has to understand them. Reading the binary here would be
/// a second, drifting reader of a contract this repository deliberately keeps in exactly one place.</para>
/// </summary>
public static class OsdiModelDiscovery
{
    /// <summary>Bounds a search over a delivery that may be very large.</summary>
    public const int DefaultMaxFiles = 64;

    /// <summary>
    /// Every <c>.osdi</c> found under <paramref name="roots"/>, with the modules it implements.
    ///
    /// <para>An artefact that cannot be loaded or described contributes <b>nothing and no exception</b>
    /// — a half-built or foreign-architecture file sitting beside good ones must not cost the others.
    /// It is reported through <paramref name="problems"/> instead, because a model the user believes
    /// they compiled and which silently does not appear is the worst outcome available here.</para>
    /// </summary>
    public static IReadOnlyList<OsdiModel> Find(
        IEnumerable<string> roots,
        string              workerPath,
        List<string>?       problems = null,
        int                 maxFiles = DefaultMaxFiles)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var found  = new List<OsdiModel>();
        var seen   = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(workerPath))
        {
            problems?.Add($"The OSDI worker was not found at '{workerPath}', so no compiled model " +
                          "could be identified.");
            return found;
        }

        foreach (string file in Candidates(roots, maxFiles, seen))
        {
            DeviceWorkerProvider? provider = null;
            try
            {
                provider = DeviceWorkerProvider.Launch("osdi", workerPath, [file]);
                var modules = provider.Describe()
                                      .Where(d => !string.IsNullOrWhiteSpace(d.TypeId))
                                      .Select(d => new OsdiModule(
                                          d.TypeId,
                                          [.. d.Parameters.Select(p => p.Name)]))
                                      .ToList();

                if (modules.Count > 0) found.Add(new OsdiModel(file, modules));
                else problems?.Add($"'{file}' loaded but declares no model.");
            }
            catch (Exception ex)
            {
                problems?.Add($"'{file}' could not be read as a compiled model: {ex.Message}");
            }
            finally
            {
                try { provider?.Dispose(); } catch { /* a worker that will not stop must not throw here */ }
            }
        }

        return found;
    }

    /// <summary>
    /// The artefact implementing <paramref name="moduleName"/>, or null.
    ///
    /// <para>Case-insensitive, because the two sides genuinely differ in case: a <c>.model</c> card
    /// writes <c>mdla_va</c> while the artefact declares <c>MDLA_VA</c>. That is not a tolerance —
    /// the dialect naming the module is case-insensitive throughout.</para>
    /// </summary>
    public static OsdiImplementor? ImplementorOf(IReadOnlyList<OsdiModel> models, string moduleName)
    {
        ArgumentNullException.ThrowIfNull(models);
        if (string.IsNullOrWhiteSpace(moduleName)) return null;

        foreach (var model in models)
            foreach (var module in model.Modules)
                if (module.TypeId.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
                    return new OsdiImplementor(model.FilePath, module.TypeId, module.Parameters);

        return null;
    }

    /// <summary>
    /// <paramref name="name"/> spelled the way this module declares it, or the name exactly as
    /// written when the module declares nothing like it.
    ///
    /// <para><b>Only this direction is safe, and it is the same rule
    /// <c>SpiceNetlistReader.AlignSubcircuitParameterCase</c> already follows one level up:</b> a
    /// name is respelled only when the DEFINITION declares a case-insensitive match for it, so an
    /// unmatched name is left exactly as written and a genuine typo is still refused by name rather
    /// than being quietly turned into something the model does accept.</para>
    /// </summary>
    public static string AlignParameterCase(IReadOnlyList<string> declared, string name)
    {
        ArgumentNullException.ThrowIfNull(declared);
        if (string.IsNullOrEmpty(name)) return name;

        foreach (string d in declared)
            if (d.Equals(name, StringComparison.OrdinalIgnoreCase)) return d;

        return name;
    }

    private static IEnumerable<string> Candidates(
        IEnumerable<string> roots, int maxFiles, HashSet<string> seen)
    {
        int count = 0;

        foreach (string root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

            // MATERIALIZED INSIDE THE TRY, and told to skip what it cannot read.
            //
            // EnumerateFiles is lazy, so the old guard caught only the failure to CREATE the
            // enumerable — and the walk actually fails on the first MoveNext, which the OrderBy below
            // triggers outside the try. An unreadable directory anywhere under the walk therefore
            // escaped as UnauthorizedAccessException and took the whole PDK import down with it.
            //
            // This is not hypothetical and not test-only: the search widens outward from the kit when
            // the kit's own tree holds no compiled model, so it routinely meets folders that are none
            // of circuitRF's business. It was found on macOS, where widening from a kit in the temp
            // directory reaches the system's own 'TemporaryItems' and gets "Operation not permitted";
            // a kit sitting near a mounted volume, a protected system folder or another user's home
            // hits exactly the same thing. A folder circuitRF may not read is an ordinary fact about a
            // filesystem, not a reason to fail an import — every other walk in this codebase already
            // treats it that way.
            //
            // NO REGRESSION TEST, said plainly rather than papered over: .NET's own walk ALREADY skips
            // an ordinary permission denial, so a fixture built from directory modes passes both with
            // and without this guard and would be measuring nothing. The escape needs the sandbox
            // refusal specifically, which is transient and not something a test can stand up. This is
            // therefore a defensive fix taken from a captured stack trace, not from a reproduction.
            string[] files;
            try
            {
                files = Directory.GetFiles(root, "*.osdi", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible    = true,
                });
                Array.Sort(files, StringComparer.Ordinal);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (string f in files)
            {
                string full;
                try { full = Path.GetFullPath(f); } catch { continue; }

                if (!seen.Add(full)) continue;
                if (++count > maxFiles) yield break;
                yield return full;
            }
        }
    }
}
