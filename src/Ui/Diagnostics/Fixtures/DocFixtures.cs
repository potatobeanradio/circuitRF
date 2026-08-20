using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using CircuitRF.Ui.DataDisplay;
using CircuitRF.Ui.DataDisplay.ViewModels;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.Em;
using CircuitRF.Ui.Schematic;
using CircuitRF.Ui.Theming;
using CircuitRF.Ui.ViewModels;
using CircuitRF.Ui.Views.Content;
using CircuitRF.Ui.Views.DataDisplay;
using CircuitRF.Ui.Views.Layout;
using CircuitRF.Ui.Views.WBond;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;
using Avalonia.VisualTree;

namespace CircuitRF.Ui.Diagnostics.Fixtures;

/// <summary>
/// The states the User-Documentation figures are captured in.
///
/// <para><b>This is the expensive half of the docs factory, and it is expensive on purpose.</b> The
/// render call is a few dozen lines; putting the interface into a state worth photographing is the
/// work. A capture of an empty schematic editor teaches a reader nothing, so every fixture below
/// loads real content.</para>
///
/// <para><b>Fixtures load SHIPPED documents, not hand-built view-models.</b> A hand-constructed
/// view-model rots silently as the model evolves — it keeps compiling and keeps producing a picture
/// of something the application no longer does. A fixture that loads a real file fails loudly
/// instead. The source is <c>src/Ui/resources/schematic-templates/*.csch</c>: four real, authored,
/// version-controlled schematics that ship embedded in the assembly and are read through the very
/// same <c>SchematicPersistence</c> a user's own file goes through.</para>
///
/// <para><b>Not <c>circuitRF_demo/</c>, and this corrects the brief.</b> The design note and the
/// brief both suggest building fixtures from the shipped example workspaces. That directory is
/// <b>git-ignored</b> — it is not in the repository, so a fresh clone and CI do not have it, and a
/// fixture reading from it would fail everywhere except the author's own machine. The embedded
/// schematic templates are the tracked equivalent and satisfy the same requirement (real documents,
/// loudly failing, one fixture serving several figures).</para>
///
/// <para><b>Cached analysis results.</b> Where a figure needs simulated data, the fixture reads a
/// cached <c>DataSet</c> rather than re-solving, so a docs regeneration is not a multi-minute run.
/// A fixture that does so states the cache path in its own header comment.</para>
/// </summary>
public static class DocFixtures
{
    /// <summary>The shipped template a schematic figure is drawn from.</summary>
    public const string SchematicTemplateId = "FET_S-Parameters";

    // ── Schematic ─────────────────────────────────────────────────────────────

    /// <summary>The schematic editor with a real, shipped S-parameter test bench open.</summary>
    public static FigureScene SchematicEditor()
    {
        var view = new SchematicView { DataContext = SchematicDoc() };
        return new FigureScene(view);
    }

    /// <summary>
    /// The schematic editor with its component context menu open — the popup case (§3.4). The menu
    /// is a separate top-level, so the generator composites its root onto the same canvas rather
    /// than hoping it turns up in the window's visual tree.
    /// </summary>
    public static FigureScene SchematicContextMenu()
    {
        var view = new SchematicView { DataContext = SchematicDoc() };
        return new FigureScene(view)
        {
            Popups = root => OpenContextMenu(root, "ComponentContextMenu", new Point(210, 190)),
        };
    }

    private static SchematicDocument SchematicDoc()
    {
        var model = ShippedSchematicTemplates.Load(SchematicTemplateId);
        var vm = new SchematicViewModel(model);
        return new SchematicDocument(SchematicTemplateId, vm);
    }

    // ── Symbol editor ─────────────────────────────────────────────────────────

    /// <summary>The symbol editor holding a real built-in symbol (the SDD's variadic body).</summary>
    public static FigureScene SymbolEditor() => new(SymbolEditorFor());

    // ── Layout editor ─────────────────────────────────────────────────────────

    /// <summary>The layout editor on an empty-but-configured view — enough to show the chrome.</summary>
    public static FigureScene LayoutEditor() => new(LayoutEditorFor());

    // ── Data Display ──────────────────────────────────────────────────────────

    /// <summary>The Data Display document surface.</summary>
    public static FigureScene DataDisplay() => new(DataDisplayFor());

    // ── EM setup ──────────────────────────────────────────────────────────────

    /// <summary>The EM Setup editor with the default setup — stackup, ports and sweep controls.</summary>
    public static FigureScene EmSetup()
    {
        var vm = new EmSetupEditorViewModel("em.emsetup", new EmSetup());
        var view = new EmSetupEditorView { DataContext = new EmSetupDocument("EM Setup", vm, "em.emsetup") };
        return new FigureScene(view);
    }

    // ── wBond ─────────────────────────────────────────────────────────────────

    /// <summary>The wBond editor carrying the shipped default wirebond design.</summary>
    public static FigureScene WBondEditor() => new(WBondEditorFor());

    // ── Toolbars ──────────────────────────────────────────────────────────────

    /// <summary>The toolbar panel lifted out of a real editor view, plus the view it came from.</summary>
    /// <param name="Panel">The live <c>DocsToolbar</c> panel — the manifest and the figure both read this.</param>
    /// <param name="Owner">Kept alive so the panel's bindings still have something to bind to.</param>
    public sealed record ToolbarFixture(Panel Panel, Control Owner);

    /// <summary>
    /// Build the named editor and lift its toolbar out for capture.
    ///
    /// <para>The panel is DETACHED from its view rather than the whole editor being captured and
    /// cropped: a crop would be a screenshot with extra steps, and would move the moment anything
    /// above the toolbar changed height. Detaching costs one thing — the panel loses its inherited
    /// DataContext — so the fixture re-attaches it explicitly, or every bound tooltip and every
    /// bound combo would come back empty and the manifest would record that as the truth.</para>
    /// </summary>
    public static ToolbarFixture Toolbar(string id)
    {
        Control view = id switch
        {
            "schematic"   => new SchematicView     { DataContext = SchematicDoc() },
            "symbol"      => SymbolEditorFor(),
            "layout"      => LayoutEditorFor(),
            "datadisplay" => DataDisplayFor(),
            "wbond"       => WBondEditorFor(),
            _ => throw new ArgumentOutOfRangeException(nameof(id), id, "No such toolbar in ToolbarCatalog."),
        };

        // The template has to be applied before the named panel exists in the tree at all.
        var probe = new Window { Width = 1200, Height = 800, Content = view };
        probe.Show();
        UiArtworkGenerator.Pump();
        probe.Measure(new Size(1200, 800));
        probe.Arrange(new Rect(0, 0, 1200, 800));
        UiArtworkGenerator.Pump();

        var panel = view.GetVisualDescendants().OfType<Panel>()
                        .FirstOrDefault(p => p.Name == ToolbarCatalog.PanelName)
                 ?? throw new InvalidOperationException(
                        $"The '{id}' view declares no panel named '{ToolbarCatalog.PanelName}'. That name is " +
                        "how the docs factory finds a toolbar; a view that loses it silently loses its " +
                        "documentation figure and its per-button table.");

        var context = panel.DataContext;
        (panel.Parent as Panel)?.Children.Remove(panel);
        (panel.Parent as ContentControl)?.SetCurrentValue(ContentControl.ContentProperty, null);
        (panel.Parent as Decorator)?.SetCurrentValue(Decorator.ChildProperty, null);
        panel.DataContext = context;

        probe.Content = null;
        probe.Close();
        return new ToolbarFixture(panel, view);
    }

    private static SymbolEditorView SymbolEditorFor()
    {
        var source = BuiltInSymbols.Primitives(SymbolKind.Sdd, 2);
        var editable = new EditableSymbol { PortCount = source.Pins.Count };
        editable.Primitives.AddRange(source.Primitives);
        editable.Pins.AddRange(source.Pins);
        return new SymbolEditorView
        {
            DataContext = new SymbolEditorDocument("SDD2", new SymbolEditorViewModel(editable)),
        };
    }

    private static LayoutEditorView LayoutEditorFor() => new()
    {
        DataContext = new LayoutDocument("Layout", new LayoutEditorViewModel(new LayoutView())),
    };

    private static DataDisplayView DataDisplayFor() => new()
    {
        DataContext = new DataDisplayDocument("Data Display", new DataDisplayDocumentViewModel()),
    };

    private static WBondEditorView WBondEditorFor() => new()
    {
        DataContext = new WBondDocument(new WBondViewModel(WBondEmbedding.DefaultDesign()), title: "wBond"),
    };

    // ── Popup helper ──────────────────────────────────────────────────────────

    /// <summary>
    /// Open a named <see cref="ContextMenu"/> declared inside <paramref name="root"/> and hand the
    /// generator its popup root plus the offset to composite it at.
    ///
    /// <para>A context menu is hosted in its own top level, which is exactly why a naive capture
    /// omits it without complaining. Returning the popup's own visual root here — rather than
    /// relying on the popup landing in the window's tree — makes the figure independent of whether
    /// the platform overlay-hosts popups or gives them a real window.</para>
    /// </summary>
    public static IReadOnlyList<PopupCapture> OpenContextMenu(Control root, string menuName, Point at)
    {
        // Open it on the control it is ATTACHED to. ContextMenu.Open refuses any other control
        // ("Cannot show ContextMenu on a different control to the one it is attached to"), which is
        // the first thing this fixture hit.
        var owner = root.GetVisualDescendants()
                        .OfType<Control>()
                        .FirstOrDefault(c => c.ContextMenu is { } m && m.Name == menuName);
        if (owner?.ContextMenu is not { } menu) return [];

        // Place the menu over the canvas rather than at the control's origin, where it would sit on
        // top of the synthetic title bar and read as a rendering fault.
        menu.HorizontalOffset = at.X;
        menu.VerticalOffset   = at.Y;
        menu.Open(owner);
        UiArtworkGenerator.Pump();
        return Describe(menu, owner, at);
    }

    /// <summary>Open a named <see cref="ComboBox"/>'s drop-down — the other popup shape a figure needs.</summary>
    public static IReadOnlyList<PopupCapture> OpenDropDown(Control root, string comboName, Point at)
    {
        var combo = root.GetVisualDescendants().OfType<ComboBox>()
                        .FirstOrDefault(c => c.Name == comboName);
        if (combo is null) return [];

        combo.IsDropDownOpen = true;
        UiArtworkGenerator.Pump();

        var presenter = combo.GetVisualDescendants().OfType<Control>()
                             .FirstOrDefault(c => c.Name == "PART_Popup")
                     ?? (Control)combo;
        return Describe(presenter, root, at);
    }

    /// <summary>
    /// Describe an opened popup for the generator: what to prove drew, and — only if it went to its
    /// own top level — what to composite and where.
    /// </summary>
    private static IReadOnlyList<PopupCapture> Describe(Visual popupContent, Visual inWindow, Point at)
    {
        var windowRoot = VisualRootOf(inWindow);
        var popupRoot  = VisualRootOf(popupContent);
        bool separate  = popupRoot is not null && !ReferenceEquals(popupRoot, windowRoot);
        return [new PopupCapture(popupContent, separate ? popupRoot : null, at.X, at.Y)];
    }

    /// <summary>Walk to the top of <paramref name="v"/>'s visual tree — its own top level.</summary>
    internal static Visual? VisualRootOf(Visual? v)
    {
        while (v?.GetVisualParent() is Visual parent) v = parent;
        return v;
    }
}
