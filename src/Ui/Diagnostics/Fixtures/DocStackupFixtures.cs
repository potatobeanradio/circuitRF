using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Styling;
using CircuitRF.Render;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Theming;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// <b>The stackup, drawn in cross-section from a REAL technology</b> — the picture the Stackup
/// chapter is about.
///
/// <para>It is built from <see cref="StarterTechnologies.MmicGaAs"/> rather than from a hand-written
/// list of bands, for the reason every other figure in this folder is built from real objects: a
/// diagram that agrees only with itself would go on agreeing with itself after the shipped
/// technology changed. Every number in it — thickness, ε<sub>r</sub>, tanδ, which conductor is the
/// ground reference, which two layers a via spans — is read off the <c>Technology</c> the
/// application ships, so if that moves, the figure moves.</para>
///
/// <h3>R-stk8-7 — it is the APPLICATION'S renderer, not a second drawing of the same subject</h3>
/// <para>Every rectangle, band, barrel and label here comes out of <see cref="StackupScene"/> and
/// <see cref="StackupRenderer"/> in <c>src/Render</c>: the same layout pass and the same paint calls
/// the Technology Editor's Stackup tab makes every frame, and that <c>circuitrf render</c> and the
/// clipboard copy make. Until this brief there were two drawings of one thing — ~250 lines of
/// <c>Border</c>s and <c>TextBlock</c>s on a <c>Canvas</c> here, and the Skia renderer there — which
/// had to be kept looking alike by hand. That is the duplication <c>src/Render</c> exists to
/// prevent.</para>
///
/// <para><b>The hand-built figures were not broken, and the repoint was judged on the pictures, not
/// on the principle</b> — the three side-by-side comparisons, what each one gained and the one thing
/// each lost, are in <c>src/Ui/Diagnostics/RESOLVED.md</c>. The short version: the drawing now states
/// each via's SPAN, its fill and its wall, draws a plated barrel with its bore, and scales conductors
/// against each other, so the 0.25 µm capacitor plate no longer draws the same thickness as a 3 µm
/// interconnect metal. What it gives up is the room for a sentence: the ground reference reads "gnd"
/// and a tie reads "patterned: &lt;plate&gt;", where this file used to write both out in full. That is
/// <c>R-stk1-4</c> holding — the drawing carries no commentary — and both sentences moved into the
/// figure CAPTIONS in <see cref="FigureCatalog"/>, which is where a qualification belongs.</para>
///
/// <para>What is left here is the three things that are the FIGURE's and not the drawing's: which
/// technology, what window on it, and the footer sentence that sits outside the picture.</para>
/// </summary>
public static class DocStackupFixtures
{
    /// <summary>
    /// The width every cross-section figure lays out at.
    ///
    /// <para><b>Wide enough that no label group wraps</b>, which is what the clipboard copy widens a
    /// page to get and what <see cref="StackupScene.WidthThatFitsLabels"/> answers:
    /// <see cref="StarterTechnologies.MmicGaAs"/>, the widest of the three, needs 949.7 at the
    /// shipped font sizes, and the other two fit at 860. One width for all three so the chapter's
    /// figures line up, with the slack in the same place for each.</para>
    ///
    /// <para>It was 860 while this file drew its own bands, which measured nothing and simply trusted
    /// the numbers to fit. A test asserts nothing wraps at this width, so the next long layer name is
    /// a failure rather than a figure that has quietly started wrapping.</para>
    /// </summary>
    internal const float Width = 960;

    /// <summary>An MMIC stackup: two signal metals, the thin-film capacitor module between them, a
    /// substrate, a backside ground plane, and the three vias that connect them.</summary>
    public static FigureScene MmicCrossSection() => CrossSection(StarterTechnologies.MmicGaAs());

    /// <summary>
    /// <b>The PCB starter technology's stackup</b> — the three bands the MoM chapter's worked example
    /// runs on, and the plated through-hole that spans them.
    ///
    /// <para>It is here rather than drawn by hand in that chapter for the reason the whole file
    /// exists: the example's prose quotes 1.6 mm, ε<sub>r</sub> 4.4, tanδ 0.02 and 1 oz copper, and
    /// this picture reads all four off the <c>Technology</c> a reader's own New Workspace hands
    /// them. If the shipped stackup moves, the figure moves with it and the prose is the only thing
    /// left to fix.</para>
    ///
    /// <para>Shorter than the MMIC figure because the stack is: two conductors and one dielectric,
    /// against seven bands.</para>
    /// </summary>
    public static FigureScene PcbCrossSection() => CrossSection(StarterTechnologies.Pcb2Layer());

    /// <summary>Any technology in cross-section, whole — for a figure whose technology is a file it read (AN-02's
    /// imported board).</summary>
    internal static FigureScene CrossSectionOf(Technology tech) => CrossSection(tech);

    /// <summary>
    /// <b>The capacitor module on its own, MIM-7</b> — the same real technology, windowed to the
    /// bands between the two interconnect metals so the three things a reader of the MIM section
    /// needs are legible at reading size: the plate metal, the tied dielectric under it (with the
    /// tie marked), and the plate via's span.
    ///
    /// <para>A WINDOW on the full picture rather than a second, invented stack — every number is
    /// still read off the shipped <c>Technology</c>, and the substrate and ground plane below are
    /// named in the footer rather than redrawn. The full seven-band cross-section is
    /// <c>stackup-mmic</c>, in the same chapter.</para>
    /// </summary>
    /// <summary>TFR-V — the shipped silicon demo: a film level between two metals, the via-contact style.</summary>
    public static FigureScene SiTfrCrossSection() => CrossSection(
        ShippedTechnologies.Load("rfic-Si_3LM_TFR_100um"), include: null, footer: null);

    public static FigureScene MimModuleCrossSection() => CrossSection(
        StarterTechnologies.MmicGaAs(),
        include: l => l.Name is "Metal2" or "Air" or "MIM Metal" or "MIM Dielectric" or "Metal1",
        footer: "…then 100 µm of GaAs and the backside ground plane, unchanged by the module.");

    /// <param name="include">
    /// A WINDOW on the stack: only the band entries this admits are drawn, and the two boundary
    /// conditions are then not stated at all (<see cref="StackupSceneOptions.ShowBoundaryConditions"/>)
    /// — a slice of a sandwich has no terminations of its own. A via whose span leaves the window is
    /// dropped with it, by the scene's own unresolved-span rule.
    /// </param>
    /// <param name="footer">
    /// One sentence UNDER the picture, outside it. Deliberately not a scene label: it is
    /// documentation prose about what the window leaves out, and <c>R-stk1-4</c>'s rule that the
    /// drawing carries no commentary applies to the figures too — a qualification belongs beside a
    /// picture, not inside one.
    /// </param>
    private static FigureScene CrossSection(
        Technology tech, Func<StackupLayer, bool>? include = null, string? footer = null)
    {
        var drawn = include is null ? tech : Windowed(tech, include);
        var scene = StackupScene.Build(
            drawn, Width, new StackupSceneOptions { ShowBoundaryConditions = include is null });

        var view = new StackupSceneView(scene);
        if (footer is null) return new FigureScene(view);

        var stack = new StackPanel { Orientation = Orientation.Vertical, Spacing = 6, Width = Width };
        stack.Children.Add(view);
        stack.Children.Add(new TextBlock
        {
            Text = footer,
            FontSize = StackupScene.SpecSize,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(StackupScene.Gutter, 0, StackupScene.Gutter, 0),
        });
        return new FigureScene(stack);
    }

    /// <summary>
    /// The same technology with only the admitted band entries in its stackup — the via entries come
    /// through untouched, so one whose span has left the window is reported by the scene's own
    /// unresolved-span rule rather than silently vanishing.
    ///
    /// <para>The drawing layers are SHARED, not copied: the scene reads them only to resolve a
    /// conductor's colour, and a second copy of a layer table is a second thing to keep in step.</para>
    /// </summary>
    private static Technology Windowed(Technology tech, Func<StackupLayer, bool> include)
    {
        var kept = new HashSet<string>(
            tech.Stackup.Layers.Where(l => l.Kind != StackupKind.Via && include(l)).Select(l => l.Name),
            StringComparer.Ordinal);

        return new Technology
        {
            Name               = tech.Name,
            DefaultDisplayUnit = tech.DefaultDisplayUnit,
            Layers             = tech.Layers,
            Stackup = new Stackup
            {
                Top    = tech.Stackup.Top,
                Bottom = tech.Stackup.Bottom,
                Layers = tech.Stackup.Layers
                    // A via is kept only when BOTH its ends are in the window. One end outside it is
                    // an UNRESOLVED span as far as the scene is concerned, and the scene is right to
                    // print that in red — R-stk1-6 exists so a broken stackup cannot look fine. It is
                    // not broken here: the conductor is in the technology, just not in this picture.
                    // Filtering it out is what makes the window a window rather than a claim.
                    .Where(l => l.Kind == StackupKind.Via
                        ? kept.Contains(l.SpanFromLayer ?? "") && kept.Contains(l.SpanToLayer ?? "")
                        : kept.Contains(l.Name))
                    .ToList(),
            },
        };
    }

    /// <summary>
    /// <b>A laid-out <see cref="StackupScene"/>, painted by <see cref="StackupRenderer"/>.</b> It
    /// holds no view model, handles no input and computes nothing — <c>StackupCanvas</c> is the
    /// interactive control and this is the figure's half of it.
    ///
    /// <para>It paints with <c>transparentBackground</c>, so the docs page's own surface shows
    /// through: a figure with no window chrome is composited onto that surface, and painting the
    /// pane's background over it would put a slab of editor colour on the page. The side effect is
    /// the wanted one — a plated barrel's bore becomes a real hole in the picture rather than a disc
    /// of some other colour.</para>
    /// </summary>
    private sealed class StackupSceneView : Control
    {
        private readonly StackupScene _scene;
        private StackupRenderTheme _theme = StackupRenderTheme.Fallback;

        internal StackupSceneView(StackupScene scene) => _scene = scene;

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            // The capture window carries the variant, exactly as it does for every other themed
            // fixture — read it here rather than from ThemeService.CurrentVariant so a figure and the
            // window it is composited into cannot disagree.
            var variant = ActualThemeVariant == ThemeVariant.Dark ? ColorVariant.Dark : ColorVariant.Light;
            _theme = StackupRenderTheme.FromTheme(ThemeService.Active, variant);
        }

        // The scene's own intrinsic height, which is what replaced this file's former `height:`
        // parameter: a figure whose height was typed beside the fixture is a figure that silently
        // clips the day a layer is added, and the scene already knows the answer.
        protected override Size MeasureOverride(Size availableSize) => new(_scene.Width, _scene.Height);

        public override void Render(DrawingContext context)
            => context.Custom(new Operation(new Rect(Bounds.Size), _scene, _theme));

        private sealed class Operation(Rect bounds, StackupScene scene, StackupRenderTheme theme)
            : ICustomDrawOperation
        {
            public Rect Bounds => bounds;
            public bool HitTest(Point p) => false;
            public bool Equals(ICustomDrawOperation? other) => false;
            public void Dispose() { }

            public void Render(ImmediateDrawingContext context)
            {
                if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } skia) return;
                using var lease = skia.Lease();
                StackupRenderer.Draw(lease.SkCanvas, scene, theme, overlay: null,
                                     transparentBackground: true);
            }
        }
    }
}
