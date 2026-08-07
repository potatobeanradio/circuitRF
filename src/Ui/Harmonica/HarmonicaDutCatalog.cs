using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Devices.External;
using CircuitRF.Harmonica;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Harmonica;

/// <summary>One device type Set DUT can offer, and the descriptor behind it.</summary>
/// <param name="Provider">Provider name a <see cref="DutSpec"/> stores — a kit name, or
/// <c>VerilogA|&lt;path&gt;</c> for a bare compiled model file.</param>
/// <param name="Descriptor">The model's own declaration: its parameters and its node labels.</param>
public sealed record HarmonicaExternalType(string Provider, ExternalDeviceDescriptor Descriptor)
{
    public string TypeId  => Descriptor.TypeId;
    public string Display => string.IsNullOrWhiteSpace(Descriptor.DisplayName)
        ? Descriptor.TypeId : Descriptor.DisplayName;
}

/// <summary>
/// What <i>Set DUT…</i> can reach, and how it reaches it (§4.3).
///
/// <para><b>R-h8-4 — a kit part needs a KIT-FOLDER LIST, not a workspace.</b>
/// <c>DeviceWorkerProviderResolver</c> has two constructors and only one of them is the workspace's;
/// the other takes plain folder paths, and <c>src/Cli</c> already ships exactly that form (its
/// <c>--kits &lt;dir&gt;</c> flag) with no workspace anywhere. So the folder list is a PREFERENCE,
/// stored beside the PCell-trust and theme entries, and <b>no in-memory workspace is created</b> —
/// a <c>WorkspaceViewModel</c> would drag in the project tree, the dock layout, technologies, PCell
/// resolvers and the launch action, none of which the device path reads.</para>
///
/// <para><b>Nothing here starts a worker until it is asked to.</b> Registering a resolver starts
/// nothing (that is the whole reason resolvers exist); a worker starts the first time something
/// actually asks for a device type, which is when the user opens the picker on a kit.</para>
/// </summary>
public static class HarmonicaDutCatalog
{
    // ── the built-in device laws (§4.3) ───────────────────────────────────────

    /// <summary>
    /// The five native large-signal FET laws, by their ENGINE type name — the same string
    /// <see cref="HarmonicaNetlist"/> writes and <c>ComponentModelFactory</c> resolves. Derived by
    /// inverting <see cref="ComponentTypeRegistry.EngineReference"/> rather than by a second literal
    /// table, so a renamed engine type cannot leave the two disagreeing (the rule
    /// <c>HarmonicaInputs</c> already follows for the same reason).
    /// </summary>
    public static IReadOnlyList<(string TypeName, string Display)> NativeFetLaws { get; } =
    [
        Law(SymbolKind.FetAngelov),
        Law(SymbolKind.FetCurtice),
        Law(SymbolKind.FetCurticeCubic),
        Law(SymbolKind.FetMaterka),
        Law(SymbolKind.FetStatz),
    ];

    private static (string, string) Law(SymbolKind kind)
        => (ComponentTypeRegistry.EngineReference(kind), ComponentTypeRegistry.DisplayName(kind));

    /// <summary>A native FET's declared parameters, at their own defaults — what a freshly-chosen law
    /// starts from. The SAME declaration the schematic parameter editor renders.</summary>
    public static IReadOnlyDictionary<string, string> DefaultParametersFor(string engineTypeName)
    {
        foreach (SymbolKind kind in Enum.GetValues<SymbolKind>())
        {
            if (!string.Equals(ComponentTypeRegistry.EngineReference(kind), engineTypeName,
                               StringComparison.OrdinalIgnoreCase)) continue;

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in ComponentTypeRegistry.DefaultParameters(kind, portCount: 2))
                map[p.Name] = p.Expression;
            return map;
        }
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    // ── the kit-folder preference (R-h8-4) ────────────────────────────────────

    /// <summary>Folders holding kits, as the user configured them. Empty is the ordinary state.</summary>
    public static IReadOnlyList<string> KitFolders()
        => AppPreferencesIo.Load().HarmonicaKitFolders ?? [];

    /// <summary>Replaces the folder list and re-registers the resolver, so a folder added in the
    /// dialog is usable without restarting.</summary>
    public static void SetKitFolders(IEnumerable<string> folders)
    {
        var list = folders.Where(f => !string.IsNullOrWhiteSpace(f))
                          .Select(f => f.Trim())
                          .Distinct(StringComparer.OrdinalIgnoreCase)
                          .ToList();

        AppPreferencesIo.Update(p => p.HarmonicaKitFolders = list.Count == 0 ? null : list);
        RegisterKitResolver();
    }

    /// <summary>Adds one folder, keeping the rest. Returns false when it was already there.</summary>
    public static bool AddKitFolder(string folder)
    {
        var current = KitFolders();
        if (current.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase))) return false;
        SetKitFolders([.. current, folder]);
        return true;
    }

    /// <summary>
    /// Installs a folder-based resolver over the configured kit folders — the SAME form
    /// <c>src/Cli</c>'s <c>--kits</c> uses, which is what proves no workspace is required.
    ///
    /// <para>Replaces any resolver a previous call installed rather than stacking one per call:
    /// <c>ClearResolvers</c> drops every non-built-in resolver, and the built-in
    /// <see cref="VerilogAFileResolver"/> survives it by design, so a bare <c>.osdi</c> keeps working
    /// with no kit folder configured at all.</para>
    /// </summary>
    public static void RegisterKitResolver()
    {
        var folders = KitFolders();
        ExternalDeviceRegistry.ClearResolvers();
        if (folders.Count > 0)
            ExternalDeviceRegistry.AddResolver(new DeviceWorkerProviderResolver(folders));
    }

    /// <summary>
    /// Every kit reachable from the configured folders, by the name a <see cref="DutSpec"/> stores.
    /// Reads manifests only — it starts nothing.
    /// </summary>
    public static IReadOnlyList<string> KitNames()
    {
        var names = new List<string>();
        var seen  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string root in KitFolders())
        {
            foreach (string dir in CandidateFolders(root))
            {
                string path = Path.Combine(dir, DeviceWorkerManifest.FileName);
                if (!File.Exists(path)) continue;

                var manifest = DeviceWorkerManifest.TryRead(path, out _);
                // The kit's own folder name is what a workspace-installed part records as its
                // provider, so it is the name offered; the manifest's own name is the fallback.
                string name = new DirectoryInfo(dir).Name;
                if (manifest is not null && string.IsNullOrWhiteSpace(name))
                    name = manifest.ProviderName;

                if (name.Length > 0 && seen.Add(name)) names.Add(name);
            }
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

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

        Array.Sort(children, StringComparer.Ordinal);
        foreach (string child in children) yield return child;
    }

    // ── describing a model (R-h8-2, R-h8-3) ───────────────────────────────────

    /// <summary>The provider name for a bare compiled model file — the built-in resolver's own form,
    /// never a second spelling of it.</summary>
    public static string ProviderForModelFile(string osdiPath)
        => VerilogAFileResolver.ProviderNameFor(osdiPath);

    /// <summary>
    /// Every device type a provider offers. <paramref name="error"/> carries whatever the provider
    /// said when it could not answer — a missing file, a kit that does not run on this machine — so
    /// the dialog can show it instead of an empty list with no explanation.
    /// </summary>
    public static IReadOnlyList<HarmonicaExternalType> Describe(string provider, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(provider)) return [];

        try
        {
            var p = ExternalDeviceRegistry.Find(provider);
            if (p is null)
            {
                error = $"Nothing answered to '{provider}'. " +
                        (KitFolders().Count == 0
                            ? "No kit folder has been added yet — add one below, or point at a " +
                              "compiled model file directly."
                            : "Check that the kit is in one of the folders listed below.");
                return [];
            }
            return [.. p.Describe().Select(d => new HarmonicaExternalType(provider, d))];
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return [];
        }
    }

    /// <summary>
    /// The descriptor for the DUT a document is carrying, or null with a reason. This is what
    /// <c>HarmonicaInputs</c> asks for the model's own parameter declaration, and what the intrinsic
    /// mapping panel asks for the model's own node labels.
    /// </summary>
    public static ExternalDeviceDescriptor? TryDescribe(DutSpec dut, out string? error)
    {
        error = null;
        if (dut.Kind != DutKind.External || dut.Provider is not { Length: > 0 } provider) return null;

        var types = Describe(provider, out error);
        var match = types.FirstOrDefault(t =>
            string.Equals(t.TypeId, dut.TypeName, StringComparison.Ordinal));

        if (match is null && error is null && types.Count > 0)
            error = $"'{provider}' does not offer a device type called '{dut.TypeName}'. It offers: " +
                    string.Join(", ", types.Select(t => t.TypeId)) + ".";

        return match?.Descriptor;
    }
}
