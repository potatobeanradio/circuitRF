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
            .Select(e => (Id: (string?)e.Attribute("Include") ?? "", Version: (string?)e.Attribute("Version") ?? ""))
            .Where(p => GpuAssemblyPrefixes.Any(x => p.Id.StartsWith(x, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        string cache = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
                       ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        foreach (var (id, version) in gpu)
        {
            string dir = Path.Combine(cache, id.ToLowerInvariant(), version);
            if (!Directory.Exists(dir)) continue;       // not restored on this machine: the build would have failed first
            var native = Directory.Exists(Path.Combine(dir, "runtimes"))
                ? Directory.EnumerateDirectories(Path.Combine(dir, "runtimes"), "native", SearchOption.AllDirectories)
                           .SelectMany(d => Directory.EnumerateFiles(d)).ToList()
                : [];
            Assert.True(native.Count == 0, $"{id} {version} carries a native library ({string.Join(", ", native.Select(Path.GetFileName))}); " +
                                           "route A is decided with no native package (R-em3d28-1c).");
        }
    }

    private static string RepoRoot()
    {
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        while (d is not null && !File.Exists(Path.Combine(d.FullName, "circuitrf.slnx"))) d = d.Parent;
        return d?.FullName ?? throw new InvalidOperationException("repository root not found");
    }
}
