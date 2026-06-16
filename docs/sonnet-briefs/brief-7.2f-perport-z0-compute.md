# Sonnet Brief — 7.2f: per-port (non-uniform/complex) Z0-correct compute for scattering traces

**Context.** Final 7.2 brief. 7.2a carries `Z0{port}` (per-port complex reference); 7.2e surfaces the badge +
warning. **7.2f makes the S-parameter compute actually correct when the source Z0 is non-uniform or complex** —
today it isn't. **No new RF math:** `RFNetwork` already has full per-port complex overloads (`SToZ`/`SToY`/
`SToS`/`Convert` taking `Complex[] z0`; stability internally renorms to uniform-real via `NormalizedS2Port`).
7.2f is plumbing + a UI gate.

## The bug being fixed (confirmed)
A scattering trace from a non-uniform/complex source is currently routed through `DataSetBuilder.ToSnp`, which
**collapses Z0 to port-1 and warns** (7.2a). The resulting `SNP.Z0` is a single `Complex`, and `Trace`'s S path
(`BuildMatrixPath`/`DataPoint`/`GetMarkerImpedanceString`) renorms with `RFNetwork.SToS(mat, Data.Z0, z0Array)`
where everything is **uniform**. So S→Z/Y, the Z0 renorm, and marker impedance are all computed against the
**wrong reference** for non-uniform/complex sources — exactly the footgun 7.2e warns about. 7.2f computes against
the true per-port vector instead.

## Owner decision — the Z0 textbox rule (LOCKED)
A user may **not** renormalize a trace whose source has **non-uniform** port normalization — changing one port's
reference rewrites every S-parameter; it's unmanageable per-trace, and the correct fix is to re-simulate with the
desired normalization. The Z0 input box is only the **simple uniform-renorm cheat**, valid only for uniform
sources. Therefore:
- **Source Z0 is `UniformReal`:** Z0 box **enabled** — exactly today's behavior (uniform renorm via the scalar
  `_z0`).
- **Source Z0 is `UniformComplex` or `NonUniform`:** Z0 box **disabled**. Compute strictly at the source's native
  per-port reference; no user renorm. (UniformComplex is disabled too — renorming away a complex reference is the
  same "manage the reference for them" trap, and the badge already flags it.)

## 1. Trace: carry the per-port reference + compute against it
`Trace` currently holds only a scalar `Z0` and reads `Data.Z0`. Give the S path the true per-port vector.

Add to `Trace` (set by the owner when it binds/refreshes a scattering trace — same place 7.2c/7.2e resolve the
entry; null ⇒ uniform/legacy):
```csharp
/// <summary>Per-port source reference impedance (index k = port k+1), from the source 'Z0' cube.
/// Null ⇒ uniform source (use Data.Z0). When non-null AND non-uniform/complex, the user Z0 box is
/// disabled and compute uses these values directly (no renorm).</summary>
public Complex[]? SourceZ0PerPort { get; set; }

/// <summary>True when the source reference is non-uniform-across-ports OR complex
/// (set by the owner from DataSetBuilder.ClassifyZ0). Drives compute path + textbox gating.</summary>
public bool SourceZ0IsUnusual { get; set; }
```

In `BuildMatrixPath`, `DataPoint`, `GetMarkerDataPoint`, and `GetMarkerImpedanceString`, branch on
`SourceZ0IsUnusual`:
- **Unusual source (per-port):** use `SourceZ0PerPort` as the old reference and **do not apply the user `_z0`**:
  - `MatrixType.S`: `raw = Data.Matrices[fi][Row,Col]` **as stored** (already referenced to `SourceZ0PerPort` —
    no `SToS`). (User renorm is disabled, so there is no target reference to move to.)
  - `MatrixType.Z`: `RFNetwork.SToZ(mat, SourceZ0PerPort)[Row,Col]`.
  - `MatrixType.Y`: `RFNetwork.SToY(mat, SourceZ0PerPort)[Row,Col]`.
  - Marker impedance: port reference is `SourceZ0PerPort[Row]` (the diagonal port); use it in the Γ→Z formula
    instead of the scalar `Z0` (`MarkerShowsImpedance` already requires `Row == Col`, so a single port's
    reference is well-defined).
- **Uniform/legacy source (`SourceZ0PerPort` null or `!SourceZ0IsUnusual`):** **unchanged** — today's scalar
  `_z0`/`Data.Z0` path stays exactly as is (the uniform cheat keeps working).

Keep `Convert(mat, Data.Type, Data.Z0, MatrixType, Z0)` for the uniform path; add a per-port sibling call for the
unusual path (`Convert` already has a `Complex[]` overload — pass `SourceZ0PerPort` for both old and, since no
renorm, the same vector, or call `SToZ/SToY` directly as above).

**Derived params (stability/Mu/MaxGain/circles):** unchanged — they already call `NormalizedS2Port`, which renorms
to a uniform-real internal reference regardless of source. For an unusual 2-port source, build the working `SNP`
for `BuildDerivedPath` from `SourceZ0PerPort` by first renorming the stored matrices to uniform-real
(`RFNetwork.SToS(mat, SourceZ0PerPort, RFNetwork.Z0Array(new Complex(SourceZ0PerPort[0].Real,0), n))`) so the
SNP it constructs is honest. (Stability math itself is reference-agnostic above that, so minimally: renorm to
uniform-real before the existing path. Verify against a non-uniform 2-port test.)

## 2. The owner sets the per-port fields (PlotInspectorViewModel / where traces are bound)
Where a scattering trace is bound/refreshed to a library entry (the 7.2c/7.2e resolution point), read the source
`Z0` cube and populate the new fields:
```csharp
if (entry.Data is { } ds && ds.Contains("Z0"))
{
    var z0cube = ds["Z0"];
    trace.SourceZ0PerPort   = z0cube.ComplexValues;                 // index k = port k+1
    trace.SourceZ0IsUnusual = entry.HasUnusualZ0;                   // from 7.2e (NonUniform||UniformComplex)
}
else { trace.SourceZ0PerPort = null; trace.SourceZ0IsUnusual = false; }
```
(`entry.HasUnusualZ0`/`Z0PerPort`/`Z0Kind` already exist from 7.2e — reuse, don't reclassify.)

## 3. Gate the Z0 textbox (TraceRowViewModel + view)
Add to `TraceRowViewModel`:
```csharp
/// <summary>Z0 box editable only for uniform-real sources (the simple uniform-renorm cheat).
/// Disabled for non-uniform/complex sources — the user must re-simulate to change normalization.</summary>
public bool IsZ0Editable => !_trace.SourceZ0IsUnusual && !_trace.IsCubeBound;
public string Z0DisabledReason => _trace.SourceZ0IsUnusual
    ? "Source has non-uniform/complex port normalization — renormalize by re-simulating."
    : "";
```
Raise `OnPropertyChanged(nameof(IsZ0Editable))` / `Z0DisabledReason` in the same spot 7.2e raises
`ShowZ0Badge`/`Z0BadgeTooltip` (signal/library change). In the trace-card XAML, bind the Z0 `TextBox`
`IsEnabled="{Binding IsZ0Editable}"` and put `Z0DisabledReason` on its `ToolTip.Tip` (and/or reuse the existing
badge tooltip). Keep it visually consistent with the §2.8 idiom; subtle, not a banner.

## Tests (`tests/Ui.Tests` + reuse RfCore math where headless)
1. **NonUniformSource_S_NoRenorm:** a 2-port S DataSet with `Z0=[50, 75−j10]`; a scattering trace on it →
   `BuildMatrixPath` Points equal the **stored** S element (no `SToS` shift); a uniform-50Ω trace is unchanged
   from before (regression).
2. **NonUniformSource_Z_UsesPerPort:** `MatrixType.Z` on the unusual source equals
   `RFNetwork.SToZ(mat, sourceZ0PerPort)[r,c]` (not the port-1-collapsed value).
3. **MarkerImpedance_PerPort:** marker impedance on the unusual source uses `SourceZ0PerPort[port]`, not 50 Ω /
   port-1.
4. **Z0Box_GatedByKind:** `IsZ0Editable` true for a uniform-real source, false for `UniformComplex` and
   `NonUniform`.
5. **Stability_NonUniform2Port:** Mu/MaxGain on an unusual 2-port match the values from a manually
   uniform-real-renormalized equivalent (the honesty check for derived params).

## Gate
Build 0W/0E; tests green. Manual: simulate S-params with per-port complex Term `Z` → its `.npy` trace shows the
badge (7.2e), the Z0 box is **disabled** with the explanatory tooltip, and S/Z/Y + marker impedance read at the
true per-port reference; a normal 50 Ω Touchstone trace is unchanged and its Z0 box still renorms. This closes
Phase 7.2.

## On completion
Note in `src/Ui/CLAUDE.md`: scattering traces now compute against the source's true per-port `Z0` vector
(`SourceZ0PerPort`/`SourceZ0IsUnusual`, populated from the `Z0` cube via 7.2e classification); the user Z0
renorm box is enabled only for uniform-real sources (the simple-renorm cheat) and disabled for
non-uniform/complex sources (re-simulate to change normalization). Uniform/Touchstone path unchanged. **Phase
7.2 complete.** Next: 7.3 (multi-dim sweep dialog / families).
