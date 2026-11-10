# Example plugins

Plugins written against `docs/design/plugins.md`, used by the tests. Each is an independent implementation of the
contract: it references nothing in this repository and speaks the frame itself.

| Folder | Kind | Shows |
|---|---|---|
| `hello/` | `action` | The minimum: one quantity parameter, then a two-resistor `.cnl` and `.csch` to place |
| `broken/` | `panel` | One script that misbehaves in the way its first argument names — `exit`, `hang`, `text`, `malformed`, `wrongid`, `outside`, `stderr` — so the tests can show circuitRF survives each |

To try one in the application, add its folder to `CRF_PLUGIN_PATH` (`;`- or `:`-separated) and open Tools ▸ Plugins.
