# Example plugins

Plugins written against `docs/design/plugins.md`, used by the tests. Each is an independent implementation of the
contract: it references nothing in this repository and speaks the frame itself. The contract is the user guide's
`docs/user/src/reference/writing-plugins.md`; the Python helper that hides the frame is `tools/plugin-python/`.

| Folder | Kind | Shows |
|---|---|---|
| `hello/` | `action` | The minimum: one quantity parameter, then a two-resistor `.cnl` and `.csch` to place |
| `calculator/` | `panel` | A panel: three quantities and a choice in groups, value and text readouts, messages, a disabled button, and a `.cnl` to place — an L-network match recalculated as you type |
| `stub/` | `panel` | **The reference plugin**, every view kind drawn from data: a single open stub matching a load on a substrate, its width and effective permittivity from circuitRF's own line model (`lineModel`), with a response plot whose marker is bound to the frequency, a Smith chart, a dimensions table, the artwork as PCell-wire shapes with a handle on the stub, and the circuit as a staged `.cnl`. **Simulate in circuitRF** runs that `.cnl` through circuitRF's engine (`simulate`) and draws its S11 beside the closed form's. Placing it writes the circuit as a `.cnl` and a drawn `.csch`, the artwork view as the cell's `.clay`, and a `.cem` naming it |
| `host/` | `panel` | Sends one host call — `simulate`, `resolve`, `evaluate` or `lineModel` — written as JSON in its one field, and shows circuitRF's `hostReply` and the one before it |
| `window/` | `window` | Opens no real window: stages a `.cnl`, sends `placed` under its own id, waits for `placedReply` and exits (with `stay`, waits for `bye` instead) |
| `csharp-window/` | `window` + headless | A C# program published with NativeAOT, with its own Avalonia window and a headless form: a microstrip line section sized by `lineModel`, placed as a `.clay`, `.cnl` and `.cem`. Publish it first (`publish.sh` / `publish.ps1`); its `golden/` frames are read back by the tests. See its own README |
| `broken/` | `panel` | One script that misbehaves in the way its first argument names — `exit`, `hang`, `text`, `malformed`, `wrongid`, `outside`, `stderr`, `exit-after-first-reply` — so the tests can show circuitRF survives each |

To try one in the application, add its folder to `CRF_PLUGIN_PATH` (`;`- or `:`-separated) and open Tools ▸ Plugins.
