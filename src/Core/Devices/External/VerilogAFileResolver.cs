using System.Runtime.InteropServices;

namespace CircuitRF.Core.Devices.External;

/// <summary>
/// Produces a device provider for a compiled model file the USER named, with no kit involved.
///
/// <para><b>Why this exists beside the kit resolver.</b> That one answers "which program evaluates
/// this kit's devices", read from a manifest beside the kit. This one answers a different question:
/// a user has compiled their own model and wants to place it on a schematic. There is no kit, no
/// manifest, and nothing to install — the only fact circuitRF needs is the path to the file, and the
/// user has just typed it into the parameter dialog.</para>
///
/// <para><b>The provider name carries the path</b> (<c>VerilogA|/abs/path.osdi</c>), which is what
/// makes the caching correct: the registry keys providers by name, so two instances naming the same
/// file share one worker process and two instances naming different files get one each. Keying on
/// anything coarser would evaluate one user's model with another's.</para>
///
/// <para><b>Built in, and deliberately not something a host registers.</b> Placing a model file must
/// work on a fresh install with no workspace, no kit and no configuration — so this resolver is
/// always in the chain and survives <c>ClearResolvers</c>, which exists to drop the resolvers
/// belonging to a workspace that is going away. This one belongs to no workspace.</para>
///
/// <para><b>Verilog-A SOURCE is accepted here and compiled first</b>
/// (<see cref="VerilogASourceCompiler"/>), which is why the compile step lives behind this one seam
/// rather than in the parameter dialog: everything that reaches a placed model — the dialog reading
/// its terminals, elaboration, <c>Cli hb</c>, harmonicaRF's Set DUT — composes a provider name and
/// arrives here. Putting it anywhere else would give a <c>.va</c> that works in the GUI and fails
/// headless.</para>
/// </summary>
public sealed class VerilogAFileResolver : IExternalProviderResolver
{
    /// <summary>Marks a provider name as "the compiled model file named after the separator".</summary>
    public const string Prefix = "VerilogA|";

    /// <summary>The worker circuitRF ships for this ABI, resolved in its own tools folder.</summary>
    private const string WorkerCommand = "osdi-worker";

    /// <summary>Composes the provider name a device instance asks for.</summary>
    public static string ProviderNameFor(string modelFilePath)
        => Prefix + Path.GetFullPath(modelFilePath);

    /// <summary>The model file a composed provider name refers to, or null for any other name.</summary>
    public static string? ModelFileIn(string providerName)
        => providerName.StartsWith(Prefix, StringComparison.Ordinal)
            ? providerName[Prefix.Length..]
            : null;

    public string Describe => "a model file named directly by a placed component";

    /// <summary>
    /// Told which compiler ran and where the artefact went, once per compile or cache hit.
    ///
    /// <para>Reported rather than silent because the two questions a user has here are "did it
    /// actually rebuild" and "which compiler did it use" — and a cache that answers invisibly is
    /// indistinguishable from one that is stale. The host routes it to the Messages panel; a
    /// headless process leaves it unset and nothing is printed.</para>
    /// </summary>
    public static Action<string>? CompileNote { get; set; }

    public IExternalDeviceProvider? Resolve(string name)
    {
        if (ModelFileIn(name) is not { Length: > 0 } modelFile) return null;

        // Absence is reported as a REFUSAL, not by returning null. Null means "this resolver has no
        // opinion", which would send the caller on to report that no provider answered to the name —
        // and the name is a file path, so the useful message is that the file is not there.
        if (!File.Exists(modelFile))
            throw new ExternalDeviceException(
                $"The model file '{modelFile}' does not exist. Point the component's model file at " +
                "a compiled model ('.osdi'), or at Verilog-A source ('.va', '.vams') for circuitRF " +
                "to compile with the Verilog-A compiler installed on this machine.");

        // Source is compiled to an artefact first; a `.osdi` is already one and is loaded as-is.
        // Compiling is CACHED on the source's own content plus the compiler's identity, so this is
        // one compile per edit and NOT one per simulation — the second Run of an unedited model
        // finds the artefact already built and starts the worker straight on it.
        if (VerilogASourceCompiler.IsSourceFile(modelFile))
        {
            modelFile = VerilogASourceCompiler.Compile(modelFile, out string note);
            CompileNote?.Invoke(note);
        }

        // Resolved in circuitRF's own tools folder — the same rule a kit uses to name a helper
        // circuitRF ships without knowing where it was installed.
        string worker = FindShippedWorker(modelFile);
        if (worker.Length == 0)
            throw new ExternalDeviceException(WhyNoWorker(modelFile));

        return new DeviceWorkerProvider(
            name, ProcessDeviceWorkerTransport.Start(worker, [modelFile], forProvider: name));
    }

    /// <summary>
    /// The shipped worker that can host <paramref name="modelFile"/>, or empty when there is none.
    ///
    /// <para><b>The worker's architecture must match THE MODEL'S, not circuitRF's.</b> This worker
    /// loads the model into its own process and a process holds exactly one instruction set — but it
    /// is a separate process, so circuitRF's own architecture never enters into it. On Windows the
    /// two genuinely differ: an arm64 machine commonly runs a translated x64 Verilog-A compiler, and
    /// that compiler emits x64 <c>.osdi</c> files. So both are shipped there
    /// (<c>osdi-worker-x64.exe</c>, <c>osdi-worker-arm64.exe</c>) and the model's own PE header
    /// decides between them.</para>
    ///
    /// <para><b>A candidate is accepted on the evidence, never on its name.</b> Each is read the same
    /// way the model was, so the flat <c>osdi-worker.exe</c> — a copy of whichever architecture the
    /// building machine had — is taken when it happens to match and passed over when it does not.
    /// That is what makes an architecture this build cannot host reported as such and, the day a
    /// worker for it ships beside the application, hosted with no code change and no message.</para>
    ///
    /// <para><b>No platform test appears anywhere in this.</b> A Mach-O or an ELF does not begin
    /// "MZ", so off Windows the model yields no architecture, nothing is matched on, and the first
    /// candidate is taken exactly as it always was. Writing the rule as a fact about the FILES rather
    /// than as a branch on the operating system is what keeps the one path in use everywhere
    /// tested.</para>
    /// </summary>
    private static string FindShippedWorker(string modelFile)
    {
        Architecture? want = MachineOf(modelFile);

        foreach ((string path, Architecture? machine) in ShippedWorkers())
        {
            // No architecture to match on — an unreadable model, or a platform that ships one
            // worker — takes the first candidate, exactly as this did before it could read either.
            if (want is null || machine is null || machine == want) return path;
        }
        return "";
    }

    /// <summary>
    /// Every worker beside the application, each with the architecture its own file declares.
    ///
    /// <para>The per-architecture names come first so an exact match is preferred over the flat copy
    /// even when both would serve. The bare name is tried with and without an executable suffix, so
    /// one rule serves every platform.</para>
    /// </summary>
    private static IEnumerable<(string Path, Architecture? Machine)> ShippedWorkers()
    {
        string dir = DeviceWorkerManifest.ToolsDirectory;
        if (string.IsNullOrEmpty(dir)) yield break;

        string[] names =
        [
            $"{WorkerCommand}-x64.exe", $"{WorkerCommand}-arm64.exe",
            WorkerCommand, WorkerCommand + ".exe",
        ];

        foreach (string candidate in names)
        {
            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(dir, candidate));
                if (!File.Exists(full)) continue;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException) { continue; }

            yield return (full, MachineOf(full));
        }
    }

    /// <summary>
    /// The architecture a file was built for, read from its own header, or null when it does not
    /// carry one this build can read. Only a prefix is read — see <see cref="PeImports.MachineOf"/>.
    /// </summary>
    private static Architecture? MachineOf(string path)
    {
        try
        {
            using FileStream fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[PeImports.HeaderPrefixBytes];
            int n = fs.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return PeImports.MachineOf(head[..n]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Why no worker could host this model, in the user's terms.
    ///
    /// <para><b>Composed from what is actually on disk, never from a written-down claim about which
    /// platforms are supported.</b> A sentence naming an architecture as unsupported is right only
    /// until someone ships a worker for it, and a stale one of those sends a user hunting a problem
    /// that no longer exists. So the two cases are separated by looking: nothing beside the
    /// application at all is one message, and workers that are all for some other architecture is a
    /// different one — and the second stops being produced the moment a matching worker appears,
    /// with nothing to remember to delete.</para>
    /// </summary>
    private static string WhyNoWorker(string modelFile)
    {
        var present = ShippedWorkers().ToList();
        string here = $"{PlatformName()} {ArchName(RuntimeInformation.ProcessArchitecture)}";

        if (present.Count == 0)
            return $"circuitRF's own model-hosting helper ('{WorkerCommand}') was not found beside " +
                   $"the application, so a compiled model cannot be evaluated. This is a {here} " +
                   "build. The helper is built from tools/osdi-worker and needs a C compiler; a " +
                   "build without one still produces a working application, but not this.";

        Architecture? want = MachineOf(modelFile);
        string have = string.Join(", ", present.Select(w => ArchName(w.Machine)).Distinct());

        return $"The model file '{Path.GetFileName(modelFile)}' is built for " +
               $"{ArchName(want)}, and circuitRF's model-hosting helper ('{WorkerCommand}') is " +
               $"present only for {have}. A helper loads the model into its own process, so the two " +
               "architectures have to match. Either compile the model with a Verilog-A compiler " +
               $"that targets {have}, or build the {ArchName(want)} helper from tools/osdi-worker " +
               "and put it beside the application.";
    }

    private static string PlatformName()
        => OperatingSystem.IsWindows() ? "Windows"
         : OperatingSystem.IsMacOS()   ? "macOS"
         : OperatingSystem.IsLinux()   ? "Linux"
         : RuntimeInformation.OSDescription;

    /// <summary>The spelling used in the helpers' own file names, and in what a user is told.</summary>
    private static string ArchName(Architecture? arch) => arch switch
    {
        Architecture.X64   => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86   => "x86",
        Architecture.Arm   => "arm32",
        null               => "an architecture circuitRF could not read",
        _                  => arch.ToString()!.ToLowerInvariant(),
    };
}
