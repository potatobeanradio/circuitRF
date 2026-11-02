# Brief — Loadpull UI 08: recognize a simulated LP `run.npy` as a loadpull source (shape-based, group-aware)

**Goal:** Make the Data Display recognize a **simulated Loadpull analysis result** (a `run.npy` group) as a
loadpull dataset — eligible for a `LoadpullSurface` + contour trace — **identically** to an ingested
`.spl`/`.lpcwave` file. Today recognition is gated on `SourceKind.Spl`/`.Lpcwave`; a simulated LP result
loads as `SourceKind.Npy`, so it is selectable but never treated as loadpull. This brief replaces the
source-kind gate with a **content/shape predicate** and is headless + fully testable.

**Depends on:** nothing new (the LP run already writes `run.npy`; the 7.4 contour stack is complete).
**Reads with:** `docs/design/loadpull-contours.md` (§2.2 "one shape, two producers"; §3 7.4f/7.4g),
`src/Engine/Loadpull/LoadpullEngine.cs` `BuildLoadpullDataSet` (the canonical shape — the contract),
`docs/sonnet-briefs/brief-7.4f-loadpull-ingest.md` (the `.spl`/`.lpcwave` readers match that contract),
`docs/sonnet-briefs/project-brief-7.4g-loadpull-source-entry-point-complete.md` (LP `run.npy` is already
listed as a selectable source).

## The exact gap (confirmed)

- **Producing end (done):** `LoadpullEngine.BuildLoadpullDataSet` emits a flat `DataSet` with cubes
  `Pout` (Watts), `Gt`/`Gp` (dB), `DE`/`PAE` (linear), `Pdc`, `BiasV*`/`BiasI*`, `Converged`, `IsTickle`,
  `PavlDbm`, `StopCode`, `ZLoad`/`GammaLoad` (Complex), `V`/`INl`, over axes `gridPoint`, `pinStep`, `node`,
  `harmonic`. `SchematicRunService` then nests these under the analysis-name **group** (e.g. `LP1`) in the
  grouped `run.npy`, written by `RunResultsWriter`.
- **`.spl`/`.lpcwave` (done):** the RfCore readers produce the **same** cube names/axes/units (the 7.4f
  contract is "match `BuildLoadpullDataSet` exactly"), exposed as `SourceKind.Spl`/`.Lpcwave` — typically
  **flat** (top-level / DefaultGroup).
- **Recognition (the gap):** the contour-trace eligibility / `LoadpullSurface` ingest keys on
  `SourceKind.Spl`/`.Lpcwave`. An LP `run.npy` is `SourceKind.Npy`, so it is excluded — even though its
  `LP1` group is byte-for-byte the canonical loadpull shape.

**So the data needs no change.** The fix is recognition + locating the loadpull cubes inside a group.

## 1 — The shape predicate (`LoadpullRecognition`)

Add a framework-free static recognizer (no Avalonia). Put it where the loadpull-surface consumer can reach
it and where it can be unit-tested — `src/Ui/DataDisplay/Models/LoadpullRecognition.cs` is fine (it operates
on `DataSet`/`DataCube`, both framework-free), or `src/Core/Data` if you prefer it shared with splotRF.

API:
```csharp
public static class LoadpullRecognition
{
    /// <summary>A loadpull "view" inside a source DataSet: the group name holding the loadpull cubes
    /// (null/empty = top level / DefaultGroup), and the cube/axis names located within it.</summary>
    public readonly record struct LoadpullView(string? Group);

    /// <summary>Returns every loadpull-shaped view in <paramref name="ds"/>: the top level AND each
    /// named group that carries the canonical loadpull signature. Empty when none (HB/DC/S-param/etc.).
    /// A run.npy with LP1 + LP2 returns two views; a flat .spl returns one (top-level).</summary>
    public static IReadOnlyList<LoadpullView> FindLoadpullViews(DataSet ds);

    /// <summary>Convenience: true when at least one loadpull view exists.</summary>
    public static bool IsLoadpull(DataSet ds) => FindLoadpullViews(ds).Count > 0;
}
```

**The signature (what makes a view "loadpull"):** within a group (or at top level), require BOTH:
1. a termination cube — **`GammaLoad`** OR **`ZLoad`** — over a single axis named **`gridPoint`**, AND
2. at least one FOM cube — any of **`Pout`/`Gt`/`Gp`/`DE`/`PAE`** — over axes **`{gridPoint, pinStep}`**
   (order: gridPoint then pinStep).

Match cube/axis names with the same casing `BuildLoadpullDataSet` uses (`Ordinal`). Use the existing
`DataSet` group API to enumerate groups + their cubes (grep how the trace picker enumerates groups —
`DataSet.DefaultGroup`, the group accessor used by `RebuildSignals` in `TraceRowViewModel`). Do not hard-code
the group name `LP1`; detect by shape so any analysis name works and so `.spl` (flat) and `run.npy` (grouped)
both resolve.

**Why both a Γ and Z accept:** the substrate (Smith vs Rect) is chosen downstream; either cube proves the
data is a swept-termination loadpull field. The LP engine emits both; `.spl`/`.lpcwave` emit at least one.

## 2 — Wire the predicate into the recognition seam

Grep `src/Ui/DataDisplay` for `SourceKind.Spl` and `SourceKind.Lpcwave` to find every place that decides
"this source is loadpull" / "offer a contour trace" / "build a LoadpullSurface" (likely in
`TraceRowViewModel`, `PlotInspectorViewModel`, and/or `PlotViewModel` — confirm by grep). At each such gate,
replace the source-kind test with a loadpull check that also accepts a shape match:
```csharp
// before: entry.Kind is SourceKind.Spl or SourceKind.Lpcwave
// after:
bool isLoadpull = entry.Kind is SourceKind.Spl or SourceKind.Lpcwave
                  || (entry.Data is { } ds && LoadpullRecognition.IsLoadpull(ds));
```
Keep `SourceKind.Spl/.Lpcwave` as a fast path (and so a flat measured file with an unusual group layout
still works); the shape check brings in `SourceKind.Npy` run results. Do **not** remove the `.spl`/`.lpcwave`
source kinds — they still drive the file-picker/loader path.

If a gate needs to know *which* group holds the loadpull cubes (it will, for the grouped run.npy — that is
brief 09's binding work), have it call `FindLoadpullViews` and carry the `Group` forward. In THIS brief, the
recognition (eligibility) is enough; the surface construction's group-awareness is brief 09.

## 3 — Tests (headless, `tests/Ui.Tests` or the contour test project)

Build synthetic `DataSet`s and assert recognition:
- **Flat loadpull** (mimics `.spl`): top-level `GammaLoad{gridPoint}` + `Pout{gridPoint,pinStep}` →
  `IsLoadpull` true; one view with `Group == null`.
- **Grouped loadpull** (mimics LP `run.npy`): cubes under group `LP1` → `IsLoadpull` true; one view with
  `Group == "LP1"`.
- **Two LP groups** (`LP1` + `LP2`) → two views.
- **Z-only** (`ZLoad` but no `GammaLoad`) → recognized.
- **Negative:** an HB group (`V{node,harmonic}` only), a DC group, an S-param DataSet (`S`, `freq`) →
  `IsLoadpull` false, zero views.
- **Near-miss:** a group with `Pout` but no `GammaLoad`/`ZLoad`, or with `GammaLoad` over a non-`gridPoint`
  axis → false (the signature requires both, with the right axis).

## Out of scope (→ brief 09)
- Constructing the `LoadpullSurface` from the located group's cubes and binding the contour card. (This
  brief only makes the source *eligible*; the surface builder's group-awareness + the end-to-end render gate
  are brief 09.)

## Verify
1. `dotnet build` zero warnings; `dotnet test` green incl. the recognition tests.
2. Firewall passes (`LoadpullRecognition` is framework-free; only the gate edits touch UI VMs).
3. After this brief, a simulated LP `run.npy` is *recognized* as loadpull (the contour trace becomes
   offerable); rendering it is brief 09.
