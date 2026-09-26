using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Em;
using CircuitRF.Design.Layout.Em3d;
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
        var generated = Em3dGenerator.Generate(setup, source, source.Technology!);
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
