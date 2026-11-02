# Brief DFR32-3 — 3D view: an air-box edit that does not show, and what a run's saved fields weigh

**Series:** designer feedback round 32 (overview: `brief-dfr32-0-overview.md`). · **Tag:** `R-dfr32-3-<n>`

## 1. An air-box edit sometimes does not show until the `.c3d` is reopened

**The gap.** Editing the air box's padding in a `.c3d`'s Properties sometimes left the drawn box where it was. Once,
the box went on showing the old 100–300 % after 20–30 % had been typed. Closing and reopening the file showed the
new box, and on one or two occasions the whole application was restarted.

DFR32-0's direct fix (re-checking the files a scene was built from when the tab is activated) covers a stale
*layout*. It does not cover this case, because the air box lives in the `.c3d` itself and reaches the scene through
`ResolveRecords`, not through a watched file.

**R-dfr32-3-1 — reproduce headlessly first.** Use a synthetic two-layer board placed in a `.c3d` with one embedded
Palace setup. Drive the same view-model calls the Properties panel makes: one face, then all six, then a fast
sequence of edits while a rebuild is still in flight. After each, settle and compare the drawn box
(`Viewer.Scene`'s air-box object) with the box `C3dProblemAssembly` resolves from the document. Also try the same
edits with the setup's editor tab open beside the `.c3d`, since an embedded setup is edited through an
`EmSetupEditorViewModel` too. `tools/UiSnapshot --c3d` gives the picture. Report which path drops the edit, fix it
there, and add the test that reproduced it. **If nothing reproduces it, stop and report.** Do not change code on a
guess.

## 2. A multi-port run's saved fields are hundreds of megabytes

**The gap.** By default a Palace driven run saves the field at the sweep's centre, once per driven port, for the 3D
view's field plots. On the 12-port board that came to 589 MB per run, kept in `results/<setup>.palace/`. The designer
read it as leftover files, and the workspace grew to hundreds of megabytes (see DFR32-0 on the archive report). Most
multi-port S-parameter runs are never opened as field plots.

*Decision D1 for the owner:*
- **(a) recommended:** when a setup states no `SaveFieldsGHz` and has more than 4 ports, save no fields, and say so
  in the run's summary line ("no fields saved: 12 ports would write about 590 MB; name a frequency in Save fields at
  to keep them"). Estimate the size from the mesh and port count, using the measured 137 MB per frequency per port at
  146,769 tetrahedra, scaled by tetrahedra. Setups that state a list are unchanged. A field plot on such a run offers
  Other frequency…, which already adds a frequency and re-runs.
- (b) Keep saving, but state the size before the run starts, beside the memory estimate, so the cost is known up
  front.
- (c) No change. Only the summary line, which already states what the field files took.

**R-dfr32-3-2 — a way to drop a run's field files.** There is no command for it today: the designer deleted the run
folders by hand before archiving. Add Delete Saved Fields to a solved setup's row menu, in the project tree and in a
`.c3d`'s setup list. It moves the run folder's field directories to the Trash, keeps the mesh, the Touchstone and the
`.npy`, says how many bytes it freed, and clears the setup's field-plot solutions, which then say why they are empty.
The run summary names the folder the fields are in, so "large files left after the simulation" has an answer on
screen.

## Gates

- One test per claim. For item 1, the reproducing test. For item 2, the default rule at 4 and 5 ports, the summary
  line's estimate, and a stated list left alone.
- Item 2 changes `PalaceConfigWriter.SavesFields`'s default: regenerate the Palace goldens deliberately and confirm
  in the diff that only the field-output lines moved (`FieldTests.Gate1_…` holds that).
- Docs: `em-3d.md`'s Fields for the 3D view section.
