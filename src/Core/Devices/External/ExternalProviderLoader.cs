using System.Reflection;

namespace CircuitRF.Core.Devices.External;

/// <summary>
/// Discovers external device providers shipped as plug-in assemblies and registers them.
///
/// <para><b>Why this is a loader and not a provider.</b> A provider is bound to whoever supplies the
/// device model — its file formats, its evaluation, its licensing. circuitRF must contain none of
/// that, so it ships the SEAM: drop an assembly exposing <see cref="IExternalDeviceProvider"/> into
/// a plug-in folder and it becomes available to every netlist that names it. Nothing here knows what
/// any provider does, and nothing here is specific to one.</para>
///
/// <para>Never throws. A plug-in folder that does not exist, an assembly that will not load, a
/// provider whose constructor fails — each is reported and skipped, because one bad plug-in must
/// never stop the application from starting.</para>
/// </summary>
public static class ExternalProviderLoader
{
    /// <summary>Folder name searched for provider plug-ins, relative to each search root.</summary>
    public const string PluginFolderName = "providers";

    /// <param name="Registered">Names of providers successfully registered, in load order.</param>
    /// <param name="Diagnostics">
    /// Everything that could not be loaded, and why. A skipped plug-in is always reported: a
    /// provider that silently fails to register surfaces much later as an incomprehensible
    /// "provider not available" at simulation time.
    /// </param>
    public sealed record LoadReport(
        IReadOnlyList<string> Registered,
        IReadOnlyList<string> Diagnostics)
    {
        public bool LoadedAnything => Registered.Count > 0;
    }

    /// <summary>
    /// The folders searched by default: a <c>providers</c> directory beside the application, and one
    /// under the user's own application-data directory so a plug-in can be installed without write
    /// access to the install location.
    /// </summary>
    public static IReadOnlyList<string> DefaultSearchPaths()
    {
        var paths = new List<string>();

        try
        {
            string? appDir = Path.GetDirectoryName(Environment.ProcessPath)
                          ?? AppContext.BaseDirectory;
            if (!string.IsNullOrEmpty(appDir))
                paths.Add(Path.Combine(appDir, PluginFolderName));
        }
        catch { /* an unavailable process path is not a reason to fail */ }

        try
        {
            string data = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrEmpty(data))
                paths.Add(Path.Combine(data, "circuitRF", PluginFolderName));
        }
        catch { /* ditto */ }

        return paths;
    }

    /// <summary>Loads from <see cref="DefaultSearchPaths"/>.</summary>
    public static LoadReport LoadDefaults() => Load(DefaultSearchPaths());

    /// <summary>
    /// Scans each directory for assemblies, registering every provider found. A directory that does
    /// not exist is skipped silently — an absent plug-in folder is the normal case, not a problem.
    /// </summary>
    public static LoadReport Load(IEnumerable<string> directories)
    {
        var registered = new List<string>();
        var diags      = new List<string>();

        foreach (var dir in directories)
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;

            string[] files;
            try
            {
                if (!Directory.Exists(dir)) continue;
                files = Directory.GetFiles(dir, "*.dll", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                diags.Add($"Provider folder '{dir}' could not be read: {ex.Message}");
                continue;
            }

            Array.Sort(files, StringComparer.Ordinal);   // deterministic load order
            foreach (var file in files)
                LoadAssembly(file, registered, diags);
        }

        return new LoadReport(registered, diags);
    }

    private static void LoadAssembly(string file, List<string> registered, List<string> diags)
    {
        Assembly asm;
        try
        {
            asm = Assembly.LoadFrom(file);
        }
        catch (BadImageFormatException)
        {
            return;   // not a managed assembly — a native dependency sitting alongside one
        }
        catch (Exception ex)
        {
            diags.Add($"'{Path.GetFileName(file)}' could not be loaded: {ex.Message}");
            return;
        }

        Type[] types;
        try
        {
            types = asm.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // A partially-loadable assembly still yields the types that DID load; use them rather
            // than discarding a plug-in whose unrelated type failed to resolve.
            types = ex.Types.Where(t => t is not null).ToArray()!;
            diags.Add($"'{Path.GetFileName(file)}' loaded only partially: {ex.Message}");
        }
        catch (Exception ex)
        {
            diags.Add($"'{Path.GetFileName(file)}' could not be inspected: {ex.Message}");
            return;
        }

        foreach (var type in types)
        {
            // IsPublic is true only for TOP-LEVEL public types; a public type nested in a public one
            // reports IsNestedPublic instead. Both are publicly constructible, which is the property
            // that actually matters here.
            if (type is null || type.IsAbstract || type.IsInterface) continue;
            if (!type.IsPublic && !type.IsNestedPublic) continue;
            if (!typeof(IExternalDeviceProvider).IsAssignableFrom(type)) continue;

            if (type.GetConstructor(Type.EmptyTypes) is null)
            {
                diags.Add($"'{type.FullName}' in '{Path.GetFileName(file)}' is a provider but has no " +
                          "public parameterless constructor, so it could not be created.");
                continue;
            }

            try
            {
                if (Activator.CreateInstance(type) is not IExternalDeviceProvider provider) continue;

                if (string.IsNullOrWhiteSpace(provider.Name))
                {
                    diags.Add($"'{type.FullName}' in '{Path.GetFileName(file)}' reports an empty " +
                              "provider name and was skipped — a netlist could never refer to it.");
                    continue;
                }

                // Last one wins, matching the registry's own overwrite semantics, but say so:
                // two plug-ins claiming one name is a real configuration problem.
                if (ExternalDeviceRegistry.Find(provider.Name) is not null)
                    diags.Add($"Provider '{provider.Name}' was already registered; the copy in " +
                              $"'{Path.GetFileName(file)}' replaced it.");

                ExternalDeviceRegistry.Register(provider);
                registered.Add(provider.Name);
            }
            catch (Exception ex)
            {
                diags.Add($"Provider '{type.FullName}' in '{Path.GetFileName(file)}' failed to " +
                          $"initialise: {ex.Message}");
            }
        }
    }
}
