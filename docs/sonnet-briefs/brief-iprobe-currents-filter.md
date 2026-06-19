# Sonnet Brief — current-picker filter to IProbe branches (V/I symmetry)

Goal: in the Data Display trace card, surface only **IProbe** branch currents by default, with a
**"Show all branches"** toggle that reveals the device-port currents (HB's `I:<instance>:<terminal>`).
This is the current-side analogue of the existing user-labeled-node voltage filter
(`__LabeledNodes` + `ShowAllNodes`). Engine Brief 1 already stamps the provenance cube `__ProbeBranches`
on DC and single-tone HB results; this brief consumes it. Display layer only.

Read first: the existing node-voltage filter in `TraceRowViewModel.RebuildAxisRolesCore`
(`__LabeledNodes` + `ShowAllNodes` + `ShowAllNodesToggleVisible`) and `RebuildSignals` (the cube-signal
loop). Build 0W/0E (`TreatWarningsAsErrors=true`); tests green.

## Background (verified on disk)

- `__ProbeBranches` is a cube with one axis named `"probe"` whose `Labels` are the probe names — i.e. the
  `I:<name>` cube keys **without** the `I:` prefix. Present once per analysis group that has IProbes
  (DC + single-tone HB).
- `RebuildSignals` already emits `I:*` cubes as cube signals: HB shows both `HB1.I:<probe>` (rank-1 over
  harmonic) and `HB1.I:<instance>:<terminal>` (device-port). They are NOT distinguished today — the user
  sees every device port. DC's `I:<probe>` are rank-0 scalars (surfaced on Table by the separate
  scalars-on-Table brief; the filter logic here still applies to them).
- The node filter is **per-trace** (`ShowAllNodes` on `TraceRowViewModel`); mirror that scope.

## Mechanism difference (why this lives in RebuildSignals, not the axis editor)

`V` is one cube with a labeled `node` axis, so its filter narrows the **pin dropdown** in the axis-role
editor. Currents are **separate `I:<key>` cubes**, so the branch filter controls **which `I:*` signals
appear in the combo** — it belongs in `RebuildSignals`, not the axis editor. Same UX, different seam.

## 1. Filter the current signals — `RebuildSignals` cube loop

Inside `foreach (var group in ds.Groups)`, before the `foreach (var (bareName, cube) in ds.CubesIn(group))`
inner loop, compute the group's probe-name set (null when the group has no `__ProbeBranches`):

```csharp
// Probe-branch provenance for this group (Brief 1). null ⇒ no IProbe info → don't filter currents.
HashSet<string>? probeSet = null;
if (ds.CubesIn(group).TryGetValue("__ProbeBranches", out var pbCube))
{
    var probeAxis = pbCube.Axes.FirstOrDefault(a => a.Name == "probe" && a.Labels is not null)
                 ?? pbCube.Axes.FirstOrDefault(a => a.Labels is not null);
    if (probeAxis?.Labels is { } pl)
        probeSet = new HashSet<string>(pl, StringComparer.Ordinal);
}
```

Then, inside the inner loop, after the existing skip rules and before emitting the `TraceDataItem`, add a
current-branch filter: an `I:<key>` cube is hidden unless it's a probe OR `ShowAllBranches` is on. Only
applies when the group actually has probe provenance (`probeSet is not null`); groups without it (e.g.
imported/legacy) are unaffected.

```csharp
// Branch-current filter: by default show only IProbe currents; hide device-port currents
// (I:<instance>:<terminal>) unless "Show all branches" is on. Mirrors the labeled-node voltage filter.
if (!ShowAllBranches && probeSet is not null
    && bareName.StartsWith("I:", StringComparison.Ordinal)
    && !probeSet.Contains(bareName[2..]))
{
    continue;
}
```
Place this alongside the other `continue` skip rules (after the rank check is fine). Everything else in the
loop (qualified name, default slice, `TraceDataItem`) is unchanged.

## 2. `ShowAllBranches` toggle — `TraceRowViewModel`

Mirror `ShowAllNodes` exactly:

```csharp
[ObservableProperty]
private bool _showAllBranches;

partial void OnShowAllBranchesChanged(bool value) => RebuildSignals();
```
(`RebuildSignals` re-filters `AvailableSignals` and re-selects the current signal — it's already the
rebuild entry point, called from `OnMatrixTypeChanged`. Calling it here is consistent. It does NOT need
the `_rebuildingAxisRoles`-style guard that `ShowAllNodes` uses; that guard exists only to stop
`RebuildAxisRoles` re-entrancy, and `RebuildSignals` is not re-entered from itself.)

Visibility predicate — show the toggle only when there is something to reveal (the trace is cube-bound and
some loaded analysis group has `I:*` cubes that are NOT in its `__ProbeBranches`):

```csharp
/// <summary>True when the "Show all branches" toggle is relevant — a cube source has device-port
/// currents hidden behind the IProbe filter.</summary>
public bool ShowAllBranchesToggleVisible
{
    get
    {
        if (!IsCubeBoundTrace) return false;
        foreach (var entry in _parent.LibraryEntries)
        {
            var ds = entry.Data;
            if (ds is null) continue;
            foreach (var group in ds.Groups)
            {
                var cubes = ds.CubesIn(group);
                if (!cubes.ContainsKey("__ProbeBranches")) continue;
                var probeAxis = cubes["__ProbeBranches"].Axes.FirstOrDefault(a => a.Labels is not null);
                var probes = probeAxis?.Labels is { } pl
                    ? new HashSet<string>(pl, StringComparer.Ordinal) : null;
                if (probes is null) continue;
                foreach (var name in cubes.Keys)
                    if (name.StartsWith("I:", StringComparison.Ordinal) && !probes.Contains(name[2..]))
                        return true;   // a hideable device-port current exists
            }
        }
        return false;
    }
}
```
Raise `OnPropertyChanged(nameof(ShowAllBranchesToggleVisible))` wherever `IsCubeBoundTrace` /
`ShowAllNodesToggleVisible` are already re-raised (in `OnSelectedSignalChanged` and `RefreshDescription`),
so the toggle appears/disappears as the source kind changes.

## 3. Toggle UI — trace card (AXAML)

In the trace-card view (`PlotInspectorView.axaml` / the cube-bound trace body), add a small checkbox
bound to `ShowAllBranches`, gated by `ShowAllBranchesToggleVisible`, placed near the signal combo — clone
the existing **`ShowAllNodes`** checkbox markup and its `IsVisible` binding, relabel to "Show all
branches". Keep the same styling/idiom.

## Tests — `tests/Ui.Tests` (headless; mirror existing TraceRowViewModel signal-list tests)

1. **Hb_CurrentPicker_ShowsOnlyProbes_ByDefault:** a grouped source with `HB1.I:Ids` (probe) +
   `HB1.I:M1:d` (device port) and `HB1.__ProbeBranches=["Ids"]` → `AvailableSignals` contains the
   `I:Ids` item, NOT `I:M1:d`.
2. **Hb_CurrentPicker_ShowAllBranches_RevealsDevicePorts:** set `ShowAllBranches = true` → `I:M1:d`
   now appears.
3. **NoProvenance_NoFiltering:** a source group with `I:*` cubes but no `__ProbeBranches` (legacy/imported)
   → all `I:*` appear regardless of `ShowAllBranches` (filter is provenance-gated).
4. **ToggleVisible_OnlyWhenHideable:** `ShowAllBranchesToggleVisible` is true when a device-port current is
   hidden, false for a probe-only (DC-style) source.
5. **Voltages_Unaffected:** `V` cube signals and the node-axis filter are unchanged by this work.

## Gate (manual)
Single-tone HB with an IProbe `Ids` and an SDD device with port currents → open a Data Display, import
`run.npy`, add a cube trace: the current combo lists `HB1.I:Ids` (the probe) but not the device-port
`HB1.I:M1:d`; ticking "Show all branches" reveals it. DC source: its probe current shows (on a Table —
scalars), with no device-port noise.

## On completion
Note in `src/Ui/CLAUDE.md`: the trace-card current list filters to `__ProbeBranches` (IProbe currents)
by default, mirroring the labeled-node voltage filter, with a per-trace "Show all branches" toggle to
reveal device-port currents. Provenance comes from the engine (`__ProbeBranches`, Brief 1).
