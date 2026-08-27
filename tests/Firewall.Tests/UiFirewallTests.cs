using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace CircuitRF.Firewall.Tests;

/// <summary>
/// Enforces the UI-framework firewall: RfCore, CircuitRF.Core, CircuitRF.Engine, CircuitRF.Design,
/// CircuitRF.Cli, CircuitRF.Harmonica and CircuitRF.WBond must not reference any UI framework
/// assembly. All UI-framework code must live exclusively in src/Ui. See
/// docs/design/ui-architecture.md §3.
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
        // wBond's framework-free half (docs/design/wbond.md §11). Its own units table predates
        // CircuitRF.Design and is left alone: LayoutUnits is now reachable across the wall, but
        // adopting it is a behaviour change to a shipping app, not part of an EM-verb project split.
        { "CircuitRF.WBond", "CircuitRF.WBond.dll" },
        // The design-layer artifacts an EM problem is built from — the layout model, the technology
        // model, the cell-folder format, the `.cem` and the extractors (brief-cli-em-verb.md
        // R-emcli-2). Gated for the reason this whole file exists: this project was carved OUT of
        // CircuitRF.Ui, so every one of its files sat next to Avalonia until the day it moved. A
        // project that starts clean and is not gated does not stay clean — and if this one stops
        // being clean, `circuitrf em` stops being buildable, silently, at whatever later date
        // someone reaches for a Dispatcher in the layout reader.
        { "CircuitRF.Design", "CircuitRF.Design.dll" },
        // The coded-diagnostics leaf (brief-localization-groundwork.md R-loc-5). Gated because its
        // ENTIRE reason for existing is to be referenceable from every project that authors
        // user-facing text — RfCore and WBond included, which are leaves with no common ancestor.
        // A diagnostic is an id, typed arguments and an English template; it references no
        // framework, and the day it does, the wall has a hole in it that reaches everywhere.
        { "CircuitRF.Diagnostics", "CircuitRF.Diagnostics.dll" },
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
