# Brief 7.4c — Off-grid power-sweep synthesis (DataInterpStack)

**Status:** Completed 2026-06-20  
**Phase:** 7.4c — power-sweep synthesis engine  
**Tests added:** 14 gate tests in `LoadpullPowerSweepTests.cs`  
**Total tests:** 179

## What was built

Extended `LoadpullSurface` with the `DataInterpStack` engine — a stack of RBF surfaces
spanning a 16-dBm back-off ladder that synthesizes a drive-up (power sweep) at any
arbitrary off-grid load Γ.

### Files changed
- `RfCore/src/Loadpull/LoadpullSurface.cs` — added 7.4c block
- `RfCore/tests/RfCore.Tests/LoadpullPowerSweepTests.cs` — 14 new gate tests

### Key decisions

**Sweep basis = "PavlDbm" (not "Pout"):**  
Brief said `SweepKeyName = "Pout"`, but "Pout" in this codebase is in Watts (converted from
dBm by `DbmToW` in `LoadpullFomDialect`). The 16-dB OBO requires a dBm quantity.
`"PavlDbm"` (available input power in dBm) is used as the sweep basis instead.
Result: the back-off ladder `linspace(PinAtComp - 16, PinAtComp, 32)` is in dBm and
the 1-D interp `Interp1D(PavlDbm_driveup, metric_driveup)` is keyed on a monotone axis.

### API added

```csharp
// Public constants
public const int InterpStackOBO  = 16;
public const int NumInterpStacks = 32;
public const int NumInterpSweep  = 160;
public const int MinStackNodes   = 12;
public const string SweepKeyName = "PavlDbm";

// Public result type
public sealed record PowerSweep(double[] X, double[] Y, string MetricX, string MetricY);

// Public method
public PowerSweep? GetPowerSweep(
    int freqIdx, Complex queryCoord, string metricX, string metricY,
    double compressionVal, SurfacePlane plane, double? z0 = null,
    RbfKernel kernel = RbfKernel.Multiquadric, double smooth = 1e-3);
```

### Algorithm notes

Port of `SPLData.generate_PS_interpolator_at_compression` + `get_power_sweep`.

- Per grid point: build 32-level back-off ladder in PavlDbm space, interp drive-up at each level
- Fit one `Rbf2D` per level (drop if `NodeCount <= 12`)
- Evaluate all 3 stacks (sweep_key, metricX, metricY) at query Γ → 32 samples
- Sort by sweep, re-interp to 160 evenly spaced final points
- Low-power HACK: clamp `result_x[0]` to `minSweepKey` to suppress edge noise
