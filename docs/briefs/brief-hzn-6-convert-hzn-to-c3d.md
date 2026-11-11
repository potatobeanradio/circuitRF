# HZN-6 — convert a `.hzn` design to a native `.c3d`

Series: `brief-hzn-0-overview.md`. Needs HZN-2.

## Why

A design that should live in circuitRF from now on, with circuitRF's cells, technology, hierarchy and history, needs a
`.c3d` of its own instead of a view into a `.hzn`.

## What to build

### R-hzn6-1 — the conversion

`HznToC3d.Translate` (brief 2) gives the document. Conversion writes it into a cell:

- **Into a new cell** (the default): created through `CellCreate`, the function New Cell calls, with a 3D view holding
  the `.c3d`.
- **Into an existing cell's 3D view:** refused if that view exists, unless replacing it is chosen explicitly.
- **Materials:** the in-memory technology's materials are matched by name against the workspace technology.
  - A material that matches exactly is used as it is.
  - A missing one is reported, and added only with `--add-materials` / the dialog's checkbox. This is the rule
    `import part` follows for layers: the technology is never written unasked.
  - A material whose name matches but whose properties differ is a refusal listing both sets of properties.
- **STEP files** an `Import` step names are copied into the cell's `3d/` folder, as `C3dStep` expects.
- **The source `.hzn` is never written.**

### R-hzn6-2 — the GUI and the CLI

- **GUI:** Convert to .c3d… on a 3D design in the tree (brief 3's menu), and Save As… from an open `.hzn` design (brief
  5). The dialog asks for the cell name, the target folder and the materials choice.
- **CLI:** `convert <file.hzn> --design D -o <cell folder>` (`--design` is required when the file holds more than one 3D
  design; the refusal lists them, as `convert` lists parts). The pair table gains the row.

## Gates

- `HznConvertTests`:
  - a converted fixture opens as a native `.c3d` equal to brief 2's document
  - missing materials are reported and not written without the flag
  - a conflicting material is refused
  - the source file is byte-identical afterwards
- The CLI as a process matches the in-process call byte for byte.
- UiSnapshot of the dialog.
