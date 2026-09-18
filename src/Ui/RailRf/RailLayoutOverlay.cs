// railRF's maps on the layout editor's own canvas (docs/sonnet-briefs/brief-railrf-8-board-view.md;
// railrf.md §11.6).
//
// ── WHAT THIS FILE IS NOT ──────────────────────────────────────────────────────────────────────
//
// It contains NO NAVIGATION CODE. Not a pan, not a zoom, not a marquee, not a fit. §11.6's whole
// instruction is that railRF's board pans and zooms exactly as the layout editor does — "not
// 'similarly'" — and the way to get that is to BE that control rather than to re-implement it:
//
//     Someone who has learned one of these windows has learned the other, and a near-miss is worse
//     than an absence because it is discovered by being wrong.
//
// So this overlay DECLINES every gesture. OnPointerPressed, OnPointerMoved, OnPointerReleased and
// OnKeyDown all return false, unconditionally, and the canvas's own state machine sees every event
// untouched. What the overlay does with a pointer move is read a value out and publish it; what it
// does with a press is nothing at all.
//
// The one thing railRF adds is R-rail8-4's WIDER keyboard gate, and it is deliberately not here:
// it is LayoutCanvas.NavigationKeysSuppressed, a predicate the window supplies. A gate belongs on
// the thing that owns the gestures; a copy of the gestures in an overlay is the near-miss above.
//
// ── AND IT NEVER TOUCHES THE LAYOUT MODEL ──────────────────────────────────────────────────────
//
// R-rail8-2, which is the overlay seam's own contract. Nothing railRF draws enters the .clay, and a
// map repaint goes through LayoutCanvas.InvalidateOverlay rather than invalidating LayoutPathCache —
// which on a real board is the difference between a repaint and a rebuild of half a million shapes.
// The only write this class makes anywhere is the classification override of R-rail8-11, and that
// goes to the RailDocument through a callback, never to LayoutView.

using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using CircuitRF.Design.Layout;
using CircuitRF.Design.Layout.Pdn;
using CircuitRF.Design.RailRf;
using CircuitRF.Render;
using CircuitRF.Ui.Controls;
using CircuitRF.Ui.Layout;
using SkiaSharp;

namespace CircuitRF.Ui.RailRf;

/// <summary>
/// The drop, |Z| and class maps drawn over the board, through the seam wBond already uses.
/// </summary>
/// <remarks>
/// <b>It draws and reads out; it decides nothing and navigates nothing.</b> The picture is
/// <see cref="RailMapScene"/>'s, below the firewall, so the window and a headless report draw alike
/// (R-rail8-13); the voltage under the cursor is <see cref="RailDcResult.VoltageAt"/>'s, so the
/// readout and the report cannot disagree at the same coordinate (R-rail5-2).
/// </remarks>
public sealed class RailLayoutOverlay : ILayoutCanvasOverlay
{
    /// <summary>Raised when the picture or the readout changed and the host should repaint. The host
    /// answers with <see cref="LayoutCanvas.InvalidateOverlay"/> and nothing else — see this file's
    /// header.</summary>
    public event Action? OverlayChanged;

    /// <summary>Raised when <see cref="Readout"/> changed, so a status strip can follow it without
    /// polling.</summary>
    public event Action<string?>? ReadoutChanged;

    /// <summary>
    /// Asked to force a region either way, or to clear the override (a null class).
    /// </summary>
    /// <remarks>
    /// A callback rather than a document reference, because the OVERRIDE is a document edit and a
    /// re-solve, and both belong to the view model. What is owned here is only which region was
    /// clicked, which is the half only the picture knows.
    /// </remarks>
    public Action<PdnRegionRef, PdnCopperClass?>? ForceRegion { get; set; }

    private RailDcResult? _result;
    private RailMapKind _kind = RailMapKind.Copper;
    private int _dbuPerMicron = LayoutUnits.DefaultDbuPerMicron;
    private RailMapTheme _theme = RailMapTheme.Fallback;

    private RailMapScene? _scene;
    private string? _readout;

    /// <summary>The rail result the maps are drawn from, or null before one exists.</summary>
    public RailDcResult? Result
    {
        get => _result;
        set { if (!ReferenceEquals(_result, value)) { _result = value; Invalidate(); } }
    }

    /// <summary>Which tab is showing. <b>Switching it rebuilds the SCENE and nothing else</b>
    /// (R-rail8-12): one canvas, one <c>LayoutView</c>, four tab states — a tab that rebuilt the
    /// canvas would drop the viewport and undo R-rail8-8.</summary>
    public RailMapKind Kind
    {
        get => _kind;
        set { if (_kind != value) { _kind = value; Invalidate(); } }
    }

    /// <summary>The artwork's resolution — everything the metres↔DBU bridge needs, and the only
    /// place railRF supplies it.</summary>
    public int DbuPerMicron
    {
        get => _dbuPerMicron;
        set { if (_dbuPerMicron != value) { _dbuPerMicron = value; Invalidate(); } }
    }

    /// <summary>railRF's own colours, projected from the active theme by the host.</summary>
    public RailMapTheme Theme
    {
        get => _theme;
        set
        {
            _theme = value ?? RailMapTheme.Fallback;
            OverlayChanged?.Invoke();       // colours only: the SCENE is unchanged, so it is not rebuilt
        }
    }

    /// <summary>
    /// What the cursor is currently over, or null when it is over nothing this overlay knows about.
    /// </summary>
    /// <remarks>
    /// <b>A latch, and therefore dropped in <see cref="OnFocusLost"/>.</b> It outlives the pointer
    /// move that set it by design — a readout that cleared itself on the next frame would be
    /// unreadable — which is exactly what makes it something that has to be cleared explicitly.
    /// </remarks>
    public string? Readout => _readout;

    /// <summary>The scene currently being drawn. Built on demand and cached until an input changes,
    /// because a pan must not re-run the field sampling.</summary>
    public RailMapScene Scene => _scene ??= RailMapScene.Build(_result, _kind, _dbuPerMicron);

    private void Invalidate()
    {
        _scene = null;
        SetReadout(null);
        OverlayChanged?.Invoke();
    }

    // ── draw ──────────────────────────────────────────────────────────────────────────────────

    public void Draw(SKCanvas canvas, LayoutViewport viewport, LayoutRenderTheme theme) =>
        RailMapRenderer.Draw(canvas, Scene, viewport, _theme);

    /// <summary>
    /// The union of the map, the legend, the source and load markers and the via callouts — <b>not
    /// the copper's bbox</b>, which the canvas already has.
    /// </summary>
    /// <remarks>
    /// §11.6 trap 4. A drop map is co-extensive with the copper, so including it looks harmless until
    /// a legend, a source marker or a flagged-via callout sits outside the copper's own bbox and Zoom
    /// to Fit cuts it off. The seam's own doc comment records the wBond case this rule comes from: a
    /// document on an empty scratch layout fitted to an EMPTY extent and landed at an arbitrary
    /// default, with every wire off screen.
    /// </remarks>
    public Bbox ContentBounds() => Scene.Bounds;

    // ── pointer: every gesture is DECLINED, and a move reads a value out ───────────────────────

    /// <summary>Declines. railRF has no press gesture of its own; a region is forced from the context
    /// menu, which is a route that cannot swallow a marquee or a pan.</summary>
    public bool OnPointerPressed(long worldX, long worldY, long tolDbu, KeyModifiers modifiers, int clickCount) =>
        false;

    /// <summary>
    /// Reads the value under the cursor and declines the gesture.
    /// </summary>
    /// <remarks>
    /// <b>Returning false is load-bearing, not a default.</b> Anything an overlay consumes never
    /// reaches the layout editor's own state machine — which here would mean the marquee, the pan and
    /// the hit test all stopping while the pointer is over the map, and §11.6's whole instruction is
    /// that they do not.
    /// </remarks>
    public bool OnPointerMoved(long worldX, long worldY, long tolDbu, bool leftButtonDown, KeyModifiers modifiers)
    {
        SetReadout(ReadoutAt(worldX, worldY, tolDbu));
        return false;
    }

    public bool OnPointerReleased(long worldX, long worldY) => false;

    /// <summary>Declines every key, so every navigation gesture reaches the canvas. railRF's addition
    /// to the keyboard is a GATE on the canvas, not a handler here — see this file's header.</summary>
    public bool OnKeyDown(Key key, KeyModifiers modifiers) => false;

    public void OnKeyUp(Key key, KeyModifiers modifiers) { }

    /// <summary>
    /// Part of the seam's companion-move contract, and inert here: railRF holds no selection of its
    /// own, so there is nothing for the layout editor's drag to bring along. Settable because the
    /// interface declares it settable, and the host sets it before every companion move.
    /// </summary>
    public bool CompanionPressResolvedNewSelection { get; set; }

    /// <summary>
    /// Focus has left the canvas: drop every latch, whether or not its release was ever seen.
    /// </summary>
    /// <remarks>
    /// R-rail8-6. The seam's own header states the rule and the bug behind it — a held key released
    /// over a button never reaches the canvas, the latch stays set, and from that moment every
    /// left-drag is a pan with nothing on screen explaining it. The recorded symptom named the wrong
    /// subsystem entirely: <i>"marquee select stopped working in the layout view"</i>. This overlay
    /// latches no KEY, but it does latch a readout, and a readout left standing describes copper the
    /// pointer is no longer anywhere near.
    /// </remarks>
    public void OnFocusLost() => SetReadout(null);

    private void SetReadout(string? value)
    {
        if (_readout == value) return;
        _readout = value;
        ReadoutChanged?.Invoke(value);
    }

    /// <summary>
    /// What the cursor is over, as the strip says it.
    /// </summary>
    /// <remarks>
    /// <b>The voltage comes from <see cref="RailDcResult.VoltageAt"/> and not from the tile under the
    /// pointer</b> (R-rail5-2, R-rail8-9): §2.4 asks for "the absolute voltage anywhere on the net",
    /// and brief 5 put the interpolation rule in exactly one place precisely so the window and the
    /// headless report cannot differ at the same coordinate. The picture decides only WHICH LAYER is
    /// being read — the nearest tile's — because the layer the user believes they are pointing at is
    /// the one they can see.
    /// </remarks>
    private string? ReadoutAt(long x, long y, long tolDbu)
    {
        var scene = Scene;

        // A marker first: it is the smallest target and it carries the most specific sentence.
        long reach = Math.Max(tolDbu, RailMapScene.MarkerReachDbu(_dbuPerMicron));
        RailMapMarker? nearestMarker = null;
        double nearestMarkerD2 = double.MaxValue;
        foreach (var m in scene.Markers)
        {
            double dx = m.X - x, dy = m.Y - y;
            double d2 = dx * dx + dy * dy;
            if (d2 <= (double)reach * reach && d2 < nearestMarkerD2) { nearestMarkerD2 = d2; nearestMarker = m; }
        }
        if (nearestMarker is { } marker) return marker.Readout;

        if (scene.Kind == RailMapKind.Drop) return DropReadoutAt(scene, x, y);

        // The class tab: the SMALLEST region whose extent covers the point, so a small piece sitting
        // inside a pour's bounding box is what answers rather than the pour.
        RailMapRegion? best = null;
        long bestArea = long.MaxValue;
        foreach (var r in scene.Regions)
        {
            if (!r.Bounds.Contains(x, y)) continue;
            long area = Math.Max(1, r.Bounds.MaxX - r.Bounds.MinX) * Math.Max(1, r.Bounds.MaxY - r.Bounds.MinY);
            if (area < bestArea) { bestArea = area; best = r; }
        }
        return best?.Readout;
    }

    private string? DropReadoutAt(RailMapScene scene, long x, long y)
    {
        if (_result is not { } result || scene.Tiles.Count == 0) return null;

        LayerKey? layer = null;
        double bestD2 = double.MaxValue;
        foreach (var tile in scene.Tiles)
        {
            double dx = tile.CentreX - x, dy = tile.CentreY - y;
            double d2 = dx * dx + dy * dy;
            if (d2 < bestD2) { bestD2 = d2; layer = tile.Layer; }
        }

        if (layer is not { } l) return null;
        if (result.VoltageAt(l, isReference: false, x, y) is not { } v) return null;

        string drop = result.SourceVoltageV is { } src
            ? $", {(src - v) * 1e3:0.###} mV below the source"
            : "";

        return $"{v:0.####} V on layer {l.Layer}/{l.Datatype}{drop}";
    }

    // ── R-rail8-11: forcing a region, from the class tab ───────────────────────────────────────

    /// <summary>
    /// The overlay's own right-click rows, prepended to the canvas's.
    /// </summary>
    /// <remarks>
    /// <b>Why a MENU and not a click.</b> §2.9 rule 2 requires that a region can be forced either way,
    /// and the obvious spelling — click the region — would mean consuming a press, which stops the
    /// layout editor's own marquee and hit test dead for as long as the class tab is showing (see
    /// <see cref="OnPointerMoved"/>). A context row costs the user one extra gesture and costs the
    /// canvas nothing.
    ///
    /// <para>Three rows and not two, because "put it back" has to be reachable: an override the user
    /// cannot clear is a document they cannot return to the measured answer, and the measured answer
    /// is the one the extraction will keep agreeing with as the artwork changes.</para>
    /// </remarks>
    public IReadOnlyList<object> BuildContextMenuItems(
        double worldX, double worldY, long tolDbu, LayoutEditorViewModel? layout, Avalonia.Visual host)
    {
        if (Kind != RailMapKind.Class || ForceRegion is null) return [];

        long x = (long)Math.Round(worldX), y = (long)Math.Round(worldY);

        RailMapRegion? best = null;
        long bestArea = long.MaxValue;
        foreach (var r in Scene.Regions)
        {
            if (!r.Bounds.Contains(x, y)) continue;
            long area = Math.Max(1, r.Bounds.MaxX - r.Bounds.MinX) * Math.Max(1, r.Bounds.MaxY - r.Bounds.MinY);
            if (area < bestArea) { bestArea = area; best = r; }
        }
        if (best is not { } region) return [];

        MenuItem Row(string header, PdnCopperClass? forced, bool ticked) => new()
        {
            Header = header,
            ToggleType = MenuItemToggleType.Radio,
            IsChecked = ticked,
            Command = new CommunityToolkit.Mvvm.Input.RelayCommand(
                () => ForceRegion?.Invoke(region.Region, forced)),
        };

        return
        [
            Row("Copper: treat as a trace", PdnCopperClass.Trace,
                region.Forced && region.Class == PdnCopperClass.Trace),
            Row("Copper: treat as spreading", PdnCopperClass.Spreading,
                region.Forced && region.Class == PdnCopperClass.Spreading),
            Row("Copper: use the measured classification", null, !region.Forced),
            new Separator(),
        ];
    }
}
