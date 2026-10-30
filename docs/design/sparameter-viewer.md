# S-Parameter Viewer

A quick look at one Touchstone file: what the file states about itself, its reflections on a Smith
chart, its transmissions on a polar chart, and every chosen term as magnitude in dB and as phase
against frequency. Companion of the user page `docs/user/src/reference/sparameter-viewer.md` and of
`cli.md` §33 (`circuitrf snp`).

## What it is, and what it is not

It answers *is this the file I think it is, and does it look right* — the check made before a file is
trusted as a model, a measurement or a reference. It is a viewer. It edits nothing, it saves no document
of its own, and it measures nothing about the network: passivity, reciprocity and causality stay where
they already live, in `TouchstoneHealth` and `circuitrf check`.

The four pictures are a Smith chart of the reflections, a polar chart of the transmissions, and
magnitude and phase of every chosen term. The polar chart draws Sxy and Syx only. Polar and phase
are off until their toolbar buttons are clicked.

## A window, not a document

The owner asked for the shape the Match Designer and the Signal Designer have: a window of its own,
opened from **Tools**, not a docked document. It needs no workspace. Several may be open; a second
request for a file already on show brings that window forward rather than opening another, because two
viewers of one file would be the same picture twice.

There is no file type. **Save as Data Display** writes an ordinary `.cdd` — the plots as they stand,
editable afterwards in the Data Display — and that is the whole of the persistence. The same bytes are
what `circuitrf snp -o x.cdd` writes, because both call `SParameterViewerPreset.Build`.

## Where it opens from

- **Tools ▸ S-Parameter Viewer**, empty, with a file picker. Both menu surfaces, as every Tools entry is.
- A Touchstone file in the Workspace, bookmarked or loose inside the folder: **Open in S-Parameter
  Viewer** on its menu, and a double-click. The double-click opens on the mouse-up, the rule a `.crail`
  already follows, because a window shown from inside the press comes up behind the workspace.
- A placed **S1P / S2P / S3P / S4P / SnP**. All five are `SymbolKind.Snp`, so the panel's own **Show**
  button covers them. It enables once the `File` parameter resolves — through `SnpPathPolicy` — to a
  file that is there, and opens this viewer. There is no second button: the file row is already full.
- A Touchstone row of a Data Display's dataset list, the same **View**.

## The split

The window owns no reading, no rule and no plot, and the verb owns none either. Four pieces, each the
only one of its kind:

| | Lives | Does |
|---|---|---|
| `TouchstoneFacts` | `src/RfCore/Data` | What the file states, and its structural defects as `Diagnostic`s |
| `TouchstoneSource` | `src/Render/DataDisplay` | One file read once, as the `IPlotDataSources` the plots resolve against |
| `SParameterViewerPreset` | `src/Render/DataDisplay` | The four plot configs, and the file pane's wording |
| the window | `src/Ui/SParameters`, `src/Ui/Views/SParameters` | Hosts the plots and offers the choices |

`circuitrf check`'s structural findings — a name that contradicts the data, a frequency axis that is
not sorted, a reference no renormalisation can use — are `TouchstoneFacts.Findings`' rules. The
sentences stay `check`'s own diagnostics, so its report is unchanged; the decision of what counts is
shared, which is the point.

## The file's own axis

The frequency axis is pinned to exactly the file's lowest and highest point, in the engineering unit
that span reads in — a file whose option line says Hz over a 1 to 5 GHz sweep still reads in GHz,
because the option line's unit is what the file was *written* in and the axis is what it *spans*. A
log-spaced file gets a log axis. Every trace is a network trace, so it draws the file's own samples
and nothing between them.

The left pane is titled **File information**. It says whether a sample sits at 0 Hz — Yes, or No with
a yellow warning, which the status bar repeats — and, when the file has any, how many comment lines
it carries. The path under the file name opens that file in the system file manager. A Smith hover
names the term at that frequency, then Γ in rectangular form and as a magnitude and angle, then
`Z=…`, ending in Ω.

A grid is called uniform or logarithmic within a part in 10³ of its step, with a floor at the
precision a frequency is printed to. `TouchstoneHealth`'s own tolerance is 10⁻⁶ and answers a
different question — whether the samples ARE equally spaced, which its transform needs. A sweep
written to six digits is still the sweep it was taken as, and calling it irregular would be a wrong
statement about the file.

## What is drawn

The Smith chart carries the reflections. Both rectangular plots carry every ticked term, magnitude in
dB and phase over ±180°. A term's colour is its place in the selection, so it is the same colour on
every plot and the terms on show take the palette's first colours. Past six terms a rectangular plot
draws no legend: sixteen entries cover the curves they name, and the tick boxes carry the colours.

The window opens on the Smith chart and magnitude. Polar and phase are off until clicked. Every
toolbar button can be off, and then the pane is empty. One plot fills the pane. Smith and polar
sit side by side. Magnitude and phase stack, magnitude on top. Two of different shapes stack, the
round chart on top. All four are Smith
and polar across the top, magnitude and phase across the bottom. The polar chart draws transmissions
only.
Strokes, grid lines and axis figures are held at one weight, so a plot that fills the pane is not
drawn with a heavier pen than one sharing it. The window draws no title above a plot and lists the
trace names beside it, up to the same six; the saved `.cdd` and `circuitrf snp` keep the preset's
titles and in-plot legend. A Smith chart
in a Data Display is given a canvas taller than the disc, for the title and the rows under it. The
window's row cannot grow, so the hosted chart drops both — a disc pushed down to make room for a
title paints past the row, over the window and over the plots below.

Up to four ports the viewer opens on every term. Above that — an `.s16p` has 256 — it opens on the
reflections and each forward neighbour S(i+1, i), and the rest are a tick away.

**A Y- or Z-parameter file is converted to S before it is plotted.** A network trace reads its
source's matrices as S whatever the option line said, so handing it the file as read would draw
admittances on a Smith chart and call them reflections. The file pane still says Y or Z, because that
is what the file is.

## Headless

`circuitrf snp` reports the file pane's own lines, draws the four plots through `RenderDataDisplay`
and writes the `.cdd` the window saves. Exit 1 when the file has a defect, after anything asked for
was written: a picture of a broken file is still the picture the window shows. Detail in `cli.md` §33.

## Out of scope, on purpose

Touchstone 2 keyword files (`[Version] 2.0`, `[Matrix Format]`, `[Number of Ports]`) — the reader does
not read them yet, and the viewer refuses a file the reader refuses. Passivity, reciprocity and
causality, which `circuitrf check` already reports.
