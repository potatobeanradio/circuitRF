---
title: Results & Data Export
slug: reference/npy-export.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Results &amp; Data Export
lede: Every run's results are written to disk as a self-describing NumPy `.npy` file you can read from Python, MATLAB, or any tool that speaks NumPy — no re-running the simulation. This chapter covers where results live, how to export them in other formats, and how to read the `.npy` in a few lines of Python.
---

<nav class="toc">
    <h2>On this page</h2>
    <ol>
      <li><a href="#datacube">The result model: DataSet &amp; DataCube</a></li>
      <li><a href="#where">Where results live</a></li>
      <li><a href="#export">Exporting in other formats</a></li>
      <li><a href="#groups">Grouped results</a></li>
      <li><a href="#python">Reading a <code>.npy</code> in Python</a></li>
      <li><a href="#stability">Format stability</a></li>
    </ol>
  </nav>

## The result model: DataSet & DataCube {#datacube}

Every analysis returns a **DataSet** — a named collection of **DataCubes**. A DataCube is an
N-dimensional array of one kind (Real *or* Complex) with **named, labelled axes**. For example, a
harmonic-balance voltage cube `V` has axes `[node, harmonic, sweep]`: the `node` axis carries
string labels like `X1.drain`, the `harmonic` axis carries integer orders, and the `sweep` axis
carries the swept values and their unit. This self-describing structure is exactly what the `.npy`
file preserves — so a trace, a marker, or a Python script all address the same data the same way.
Measurements ([figures of merit](measurements.html)) are added to the DataSet as named cubes too.

## Where results live {#where}

A run writes **one file** holding every analysis it produced, plus a `measurements` group. It goes to
the workspace's shared results folder:

```netlist
<workspace>/results/<name>.npy
```

**You choose `<name>`, in the Analyses panel.** Above the analyses list there is a single
**Results file** field — one field for the whole run, not one per analysis card, because a run produces
one file.

<table class="param-table">
<thead><tr><th>Results file field</th><th>What happens</th></tr></thead>
<tbody>
<tr><td>Left blank <em>(the default)</em></td><td>The run writes <code>&lt;schematicKey&gt;.npy</code>, where the key is the cell or schematic name. Every run under that schematic overwrites it.</td></tr>
<tr><td>A name you type</td><td>The run writes that file instead. <strong>This is how you keep a baseline:</strong> name one run <code>before-tuning.npy</code>, clear the field, and subsequent runs go back to the default file while the baseline stays put.</td></tr>
</tbody>
</table>

The name is sanitised to a plain file name — `.npy` is appended if you leave it off, and path
separators are stripped, so the field names a **file**, not a folder. Every result of every schematic
in the workspace lands side by side in that one `results/` directory, which is what lets the Data
Display offer them all in one picker.

<div class="callout warn">
<span class="label">A named file still overwrites</span>
<p>Naming a file preserves it only until you run again <em>under the same name</em>. There is no
automatic run history and no numbered suffixes: the named file is overwritten silently on every
subsequent run under that name. Clear the field once you have the baseline you want.</p>
</div>

<p class="small">A scratch schematic — one you have not saved into a workspace — still writes its
results, into the scratch recovery session's own <code>results/</code> folder, so a quick experiment is
plottable without saving anything first.</p>

See [Grouped results](#groups) below for what is inside the file. The
[Data Display](data-display.html) reads these files directly; nothing is recomputed to plot a result you
already ran.

## Exporting in other formats {#export}

Open the exporter from **File → Export…** or the Export button on the Data Display toolbar. Pick a
run (any results file in the workspace's `results/` folder), choose what to include, and pick a
format:

<table class="param-table">
    <thead><tr><th>Format</th><th>Extension</th><th>What it's for</th></tr></thead>
    <tbody>
      <tr><td>NumPy</td><td><code>.npy</code></td><td>The full grouped DataSet, lossless — the format this chapter documents.</td></tr>
      <tr><td>MATLAB</td><td><code>.mat</code></td><td>The same cubes as MATLAB struct fields.</td></tr>
      <tr><td>Tab-delimited text</td><td><code>.txt</code></td><td>Columns of numbers for spreadsheets / quick inspection.</td></tr>
      <tr><td>Touchstone</td><td><code>.s<em>N</em>p</code></td><td>S-parameters for a single group — renormalized to one real reference impedance (you set <code>Z0</code>); choose magnitude-angle, dB, or real-imag.</td></tr>
      <tr><td>Loadpull SPL</td><td><code>.spl</code></td><td>A loadpull-shaped result as an SPL dataset (offered only for loadpull runs).</td></tr>
      <tr><td>Loadpull LP-CWave</td><td><code>.lpcwave</code></td><td>A loadpull-shaped result as an LP-CWave dataset (offered only for loadpull runs).</td></tr>
    </tbody>
  </table>

You can include or exclude the `measurements` group, and Touchstone/loadpull exports let you pin
or iterate sweep axes to slice out the block you want. Multi-frequency loadpull results export
across all their frequency blocks.

**The same formats come out of a headless run.** Every run verb takes `-o <path>`, and the
**extension picks the format** — `hb`, `lp` and `lpp` write `.mat`, `.npy` or `.txt`; `lp` also writes
`.spl` and `.lpcwave`; `sparam` always writes Touchstone; `em` writes both a Touchstone and a grouped
`.npy` carrying its diagnostics. See [The Command Line](cli.html).

## Grouped results {#groups}

A run that has several analyses produces **one grouped DataSet**: a group per analysis (named for
the analysis — `HB1`, `SP1`, `DC1`, …) plus a `measurements` group for your figures of merit. In
the `.npy` file:

- The metadata lists the groups in order, and every cube records which group and cube it is.

- A cube is addressed by its **qualified** name (`HB1.V`) or, when unambiguous, its **bare** name
  (`V`) — the same way a [measurement](measurements.html) references it.

A single-analysis run (e.g. a [CLI](cli.html) S-parameter export) typically has just one group plus possibly
`measurements`.

## Reading a `.npy` in Python {#python}

The file is a single NumPy structured array. The `__meta__` field is a JSON blob describing every
cube — its group, kind, and axes (names, units, values, labels). Read the metadata, then index the
cube field:

```text
import json, numpy as np

arr  = np.load('run.npy', allow_pickle=False)
meta = json.loads(arr['__meta__'][0])          # bytes → dict
assert meta['format_version'] == 2

print('groups:', meta['groups'])               # e.g. ['HB1', 'measurements']

# Map (group, cube) → the numpy field name. Read group/cube from __meta__;
# the field name itself is opaque — never parse it.
field_of = { (e['group'], e['cube']): f
             for f, e in meta.items()
             if isinstance(e, dict) and 'cube' in e }

# Pull the HB voltage cube and its axes
vf   = field_of[('HB1', 'V')]
V    = arr[vf][0]                               # shape [node, harmonic, sweep], complex128
axes = meta[vf]['axes']

# Find a node by its label, plot its fundamental vs the sweep
nodes  = axes[0]['labels']                      # ['X1.gate', 'X1.drain', ...]
drain  = nodes.index('X1.drain')
sweep  = axes[2]['values']                      # e.g. Pin in dBm
fund   = V[drain, 1, :]                         # harmonic order 1, all sweep points

for pin, v in zip(sweep, fund):
    print(f'Pin={pin} dBm  |V_drain|={abs(v):.4f} V')
```

<div class="callout note">
    <span class="label">Reconstructing un-probed nodes</span>
    <p>An export can optionally carry the run's <strong>linear network</strong> (the MNA matrices and source
    vectors), which lets a consumer reconstruct <em>any</em> linear-interior node voltage or branch current
    that wasn't stored as a cube — by solving the linear system rather than re-running the simulation. The full
    math, field-by-field layout, and a worked Python example are in the developer guide
    <code>docs/design/npy-data-consumer-guide.md</code>.</p>
  </div>

## Format stability {#stability}

<div class="callout warn">
    <span class="label">Alpha — not yet stable</span>
    <p>The on-disk <code>.npy</code> layout is <strong>not stable</strong> during the alpha. The current
    <code>format_version</code> is <strong>2</strong>; always check it before reading and reject a mismatch.
    Backward compatibility is explicitly declined — if the exporter changes, regenerate your files. Don't build
    persistent archives or third-party tooling on this format until it is declared stable (post-v1.0).</p>
  </div>

---

<p class="small">See also: <a href="measurements.html">Measurements</a> · <a href="plot-types.html">Plot
  types</a> · <a href="simulations.html">Simulations</a>. Full developer-facing format spec (groups, the
  <code>__meta__</code> schema, and linear-network reconstruction):
  <code>docs/design/npy-data-consumer-guide.md</code>.</p>
