# Brief DFR32-0 — designer feedback round 32: overview and triage

**Series:** designer feedback round 32 (2026-10-30). · **Tag:** `R-dfr32-<brief>-<n>`
**Source:** a field test that took a two-layer evaluation board, redrawn from a picture, through a 12-port Palace driven
solve (Draft, 201 points, 43 min, 4 processes on an 8 GB machine). The solved board was then used in a schematic with
the amplifier's own S-parameters and supplier S-parameters for the matching parts. Also a trace-plus-via comparison
between an EM solve and the circuit models. The workspaces, logs and Palace log are kept outside the repo.

Briefs in this round:

| Brief | What | Size |
|---|---|---|
| DFR32-1 | The default air box: λ/8 or the structure's extent on every face, z included, is far too much air round a thin board | medium: measurement first |
| DFR32-2 | Ports: an edge port on a wide conductor takes the whole edge, a port on a round pad blows up the mesh, port labels pile up | medium |
| DFR32-3 | 3D view: an air-box edit that does not show until the file is reopened, and how much a run's saved fields weigh | small/medium, needs a repro first |
| DFR32-4 | About 1 dB of loss a bench does not see, on the ports behind a via between two planes the solve region cuts | investigation, then a note and maybe a default |
| DFR32-5 | Trace Impedance fails every trace that enters its own via's clearance hole | small |

## Fixed directly this round (not briefed)

1. **Palace aborted with "Save frequency 2.7 not found in sample frequencies".** The setup asked for no field
   frequency, so the run saved the sweep's centre. On a 0.4–5 GHz, 101-point sweep that centre (2.7 GHz) is also one
   of the sweep's own points, which Palace computes as 2.6999999999999997. Palace merges samples closer than 1e-9 GHz
   and keeps the lower one, then looked the save frequency up by exact equality under `SaveStep` and failed. The writer
   now names save frequencies in Driven's `Save` list, which Palace snaps to the merged sample within that tolerance.
   The Point sample that makes each one a sample stays. Checked with the installed Palace 0.18.1's own `--dry-run`: the
   old shape reproduces the designer's error and the new one passes config parsing. Goldens `microstrip` and `via`
   regenerated (the `Save` list replaces `SaveStep`; nothing else moved). Test:
   `FieldTests.SaveFrequency_OnASweepPoint_IsNamedInDrivenSave_NotBySaveStep`.
2. **A `.c3d` sometimes stayed on the old layout after the layout was saved**, until it was closed and reopened (more
   than once, on one occasion). The `.c3d` elaborator reads each placed `.clay` from disk, keyed by its stamp, and the
   only thing that told it to look again was a `FileSystemWatcher` over the whole workspace. That watcher had the
   default 8 KB buffer and no `Error` handler. The designer's workspace sits in a synced cloud folder, and a Palace run
   writes hundreds of field files into the same tree. A dropped event left the scene stale, with nothing to say so.
   Now the editor records the stamps of every file its scene was built from (`C3dElaboration.FilesRead`). It
   re-checks them whenever its tab is activated and whenever the watcher reports an error, and the buffer is 64 KB.
   Test: `HierarchyGateTests.RecheckInputs_APlacedLayoutChangedUnseen_IsElaboratedAgain`.
3. **A supplier's series-measured S2P made a passive filter show gain at single frequencies** (+1.09 dB at 625 MHz,
   with steps at 1000 and 1150 MHz). This was the designer's "started adding points". A part measured in series has
   S11 + S21 = 1, and I − S is singular for every series element, so `SnpModel.Stamp`'s S → Z conversion was rounding
   noise: cond(I − S) was about 1e16 at every sample of the file. Most frequencies happened to come out solvable, but
   wherever a sweep point fell exactly on one of the file's samples (100 MHz + k·21 MHz against a 25 MHz sweep, every
   525 MHz) a 3.9 pF part read |S11|² + |S21|² = 3.86. A block whose converted Z exceeds 1e6·Z0 is now stamped as its S
   through the wave constraints `IdealSBlockModel` already uses for an ideal open, which is exact for any S. A
   well-conditioned block keeps its Z stamp, so no golden moved: the 309 SnP, S-parameter and hero tests and all of
   `Core.Tests` pass. On the designer's board the three part files are now passive at every point and the curve is
   smooth. Test: `SnpStampTests.SeriesMeasuredFile_OnAndBetweenItsOwnSamples_IsTheSeriesElement`, which fails with
   the old stamp.

4. **The default sweep for a new EM setup is 0.5–8 GHz in 50 MHz steps** (151 points), not 1–20 GHz. That applies to
   File ▸ New EM Setup, the layout's EM setup command and a `.c3d`'s Add Setup. The model's own default stays 1–20 GHz,
   because that is what a `.cem` without a Frequency block has always meant. Docs: `em-setup.md`.

## Closed without the file

**"An archive of 500 MB that zipped to 18 MB; after the next run 900 MB."** The file will not be available. Archive
Workspace writes every entry at `CompressionLevel.Optimal`, and history ignores solver run folders (`*.palace/`). A
circuitRF zip cannot re-zip 27×, so the 500 MB file was most likely the workspace folder itself, swollen by run
folders. That fits what the logs do show: the 12-port run's field files alone were 589 MB, and each run replaces its
folder's contents with a new set. DFR32-3 item 2 addresses that cause (the field-saving default, and a command to
drop saved fields). Round 31 already fixed unticked results being archived anyway. Nothing further is planned for
the archive itself.

## Answers to give the designer (not defects)

1. **The EM run showed more loss than the circuit models on the trace-plus-via comparison** (−0.45 dB against
   −0.31 dB at 4 GHz). Three things differ between the two sides, and none of them is dissipation:
   - The circuit side models the inner-layer line as a microstrip. The run printed that it is a stripline, with
     ground above and below, so the microstrip model is the wrong model there.
   - The EM side's port 2 is a lumped sheet 1.07 mm tall, up to the setup's named reference 'Inner 1'. The floor plane
     is 0.2 mm below the line. A port that tall adds its own series inductance, and the mismatch it causes shows in
     |S21|.
   - FR-4 at tanδ 0.02 over 4 mm of line dissipates about 0.05–0.1 dB at 4 GHz. Most of both curves' |S21| is
     therefore mismatch, so compare 1 − |S11|² − |S21|² (the power actually lost) rather than |S21|.
   The 868 MHz fit with a 1.4 pF shunt per part is consistent with this: a small band, where the parts' pads dominate.
2. **Each EM run starts from scratch**: a new mesh, no earlier solution reused. A changed port or air box remeshes.
   An unchanged geometry script reuses its mesh (the run says so).
3. **Forgetting a port before a one-hour run.** The run's first lines list every port. A `check` of the `.c3d` before
   Simulate lists them too. No change is proposed.

## Rules for every brief in this round

- Do not quote the designer or the owner, in the repo or in commits. Paraphrase.
- The field-test board is private. Nothing from it (layout, net names, part designators, coordinates) goes into the
  repo, a test fixture or an example. Reproduce each defect on synthetic geometry.
- Name no component maker and no other EDA tool, anywhere.
- Findings go in the relevant `RESOLVED.md`, never `CLAUDE.md`.
- One test per claim. Run only the test classes the change can reach, never the full suite. Keep any EM run short
  and scoped. A run that would take more than a few minutes needs the owner's say-so first.
