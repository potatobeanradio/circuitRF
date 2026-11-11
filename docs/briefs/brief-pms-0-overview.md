# Brief series PMS — planar mesh scale on medium MMIC layouts

**Goal.** A medium-sized MMIC layout reaches a verdict within seconds of pressing Mesh or Simulate,
and a layout of that size becomes solvable by the planar kernel rather than only describable by it.

GitHub discussion 57 reports a 1.4 mm GaAs MMIC: three ports, one frequency, default mesh settings,
backside vias, two interconnect via layers, MIM windows, and one large region via (about
390,000 µm², where Metal2 overlaps Metal1 and is joined to it). Meshing never finished. The progress
line stopped at `scanning via footprints: 6,885 / 6,885`, one core ran at 100 %, and memory climbed
past 4.5 GB (6.4 GB and swapping on a headless dry run after 18 minutes). The report suggested the
via-footprint scan in `SurfaceMesher` as the cause.

The layout itself is not available, so this series is built and gated on a **synthetic MMIC chip**
(brief 1 creates it, brief 2 extends it into a family). A synthetic chip with the same ingredients
reproduces the report exactly. The measurements below were taken on it with a scratch harness, in
Release, on the development machine.

## What was measured

Fixture S is a 1.4 × 1.4 mm chip on the two-level GaAs medium: Metal1 at the GaAs/3 µm-dielectric
interface and Metal2 on top. It has 20 Manhattan traces per level (8–28 µm wide, 80–380 µm long),
80 Metal1–Metal2 vias (6 µm squares on 14 µm pads on both levels), 20 octagonal backside vias
(60 µm on 80 µm Metal1 pads), one 625 × 625 µm region via with metal on both levels, and three
70 µm edge feeds on Metal2 with edge ports. It runs at 10 GHz with default mesh settings and a
7.6 GB memory limit.

| Step | Result |
|---|---|
| `SurfaceMesher.Mesh` | **1.3 s.** Grid 795 × 790 (800 rows with ports), **941,867 unknowns**. The via scan is about 1.0 s of it |
| `PlanarPorts.ResolveAll` | 0.15 s |
| `PlanarRunMemory.StandardBytes` | **Did not finish in 5 minutes.** Single core, memory climbing |
| — one standard's `PlanarCoreFootprint.Count` | 10,248 cells / 20,210 unknowns: **34 s, 5.9 GB managed, 53.2 million distinct classes** |
| — the six standards | 20,210 to 51,879 unknowns each. A 70 µm port line carries 40 cells across, because the standards are built on the DUT's own gridlines and those are the chip's |
| Accelerated-solve near-field estimate (`PlanarNearFieldEstimate.For`) | 58 s, 89.6 × 10⁹ entries, 14.7 % dense |
| Same chip, region via removed | 297,201 unknowns, grid 806 × 792 |
| Same chip, edge mesh off | 374,492 unknowns |
| Fixture L (150 small vias, 30 traces/level) | Grid 1,103 × 1,097, **1,687,424 unknowns** (282,475 vertical). Mesh 2.9 s, via scan 2.6 s |

A stack dump during the stall shows `StandardBytes → PlanarCoreFootprint.Count →
PlanarFill.CountCores → DistinctBandKeys`, with the remaining worker in `HashSet<long>.UnionWith`
under the merge lock: one core at 99.7 %, 3 GB and rising. This matches the report. Nothing after the
mesher opens a progress stage of its own, so the panel keeps showing the mesher's last label.

**Three findings, and they set the shape of the series:**

1. **The stall is the calibration-standard pricing, not the mesher.** `StandardBytes` counts every
   standard's cores exactly (`Count`, not `For`), with no size bound. The counted figure cannot change
   the verdict, because the structure alone is about 25× past the machine limit before any standard
   is priced. The same call runs again inside the run (`PlanarKernel.cs`, "CAP-1 follow-up — a
   de-embedded run is judged with its standards"), so Simulate Anyway would stall a second time.
2. **The via scan is a real inefficiency but a small one.** It is O(via groups × grid cells ×
   polygons per group) with no bounding-box restriction, exactly as the report reads it. It costs
   seconds here.
3. **Even with both fixed, the chip cannot be solved.** 941,867 unknowns against a dense ceiling of
   about 40,000 on 16 GB. Two things drive it. (a) **The shared tensor grid.** Every edge of every
   small pad runs a gridline across the whole chip, so 1.4 mm carries about 800 lines per axis. This
   holds whether the edge mesh is on or off, and whether the region via is there or not. (b) **The
   region via.** A fully overlapped 390,000 µm² region is meshed as two levels plus one vertical
   unknown per cell, which is two thirds of the count.

## The briefs, in order

| Brief | What | Depends on |
|---|---|---|
| `brief-pms-1-preflight-stall.md` | The synthetic chip fixture. Standards pricing that cannot stall: skipped when decided, bounded, staged, cancellable. A bounded via scan. The accelerated estimate bounded | — |
| `brief-pms-2-measure-mesh-drivers.md` | The fixture family, a breakdown of where N comes from, and an offline estimate of what a local grid and a stacked-metal region would each save. **A go/no-go gate for briefs 3–7** | 1 |
| `brief-pms-3-stacked-metal-regions.md` | A fully overlapped, via-joined two-level region meshed as one conductor with a rim of vertical unknowns | 2 (go) |
| `brief-pms-4-local-grid-mesher.md` | One shared, locally refined grid: lines live near the feature that asked for them. Cells, bases and the ordering contract | 2 (go) |
| `brief-pms-5-local-grid-fill.md` | The fill, singular integrals and translation classes over a rooftop across a refinement step | 4 |
| `brief-pms-6-local-grid-consumers.md` | Ports and standards, current density, conduction (DC), far field, rooftop merging, conformal cells, AIM, the mesh view | 4, 5 |
| `brief-pms-7-defaults-and-docs.md` | The default, the panel wording, the CLI/MCP reference and the user docs | 3 and/or 6 |

Brief 1 ships on its own and is worth shipping whatever brief 2 concludes. Brief 2 decides which of
brief 3 and briefs 4–6 are built. A local grid coarsens the region via's interior by itself, so if
4–6 go ahead brief 3 may not pay; brief 2 measures both and says which.

## Decisions already made

- **D1 — No user layout is needed.** Every gate runs on the synthetic fixtures, built from the
  shipped MMIC GaAs technology (`mmic-GaAs_2LM_100um.ctech`: Metal1, MIM Metal, Metal2, Backside Via,
  Metal1-Metal2 Post, MIM Via) or on the equivalent engine-level `PlanarProblem`. Fixtures are
  generated from a fixed seed in test code. No binary layout is committed.
- **D2 — Accuracy references are today's solver, where today's solver fits.** A new representation
  (brief 3, briefs 4–6) is gated against the current tensor-grid solve on a fixture small enough to
  run dense. The comparison is in |ΔS| against the de-embedding residual already on record for that
  stack and band (`src/Engine/Mom/CLAUDE.md` §5). A short 3D cross-check is allowed but never
  required.
- **D3 — Nothing shipped moves unless a brief says so.** Every existing mesh test, every starter and
  every shipped example produces the same mesh, bases and `.snp` on the paths these briefs touch. That
  holds unless a brief names the change and measures it. A new representation ships behind a setting
  until brief 7.
- **D4 — The verdict is advisory, as CAP-1 left it.** Past the machine limit is still a warning and a
  question (Simulate Anyway / `--force`), never a new refusal. What changes is that the question
  arrives in seconds.
- **D5 — Ordering contract.** R-msh-2 / R-via-5 are kept: all horizontal bases before all vertical
  ones, integer ordering, no set or dictionary iteration on the basis path. A local grid changes what
  the integers index, not the contract.

## Open for the owner

- Whether a new representation (brief 3 or the local grid) becomes the default in brief 7, or stays
  opt-in with a note that points at it.
- Whether the accelerated ceiling's single-level restriction (`UsesAcceleratedCeiling`, P12) is
  revisited once a local grid exists. These briefs do not touch it.
- Whether brief 7 adds a small MMIC example workspace. Examples must run in minutes, so it would be a
  sub-block, not a chip.

## Rules that apply throughout

- **Tests:** one per claim, named classes only. Never the full suite and never all of `Ui.Tests`. No
  new `Category=Benchmark` test and **no timing assertion**: assert counters, flags and shapes, and
  report wall time in the completion note.
- **Measure with a scratch harness**, not the benchmark tier. EM solves in gates use the small
  fixtures, a few frequencies, minutes at most. Never launch a whole-chip solve.
- **Memory watchdog** on any run of fixture S or L: kill the process past 8 GB resident.
- **No citations** of papers, authors or other tools. No commercial tool or PDK names anywhere.
- **UI work is checked visually** with `tools/UiSnapshot` (the mesh view and the EM panel), and the
  PNGs are looked at and listed in the completion note.
- Findings go in `src/Engine/Mom/RESOLVED.md` (engine) or `src/Ui/Layout/Em/RESOLVED.md` (panel),
  never `CLAUDE.md`. A phase narrative goes in `src/Engine/Mom/HISTORY.md`.
