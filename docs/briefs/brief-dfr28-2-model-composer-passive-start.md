# Brief DFR28-2 — Starting a passive model from an S-parameter file

**Series:** designer feedback round 28 (2026-10-26). · **Tag:** `R-dfr28-2-<n>`
**Area:** `src/Ui/ModelComposer/ModelComposerViewModel.Data.cs` (`ImportAsync`), `ModelImportViewModel`,
`src/Ui/Views/ModelComposer/ModelComposerWindow.axaml.cs` (`PickSavePath`), `src/Design/Modeling/Passive/PassiveFit.cs`
(`Propose`), the Topology step's view model. Findings go in `src/Ui/RESOLVED.md`.
**Tests:** `tests/Ui.Tests/ModelComposer/Passive/` (beside `PassiveImportDialogTests`).

---

## 0. What the designer did

He opened the Model Composer for a passive block and fed it the six-port Touchstone file his 3D EM run wrote for a
board. Three things got in the way. The first two are fixed in this round's commit; this brief covers the rest.

1. **Fixed:** the Import Data dialog opened with the transistor pattern `Vgs{vgs}_Vds{vds}` and refused the file,
   because its name carries no bias. A passive document now starts with an empty pattern, which the reader already
   treats as "no pattern", and has no Quiescent row (`PassiveImportDialogTests`).
2. **Fixed:** the file picker's Data filter listed `.s1p`–`.s4p` only, so the `.s6p` showed only under All Files.
3. **Import a data set** on a new document **opens a Save dialog first**, titled "Save the Model Composer
   document", before any data is chosen. The reason is real: sources are stored relative to the `.cmc`, and the
   import writes its cache beside it. But nothing on screen says so, and the user is asked to name a file before
   they have said what goes in it.
4. A six-port block has **no built-in topology**. The library has one-ports and two-ports only
   (`PassiveTopologies.All`), so Propose finds nothing for six ports unless the user draws a topology.

## 1. What to do

### R-dfr28-2-1 — Choose the data first, then save

On an unsaved document, **Import a data set** opens the importer first. Give it a provisional `.cmc` path: the
data's folder, named after the first source's stem, so the preview, relative sources and refusals all work as
before. On OK, the Save dialog opens in the data's folder with that name pre-filled and a title that says why
("Save the model before importing — its data is kept beside it"). Cancel at the Save dialog imports nothing and
leaves the document as it was. Do not save a document under a name the user never confirmed.

Gate: a test drives an unsaved document through Import with a stub importer and save picker. The save picker is
asked once, after the importer, with the data's folder and stem. Cancel leaves `Document.DataSets` empty.

### R-dfr28-2-2 — Say what a many-port block can be fitted with

When Propose has no built-in topology for the block's port count and no drawn one, it says so at the Topology step
in one line: "No built-in topology has N ports: draw one (pins 1…N are the ports) or place the file itself as an
S-parameter component." That is the honest answer for a board's six-port EM result, which the designer wanted in a
schematic. The S-parameter (SnP) component already takes it as it is. Today the step finds nothing and says nothing
about why.

Gate: Propose on a synthetic three-port block with no drawn topology returns no proposals and that note.

## 2. Not in scope

A many-port topology library. Built-in topologies stay one- and two-port.
