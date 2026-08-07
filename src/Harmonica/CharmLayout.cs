namespace CircuitRF.Harmonica;

/// <summary>Which panel a placement describes. Stable strings — they appear in every saved
/// <c>.charm</c>, so renaming one silently drops that panel's placement for every existing file.</summary>
public static class HarmonicaPanelId
{
    public const string SmithPower      = "smith.power";
    public const string SmithEfficiency = "smith.efficiency";
    public const string Loadline        = "loadline";
    public const string PowerSweep      = "powersweep";
    public const string ReadoutStrip    = "readout";
}

/// <summary>
/// One panel's placement, as FRACTIONS of the document area — never pixels, so the layout survives a
/// window resize and a different display without re-deriving anything.
/// </summary>
public readonly record struct CharmPanelPlacement(string PanelId, double X, double Y, double W, double H);

/// <summary>
/// R-h45-1 — the §7.1 layout, <b>as data</b>.
///
/// <para>"Locked by default; Edit Display (H7) unlocks it. The layout is data, not code — it persists
/// in the <c>.charm</c> — so that H7 has something to unlock rather than something to rewrite."
/// That last clause is the whole reason this type exists in M3 rather than in H7: if the four panels
/// were positioned by a hand-written AXAML grid, H7 would have to REPLACE the layout mechanism to
/// make it editable, and every <c>.charm</c> written before then would carry no placement at all.
/// Storing fractions from the start means H7 only has to flip <see cref="Locked"/> and start writing
/// to the same field.</para>
///
/// <para><b>The default IS §7.1, transcribed</b>: two Smith charts side by side across the top-left
/// (power left, efficiency right), the dense settings/readout strip spanning beneath BOTH of them,
/// and a right-hand column holding the loadline above the power sweep at full height.</para>
/// </summary>
public sealed record CharmLayout
{
    /// <summary>Fraction of the width taken by the right-hand (loadline + power sweep) column.</summary>
    public const double RightColumnWidth = 0.35;

    /// <summary>Fraction of the height above the readout strip, in the left region.</summary>
    public const double ChartsHeight = 0.62;

    /// <summary>Fraction of the height taken by the loadline, in the right column.</summary>
    public const double LoadlineHeight = 0.50;

    public IReadOnlyList<CharmPanelPlacement> Panels { get; init; } = DefaultPanels;

    /// <summary>§7.1 — "locked by default". H7's Edit Display is what clears this.</summary>
    public bool Locked { get; init; } = true;

    public static readonly IReadOnlyList<CharmPanelPlacement> DefaultPanels =
    [
        // ┌───────────────────┬───────────────────┬──────────────────────┐
        // │ Smith — POWER     │ Smith — EFFICIENCY│ Rect — DCIV+LOADLINE │
        // │                   │                   ├──────────────────────┤
        // ├───────────────────┴───────────────────┤ Rect — POWER SWEEP   │
        // │  DENSE SETTINGS / READOUTS            │                      │
        // └───────────────────────────────────────┴──────────────────────┘
        new(HarmonicaPanelId.SmithPower,      0.0,                    0.0,          (1 - RightColumnWidth) / 2, ChartsHeight),
        new(HarmonicaPanelId.SmithEfficiency, (1 - RightColumnWidth) / 2, 0.0,      (1 - RightColumnWidth) / 2, ChartsHeight),
        new(HarmonicaPanelId.ReadoutStrip,    0.0,                    ChartsHeight, 1 - RightColumnWidth,       1 - ChartsHeight),
        new(HarmonicaPanelId.Loadline,        1 - RightColumnWidth,   0.0,          RightColumnWidth,           LoadlineHeight),
        new(HarmonicaPanelId.PowerSweep,      1 - RightColumnWidth,   LoadlineHeight, RightColumnWidth,         1 - LoadlineHeight),
    ];

    public static readonly CharmLayout Default = new();

    /// <summary>True when nothing has been moved and the layout is still locked — the ordinary case,
    /// and the one that must write NO block so an untouched <c>.charm</c> re-serialises unchanged.</summary>
    public bool IsDefault
    {
        get
        {
            if (!Locked || Panels.Count != DefaultPanels.Count) return false;
            for (int i = 0; i < Panels.Count; i++)
                if (Panels[i] != DefaultPanels[i]) return false;
            return true;
        }
    }

    /// <summary>The placement for a panel, or the §7.1 default when the file did not state one — so a
    /// <c>.charm</c> written before a panel existed still positions it sensibly rather than at (0,0).</summary>
    public CharmPanelPlacement PlacementOf(string panelId)
    {
        foreach (var p in Panels) if (p.PanelId == panelId) return p;
        foreach (var p in DefaultPanels) if (p.PanelId == panelId) return p;
        return new CharmPanelPlacement(panelId, 0, 0, 1, 1);
    }
}
