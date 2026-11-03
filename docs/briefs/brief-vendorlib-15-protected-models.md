# Brief VL-15 — protected models: compiled-only model folders, and signed libraries

**Series:** [Vendor libraries](brief-vendorlib-0-overview.md), model-library part ([VL-6](brief-vendorlib-6-model-library-format.md)) · **Tag:** `R-vl15-m`
**Area:** `src/Design/VendorLibraries/ModelLibrary/`, `src/Core/Devices/External/VerilogAFileResolver.cs`,
`tools/osdi-worker/` (verification only, unless §4's test finds a gap), `src/Cli/Check.cs`, new
`src/Cli/Sign.cs`, the Export to Library dialog (brief 10), the vendor-library palette (brief 9),
`docs/user/src/reference/model-library-format.md`, new `docs/user/src/reference/protected-models.md`
**Depends on:** 6, 9, 10. Independent of 11–14.
**Rule:** overview §0's naming rule holds unchanged. No manufacturer, part number, other simulator or
other simulator's protection scheme is named anywhere. Fixtures are synthetic (`TSTFET100`).

---

## 0. Why, and what this brief does not do

Device makers ship their nonlinear models encrypted, each to one simulator's own key (overview §0). A
simulator decrypts such a model on the user's machine, and in a closed program the key is hidden inside that
program's binary. **circuitRF cannot take part in that.** Its source is public, so any key it held would be
public, and anyone could rebuild it to write the decrypted model to disk. An encryption feature here would
protect nothing, and the people it is meant to reassure would see that at once.

What does protect a model, in any simulator, is **compiled code**: the maker compiles their own model, and the
host only asks it for currents, charges and derivatives at given terminal voltages. circuitRF already runs a
compiled `.osdi` in a separate worker process (`veriloga.md`). That protection lives entirely in the maker's
binary, so the host being open source costs the maker nothing. **circuitRF never holds the maker's secret, so
there is nothing in circuitRF to extract.**

Two things are missing, and this brief adds them:

1. **A model folder cannot be compiled-only.** Brief 6 assumes every model ships its source. A maker who wants
   to publish a protected model has no way to put it in a library.
2. **A library cannot say who published it.** `PublishedHash` (brief 6 §3b) shows that a part changed, but
   anyone who edits a part can recompute it. For a library that carries native code, "is this the maker's
   file, unmodified" is a security question as well as a trust one.

**Not in this brief, deliberately:** encryption of any file, a licence-key scheme run by circuitRF, a
licence server, remote model evaluation, and obfuscation presented as protection.

## 1. What is compiled, and what is not

A protected model folder is an ordinary model folder (brief 6 §3) in which **the device the maker wants to
protect is a compiled `.osdi` with no source beside it.** Everything else stays as it is:

- **The `.cnl` stays plain text and is still what runs (brief 6 D1).** It is the thin wrapper that names the
  compiled device, its pins and its parameters, and wires it to whatever surrounds it. circuitRF does not
  compile, encrypt or hide a netlist, and nothing here changes how the `.cnl` reads.
- **SnP data stays a plain `.sNp` beside the model.** A compiled device cannot read a Touchstone file, so a
  measured package or fixture remains an ordinary `SnP` instance in the `.cnl`. That gives up nothing: the
  S-parameters of any placed part can be simulated and exported, so linear data was never protectable.
- **The topology a maker wants hidden goes INSIDE the compiled device.** If the package parasitics or an
  internal network are part of the secret, the maker writes them into the same Verilog-A module as internal
  nodes, then compiles it. What sits in the `.cnl` is what the maker chose to publish. Writing it by hand is
  this brief's route; [VL-16, Compile Netlist](brief-vendorlib-16-compile-netlist.md) generates that module
  from a circuitRF cell, ComposedFETs included.

The format page (§6) states this split in exactly these terms, so a maker deciding what to compile reads it
before starting.

## 2. Decisions

- **D1 — The `.cnl` is platform-neutral.** It names `File="models/TSTFET100.osdi"` exactly as an unprotected
  model would. Which platform's build that resolves to is a resolution rule (§3a), not something the `.cnl`
  spells out. One `.cnl` then runs on every platform the maker built for, and `check` (§3c) proves which ones.
- **D2 — Platform builds live in `models/<rid>/`**, where `<rid>` is the runtime identifier circuitRF
  packages for: `win-x64`, `win-arm64`, `win-x86`, `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`.
  These are the same identifiers the packaging scripts use, so there is one list.
- **D3 — A compiled-only model is listed on every platform and placeable only where a build exists.** On a
  platform with no build it shows in the palette greyed, with *"No build of this model for <rid>. The
  publisher ships it for: …"* It is never hidden: a part that disappears from one colleague's palette and not
  another's is a support call nobody can explain.
- **D4 — Signing is information, not a gate.** An unsigned library behaves exactly as it does today. A
  signature adds a line to the library's row (§5c); it never blocks a run. The thing that may stop a maker's
  code from running stays the existing external-worker policy (`ExternalWorkerPolicy`), unchanged.
- **D5 — Signatures are ECDSA P-256 over SHA-256, using `System.Security.Cryptography` only.** It is in every
  supported runtime, so no package and no native dependency is added.
- **D6 — Trust is per user, on first use.** circuitRF ships no list of trusted publishers and runs no
  authority. The first time a user adds a signed library, they see the publisher name and the key's
  fingerprint and choose whether to trust it. A later library that names the same publisher with a
  DIFFERENT key gets the strongest warning in this brief (§5c).

File names (`models/<rid>/`, `library.sums`, `library.sig`) are this brief's choice. Change them before the
build starts if wanted; once built they are part of the published format.

## 3. `R-vl15-1` — compiled-only model folders

- **`R-vl15-1a` Platform resolution.** Where a `.osdi` reference is resolved (`VerilogAFileResolver`, through
  brief 8's owning-folder rule), look for `<dir>/<rid>/<name>.osdi` **first**, then `<dir>/<name>.osdi`. The
  platform folder wins, because a plain `<name>.osdi` beside it is almost always one platform's build that was
  copied up, and loading the wrong platform's library fails with a loader message that never mentions the
  platform. The rule applies to any `.osdi` reference, not only inside a model folder, so a workspace can
  carry several platforms' builds too. When neither file exists, the refusal names the current `<rid>` and
  lists the `<rid>` folders that do exist.
- **`R-vl15-1b` `part.cpart` gains `"Builds": ["osx-arm64", "win-x64", …]`**, an optional key, empty or
  absent for a model with source. Under brief 6 D7 it is a describing fact only: the palette reads it for
  D3's greyed state without listing folders, and `check` proves it matches `models/<rid>/`. `FormatVersion`
  stays 1, because brief 6 keeps unknown keys and an older reader ignores this one.
- **`R-vl15-1c` A compiled-only model is recognised, not declared.** A model is compiled-only when its
  `.cnl` names a `.osdi` with no `.va`/`.vams` of the same module anywhere in the folder. Nothing writes a
  "protected" flag, because a flag could disagree with the files.
- **`R-vl15-1d` Export to Library (brief 10).** When the cell being exported places a `.osdi` with no source,
  Export copies the build it ran with into `models/<rid>/` and shows one more row, **Builds for other
  platforms…**, a list of `<rid>` → file that the maker fills from builds made elsewhere. The CLI spelling is
  `--build <rid>=<path>`, repeatable. Export's self-check (brief 10) checks only the current platform's
  build, and says so: *"Checked on osx-arm64. The other 3 builds were copied, not run."*
- **`R-vl15-1e` Edit Vendor Model / Copy to Workspace (brief 12) work unchanged.** The wrapper schematic is
  editable, and the compiled device is a file that is copied, never opened. Confirm this with one test, and
  change nothing unless the test fails.

## 4. `R-vl15-2` — a model that refuses to run

A maker who wants to restrict use builds that check into their own binary: node-locking, an expiry date, a
licence file, a licence server. circuitRF neither knows nor cares which. What it owes the maker is a
guarantee that **a model which refuses to start stops the run, and the user reads the model's own
sentence.**

Most of this already exists: `tools/osdi-worker` routes a model's `$fatal`/`$error` through `osdi_log` to
stderr, and a point that returns `EVAL_RET_FLAG_FATAL` is refused. This brief **proves it end to end and fixes
only what the proof finds.** The cases are:

- a model whose setup reports errors (`OsdiInitInfo.num_errors`), which today reports *"create: model setup
  reported errors"*: confirm the model's own logged sentence comes before it in what the user sees
- a model that raises `$fatal` on its first evaluation
- a model that exits its process during load or setup

Each must end as **one refusal carrying the model's text**, in the GUI's Messages panel and in the CLI's
stderr with exit 1. None may end as a run that continues on zeros, a Newton solve that retries thousands of
refused points, or a worker that hangs. Extend `tools/fake-osdi-model` with a refusing device, selected by a
model parameter rather than an environment variable so the test can place it, and keep its closed-form
comment style.

## 5. `R-vl15-3` — signed libraries

### 5a. The files

```
<library>/
  library.sums      text: one line per signed item, sorted by path, LF endings
  library.sig       JSON: Format, Publisher, PublicKey (SubjectPublicKeyInfo, base64), Signature (base64)
```

`library.sums` holds one line per model folder, `sha256:<hex>  <relative path>`, where the hash is that
folder's `PublishedHash` (brief 6 §3b, one function), plus one line for `library.clib`. It is plain text so a
person can read what was signed. `Signature` is over the exact bytes of `library.sums`. `Publisher` must
equal `library.clib`'s `Publisher`.

**Every file in a model folder is covered, including each `models/<rid>/*.osdi`**, because `PublishedHash`
already covers every file. That matters most for the `.osdi` files, since they are native code that runs on
the user's machine.

### 5b. `R-vl15-3a` — `circuitrf sign`

- `circuitrf sign <library> --key <private.pem>` recomputes every model folder's hash, writes
  `library.sums`, and signs it. It refuses when any model folder fails `check` at error severity: a signature
  over a broken library certifies the breakage.
- `circuitrf sign --new-key <private.pem>` writes a new P-256 key and prints its public fingerprint (SHA-256
  of the SubjectPublicKeyInfo, as colon-separated hex, first 16 bytes). It refuses to overwrite a file.
- **It refuses a `--key` or `--new-key` path inside the library folder.** A private key shipped in a library
  is the one mistake that cannot be undone after publication.
- The verb follows `docs/design/cli.md`'s anatomy and the authoring rule: the signing and verifying logic
  lives in `src/Design/VendorLibraries/ModelLibrary/LibrarySignature.cs`, and `src/Cli/Sign.cs` is argument
  parsing, refusals and reporting only. No GUI command signs; publishing is a maker's build step.

### 5c. `R-vl15-3b` — verifying, and what the user sees

`LibrarySignature.Verify(libraryDir, trustStore)` returns one of:

| State | Shown on the library's row (Add Vendor Library, Settings' library list) | `check` |
|---|---|---|
| Unsigned | nothing | nothing |
| Signed, trusted, intact | *Signed by <Publisher>* | info |
| Signed by a key not yet trusted | *Signed by <Publisher>, key <fingerprint>. Trust this publisher?* (Trust / Not now) | info |
| Signed, but parts changed since | *Signed by <Publisher>. N parts changed since signing: …* | warning, one per part |
| Signature does not verify | *The signature does not match this library's contents.* | error |
| Trusted publisher name, different key | *<Publisher> was trusted with a different key. This library may not come from them.* | error |

- Verification runs where brief 9 builds the index (Add, and workspace open), so it costs one hash pass that
  brief 9 already makes. Measure it on brief 9's synthetic library, record the figure in
  `src/Design/RESOLVED.md`, and do not add a cache (overview D5).
- **The trust store is per user**: `publisher name → fingerprint` in the per-user preferences, next to the
  vendor-library list (overview D3). It is never written into a workspace, because a workspace that told
  circuitRF which keys to trust would let anyone who sends a workspace vouch for themselves.
- **Brief 12's "edited since exported" stays as it is.** A user's own edit to a signed library shows as
  "changed since signing" on the library's row. That is accurate and is not an error.
- A headless run never prompts. An untrusted key is info and the run proceeds, per D4.

## 6. `R-vl15-4` — the pages

- **`R-vl15-4a`** `model-library-format.md` (brief 6) gains: compiled-only folders and `models/<rid>/`,
  `Builds`, `library.sums`/`library.sig` with an exact byte-level description (enough for another tool to
  verify a library with no circuitRF code), and how to run `circuitrf sign`.
- **`R-vl15-4b`** New `protected-models.md`, in `_nav.txt` beside the format page, written for a device maker
  deciding whether to publish here. It states, plainly and in this order:
  1. circuitRF does not encrypt models, and why (§0's argument, in two paragraphs).
  2. What to compile and what can stay outside (§1).
  3. **What a compiled model still reveals**, so nobody discovers it afterwards: terminal names, parameter
     names, defaults and descriptions, internal node names, the Jacobian's sparsity pattern (which shows
     which nodes couple), and the device's responses to any voltages someone applies. Suggest opaque
     internal node names, and say that the sparsity pattern cannot be hidden.
  4. How to restrict use from inside the model (§4's guarantee), with a short Verilog-A sketch that calls
     `$fatal` with a sentence of the maker's own. No licensing product is named or recommended.
  5. Signing (§5), and the platforms to build for (D2).

  Served as the `reference` topic `protected-models`, as brief 6 does for the format page.

## 7. Tests (minimal)

1. Resolution: with both `models/<rid>/X.osdi` and `models/X.osdi` present, the platform build is chosen;
   with neither, the refusal names the `<rid>` and the folders that exist.
2. A compiled-only model folder built from `fake_osdi.osdi` `check`s clean, runs DC through its `.cnl`, and an
   `SnP` beside it in the same `.cnl` is used. A `Builds` entry with no folder is an error.
3. D3: with the current `<rid>` removed from `Builds`, the palette entry is greyed with the sentence, and
   placing it is refused.
4. §4's three refusing cases, each ending as one refusal carrying the model's text, CLI exit 1, within a
   bounded time.
5. Sign, then verify: intact; one byte changed in an `.osdi` gives "changed since signing" naming that part;
   one byte changed in `library.sums` gives "does not verify"; a trusted name with a new key is an error.
6. `sign` refuses a key path inside the library, and refuses a library that fails `check`.
7. The format page's signature description is enough: the test verifies a signed fixture library using only
   `System.Security.Cryptography` and the page's own stated rules, with no `LibrarySignature` call.

Run only the classes you touch.

## 8. On completion

As brief 6 §9: findings in `src/Design/RESOLVED.md` (and `src/Ui/RESOLVED.md` for the palette and dialog
rows), never in any `CLAUDE.md`. Name the user-doc pages changed. Do not run DocGen. Each new UI row and the
greyed palette entry gets a `tools/UiSnapshot` render that has been looked at.
