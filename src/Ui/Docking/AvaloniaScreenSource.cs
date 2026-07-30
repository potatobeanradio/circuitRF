using System;
using System.Collections.Generic;
using Avalonia.Controls;

namespace CircuitRF.Ui.Docking;

/// <summary>
/// The one place device pixels become logical units, and back (R-dock-7).
///
/// <para><b>The mixed-unit trap this exists to contain</b>, confirmed by decompiling
/// <c>Dock.Avalonia.Controls.HostWindow</c> rather than assumed:
/// <c>SetPosition</c> assigns <c>Window.Position = new PixelPoint((int)x, (int)y)</c> — <b>device
/// pixels</b> — while <c>SetSize</c> assigns <c>Layoutable.Width/Height</c> — <b>logical DIPs</b>.
/// So <see cref="Dock.Model.Core.IDockWindow"/>'s own X/Y and Width/Height are in DIFFERENT units.
/// Persisting those four numbers unconverted is the bug R-dock-7 describes: subtly wrong on one
/// machine, absurd on another, and invisible when testing on a single monitor.</para>
///
/// <para>What is stored in <c>.cws</c> is therefore uniformly <b>logical</b>: the position divided by
/// the scaling of the screen it sits on, and the size as Avalonia already reports it.
/// <see cref="Avalonia.Platform.Screen.WorkingArea"/> — <b>working area, not bounds</b> (R-dock-6
/// step 1: the working area excludes the taskbar, dock and menu bar, and a window placed under one
/// of those is effectively lost) — is divided by that screen's own scaling for the same reason.</para>
///
/// <para><b>Stated limitation.</b> On a MIXED-DPI multi-monitor setup this per-screen division does
/// not produce a contiguous global logical space (screen origins live in one shared device-pixel
/// space), so a saved logical position can land between two derived screen rectangles. The
/// consequence is bounded and safe: <see cref="ScreenPlacement"/> then treats the window as
/// unreachable and relocates it onto the nearest screen. A window is never lost; at worst it is
/// moved when it need not have been. Uniform-DPI setups — including every single-monitor one — are
/// exact.</para>
///
/// <para>Deliberately thin and untested: everything that makes a decision lives in
/// <see cref="ScreenPlacement"/>, which knows nothing about Avalonia and is unit-tested against
/// synthetic screens.</para>
/// </summary>
public static class AvaloniaScreenSource
{
    /// <summary>Current screens' working areas in logical units. Empty when no display info is available.</summary>
    public static IReadOnlyList<ScreenRect> WorkingAreas(Screens? screens)
    {
        var result = new List<ScreenRect>();
        if (screens?.All is not { } all) return result;

        foreach (var screen in all)
        {
            if (screen is null) continue;
            var wa = screen.WorkingArea;
            result.Add(ScreenPlacement.WorkingAreaToLogical(
                new ScreenRect(wa.X, wa.Y, wa.Width, wa.Height), ScalingOf(screen)));
        }
        return result;
    }

    /// <summary>
    /// Converts a floating window's raw Dock-model values into one logical rectangle.
    /// <paramref name="deviceX"/>/<paramref name="deviceY"/> are device pixels (see the class note);
    /// <paramref name="logicalWidth"/>/<paramref name="logicalHeight"/> are already logical.
    /// </summary>
    public static ScreenRect ToLogical(double deviceX, double deviceY, double logicalWidth, double logicalHeight, Screens? screens)
    {
        var scaling = ScalingAtDevicePoint(deviceX, deviceY, screens);
        return new ScreenRect(
            ScreenPlacement.DeviceToLogical(deviceX, scaling),
            ScreenPlacement.DeviceToLogical(deviceY, scaling),
            logicalWidth, logicalHeight);
    }

    /// <summary>Inverse of <see cref="ToLogical"/>: the device-pixel position for a logical rectangle.</summary>
    public static (double X, double Y) ToDevicePosition(ScreenRect logical, Screens? screens)
    {
        var scaling = ScalingAtLogicalPoint(logical.X, logical.Y, screens);
        return (ScreenPlacement.LogicalToDevice(logical.X, scaling),
                ScreenPlacement.LogicalToDevice(logical.Y, scaling));
    }

    /// <summary>DPI factor of the screen whose working area contains the given DEVICE-pixel point; 1 if none.</summary>
    public static double ScalingAtDevicePoint(double x, double y, Screens? screens)
    {
        if (screens?.All is not { } all) return 1.0;
        foreach (var screen in all)
        {
            if (screen is null) continue;
            var wa = screen.WorkingArea;
            if (x >= wa.X && x < wa.X + wa.Width && y >= wa.Y && y < wa.Y + wa.Height)
                return ScalingOf(screen);
        }
        return PrimaryScaling(screens);
    }

    /// <summary>DPI factor of the screen whose LOGICAL working area contains the given point; 1 if none.</summary>
    public static double ScalingAtLogicalPoint(double x, double y, Screens? screens)
    {
        if (screens?.All is not { } all) return 1.0;
        foreach (var screen in all)
        {
            if (screen is null) continue;
            var scaling = ScalingOf(screen);
            var wa      = screen.WorkingArea;
            var r       = ScreenPlacement.WorkingAreaToLogical(
                              new ScreenRect(wa.X, wa.Y, wa.Width, wa.Height), scaling);
            if (x >= r.X && x < r.Right && y >= r.Y && y < r.Bottom)
                return scaling;
        }
        return PrimaryScaling(screens);
    }

    private static double ScalingOf(Avalonia.Platform.Screen screen) =>
        screen.Scaling > 0.0 ? screen.Scaling : 1.0;

    private static double PrimaryScaling(Screens? screens) =>
        screens?.Primary is { } p ? ScalingOf(p) : 1.0;
}
