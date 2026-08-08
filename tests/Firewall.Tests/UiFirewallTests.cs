using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace CircuitRF.Firewall.Tests;

/// <summary>
/// Enforces the UI-framework firewall: RfCore, CircuitRF.Core, CircuitRF.Engine, and
/// CircuitRF.Cli must not reference any UI framework assembly. All UI-framework code must
/// live exclusively in src/Ui. See docs/design/ui-architecture.md §3.
/// </summary>
public class UiFirewallTests
{
    public static TheoryData<string, string> NonUiAssemblies => new()
    {
        { "RfCore",           "RfCore.dll"           },
        { "CircuitRF.Core",   "CircuitRF.Core.dll"   },
        { "CircuitRF.Engine", "CircuitRF.Engine.dll" },
        { "CircuitRF.Cli",    "CircuitRF.Cli.dll"    },
        // harmonicaRF's framework-free half (docs/design/harmonicarf.md §3.2). It ships as a
        // standalone binary too, which does NOT weaken this: the standalone app is src/Ui with a
        // different Main, and src/Harmonica stays on this side of the wall.
        { "CircuitRF.Harmonica", "CircuitRF.Harmonica.dll" },
        // wBond's framework-free half (docs/design/wbond.md §11). Note that src/Ui/Layout's
        // model/units/persistence files contain no Avalonia in their SOURCE but live in
        // CircuitRF.Ui, which does — so src/WBond carries its own units table rather than
        // referencing LayoutUnits. This assertion is what enforces that.
        { "CircuitRF.WBond", "CircuitRF.WBond.dll" },
    };

    [Theory, MemberData(nameof(NonUiAssemblies))]
    public void Assembly_ReferencesNoUiFramework(string projectName, string dllFileName)
    {
        var dllPath = Path.Combine(AppContext.BaseDirectory, dllFileName);
        Assert.True(File.Exists(dllPath),
            $"Assembly '{dllFileName}' not found in test output directory " +
            $"'{AppContext.BaseDirectory}' — was the project built?");

        using var stream = File.OpenRead(dllPath);
        using var peReader = new PEReader(stream);
        var metadata = peReader.GetMetadataReader();

        var uiRefs = new List<string>();
        foreach (var handle in metadata.AssemblyReferences)
        {
            var name = metadata.GetString(metadata.GetAssemblyReference(handle).Name);
            if (IsUiFramework(name))
                uiRefs.Add(name);
        }

        Assert.True(uiRefs.Count == 0,
            $"UI firewall violated: project '{projectName}' references UI framework " +
            $"assembly '{string.Join(", ", uiRefs)}'. All UI-framework code must live " +
            $"in src/Ui only — see docs/design/ui-architecture.md §3.");
    }

    // SkiaSharp is explicitly allowed: headless 2D graphics is not a UI framework.
    // The firewall targets Avalonia and its integration packages only (§3.3).
    private static bool IsUiFramework(string assemblyName) =>
        assemblyName.StartsWith("Avalonia", StringComparison.OrdinalIgnoreCase);
}
