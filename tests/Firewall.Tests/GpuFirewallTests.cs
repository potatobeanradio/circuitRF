using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Xml.Linq;

namespace CircuitRF.Firewall.Tests;

/// <summary>
/// brief-em3d-28 gate 1 — the 3D view's split across the firewall (R-em3d28-1a/1c):
/// <list type="bullet">
/// <item>no project below the firewall references a GPU API binding or P/Invokes a native GPU library —
///   the scene model, camera, picking and colour maps in <c>src/Render/Scene3D</c> stay drawable
///   headlessly later without a second renderer (em-3d.md §8.5);</item>
/// <item>route A adds no NATIVE package: every GPU binding <c>src/Ui</c> references is managed code, and
///   the GPU itself is reached through the operating system's own libraries.</item>
/// </list>
/// The UI-framework half of the wall is <see cref="UiFirewallTests"/>'; the assemblies are its list.
/// </summary>
public class GpuFirewallTests
{
    /// <summary>Managed GPU-API bindings and their runtimes.</summary>
    private static readonly string[] GpuAssemblyPrefixes =
        ["Vortice", "SharpGen", "Silk.NET", "WebGPU", "Wgpu", "Veldrid", "OpenTK", "SharpDX", "Evergine", "Stride"];

    /// <summary>Native GPU libraries, as a DllImport names them (any case, with or without extension).</summary>
    private static readonly string[] GpuNativeModules =
        ["metal", "d3d11", "d3d12", "dxgi", "d3dcompiler", "vulkan", "libvulkan", "wgpu_native", "libwgpu_native",
         "opengl32", "libgl", "opengl", "libegl", "libglesv2", "iosurface", "quartzcore"];

    [Theory, MemberData(nameof(UiFirewallTests.NonUiAssemblies), MemberType = typeof(UiFirewallTests))]
    public void Gate1_NoProjectBelowTheFirewall_ReferencesAGpuApiOrNativeGpuLibrary(string projectName, string dllFileName)
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, dllFileName);
        Assert.True(File.Exists(dllPath), $"'{dllFileName}' is not in the test output — was it built?");
        using var stream = File.OpenRead(dllPath);
        using var pe = new PEReader(stream);
        var md = pe.GetMetadataReader();

        var found = new List<string>();
        foreach (var h in md.AssemblyReferences)
        {
            string name = md.GetString(md.GetAssemblyReference(h).Name);
            if (GpuAssemblyPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase))) found.Add(name);
        }
        for (int row = 1; row <= md.GetTableRowCount(TableIndex.ModuleRef); row++)
        {
            string module = md.GetString(md.GetModuleReference(MetadataTokens.ModuleReferenceHandle(row)).Name);
            string stem = Path.GetFileNameWithoutExtension(module.Replace('\\', '/').Split('/')[^1]).ToLowerInvariant();
            if (GpuNativeModules.Contains(stem)) found.Add(module);
        }
        Assert.True(found.Count == 0,
            $"{projectName} is below the UI firewall and references GPU code: {string.Join(", ", found)}. " +
            "GPU backends live in src/Ui/Viewer3D only (brief-em3d-28 R-em3d28-1a).");
    }

    [Fact]
    public void Gate1_RouteA_AddsNoNativePackage()
    {
        string root = RepoRoot();
        var csproj = XDocument.Load(Path.Combine(root, "src", "Ui", "CircuitRF.Ui.csproj"));
        var gpu = csproj.Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include") ?? "")
            .Where(id => GpuAssemblyPrefixes.Any(x => id.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        // Vacuity guard: the backends need their bindings, so finding none means they moved (to a props
        // file, say) and this test would otherwise pass having checked nothing.
        Assert.NotEmpty(gpu);

        // The RESOLVED graph, transitive dependencies included, as restore wrote it: every GPU-binding
        // library's file list, read from the assets file rather than a package cache that may not hold it.
        string assets = Path.Combine(root, "src", "Ui", "obj", "project.assets.json");
        Assert.True(File.Exists(assets), $"{assets} is missing: restore src/Ui before running the firewall tests.");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(assets));
        var checkedLibs = new List<string>();
        foreach (var lib in doc.RootElement.GetProperty("libraries").EnumerateObject())
        {
            if (!GpuAssemblyPrefixes.Any(x => lib.Name.StartsWith(x, StringComparison.OrdinalIgnoreCase))) continue;
            checkedLibs.Add(lib.Name);
            var native = lib.Value.TryGetProperty("files", out var files)
                ? files.EnumerateArray().Select(f => f.GetString() ?? "")
                       .Where(f => f.StartsWith("runtimes/", StringComparison.OrdinalIgnoreCase)
                                   && f.Contains("/native/", StringComparison.OrdinalIgnoreCase)).ToList()
                : [];
            Assert.True(native.Count == 0, $"{lib.Name} carries a native library ({string.Join(", ", native)}); " +
                                           "route A is decided with no native package (R-em3d28-1c).");
        }
        foreach (string id in gpu)
            Assert.Contains(checkedLibs, l => l.StartsWith(id + "/", StringComparison.OrdinalIgnoreCase));
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitrf.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
