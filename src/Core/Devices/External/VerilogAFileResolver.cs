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

    public string Describe => "a compiled model file named directly by a placed component";

    public IExternalDeviceProvider? Resolve(string name)
    {
        if (ModelFileIn(name) is not { Length: > 0 } modelFile) return null;

        // Absence is reported as a REFUSAL, not by returning null. Null means "this resolver has no
        // opinion", which would send the caller on to report that no provider answered to the name —
        // and the name is a file path, so the useful message is that the file is not there.
        if (!File.Exists(modelFile))
            throw new ExternalDeviceException(
                $"The compiled model file '{modelFile}' does not exist. Point the component's model " +
                "file at a compiled model, or compile one from your Verilog-A source with your own " +
                "compiler — circuitRF does not build them.");

        // Resolved in circuitRF's own tools folder — the same rule a kit uses to name a helper
        // circuitRF ships without knowing where it was installed.
        string worker = FindShippedWorker();
        if (worker.Length == 0)
            throw new ExternalDeviceException(
                $"circuitRF's own model-hosting helper ('{WorkerCommand}') was not found beside the " +
                "application, so a compiled model cannot be evaluated. It is built from " +
                "tools/osdi-worker and needs a C compiler; a build without one still produces a " +
                "working application, but not this.");

        return new DeviceWorkerProvider(
            name, ProcessDeviceWorkerTransport.Start(worker, [modelFile], forProvider: name));
    }

    /// <summary>
    /// The shipped worker beside the application, or empty when it was not built. Tries the
    /// executable suffix as well as the bare name, so one rule serves every platform.
    /// </summary>
    private static string FindShippedWorker()
    {
        string dir = DeviceWorkerManifest.ToolsDirectory;
        if (string.IsNullOrEmpty(dir)) return "";

        foreach (string candidate in new[] { WorkerCommand, WorkerCommand + ".exe" })
        {
            try
            {
                string full = Path.GetFullPath(Path.Combine(dir, candidate));
                if (File.Exists(full)) return full;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException) { /* try the next */ }
        }
        return "";
    }
}
