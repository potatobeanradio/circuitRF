---
title: Model Library Format
slug: reference/model-library-format.html
doc-kind: Reference Guide
breadcrumb: Docs > Reference > Model Library Format
lede: The folder format a model library is written in — every file and key, what a reader must do, and a complete example — for writing a library by hand or by script, or reading one in another tool.
keywords: model library, model folder, library.clib, part.cpart, vendor library, part library, Export to Library, share a model, Verilog-A part, library format, PublishedHash, agent
---

<nav class="toc">
<h2>On this page</h2>
<ol>
<li><a href="#what">What a model library is</a></li>
<li><a href="#layout">The folder layout</a></li>
<li><a href="#clib">library.clib</a></li>
<li><a href="#cpart">part.cpart</a></li>
<li><a href="#cnl">The model's netlist</a></li>
<li><a href="#hash">PublishedHash</a></li>
<li><a href="#reader">What a reader must do</a></li>
<li><a href="#example">A complete example</a></li>
<li><a href="#checking">Checking a library</a></li>
<li><a href="#making">Making a library</a></li>
</ol>
</nav>

## What a model library is {#what}

A **model library** is a folder of **model folders**. Each model folder is one part: a cell with pins and
cell parameters, packaged with every file it needs so it runs on a machine that has nothing else. A model can
hold any component circuitRF has — R, L and C, Verilog-A, a ComposedFET, an SnP, a SpiceModel, sub-cells.

A workspace lists a model library the way it lists a [vendor part library](pdk-integration.html#vendor):
**Add Vendor Library…** on its folder. Model folders and SPICE files can share one library.

**circuitRF defines this format, and the files in it are plain text.** What runs is the model's netlist, a
[`.cnl`](netlist.html) — documented and readable by another tool. Every model file it uses (`.va`, `.sNp`, …)
sits beside it, so a tool that does not read `.cnl` can take a `.va` directly.

## The folder layout {#layout}

```
<library>/
  library.clib                 optional
  <Category>/<Sub>/            ordinary folders: the palette's sub-menus
    <PartNumber>/              a model folder
      part.cpart               required; its presence makes this a model folder
      <PartNumber>.cnl         required; the netlist that runs
      .ccell                   the cell's own file
      schematic/<PartNumber>.csch
      symbol/<PartNumber>.csym
      models/                  .va and every file it includes, .cmodel, .sNp, SPICE files a SpiceModel names
      cells/<Sub>/             sub-cell folders the model's schematic places
      docs/                    optional; the palette's ? button opens these
```

| Entry | Required | What it is |
|---|---|---|
| `part.cpart` | Yes | What finds and describes the part ([below](#cpart)). |
| `<PartNumber>.cnl` | Yes | The netlist that runs ([below](#cnl)). |
| `.ccell` | No | The cell file every circuitRF cell has. Export to Library writes it. |
| `schematic/` | No | The schematic the `.cnl` was extracted from. Export writes it so the model can be opened and edited; nothing reads it to run. A model written by hand or by script may leave it out. |
| `symbol/` | When `Glyph` is empty | The part's symbol. With a `Glyph`, the part draws that glyph and needs no symbol. |
| `models/` | When used | Every file a parameter in the `.cnl` names. |
| `cells/` | When used | The sub-cells the schematic places, each an ordinary cell folder. |
| `docs/` | No | Data sheets and notes. |

**A model folder is also a cell folder**, so it opens, renders and checks as one. The folder is named after the
part number. A `.lib` inside `models/` is the model's own file and is never listed as a part, however the library
is indexed.

## library.clib {#clib}

```json
{
  "Format": "circuitrf-model-library",
  "FormatVersion": 1,
  "Name": "Example Devices",
  "Publisher": "",
  "LibraryVersion": "1.0.0",
  "License": "",
  "Url": "",
  "ReadOnly": false
}
```

| Key | Required | Meaning |
|---|---|---|
| `Format` | Yes | Always `circuitrf-model-library`. |
| `FormatVersion` | Yes | `1`. A higher number is refused, naming both versions. |
| `Name` | No | The library's name. Without it, or without the file, the library takes its folder's name. |
| `Publisher` | No | Who made it. |
| `LibraryVersion` | No | The publisher's own version string. |
| `License` | No | The terms the publisher states. circuitRF displays it and does nothing else with it. |
| `Url` | No | Where the library comes from. |
| `ReadOnly` | No | `true`: copy a part to change it. It greys out **Edit Vendor Model…** and nothing else. |

A key circuitRF does not know is kept when it rewrites the file.

## part.cpart {#cpart}

```json
{
  "Format": "circuitrf-model",
  "FormatVersion": 1,
  "Part": "TSTFET100",
  "Cell": "TSTFET100",
  "Kind": "Active",
  "Description": "Low-noise pHEMT, packaged",
  "Pins": ["g", "d", "s"],
  "Parameters": [ { "Name": "Vds", "Default": "3 V" } ],
  "Glyph": "FET",
  "Value": "",
  "Frequency": { "Low": "0 Hz", "High": "20 GHz" },
  "Conditions": "",
  "Footprint": "",
  "CaseMetric": "",
  "CaseImperial": "",
  "Revision": 1,
  "PublishedHash": "sha256:…",
  "Converted": [ { "Instance": "TL1", "Cell": "TSTFET100", "Type": "MLIN", "File": "models/TL1.s2p",
                   "Parameters": "W=1.1 mm L=8 mm", "Technology": "PCB 2-Layer RO4350B (20mil, 1oz)",
                   "Frequency": "start=0 GHz stop=20 GHz npts=1001; DC always included", "Version": "…" } ]
}
```

| Key | Required | Meaning |
|---|---|---|
| `Format` | Yes | Always `circuitrf-model`. |
| `FormatVersion` | Yes | `1`. A higher number is refused, naming both versions. |
| `Part` | Yes | The part number. Equals the model folder's name. |
| `Cell` | Yes | The top `define` in the `.cnl`. |
| `Kind` | Yes | `Active` or `Passive`. |
| `Pins` | Yes | The top cell's pins, in the `.cnl`'s order. |
| `Description` | No | One line, searched by the palette. |
| `Parameters` | No | The top cell's parameters, each `{ "Name", "Default" }`, in the `.cnl`'s order and spelling. |
| `Glyph` | No | `R`, `C`, `L` or `FET` to draw that glyph; empty to draw the model's own symbol. |
| `Value` | No | For a passive, its value with a unit (`10 pF`), so search by value finds it. |
| `Frequency` | No | `{ "Low", "High" }`, the range the model is valid over. A run outside it is warned about. |
| `Conditions` | No | The bias, temperature or other conditions the model holds at, as text. |
| `Footprint` | No | A footprint name; empty to take the case code's. |
| `CaseMetric`, `CaseImperial` | No | The case size (`1005`, `0402`), which also gives the footprint. |
| `Revision` | No | How many times this part number has been exported. |
| `PublishedHash` | No | Written by Export ([below](#hash)). Empty or absent in a hand-made model. |
| `Converted` | No | The parts Export replaced by S-parameter files ([below](#converted)): each one's instance, the cell that placed it, its type, its file, its parameters as written, the technology's name, the frequency plan and the circuitRF version. Provenance; nothing reads it to run. |

**Nothing in `part.cpart` changes a result.** What runs is the `.cnl`. `Pins` and `Parameters` repeat what the
`.cnl` states so a library can be listed without reading every netlist, and `check` says when the two disagree.

## The model's netlist {#cnl}

`<PartNumber>.cnl` is a [`.cnl`](netlist.html) holding one or more `define … end` blocks and **nothing at the
top level except comments**:

- no analyses, no global variables, no `technology` line and no `measure` lines
- exactly one `define` that no other `define` places: the top cell, named by `Cell`; the others are its sub-cells
- the top cell's pins and parameters are `part.cpart`'s `Pins` and `Parameters`, in the same order
- every file a parameter names (`File=`) is a path **relative to the model folder** that stays inside it — a
  model never names a file outside itself, another library, or a kit

```netlist
define TSTFET100 (g d s)
  parameters Vds=3 V
  SnP:S1 g d s NumPorts=3 File="models/TSTFET100.s3p"
end TSTFET100
```

### Tolerances {#tolerances}

A model states its spread with the expression engine's distribution functions, written in a value:

| Function | Draws |
|---|---|
| `gauss(nom, rel, k)` | Gaussian, `rel` a fraction of the nominal at `k` σ |
| `agauss(nom, dev, k)` | Gaussian, `dev` an absolute deviation at `k` σ |
| `unif(nom, rel)` | uniform on nom·(1 ± rel) |
| `aunif(nom, dev)` | uniform on nom ± dev |

Outside a Monte Carlo or yield trial each one is exactly its first argument, so an ordinary run of the model is
unchanged. Inside a trial it draws once per **instance**: two placed copies of a part vary independently. A
parameter's default carries it, and so does any value inside the model that is not a parameter:

```netlist
define TSTCAP (in out)
  parameters C0=gauss(10, 0.05, 3) pF  Rs=0.1 Ohm
  C:C1 in mid C=C0
  R:R1 mid out R=agauss(0.1,0.01,1)
end TSTCAP
```

An absolute width is in the value's own unit: `agauss(0.1,0.01,1)` on a resistance in Ω is 0.01 Ω at 1 σ.

### Corners {#corners}

`corner <name> <parameter>=<value> [unit] …` inside a `define` names a set of the cell's own parameter values —
the part's stated limits. A parameter the corner leaves out keeps its nominal default.

```netlist
define TSTCAP (in out)
  parameters C0=gauss(10, 0.05, 3) pF
  corner min C0=9 pF
  corner max C0=11 pF
  C:C1 in out C=C0
end TSTCAP
```

A corner binds only parameters the cell declares. `temp=` is refused (a part does not choose its temperature; the
bench does), and so are a name that is not a parameter, two corners of one name, and the name `nominal`, which is
the cell's own defaults. A cell that declares a parameter named `Corner` cannot be exported: on a placed part that
name chooses the corner.

**A model never carries a technology.** A part whose substrate would come from a technology — a microstrip line,
a placed layout — is an S-parameter file in `models/` instead.

**Verilog-A needs a compiler wherever it runs.** A `.va` ships as source; anyone who places the model needs a
Verilog-A compiler, as for any [Verilog-A part](components.html#veriloga). Precompiled models are not part of the
format.

## PublishedHash {#hash}

`PublishedHash` lets circuitRF say a model was edited after it was exported. It is `sha256:` followed by the
lower-case hex SHA-256 of a list of lines, one per file:

1. Take every file under the model folder except `part.cpart` itself, `.DS_Store`, `Thumbs.db`, `desktop.ini`
   and anything inside a `.git` folder.
2. For a text file (`.cnl .csch .csym .ccell .va .vams .sNp .lib .cir .sub .txt .md`, where `.sNp` is any N)
   replace every CR LF with LF in its content. A changed line ending is then not an edit.
3. Write one line per file, `<lower-case hex SHA-256 of the content>  <path>` with two spaces and a LF, where
   `<path>` is relative to the model folder with `/` separators. Order the lines by path, comparing bytes.
4. Hash the lines as UTF-8.

A model with no `PublishedHash` is never reported as edited. A script that makes a library may write the hash
too; it then gets the same warning when someone edits its output.

## What a reader must do {#reader}

- **Find models by `part.cpart`.** Any folder holding one is a model folder; do not look inside it for more.
- **Run the `.cnl`.** Place the top cell with its pins in `Pins` order, and override its parameters by name.
- **Resolve every relative path against the model folder** — the folder holding `part.cpart`, whichever file
  names it.
- **Assume no technology** and no workspace. A model needs nothing outside its own folder.
- **Keep keys you do not know** when you rewrite a manifest, and refuse a `FormatVersion` above the one you read.

## A complete example {#example}

A library with one passive part, written by hand. Each file is shown with its path inside the library; save them
there and `circuitrf check example-devices` reports no errors and no warnings.

<!-- example:begin -->

`example-devices/library.clib`

```json
{
  "Format": "circuitrf-model-library",
  "FormatVersion": 1,
  "Name": "Example Devices",
  "LibraryVersion": "1.0.0"
}
```

`example-devices/Capacitors/0402/TSTCAP0402/part.cpart`

```json
{
  "Format": "circuitrf-model",
  "FormatVersion": 1,
  "Part": "TSTCAP0402",
  "Cell": "TSTCAP0402",
  "Kind": "Passive",
  "Description": "10 pF capacitor, 0402, with its series loss and inductance",
  "Pins": ["p1", "p2"],
  "Glyph": "C",
  "Value": "10 pF",
  "Frequency": { "Low": "0 Hz", "High": "6 GHz" },
  "CaseMetric": "1005",
  "CaseImperial": "0402"
}
```

`example-devices/Capacitors/0402/TSTCAP0402/TSTCAP0402.cnl`

```netlist
; TSTCAP0402: 10 pF in series with its ESR and ESL
define TSTCAP0402 (p1 p2)
  C:C1 p1 a C=10 pF
  L:L1 a b L=0.45 nH
  R:R1 b p2 R=0.12 Ohm
end TSTCAP0402
```

<!-- example:end -->

The part has no symbol, because `Glyph` draws a capacitor, and no schematic, because the `.cnl` is what runs.

## Checking a library {#checking}

`circuitrf check` takes a library folder, a model folder, a `part.cpart` or a `library.clib`. Over MCP it is
`check path=<folder>`. Besides the checks every cell gets, it reports:

| Finding | Severity |
|---|---|
| `part.cpart` unreadable, not this format, a newer `FormatVersion`, or a required key missing | Error |
| `Part` different from the folder's name | Error |
| The `.cnl` missing or unreadable, or holding a line outside every `define` | Error |
| The top `define` not `Cell`, or more than one top `define` | Error |
| The top cell's pins or parameters different from `Pins` / `Parameters` | Error |
| A file parameter naming a file outside the model folder | Error |
| Two model folders with one part number | Error |
| The folder no longer matching `PublishedHash` — edited since exported | Warning |

A folder holding model folders is checked to its full depth without `--recursive`.

## Making a library {#making}

**Export to Library…** packages a cell as a model folder. It is on the **File** menu and on the schematic
toolbar, and it is available when the active document is a saved cell's primary schematic with at least one
**Pin**; otherwise it is greyed, and its tooltip says why (a test bench, unsaved changes, no Pin). An empty
folder chosen as the library starts a new library.

The dialog asks for:

- **Library** and **Category**. Pick a folder, or one of the libraries the workspace already references. A
  category is a folder in the library, and so a sub-menu in the palette; typing `Amplifiers/LNA` creates both
  folders.
- The **library's** Name, Publisher, Version and License, written to `library.clib`. They are asked only when
  the folder has no `library.clib` yet.
- The **part**: its number (the cell's name by default), Description, **Kind** (Active when any component is
  nonlinear — Verilog-A, ComposedFET, a FET, a diode, SDD or an external device — Passive otherwise), a passive's
  **Value**, the **Frequency** range it is valid over, **Conditions**, **Footprint**, and any **Documents** to copy
  into `docs/`.
- The **symbol**. The default is the cell's own symbol when it has one; otherwise the FET glyph for three or four
  pins whose names read as gate, drain and source on an active part; otherwise R, C or L for a two-pin passive
  whose value is a resistance, capacitance or inductance; otherwise a box. A built-in glyph is written into the
  model as `symbol/<part>.csym`, with its pins named after the cell's, and recorded as `Glyph`.

- The **Parameters** table: each of the cell's parameters with its nominal and a **Tolerance**. Choose Gaussian or
  Uniform, then type the spread as the Yield panel takes it: `± 2 %`, `± 2 % at 2σ`, `σ 0.1 pF`, or `9 … 11 pF` on a
  Uniform. It is written into the parameter's default as a [distribution function](#tolerances):

  | Spread | Default written |
  |---|---|
  | Gaussian `± 5 % at 3σ` on 10 pF | `gauss(10,0.05,3)` |
  | Gaussian `σ 0.1 pF` | `agauss(10,0.1,1)` |
  | Uniform `± 2 %` | `unif(10,0.02)` |
  | Uniform `9 … 11 pF` | `aunif(10,1)` |

  Lognormal, Discrete and truncation have no distribution function, so they are not offered.
- The **Corners** table: one row per corner, one column per parameter, and a blank cell is nominal. **Add corner**
  and the row's × add and remove one.

  Both tables write the **source cell's** `.ccell`, so the next export keeps them; the dialog says so when it has.

While you fill it in, the dialog lists what will be copied in — every file, every sub-cell, every vendor part
and the library it came from — and every reason the export cannot go ahead. **Export** stays greyed while any
reason stands. **Add this library to the workspace** puts the part in the palette as soon as the export
finishes.

What Export does, in order:

1. **Refuses before writing anything** when the cell places a kit (`pdk://`) part or a file reference that does
   not resolve, when it has no Pin, or when a part takes its substrate from the technology and the schematic
   has none to sample it on (`TL1 (MLIN) needs a technology to be converted, and this schematic has none.`).
2. **Copies the cell** into a temporary folder beside the destination: `.ccell`, the schematic and the symbol,
   renamed to the part number. Sub-cells, including any from a referenced workspace, go to `cells/<name>/`.
   Every file a document references goes to `models/`, and its reference is rewritten relative to the model
   folder. A `.va` brings every file it `` `include ``s, laid out as before so the include still resolves. A
   name already taken in `models/` gets a numeric suffix. A vendor part is copied in as well: a SPICE part's
   own `.subckt` block into `models/`, a model part as a sub-cell.
   Every part that takes its substrate from the technology is **converted** to an SnP first
   ([below](#converted)).
3. **Writes the netlist** by extracting the copied cell, so every reference in it is model-relative, then
   `part.cpart` (pins and parameters from the cell, so they agree with the `.cnl`), `library.clib` for a new
   library, and the `PublishedHash` last.
4. **Checks** the folder as `circuitrf check` does, and then **self-checks** it: the source cell and the model,
   each with a 50 Ω port from every pin to ground at the default parameters, must give the same S-parameters at
   eleven frequencies (DC, then ten log-spaced over the stated range, or 1 MHz to 10 GHz) and the same DC voltage
   at every pin, to 1e-9 relative. A difference is a refusal naming the worst entry and frequency. A model with
   converted parts is checked harder, as [below](#converted) says.
5. **Renames** the folder into place. A refusal or **Cancel** at any step leaves nothing behind.

### Parts that use the technology {#converted}

A model never carries a technology. MLIN and every other part that takes its substrate from one — the
microstrip family and the GSG pad, CPWG and SLIN, the broadside pairs, VIA and VIAGND, the MMIC passives and the
test pad — would otherwise run on whatever substrate the design placing the model has. So Export samples each
such part on the technology Simulate would use for the source schematic (its own, else the workspace's), with
the circuit model a run uses: the part on its own, with a 50 Ω port from each terminal to ground. Its
S-parameters go to `models/<instance>.s<N>p`, where N is its terminal count (MLIN 2, MTEE 3, MCLIN 4, VIAGND
1). Sub-cells' parts are converted too.

The conversion is exact at every sampled frequency. Between samples the SnP interpolates, and outside the
plan the [range warning](#cpart) applies.

- **The frequency plan** is the dialog's **Frequency** group, the EM Setup's own plan editor: Start, Stop, Step
  or Points, Lin or Log, **+ Segment** and **Single**. The default is one linear segment, 0 Hz to 20 GHz, 1001
  points. The line above it names what will be converted; with nothing to convert the group is greyed and reads
  *No component uses the technology.* The part's **Frequency** range fills in from the plan. It can be narrowed,
  but widening it past the plan is a refusal, since the S-parameters stop there.
- **DC is always in the file.** A plan that starts above 0 Hz gets a 0 Hz row added. Without one, a DC run
  holds an SnP at its lowest frequency, and a line feeding a drain inside an active model would bias wrongly
  with nothing said. The DC row is what circuitRF's DC engines do with the part, not its RF model at 0 Hz: a
  line is a short from end to end, so its row is the ideal junction of each pair of terminals it joins, and a
  terminal it joins to nothing is open. Any other part's row is the S-parameter analysis at 0 Hz, and a part
  with no finite answer there is a refusal naming its type.
- **The converted instance keeps its glyph.** In the exported `.csch` it is an SnP with its name, position and
  rotation, its `File`, and [`Symbol`](dynamic-symbols.html#snp) set to its old type, so its pins stay on its
  wires and the picture still reads as a line. The `.cnl` is extracted from that schematic as usual.
- **Provenance.** Each file's header comment names the part's type, its parameters as written, the
  technology by name (never its path), the frequency plan and the circuitRF version. `part.cpart`'s
  `Converted` holds the same record.
- **The self-check is harder.** The S-parameters are compared at every sampled frequency above 0 Hz. The DC
  comparison drives current through the converted parts — behind pin *k*'s 50 Ω is a source of *k* × 0.1 V —
  so a wrong DC row shows as a voltage; each pin voltage is judged against at least 0.1 V, the smallest
  drive. The success message gives the worst |S| difference halfway between samples, over every entry above
  −40 dB: *Exported TSTBRD to MyLib (revision 1). 4 parts converted to S-parameters, 1001 points; between
  samples, within 0.00029 dB.* That number is information, not a gate.

Exporting a part number that is already in the library asks first, and replaces it with `Revision` one higher.
Messages says *Exported TSTFET100 to MyLib/Amplifiers (revision 1).*

On the command line:

```
circuitrf export part <cell folder> --library <dir> [--category a/b] [--part PN] [--description …]
    [--kind active|passive] [--value …] [--freq-low … --freq-high …] [--conditions …] [--footprint …]
    [--doc <file>]… [--symbol own|fet|r|c|l|box] [--replace] [--include-vendor-parts]
    [--sweep start=… stop=… npts=…|step=… [unit=GHz] [log]]…
    [--library-name … --publisher … --license … --library-version …] [--json]
```

The defaults are the dialog's. What the dialog would have asked about is a refusal naming the flag that
answers it: `--replace` for a part already exported, and `--include-vendor-parts` for a cell that places vendor
parts, whose libraries' terms then travel with the model. `--sweep` is one segment of the frequency plan, in
the `sparam` directive's own keys (`unit=` defaults to Hz, as it does there), repeated for more; the default is
the dialog's. With no part to convert, `--sweep` is refused rather than ignored. The paths created are printed, and listed in
`--json`. Over MCP it is `create what=libraryPart cell=<cell folder> library=<dir>`, with `sweep` a list of
segments (`["start=1 stop=20 npts=101 unit=GHz"]`). What a converted part's model says about itself, such as a
junction used outside its calibration, is reported once per part as a warning.

A library can also be written by hand or by a script: the [example](#example) is the smallest one. Check it
with `circuitrf check <library>`.

The MCP server's `reference` tool serves this page as the topic `model-library`.
