using System;
using System.ComponentModel;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Renderers;
using CircuitRF.Ui.WBond;
using CircuitRF.WBond;

namespace CircuitRF.Ui.Views.WBond;

/// <summary>
/// The profile view (wbond.md §6.2) — the canvas, its two rulers, and its group context menu.
///
/// <h3>One control, two hosts</h3>
/// <para>WB39a/M3: the wBond editor stacks this above the layout view (§6.1), and the workspace offers
/// the same control as a dock tool that follows the active layout (§10.1). Its <c>DataContext</c> is
/// the <see cref="WBondViewModel"/> it edits — everything else it needs is a property, so a host with
/// no wBond document (a wirebond cell in the ordinary Layout Editor) supplies nothing at all.</para>
///
/// <h3>The rulers</h3>
/// <para>Driven at 1,000 DBU/µm — the resolution at which one database unit IS one nanometre, which is
/// this canvas's own world unit. Driving them at the reference layout's resolution instead would put a
/// ruler on the profile view whose labels disagreed with the profile view by that factor, and would
/// agree exactly on the 1,000 DBU/µm default, which is where such a bug would never be noticed.</para>
/// </summary>
public partial class WBondProfileView : UserControl
{
    public WBondProfileView()
    {
        InitializeComponent();

        ProfileCanvas.ViewportChanged    += (_, _) => SyncRulers();
        ProfileCanvas.CursorWorldChanged += OnCursorWorldChanged;
        ProfileCanvas.ThemeRefreshed     += () => ThemeRefreshed?.Invoke();

        DataContextChanged += (_, _) => Rebind();
    }

    /// <summary>The resolution at which one database unit IS one nanometre — this canvas's own.</summary>
    private const int NanometreDbuPerMicron = 1000;

    private WBondViewModel? Editor => DataContext as WBondViewModel;

    private WBondViewModel? _bound;

    private void Rebind()
    {
        if (_bound is not null) _bound.PropertyChanged -= OnEditorPropertyChanged;

        _bound = Editor;
        if (_bound is not null) _bound.PropertyChanged += OnEditorPropertyChanged;

        ApplyAzimuth();
        SyncRulers();
    }

    private void OnEditorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WBondViewModel.ProfileAzimuthRadians))
        {
            ApplyAzimuth();
            ProfileCanvas.InvalidateVisual();
        }
        else if (e.PropertyName == nameof(WBondViewModel.DisplayUnit))
        {
            // §6.5 — the rulers are a READOUT and follow the editor's unit, exactly as the inductance
            // panel's length rows do.
            SyncRulers();
        }
        // A selection made from somewhere else entirely — the Array Inductance panel's array
        // double-click is the one that exposed this — has to light up here too. The canvas repaints
        // itself on ReadoutChanged, but a selection changes no geometry and raises none, so in the
        // DOCKED profile view nothing happened at all (owner, 2026-08-17). The wBond editor never saw
        // it because its own code-behind repaints both canvases off the same notification.
        else if (e.PropertyName is nameof(WBondViewModel.Selection)
                                or nameof(WBondViewModel.PreviewSelection))
        {
            Repaint();
        }
    }

    private void ApplyAzimuth()
    {
        ProfileCanvas.Azimuth = _bound?.ProfileAzimuthRadians;
        SyncProfileAxisCombo();
    }

    // ── The plane control, overlaid on the canvas (§6.2) ──────────────────────
    //
    // It lived in the wBond editor's toolbar, which meant it did not exist at all in the dockable Wire
    // Profile panel — where the setting matters just as much (owner, 2026-08-17). It is this view's
    // control, so it now travels with it: one implementation, reachable in every host.

    private void OnProfileAxisCommit(object? sender, RoutedEventArgs e)
    {
        if (sender is ComboBox box) Commit(box.Text ?? "");
    }

    private void OnProfileAxisKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return) || sender is not ComboBox box) return;

        Commit(box.Text ?? "");
        e.Handled = true;
        FocusCanvas();
    }

    private void OnProfileAxisSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingProfileAxis || sender is not ComboBox { SelectedItem: string preset }) return;
        Commit(preset);
    }

    /// <summary>
    /// Commits a plane and puts the box back on the CANONICAL spelling of what was accepted — so typing
    /// "90" reads back as "YZ", and text that means nothing snaps back rather than sitting there looking
    /// as though it took.
    /// </summary>
    private void Commit(string text)
    {
        // The canvas and the rulers follow ProfileAzimuthRadians through this view's own property-change
        // handler, so committing is all this has to do.
        _bound?.CommitProfileAxisText(text);
        SyncProfileAxisCombo();
    }

    private void SyncProfileAxisCombo()
    {
        _updatingProfileAxis = true;
        ProfileAxisCombo.SelectedItem = null;
        ProfileAxisCombo.Text = _bound?.ProfileAxisText ?? "";
        _updatingProfileAxis = false;
    }

    private bool _updatingProfileAxis;

    // ── The host surface ──────────────────────────────────────────────────────

    /// <summary>Whether the two ruler strips are showing.</summary>
    public bool RulersVisible
    {
        get => ProfileRulerRow.IsVisible;
        set { ProfileRulerRow.IsVisible = value; ProfileVRuler.IsVisible = value; }
    }

    /// <summary>WB22a — per-view, because the two views of one design are usually at different zooms.</summary>
    public WireThicknessMode Thickness
    {
        get => ProfileCanvas.Thickness;
        set { ProfileCanvas.Thickness = value; ProfileCanvas.InvalidateVisual(); }
    }

    /// <summary>
    /// The draw-a-wire tool, armed. The overlay's own flags follow <c>ActiveTool</c> on the wBond
    /// document already; this canvas is a control and cannot see it, which is exactly why the Wire
    /// tool did nothing here (owner, 2026-08-16).
    /// </summary>
    public bool WireDrawArmed
    {
        get => ProfileCanvas.WireDrawArmed;
        set => ProfileCanvas.WireDrawArmed = value;
    }

    /// <summary>The grid pitch, in nanometres — the reference layout's own snap, pushed in by the host.</summary>
    public long GridPitchNm
    {
        get => ProfileCanvas.GridPitchNm;
        set { ProfileCanvas.GridPitchNm = value; ProfileCanvas.InvalidateVisual(); }
    }

    /// <summary>
    /// The resolved wire palette. This canvas is the control that has the theme notifications, so a
    /// host pushes it from here onto the layout overlay — the two views of the same wires cannot show
    /// them in different colours.
    /// </summary>
    public WBondRenderTheme WireTheme => ProfileCanvas.WireTheme;

    /// <summary>Raised after <see cref="WireTheme"/> has been re-resolved.</summary>
    public event Action? ThemeRefreshed;

    /// <summary>
    /// What the menu's "Copy" item runs. The wBond editor supplies its own MIXED copy (wires plus
    /// layout geometry plus the picture formats, §6.7); left null the item is absent, which is the
    /// honest state for a host with no clipboard story of its own.
    /// </summary>
    public Func<Task>? CopyRequested { get; set; }

    public void ZoomToFit() => ProfileCanvas.ZoomToFit();
    public void ZoomIn()    => ProfileCanvas.ZoomIn();
    public void ZoomOut()   => ProfileCanvas.ZoomOut();
    public void Zoom1To1(WBondUnit unit) => ProfileCanvas.Zoom1To1(unit);

    /// <summary>Repaints the canvas.</summary>
    public void Repaint() => ProfileCanvas.InvalidateVisual();

    /// <summary>Puts keyboard focus on the canvas, so its own key handling applies.</summary>
    public void FocusCanvas() => ProfileCanvas.Focus();

    /// <summary>True when the canvas itself is on screen — a host cycling view modes asks before focusing.</summary>
    public bool CanvasIsVisible => ProfileCanvas.IsEffectivelyVisible;

    // ── Rulers ────────────────────────────────────────────────────────────────

    private void SyncRulers()
    {
        var vp = ProfileCanvas.CurrentViewport;
        ProfileHRuler.SetViewport(vp.PanX, vp.PanY, vp.Zoom, vp.Width, vp.Height);
        ProfileVRuler.SetViewport(vp.PanX, vp.PanY, vp.Zoom, vp.Width, vp.Height);

        var unit = WBondDocumentViewModel.ToLayoutUnit(_bound?.DisplayUnit ?? WBondUnit.Mil);
        ProfileHRuler.SetUnits(NanometreDbuPerMicron, unit);
        ProfileVRuler.SetUnits(NanometreDbuPerMicron, unit);
    }

    private void OnCursorWorldChanged(object? sender, (double Span, double Z)? world)
    {
        ProfileHRuler.SetCursorWorld(world?.Span);
        ProfileVRuler.SetCursorWorld(world?.Z);
    }
}
