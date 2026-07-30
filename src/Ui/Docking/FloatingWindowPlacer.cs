using System.Collections.Generic;

namespace CircuitRF.Ui.Docking;

/// <summary>
/// Runs <see cref="ScreenPlacement.Place"/> across ONE restore pass, remembering what it has already
/// placed so the cascade (R-dock-6 step 5) works across every floating window rather than per list.
///
/// <para>This exists because a workspace restores two KINDS of floating window — tool panels, placed
/// by the layout builder, and torn-off documents, placed afterwards once the documents themselves are
/// open. Two independent cascade states would happily land a tool panel exactly on top of a document
/// window, which is the one thing the cascade is for.</para>
///
/// <para>Framework-free and stateful-by-design, so a test can drive the whole restore's placement
/// decisions in order with no display attached.</para>
/// </summary>
public sealed class FloatingWindowPlacer
{
    private readonly IReadOnlyList<ScreenRect> _screens;
    private readonly bool _sameConfiguration;
    private readonly List<ScreenRect> _placed = [];

    public FloatingWindowPlacer(IReadOnlyList<ScreenRect> screens, bool sameConfiguration)
    {
        _screens           = screens;
        _sameConfiguration = sameConfiguration;
    }

    /// <summary>Every rectangle assigned so far, in the order it was assigned.</summary>
    public IReadOnlyList<ScreenRect> Placed => _placed;

    /// <summary>Validates one saved rectangle and records the result for later cascade checks.</summary>
    public ScreenRect Place(ScreenRect saved)
    {
        var result = ScreenPlacement.Place(saved, _screens, _placed, _sameConfiguration);
        _placed.Add(result);
        return result;
    }

    /// <summary>Convenience for the four loose numbers the schema stores.</summary>
    public ScreenRect Place(double x, double y, double width, double height) =>
        Place(new ScreenRect(x, y, width, height));

    /// <summary>Builds a placer for the current screens from a layout's own recorded configuration (R-dock-8).</summary>
    public static FloatingWindowPlacer For(CwsDockLayout layout, IReadOnlyList<ScreenRect> currentScreens)
    {
        var saved = new List<ScreenRect>(layout.Screens.Count);
        foreach (var s in layout.Screens) saved.Add(new ScreenRect(s.X, s.Y, s.Width, s.Height));
        return new FloatingWindowPlacer(currentScreens, ScreenPlacement.SameConfiguration(saved, currentScreens));
    }
}
