# Schematic — local conventions for `src/Ui/Schematic/`

Read with root `CLAUDE.md` and `src/Ui/CLAUDE.md`.

## SDD placement defaults (brief-p1tone-num-sddx-defaults, 2026-06-17)

`ComponentTypeRegistry.DefaultParameters(SymbolKind.Sdd, portCount)` now returns:
- `NumPorts = portCount`
- One `I[x,0] = _vx/50` per port x ∈ [1, portCount] (`ShowOnSchematic = true`)

This means a freshly-placed SDD acts as N independent 50 Ω conductances without any user edits — it
can be run through S-parameter analysis immediately and produces physically meaningful results.

The notation `_vx` is the port-x voltage (`V(net[2x]) − V(net[2x+1])`) in SDD equation syntax. The
engine parses `I[x,0]` as a two-index port-x current at harmonic 0 (the DC/baseband member in HB;
the only current in S-param mode where the SDD is treated as linear).

**Do not change these defaults without also updating `ParameterEditorRegistryTests` and
`SddDefaultParamsTests`** — they are the gate tests for this behavior.

## P1Tone Num parameter and port-number pool (brief-p1tone-num-sddx-defaults, 2026-06-17)

`DefaultParameters(SymbolKind.P1Tone, 0)` now includes `Num` as the first parameter (before Pavl/Z/Freq/Phase).
`Num` is the S-parameter port index; it is auto-assigned at placement from the **shared Term + P1Tone pool**
(`NextFreeTermNum` scans both symbol kinds) so Term:T1 (Num=1) and P1Tone:P1 (Num=1) can never coexist
on the same testbench top level.

`CommitPlacement` and `CommitInlineEdit` both handle the P1Tone case, mirroring the Term case.
