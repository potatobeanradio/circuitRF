# UpdateLayoutHarness

Runs **Design ▸ Update Layout from Schematic** headlessly, the way the menu item runs it: a real `WorkspaceViewModel`
opens a scratch workspace and the schematic, and each `run` goes through `WorkspaceViewModel.RunLayoutUpdate`, with
the user preferences it reads, the layout opened as a document, the EM setup step and every line it posts to the
Messages panel. Between runs, the steps edit the schematic and the layout as a designer would, so a re-run's
behaviour (SDL-4's hand edits, SDL-5's port labels, locks, Re-snap, Restore Removed Parts) can be watched from a shell.

It is not part of the application and not in `circuitRF.slnx`, so a plain `dotnet build` does not build it.

```
dotnet build tools/UpdateLayoutHarness
dotnet run --project tools/UpdateLayoutHarness --no-build -- <design> -o <out-dir> [--tech <file.ctech>] \
    [--no-connect] [--em-setup] [steps…]
```

`<design>` is a folder name under `testdata/schematic-to-layout/` (`C2-branch-line`), or the path of any cell folder
that has a `schematic/` folder. It is copied into `<out-dir>/ws`, a fresh workspace whose default technology is
`--tech` (default `testdata/schematic-to-layout/microstrip-0508.ctech`); the source is never written. Preferences live
in `<out-dir>/state` unless `CRF_STATE_DIR` is already set, so the run neither reads nor writes your own.
`--no-connect` turns off Settings ▸ Connect parts on Update Layout; `--em-setup` turns on Create or update EM setup.

## Steps, in the order given

| Step | Does |
|---|---|
| `run` | Update Layout from Schematic. |
| `restore` | Layout ▸ Restore Removed Parts (a run that puts back parts deleted by hand). |
| `resnap:A,B` | Layout ▸ Re-snap to Schematic on those parts. |
| `set:R1.L=4` | A schematic parameter's expression (its unit is kept). A VAR block's variable is `set:VAR1.L90=5.6604`. |
| `rot:TL3=90`, `mirror:TL3` | A schematic symbol's rotation (0/90/180/270) or mirror. Wires are not dragged with it, so its pins may leave their nets. |
| `move:TL2=500,0` | A layout instance moved by hand, in µm. |
| `turn:TL2=90` | A layout instance turned about its own origin, degrees counter-clockwise. |
| `delete:TL3` | A layout instance deleted by hand. |
| `lock:TL1`, `unlock:TL1` | Lock Position. |
| `undo`, `redo` | The layout's own undo stack, so an undo of a run can be checked. |
| `dump` | Every instance (origin, rotation, mirror, lock), every port label and the placement record. |
| `save[:name]` | Saves the layout to the cell's own `.clay` (so it opens with the workspace) and copies it to `<out-dir>/<name>.clay`. |

Layout edits (`move`, `turn`, `delete`, `lock`) change the model directly rather than through the editor's undoable
commands — what a re-run reads is the same either way. A step that fails prints `!` and the run carries on; the exit
code is then 1.

## Looking at the result

Save, then photograph the layout in the real editor view with UiSnapshot:

```
dotnet run --project tools/UpdateLayoutHarness --no-build -- C2-branch-line -o /tmp/c2 run move:TL2=500,0 \
    set:VAR1.L90=5.6604 run dump save
dotnet run --project tools/UiSnapshot --no-build -- --clay /tmp/c2/ws/C2-branch-line/layout/C2-branch-line.clay \
    -o /tmp/c2/after.png
```
