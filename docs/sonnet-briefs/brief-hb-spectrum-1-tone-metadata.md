# Sonnet Brief — HB spectrum, stage 1: per-sweep-point tone frequencies as stacking metadata

**Context.** The HB harmonic axis stores physical frequencies `k·f0` (two-tone: `k₁f₁+k₂f₂`). When a
sweep changes the fundamental (sweeping the tone variable), `DataCube.PrependAxis` freezes the
harmonic/mixIndex axis at the **first** sweep point, so absolute harmonic frequencies are wrong for
every later point (the *order* stays correct). The durable fix is to make that axis carry integer
**indices** and derive frequency from the per-operating-point tone frequencies — but that flip breaks
every consumer at once, so we stage it.

**This brief is stage 1 — purely additive, no representation change, no user-visible behavior change.**
It guarantees the per-sweep-point tone frequencies survive stacking (single-tone currently emits none),
so stage 2 can flip the axis to indices and reconstruct frequency from this metadata. Two-tone already
emits a `ToneFreqs` cube that stacks; single-tone is the gap.

**Owner decision (LOCKED).** Long-term we adopt the index-axis representation (chosen for two-tone,
where a frozen `k₁f₁+k₂f₂` axis can't even be inverted to `(k₁,k₂)`). Stage 1 lays the data foundation;
stage 2 does the flip. Keep everything green at stage 1.

---

## Part A — Single-tone emits `ToneFreqs` (matches two-tone)

`HbEngine.BuildSingleToneDataSet` (`src/Engine/HarmonicBalance/HbEngine.cs`) currently emits no
fundamental metadata. Add, mirroring `BuildTwoToneDataSet`'s `ToneFreqs`:

```csharp
var toneAxis = new Axis("tone", [1.0], "");
ds.Add("ToneFreqs", new DataCube([toneAxis], new double[] { f0 }));
```

- `f0` is already a parameter of `BuildSingleToneDataSet`. Emit it for **every** single-tone HB run
  (swept or not) so reconstruction is uniform.
- It must be a **non-`__`** cube so `DataSet.StackSweepAxis` *stacks* it (the `__`-prefix path is
  pass-through, which is wrong here — `f0` varies per sweep point when the tone is swept). After
  stacking it becomes `ToneFreqs[sweep…, tone(1)]`, carrying `f0` per point.

`BuildTwoToneDataSet` already emits `ToneFreqs[f1, f2]` (non-`__`) — **verify** it stacks to
`[sweep…, tone(2)]` per point (it does, by being non-`__`). No change needed there beyond a test.

## Part B — Reconstruction helper (single home for the rule)

Add a tiny tested helper (Core `src/Core/Expressions/` or an engine static — pick the layer the stage-2
UI can reference; RfCore is fine if the UI references RfCore). It is the one place the
index/order→frequency rule lives, ready for stage 2:

```csharp
public static class HbSpectrum
{
    /// Single-tone: physical frequency of harmonic `order` at fundamental `f0`.
    public static double HarmonicFreqHz(int order, double f0) => order * f0;

    /// Two-tone: physical frequency of mixing product (k1,k2) at tones (f1,f2).
    public static double MixFreqHz(int k1, int k2, double f1, double f2) => k1 * f1 + k2 * f2;
}
```

Not consumed yet — stage 2 wires it into the marker/table/label paths. Including it now keeps stage 2
mechanical and gives us a tested unit.

## Part C — Keep `ToneFreqs`/`MetaMixOrder` out of the signal picker

So stage 1 adds no user-visible signal: ensure the Data Display's signal/axis picker excludes the
run-metadata cubes `ToneFreqs` and `MetaMixOrder` (they are not plottable traces). If two-tone already
filters them, single-tone inherits it; otherwise add name-based exclusion alongside the existing
`__`-prefix filter. Verify a single-tone HB run shows no new `ToneFreqs` entry in the picker.

---

## Tests
Engine (`tests/Engine.Tests`):
1. **SingleTone_EmitsToneFreqs:** a single-tone HB run's DataSet contains `ToneFreqs` with axis `tone`
   length 1, value == `f0`.
2. **ToneFreqs_StacksPerSweepPoint (single-tone):** an HB wrapped in a sweep of the tone variable
   (e.g. `RFfreq` over `[1,5.5,10] GHz`) → stacked `ToneFreqs[sweep, tone]` has values
   `[[1e9],[5.5e9],[1e10]]` (per point, **not** frozen). This is the regression guard that makes the
   stage-2 fix possible.
3. **TwoTone_ToneFreqs_StacksPerSweepPoint:** same for two-tone (`ToneFreqs[sweep, tone(2)]` carries
   the per-point `f1,f2`).

Core (`tests/Core.Tests`):
4. **HbSpectrum_Reconstruct:** `HarmonicFreqHz(2, 5.5e9) == 11e9`; `MixFreqHz(1,-1, 2e9, 2.1e9) == -1e8`.

UI (`tests/Ui.Tests`):
5. **Picker_ExcludesToneFreqs:** a single-tone HB DataSet exposes no pickable signal named `ToneFreqs`
   (nor `MetaMixOrder`).

## Gate
Build 0W/0E; full suite green. **No user-visible change:** existing single-tone and two-tone plots,
markers, tables, and exports are byte-identical (the harmonic axis is unchanged this stage); the only
addition is the `ToneFreqs` metadata cube, hidden from the picker.

## On completion
Note in `src/Engine/CLAUDE.md` and `docs/design/harmonic-balance.md`: every HB run now emits a stacking
`ToneFreqs` cube (single-tone `[f0]`, two-tone `[f1,f2]`) carrying the per-operating-point fundamental(s)
— it survives a sweep of the tone (unlike the frozen harmonic-axis frequencies). `HbSpectrum`
centralizes index/order→frequency reconstruction. This is the foundation for stage 2.

**Stage 2 (next brief) — the coordinated flip (do NOT do here):** change the harmonic axis to integer
orders `[0..K]` and the `mixIndex` axis to integer indices `[0..M-1]` (labels `(k₁,k₂)` retained / emit
the integer `(k₁,k₂)` pairs as metadata), with unit no longer a frequency unit; then update every
consumer to read the index and reconstruct frequency via `HbSpectrum` + `ToneFreqs[slice]`:
`Trace.cs` (marker family/X readouts, `GetStemFreqString`, `HarmonicOrderOf` removal,
`BuildCubePath`/`SetFamilyData` xScale), `TableRenderer.cs` (`IsFreqUnit` column scaling),
`Plot.XLabel`, `.npy`/export content + `docs/design/npy-data-consumer-guide.md`, and engine-side
`TwoToneMeasurements`. The marker/table/label code will need per-slice access to `ToneFreqs` — that
sibling-cube plumbing is the main design item for stage 2.
