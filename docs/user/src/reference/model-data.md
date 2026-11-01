---
title: Model Data
slug: reference/model-data.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Model Data
lede: The data a Model Composer model is built from — what it needs, how to sample it in another simulator, and the file formats, each with an example to copy.
keywords: Model Composer, data formats, import, .mat, .npz, .npy, .ds, binary simulation dataset, CSV, Touchstone, simulated tables, design kit, reference kit, DC I-V, Y-parameters, S-parameters, capacitance table, bias grid, array layout, dry run, MATLAB, NumPy, scipy.io.savemat, numpy.savez
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#needs">What a model needs</a></li>
<li><a href="#ranges">Bias ranges and steps</a></li>
<li><a href="#from-simulator">From another simulator</a></li>
<li><a href="#import-formats">File formats</a></li>
<li><a href="#dry-run">Checking a file before importing it</a></li>
<li><a href="#import-checks">What the import checks</a></li>
<li><a href="#checklist">Checklist</a></li>
</ol>
</nav>

The [Model Composer](model-composer.html) builds a model from data that can come from a lab bench or from another
simulator. This page is everything about that data: what a model needs, how to produce it, and how to write it so the
import reads it. It is also served to scripts and agents as `circuitrf reference model-data` (the `reference` tool,
`topic=model-data`, over MCP).

## What a model needs {#needs}

| Data | Needed | What it gives the model |
|---|---|---|
| I–V: DC, or a simulator's DC currents; 4 points or more, varying both Vgs and Vds | **Required** | `Id` and `Ig`. With no gate current in the data, `Ig` is zero |
| S, Y or a capacitance table | Optional | `Qg` and `Qd`. None: the charges are constant capacitances you type, or zero. One bias or more: fitted linear charges — constant capacitances, exact at a single bias — with the `Linear` equation; four biases or more allow ANN or Table charges. Across the I–V plane: nonlinear charges |
| S at Vds = 0 V, pinched off and with the gate forward-biased | Optional | The cold-FET network fit |
| Open and short pad dummies | When measured at the probe tips | Removal of the pads |
| Any set at two or more temperatures | Optional | `T` as an input: the model follows the run's `temp`, and self-heating becomes available |
| Pulsed I–V at two or more chuck temperatures, from a quiescent point that dissipates nothing, plus DC I–V at the same biases | Optional | An extracted Rth. Without this data, type Rth |
| S or Y down to the thermal roll-off of `Re Y22` (typically kHz to MHz) | Optional | A fitted τth |
| Pulsed I–V from two or more quiescent drain voltages | Optional | Drain lag |
| Pulsed I–V from two or more quiescent gate voltages | Optional | Gate lag |
| A double pulse | Optional | The clearest trap data |
| Pulsed I–V from one quiescent point | Optional | One trap level, with a warning |
| Pulsed S | Optional | A trap-dependent `Qd` |
| No pulsed data; DC I–V and multi-bias S | Optional | Threshold-shift traps: time constants typed, strengths typed or fitted from the multi-bias S |
| Power sweeps, two-tone sweeps, loadpull | Optional | Validation only |

Trained trap states need pulsed I–V. Without it a model can still have traps: the states shift the threshold of the
trap-free current, with strengths fitted from the difference between the DC and RF conductances in the multi-bias S, or
typed. **DC/RF dispersion** is the simpler alternative, with one linear time constant and no fast capture.

## Bias ranges and steps {#ranges}

| | Measured | Simulated |
|---|---|---|
| Vgs | From about 1 V below pinch-off to forward gate conduction | Wider costs nothing: from 2 V below pinch-off to well into forward conduction |
| Vds | From 0 V to the peak drain swing (about 2.5 × the supply for class AB, more for switching classes), staying inside the safe operating area | The same range, within the source model's valid range |
| High-power corner | Pulsed I–V, where DC would overheat the device | DC, with self-heating off |
| I–V step | 0.1 V in Vgs (0.05 V near threshold); 0.25–0.5 V in Vds below the knee, 1–2 V above it | A uniform 0.05–0.1 V grid |
| S biases | 100 or more: every 0.25–0.5 V in Vgs and 1–2 V in Vds, plus the two cold points | The same bias grid as the I–V |
| Frequencies | 20–50, from 0.1–0.5 GHz to at least three times the operating frequency | One low frequency (1 GHz, say) for an intrinsic table; a span when the data includes parasitics |

The model is continued linearly beyond its data, so cover the whole swing the circuit will make. The charges are
read between a tenth and a half of the top frequency. The default ANNs want a few hundred points or more, and a fit
reports when its data is thin for its network.

## From another simulator {#from-simulator}

A transistor that already has a model in another simulator's design kit can be sampled there and fitted here. This
section is what to run on that side; [AN-04](../app-notes/an04-model-from-a-reference-kit.html) is the same flow
worked through, with the negative network drawn out. One bench per run: the transistor at the size to be modelled, the
bias, and the ports, nothing else.

| Run | Analysis | Sweep | What to write |
|---|---|---|---|
| I–V | DC | Vgs inside Vds, inside temperature when there are several | Vgs, Vds and the gate and drain currents at every point; optionally the core's own voltages (`vgsi`, `vdsi`), which the composer checks against its own |
| Small-signal | S-parameters at every bias of the DC sweep | One low frequency for the charges (a few hundred MHz to 1 GHz) at the intrinsic plane; a span (0.5–40 GHz, say) at the device plane | Y (or S) of the two-port: gate port 1, drain port 2, source grounded |
| Capacitances (optional) | The core's operating-point capacitances, where the simulator reports them | The same bias grid | `cgs`, `cgd`, `cds` and optionally `cm`, or the matrix `C` |
| Temperature | Both sweeps at two or more temperatures | Spanning the junction temperatures the circuit reaches: the ambient plus the heating | A `T` grid axis, or one value per point |
| Pulsed I–V (optional) | Transient: hold the quiescent bias until the trap and thermal states settle, step to the pulse bias, sample the currents at the sample delay | One run per quiescent point | A CSV per quiescent point, stating `Quiescent`, `PulseWidth` and `SampleDelay` |
| Large signal (validation) | Harmonic balance: a power sweep at the operating bias, a two-tone sweep, a loadpull | Drive up into compression | A power or two-tone sweep as a [CSV](#format-large-signal); a loadpull as `.spl` or `.lpcwave` |

**Settings on that side.** Each of these, missed, gives a model that is right at the operating point and wrong
elsewhere:

- **Self-heating off** (Rth = 0, or the model's thermal switch off) for every fitting run. The composer fits
  isothermal currents at each run's temperature and adds the heating itself; data that already holds it is heated
  twice. Note the kit's Rth and Cth: they are typed on the Thermal step.
- **Traps and dispersion off**, unless they are to be modelled from pulsed transients or as DC/RF dispersion. A DC sweep
  with traps on and a small-signal analysis with them frozen describe two different devices.
- **Stay inside the model's valid range.** A model that clamps its inputs outside it samples as flat data, which the
  fit then reproduces.
- **Sample wide.** From 2 V below pinch-off to well into forward gate conduction, and from 0 V to beyond the peak drain
  swing ([steps](#ranges)). The model is continued linearly past its data.
- **Note the base size.** The sampled device's finger count and unit gate width are `N0` and `W0`
  (`model new --fingers --unit-width`); every instance scales from them.

**The plane.** The composer fits the core inside the extrinsic network. Sample on one side of that network or the
other, and say which with `ReferencePlane`:

| Plane | On that side | Here |
|---|---|---|
| `intrinsic` (preferred) | Record the core's internal node voltages, if the kit exposes them; or put the kit's extrinsic network, every value negated and read from the core outwards, between each port and the device | No network is fitted: the points lie exactly on the swept grid, and the kit's network values are typed and locked |
| `device` | Sample at the device's terminals, as it is | A six-pin network holding the kit's values, locked; the composer removes it. With the values unknown, it fits them, which needs S over a span and the two cold biases (`Vds = 0`, pinched off and forward-biased) |

The negated network is exact in DC and small-signal analyses only; the large-signal runs use the device as it is. At DC
only its resistances matter: the `−R` cancels the kit's own drop, so the core sees the port's voltage. A value that
depends on temperature is negated at each temperature run.

**The order of work.** Write the files, check each one with [a dry run](#dry-run) until it reads as intended with no
warnings, then build the model:

```
circuitrf model import kit.cmc kit_tables.npz --as SimulatedTables --dry-run
circuitrf model new kit.cmc --kind active --network gan-two-shell --fingers 4 --unit-width "125 um"
circuitrf model import kit.cmc kit_tables.npz --as SimulatedTables --condition ReferencePlane=intrinsic
circuitrf model fit kit.cmc --step all
circuitrf model validate kit.cmc
circuitrf model export kit.cmc -o kit.cmodel
```

Over MCP these are the `model` tool's nouns, with `dryRun=true` on `import`.

## File formats {#import-formats}

Each kind of data has the formats it is usually written in. The examples below are short but complete: each can be
copied, extended with more rows or arrays, and imported as shown. The
[example workspace](model-composer.html#example)'s `synthetic-gan` folder holds full-size files in the first two formats.

| Data | Format | Kinds |
|---|---|---|
| I–V from a source-measure unit, a pulsed I–V system or a simulator's DC sweep | [CSV or TSV](#format-csv), one row per bias | `DcIv`, `PulsedIv` |
| S-parameters from a network analyser, or a simulator's S at each bias | [A folder of Touchstone files](#format-touchstone), one per bias | `SParams`, `PulsedSParams` |
| A simulator's tables: currents with Y, S or capacitances, over a bias grid | [Named arrays](#format-arrays): `.mat`, `.npz`, a [binary simulation dataset](#format-ds) (`.ds`) or circuitRF's `.npy` | `SimulatedTables` (also `DcIv`) |
| Power sweeps and two-tone sweeps | [CSV](#format-large-signal) | `LargeSignal` |
| Loadpull | `.spl` or `.lpcwave` | `LargeSignal` |

Currents are positive **into** the terminal, in every format. Gate is port 1 and drain port 2, with the source
grounded.

### I–V as CSV {#format-csv}

One row per bias point, in any order. Any common delimiter works (comma, tab, semicolon, spaces), and lines starting
`#`, `!` or `%` are skipped. Columns are found by their header names; the map says which is which and what unit its
numbers are in. This is the start of `synthetic-gan/dc/dc_25C.csv`, with the rows at Vgs = 0 V:

```
Vgs,Vds,Ig,Id
0,0,0,0
0,0.5,-1.97642744E-12,0.01990036447
0,1,-1.997927465E-12,0.03276733857
0,2,-1.999158983E-12,0.03755021927
0,5,-1.999400699E-12,0.03934713576
0,10,-1.999692969E-12,0.04289384235
```

```
circuitrf model import fet.cmc dc_25C.csv --as DcIv --map Vgs=vgs:V --map Vds=vds:V --map Ig=ig:A --map Id=id:A --condition AmbientC=25 --condition ReferencePlane=device
```

The same data written by an instrument with its units in the header and a different delimiter reads with a different
map. A cell that carries its own unit (`12 mA`) uses it, whatever the column's unit:

```
# Vgs (V)	Vds (V)	Ig (mA)	Id (mA)
Vgs	Vds	Ig	Id
0	0.5	-1.98e-9	19.90
0	1	-2.00e-9	32.77
```

with `--map Vgs=vgs:V --map Vds=vds:V --map Ig=ig:mA --map Id=id:mA`. A `T` column (°C) gives each row its own
temperature; without one, `AmbientC` gives the whole file one. The intrinsic voltages a simulator reports can come in
`vgsi` and `vdsi` columns. **Pulsed I–V** is the same table, one file per quiescent point: `pulsed_q20_25C.csv` beside
`pulsed_q0_25C.csv`, each imported with its own `Quiescent`, `PulseWidth` and `SampleDelay`.

### S-parameters as Touchstone files {#format-touchstone}

One `.s2p` per bias point, all in one folder and all on the same frequencies, in RI, MA or DB. The bias is read from
each file's name by a pattern. This is part of `synthetic-gan/s_25C`:

```
s_25C/
  Vgs-0.4_Vds0.5_Id12.969913.s2p
  Vgs-0.4_Vds1_Id17.993038.s2p
  Vgs-0.4_Vds10_Id21.844400.s2p
  Vgs-0.8_Vds0.5_Id4.648155.s2p
  …
```

```
circuitrf model import fet.cmc s_25C --as SParams --pattern "Vgs{vgs}_Vds{vds}_Id{id:mA}" --condition AmbientC=25 --condition ReferencePlane=device
```

The fields are `{vgs}`, `{vds}`, `{T}`, `{id}` and `{ig}`, each with an optional unit (`{id:mA}`); the currents and
`{T}` are optional. When the bias is in the
comment lines instead, the pattern reads those (`--pattern-source comment`); a space in the pattern matches any run of
spaces:

```
! Vgs = -0.4 V   Vds = 10 V   Id = 21.84 mA
# Hz S RI R 50
500000000 0.98989 -0.12987 -1.02909 0.10819 0.00118 0.01397 0.02200 -0.01676
1000000000 0.96024 -0.25528 -1.00438 0.21265 0.00466 0.02741 0.01889 -0.03307
```

with `--pattern "Vgs = {vgs} V Vds = {vds} V Id = {id:mA} mA" --pattern-source comment`. The cold-FET points are files
like any other, at `Vds = 0`.

### Simulated tables as named arrays {#format-arrays}

A simulator's tables are a few arrays over a bias grid. Write them as a MATLAB `.mat` (version 5, 7 or 7.3, from
MATLAB, `scipy.io.savemat` or h5py) or as a NumPy `.npz` (`numpy.savez`). The names are yours: the map says which
array is which quantity. A simulator that saves its results as a
[binary simulation dataset](#format-ds) (`.ds`) needs no conversion: the file imports as it is.

| Quantity | Holds | Unit | Shape |
|---|---|---|---|
| `vgs`, `vds` | The bias | V | One value per point, or one per grid axis |
| `id`, `ig` | The currents, positive into the terminal | A | One value per point |
| `T` | The temperature | °C | One value per point, one per grid axis, or a single value |
| `vgsi`, `vdsi` | The core's own voltages, when the simulator reports them; checked against the composer's | V | One value per point |
| `freq` | The frequencies of Y or S | Hz | One per frequency |
| `Y` or `S` | The two-port table | S, or none | `[points…, freq, 2, 2]` |
| `Y11` … `Y22`, or `S11` … `S22` | The same table as four arrays, all four together | S, or none | `[points…, freq]` |
| `C` | The capacitance matrix, `Cij = ∂Qi/∂vj` (so `C12 = −Cgd`) | F | `[points…, 2, 2]` |
| `cgs`, `cgd`, `cds`, and optionally `cm` | The circuit set, instead of `C` | F | One value per point |

"Points…" is either one axis with a value per point, or the grid's own axes. A single-bias table has no point axis, and
a table at one frequency needs no frequency axis (state `FrequencyHz` instead of mapping `freq`).

**An example.** A Vgs × Vds grid of 41 × 17 points at one temperature, with Y at 1 GHz, written from Python:

```python
import numpy as np
vgs = np.linspace(-4, 1, 41)                     # (41,)  V
vds = np.linspace(0, 40, 17)                     # (17,)  V
# id, ig: (41, 17) in A, one value per vgs × vds; freq: (1,) in Hz; Y: (41, 17, 1, 2, 2) complex, in S
np.savez("tables.npz", vgs=vgs, vds=vds, id=id, ig=ig, freq=freq, Y=Y)
# or, the same arrays as a .mat:
import scipy.io as sio
sio.savemat("tables.mat", {"vgs": vgs, "vds": vds, "id": id, "ig": ig, "freq": freq, "Y": Y})
```

or from MATLAB, which keeps the 2 × 2 matrix first:

```
% Y(i, j, f, g, d): (2, 2, 1, 41, 17) complex
save('tables.mat', 'vgs', 'vds', 'id', 'ig', 'freq', 'Y')
```

The importer lists what the file holds before anything is mapped — the right side of its window, and the arrays
named in a refusal — so the file can be checked against the table above:

```
freq   1
id     41 × 17
ig     41 × 17
vds    17
vgs    41
Y      41 × 17 × 1 × 2 × 2   complex
```

```
circuitrf model import fet.cmc tables.npz --as SimulatedTables --condition ReferencePlane=intrinsic --condition AmbientC=25
```

With no `--map`, the map is taken from the array names, and printed: `map from the array names: vgs=vgs, vds=vds,
ig=ig, id=id, freq=freq, Y=Y.` The importer's window fills its map field the same way. Any of the usual spellings are recognised — `Vg`,
`VGS`, `v_gs` and `Vgate` for `vgs`, `Ids` and `I_D` for `id`, `f` and `frequency` for `freq`, `Temp` for `T` — and a
unit suffix gives the array its unit: `Ids_mA`, `freq_GHz`, `T_K`. Two arrays that both look like one quantity are left
for you to map. A map written out says the same thing explicitly:

```
--map vgs=vgs --map vds=vds --map id=id --map ig=ig --map freq=freq --map Y=Y
```

**Units.** Arrays are read in SI (°C for `T`) unless the map gives a unit: `--map f=freq:GHz`, `--map Ids=id:mA`,
`--map Tj=T:K` (kelvin is converted to °C). The window's map takes the same `name=quantity:unit`.

**More than one temperature.** Add `T` as a third grid axis, and give every per-point array that axis too. With
`T = [25, 85, 125]`, `id` is `(41, 17, 3)` and `Y` is `(41, 17, 3, 1, 2, 2)`. One value per point works as well:
`vgs`, `vds`, `T`, `id` and `ig` all of length 41 × 17 × 3 = 2,091, and `Y` shaped `(2091, 1, 2, 2)`.

**Capacitances.** A table of the core's capacitances is read at `ReferencePlane=intrinsic` only. On its own it feeds
the charges at every point, with nothing to fit the network, `Gm`, `Gds`, dispersion or τth to. Beside Y or S, Y feeds
the charges and the table is checked against it: `max |ΔC| 0.02 % between its capacitance table and Im Y/ω`.

**The layout is worked out from the shapes.** The order of the axes need not match the table above; the importer reads
whichever order fits:

- The grid's axes in any order: `id` written `(17, 41)`, vds first, reads the same as `(41, 17)`.
- The table's axes in any order, with `i` before `j`: MATLAB's `Y(i, j, f, g, d)`, the 2 × 2 first, reads as written.
- Per-point arrays flattened, with Vgs varying slowest, then Vds, then T, as `numpy.ravel` and `meshgrid(…,
  indexing="ij")` give them; or two-dimensional bias arrays from a `meshgrid`, whichever way round.
- A frequency array written once per point, `(697, 1)` or `(41, 17, 1)`, reduced to its distinct values.
- Axes of length 1 ignored: `(41, 1)` is 41 values.

When the reading is not the documented layout, the import says how it read each array. The MATLAB file above gives
`'Y' (2 × 2 × 41 × 17) read as [i, j, vgs, vds]`: the shape without its length-1 frequency axis, then what each axis
was read as. When two axes have the same length (a 17 × 17 grid, or
two frequencies) more than one order fits; the import reads the most likely one and adds `another order of its axes fits
too — check the plots`. When no order fits, it refuses, listing every mapped array's shape and the shapes it needed.

**Not read.** A cell array, text or an object in a `.mat` is listed with the reason and otherwise ignored, so labels and
settings can stay in the file. A struct's fields are mapped as `s.field`. A plain `.npy` from `numpy.save` holds one
unnamed array and is refused; save the arrays together with `numpy.savez`. A `.npy` that circuitRF wrote, with its
own named arrays, is read like a `.mat`. Touchstone files and a CSV, which most simulators can also write, work too.

### Simulated tables as a binary simulation dataset {#format-ds}

A binary simulation dataset (`.ds`) holds one block per analysis, each a nested sweep. The importer reads it as named
arrays, like a `.mat`, and names each one `Group.Variable`: the group is the analysis's name (`DC1`, `SP1`), and
every swept variable is an array of its values (`SP1.Vgs`) beside every result over the block's grid. A complete
family of elements `S[1,1]` … `S[2,2]` reads as one array `SP1.S` with the two port axes last, so it maps as `S`; a
family written as `S11` … `S22` maps element by element. Text variables, such as port names, are listed with the
reason and ignored. A DC sweep and an S-parameter sweep at the same biases, Vgs inside Vds, list as

```
DC1.VD.i   17 × 41
DC1.Vds    17
DC1.VG.i   17 × 41
DC1.Vgs    41
SP1.freq   1
SP1.S      17 × 41 × 1 × 2 × 2   complex
SP1.Vds    17
SP1.Vgs    41
```

Both blocks sweep the same Vgs and Vds, so their arrays combine into one data set: the bias from either block, the
currents from `DC1` and S from `SP1`. With `Vgs` and `Vds` in both blocks, and the currents named after their
sources, the names alone do not give the map; write it out:

```
circuitrf model import fet.cmc sweep.ds --as SimulatedTables --map DC1.Vgs=vgs --map DC1.Vds=vds --map -DC1.VD.i=id --map -DC1.VG.i=ig --map SP1.freq=freq --map SP1.S=S --condition ReferencePlane=intrinsic --condition AmbientC=25
```

**A leading `-` reads an array negated.** A voltage source's current is usually recorded flowing into the source,
which is out of the device, so the bias sources' currents (`VD.i`, `VG.i`) come out with the wrong sign. `-DC1.VD.i=id`
reads the drain current as positive into the drain. Mapped without the `-`, the import flags it and names the
spelling: `To read it negated, map it as -DC1.VD.i=id.` The window's map takes the same spelling.

**Only a full grid is read.** Every outer point must sweep the same inner values. A sweep whose inner points change
from one outer point to the next is refused, naming the variable.

### Large-signal sweeps as CSV {#format-large-signal}

One row per input power. The quantities are `pin`, `pout`, `im3` and `im5` in dBm, `gain` in dB, `pae` and `de` in %,
and the DC currents `id` and `ig` in any unit; `pin` is required. The conditions say what was measured: the
measurement, the frequency, the bias and the terminations.

```
Pin,Pout,Gain,PAE,Id
-5,9.21,14.21,1.8,0.0482
0,14.18,14.18,5.6,0.0511
5,19.02,14.02,16.4,0.0613
10,23.31,13.31,36.9,0.0905
15,25.88,10.88,52.7,0.1287
```

```
circuitrf model import fet.cmc psweep.csv --as LargeSignal --map Pin=pin:dBm --map Pout=pout:dBm --map Gain=gain:dB --map PAE=pae:% --map Id=id:A --condition LargeSignal.Measurement=PowerSweep --condition LargeSignal.FrequencyHz=2e9 --condition LargeSignal.Vgs=-0.6 --condition LargeSignal.Vds=6 --condition LargeSignal.LoadZ=18,12
```

A two-tone sweep is the same with `--condition LargeSignal.Measurement=TwoTone`, `LargeSignal.SpacingHz` and the
`im3`/`im5` columns.

## Checking a file before importing it {#dry-run}

`model import --dry-run` reads the sources exactly as an import would and writes nothing: no data set is added, no
cache is touched, and the `.cmc` need not exist yet. It prints what the first file holds, the map it used (given, or
taken from the array names), what the import would read, how it read any array not in the documented layout, every
warning the import would add, and how many points the data checks flag (a current plateau, an oscillating point). A file
that would be refused is refused the same way, with the same sentence. A text source shows its first lines instead of
its arrays.

```
$ circuitrf model import kit.cmc kit_tables.mat --as SimulatedTables --dry-run
map from the array names: Vg=vgs, Vd=vds, Ids_mA=id:mA, freq_GHz=freq:GHz, Y=Y.
arrays in kit_tables.mat: freq_GHz (1 × 1), Ids_mA (41 × 17), Vd (1 × 17), Vg (1 × 41), Y (2 × 2 × 1 × 41 × 17, complex).
not read: labels — it is a cell array; only numeric arrays and structs of them are read.
kit_tables: SimulatedTables, 697 point(s), Vgs -4 to 1 V, Vds 0 to 40 V, 1 to 1 GHz in 1 frequencies.
kit_tables: 'Y' (2 × 2 × 41 × 17) read as [i, j, vgs, vds].
nothing written (dry run).
```

Over MCP, `model noun=import dryRun=true`; `--json` gives the same lines as items. Exit 0 when it would import, 1 when it
would be refused.

## What the import checks {#import-checks}

Some mistakes still give a file that imports. The import reads the data as written and adds one warning for each of
these, in the window's warnings and on the `model import` output:

| Warning | Likely cause |
|---|---|
| `Id < 0 at … points with Vds > 0` | The drain current was recorded flowing out of the terminal |
| `\|S12\| > \|S21\| at … conducting biases` | The ports are swapped, or the table is transposed: gate must be port 1 |
| `the frequencies run 1 to 10 Hz` | The frequency array is in GHz: give it its unit, `freq:GHz` |
| `Id reaches … A` | The currents are in mA: `id:mA` |
| `T runs 298 to 398 °C` | The temperatures are in kelvin: `T:K` |
| `\|S11\| never exceeds …` | A Y table was mapped as S |
| `C12 > 0 at … points` | The circuit set (`cgd`) was written as the matrix `C` |

If your tool writes the right data in a layout none of these read, send a sample: the importer is extended to read
what tools actually write.

## Checklist {#checklist}

- The I–V covers the circuit's whole Vgs and Vds swing, gate forward conduction included.
- S, Y or capacitances, if there are any, are at one bias or more for fitted linear charges, 4 or more for ANN or Table charges, and a
  hundred or more for nonlinear ones. Without it, the capacitances are typed (a datasheet's will do) or left at zero.
- Currents are positive into the terminal, and every column has its unit.
- Every set states its reference plane and ambient temperature.
- Measured: the compliance limits are stated, so points at the limit are flagged.
- Simulated: self-heating is off, and traps are off unless they are modelled.
- Pulsed sets state their quiescent point (and pre-pulse), pulse width and sample delay.
- N0 and W0 are those of the device the data came from.
