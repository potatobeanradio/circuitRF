using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
using CircuitRF.Design.ThreeD;
using CircuitRF.Design.Workspace;
using CircuitRF.Render;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The 3D EM guide's section views (<c>brief-em3d-30-showcase.md</c> <c>R-em3d30-2b</c>): one cut
/// through each cell of the shipped <c>examples/3D EM/</c> workspace.
/// </summary>
/// <remarks>
/// <b>Of the example a reader opens, read from disk</b> — <see cref="DocSmithFixtures"/>' rule, for its
/// reason: the page quotes the example's dimensions, and a change to the example moves the picture.
///
/// <para><b>The same three calls <c>render --section</c> makes</b>: the <c>.cem</c> is read and resolved
/// through <see cref="EmSetupResolver"/>, the problem built by <see cref="Em3dGenerator"/>, and the page
/// painted by <see cref="Em3dSectionRenderer"/>. Nothing here draws. The 3D VIEW's own pictures are not
/// in this file and cannot be: they need a GPU and a window (overview §1e), so the page carries named
/// placeholders for them instead.</para>
/// </remarks>
public static class DocEm3dFixtures
{
    private const string ExampleFolder = "3D EM";

    /// <summary>The bond wire from the side: pads on alumina, the wire's loop, both port sheets.</summary>
    public static FigureScene BondWireSide() => Section("Bond wire/em/Bond wire 3D.cem", Side);

    /// <summary>The via transition from the side: the top line, the via through the plane's clearance,
    /// and the bottom line leaving the other way.</summary>
    public static FigureScene ViaSide() => Section("Via through a plane/em/Via 3D.cem", Side);

    /// <summary>The package from the side: leads, bond wires and die pads under the lid.</summary>
    public static FigureScene PackageSide() => Section("Package/em/Package lid modes.cem", Side);

    private static readonly Em3dView Side = new(Em3dViewKind.SectionY, 0);

    // ── The 3D Editor chapter (brief-em3d-52): the 3D Package example's drawn package ───────────────────────

    /// <summary>The package, closed: floor, walls and lid around everything.</summary>
    public static FigureScene PackageClosed() => ThreeDView(Em3dView.Iso, []);

    /// <summary>The package with its lid and two walls lifted off: the base, the attach pad on its vias, the die,
    /// the wires and the leads.</summary>
    public static FigureScene PackageOpen() => ThreeDView(Em3dView.Iso, ["lid", "wall_s", "wall_e"]);

    /// <summary>The package cut along its leads.</summary>
    public static FigureScene PackageThreeDSide() => ThreeDView(Side, []);

    /// <summary>The example's .c3d through its Driven setup — what <c>render --iso</c> / <c>--section</c> draw for
    /// it — less the named objects, for a picture that shows inside.</summary>
    private static FigureScene ThreeDView(Em3dView view, string[] leaveOut)
        => ThreeDView("3D Package", "Package/3d/Package.c3d", "Driven", "ceramic-package.ctech", view, leaveOut);

    private static FigureScene ThreeDView(string example, string c3d, string setup, string techFile, Em3dView view, string[] leaveOut)
    {
        string root = ExampleWorkspaces.ResolveRoot()
            ?? throw new InvalidOperationException($"No examples/ tree beside the generator or above it, so the {example} figures have no document.");
        string ws = Path.Combine(root, example);
        string path = Path.Combine(ws, c3d);
        var doc = C3dPersistence.LoadFromFile(path);
        var (embedded, why) = C3dSetups.Select(doc, setup);
        if (embedded is null) throw new InvalidOperationException(why);
        var generated = C3dProblemAssembly.Assemble(C3dSetups.ForRun(embedded, path), doc, path, Path.Combine(ws, ".cws"));
        if (!generated.Ok) throw new InvalidOperationException($"{Path.GetFileName(c3d)}: {generated.Refusal}");
        var problem = generated.Problem! with { Solids = [.. generated.Problem!.Solids.Where(s => !leaveOut.Contains(s.Name))] };
        var tech = TechPersistence.LoadFromFile(Path.Combine(ws, "tech", techFile));
        return new FigureScene(new SectionView(generated with { Problem = problem }, tech, view, ws));
    }

    // ── The 3D Connector example (brief-em3d-70): a connector launch built from booleans, a fillet and a STEP part ──

    private const string Connector = "3D Connector", Launch = "Launch/3d/Launch.c3d", ConnectorTech = "board-and-connector.ctech";

    /// <summary>The launch from the iso view: the housing united with its flange, the pin over the board's line.</summary>
    public static FigureScene ConnectorIso() => ThreeDView(Connector, Launch, "Palace", ConnectorTech, Em3dView.Iso, []);

    /// <summary>The launch cut along the coax's axis: the housing, the kept PTFE fill, the pin and its rounded tip on the
    /// line, the board.</summary>
    public static FigureScene ConnectorSection() => ThreeDView(Connector, Launch, "Palace", ConnectorTech, Side, []);

    /// <summary>
    /// The 3D editor's Setups panel on the launch — the control Simulate ▸ Setup Analyses… and the Analyses panel host —
    /// with each setup's fidelity rows as the editor computes them (brief-em3d-65): openEMS's warning that it will not
    /// represent the pin's fillet. The editor's own view model on the example's file; its 3D pane is never drawn, so its
    /// backend draws nothing.
    /// </summary>
    public static FigureScene ConnectorSetups()
    {
        string root = ExampleWorkspaces.ResolveRoot()
            ?? throw new InvalidOperationException("No examples/ tree beside the generator or above it, so the 3D Connector figures have no document.");
        string ws = Path.Combine(root, Connector);
        string path = Path.Combine(ws, Launch);
        var vm = new CircuitRF.Ui.ThreeD.C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => new NoPixels(),
                                                            () => Path.Combine(ws, ".cws"), a => a());
        vm.RestoreActiveSetup("Palace");
        vm.Start();
        if (!System.Threading.SpinWait.SpinUntil(() => vm.SetupItems.Any(i => i.HasFidelity), TimeSpan.FromSeconds(120)))
            throw new InvalidOperationException("Launch.c3d: the setups' fidelity rows never arrived.");
        vm.SelectedSetupItem = vm.SetupItems.FirstOrDefault(i => i.Name == "openEMS");
        return new FigureScene(new CircuitRF.Ui.Views.ThreeD.C3dSetupAnalysesView { DataContext = vm }) { Cleanup = vm.Dispose };
    }

    /// <summary>The Look panel (brief-em3d-108) over the 3D Package example, which states no Look: the panel as it first opens,
    /// on the default studio and the default lighting. The panel is captured on its own, out of its flyout.</summary>
    public static FigureScene LookPanel()
    {
        string root = ExampleWorkspaces.ResolveRoot()
            ?? throw new InvalidOperationException("No examples/ tree beside the generator or above it, so the Look panel figure has no document.");
        string ws = Path.Combine(root, "3D Package");
        string path = Path.Combine(ws, "Package/3d/Package.c3d");
        var vm = new CircuitRF.Ui.ThreeD.C3dEditorViewModel(path, C3dPersistence.LoadFromFile(path), () => new NoPixels(),
                                                            () => Path.Combine(ws, ".cws"), a => a());
        return new FigureScene(new CircuitRF.Ui.Views.ThreeD.LookPanel { DataContext = vm.LookPanel }) { Cleanup = vm.Dispose };
    }

    /// <summary>A 3D backend that draws nothing: the Setups and Look panels' figures never show the pane.</summary>
    private sealed class NoPixels : CircuitRF.Ui.Viewer3D.Viewer3DBackend
    {
        public override string Description => "no pixels (a figure of a panel)";
        public override void UploadScene(CircuitRF.Render.Scene3D.Scene3DModel scene) { }
        public override void UploadShade(CircuitRF.Render.Scene3D.Scene3DModel scene) { }
        public override void ReleaseShade() { }
        public override void UploadAppearances(float[] table) { }
        public override void UploadEnvironment(CircuitRF.Render.Scene3D.Look.PrefilteredEnvironment environment) { }
        public override void ReleaseEnvironment() { }
        public override void UploadOverlay(CircuitRF.Render.Scene3D.Scene3DBuffer slot, CircuitRF.Render.Scene3D.Scene3DVertex[] lines) { }
        public override void UploadField(CircuitRF.Render.Scene3D.Fields.FieldVertex[] vertices) { }
        public override void UploadFieldNormals(float[] normals) { }
        public override byte[] RenderPixels(CircuitRF.Render.Scene3D.Scene3DFramePlan plan) => new byte[plan.Width * plan.Height * 4];
        public override string? CheckInterop(Avalonia.Rendering.Composition.ICompositionGpuInterop interop) => null;
        public override void CreateImages(Avalonia.Rendering.Composition.ICompositionGpuInterop interop, int width, int height, int count) { }
        public override void ReleaseImages() { }
        public override bool WaitReusable(int image, int timeoutMs) => true;
        public override void Render(int image, CircuitRF.Render.Scene3D.Scene3DFramePlan plan, ulong frame) { }
        public override void Present(Avalonia.Rendering.Composition.CompositionDrawingSurface surface, int image, ulong frame) { }
        public override void Dispose() { }
    }

    private static FigureScene Section(string cem, Em3dView view)
    {
        string root = ExampleWorkspaces.ResolveRoot()
            ?? throw new InvalidOperationException(
                "No examples/ tree beside the generator or above it, so the 3D EM figures have no "
              + "document. They are OF the shipped example on purpose — see this type's remarks.");
        string path = Path.Combine(root, ExampleFolder, cem);

        var setup = EmSetupPersistence.LoadFromFile(path);
        var resolution = EmSetupResolver.Resolve(path, setup.LayoutRef,
                                                 WorkspaceRootFinder.FindAncestorCws(Path.GetDirectoryName(path)),
                                                 new TechnologyCache());
        var source = resolution.Source
            ?? throw new InvalidOperationException($"{cem}: {string.Join(" ", resolution.Diagnostics)}");
        var generated = CircuitRF.Design.ThreeD.Em3dSetupProblem.Of(setup, source, source.Technology!);
        if (!generated.Ok) throw new InvalidOperationException($"{cem}: {generated.Refusal}");

        return new FigureScene(new SectionView(generated, source.Technology, view,
                                               Path.Combine(root, ExampleFolder)));
    }

    private sealed class SectionView(Em3dGenerationResult generated, Technology? tech, Em3dView view,
                                     string workspaceRoot) : Control
    {
        private ColorVariant _variant = ColorVariant.Light;

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            // The capture window carries the variant, as for every other themed fixture.
            _variant = ActualThemeVariant == ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light;
        }

        public override void Render(DrawingContext context)
        {
            var problem = generated.Problem!;
            var theme = ThemeResolver.Resolve(ThemeResolver.DefaultThemeName, workspaceRoot);
            var style = new Em3dRenderStyle(
                Em3dSectionRenderer.ObjectColours(problem, generated.Origins, tech, theme, _variant),
                theme, _variant, DocumentExtents.DefaultMargin, Transparent: true);
            context.Custom(new Operation(new Rect(Bounds.Size), Em3dSectionScene.Build(problem, view), style));
        }

        private sealed class Operation(Rect bounds, Em3dScene scene, Em3dRenderStyle style) : ICustomDrawOperation
        {
            public Rect Bounds => bounds;
            public bool HitTest(Point p) => false;
            public bool Equals(ICustomDrawOperation? other) => false;
            public void Dispose() { }

            public void Render(ImmediateDrawingContext context)
            {
                if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } skia) return;
                using var lease = skia.Lease();
                Em3dSectionRenderer.Draw(lease.SkCanvas, (int)bounds.Width, (int)bounds.Height, scene, style);
            }
        }
    }
}
