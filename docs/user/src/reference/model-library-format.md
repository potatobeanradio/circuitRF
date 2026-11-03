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
  "Converted": [ { "Instance": "TL1", "Type": "MLIN", "File": "models/TL1.s2p" } ]
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
| `Converted` | No | The parts Export replaced by S-parameter files. Provenance; nothing reads it to run. |

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

**Export to Library…** on a cell packages it as a model folder: it copies in every file the cell uses, converts
each part that needs a technology to an S-parameter file, writes `part.cpart`, the `.cnl` and the hash, and
checks the result. The command line spelling is

```
circuitrf export part <cell folder> --library <dir> [--category a/b] [--part PN] [--description …]
```

A library can also be written by hand or by a script: the [example](#example) is the smallest one. Check it
with `circuitrf check <library>`.

The MCP server's `reference` tool serves this page as the topic `model-library`.
