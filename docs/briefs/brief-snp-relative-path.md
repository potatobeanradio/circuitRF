# Sonnet Brief — relative SnP/Touchstone file paths resolved against the workspace root

Feature: an SnP component's `File` parameter may be a **relative** path; it resolves against the
**workspace root** (the directory that holds the `.cws`, i.e. `Path.GetDirectoryName(CurrentWorkspacePath)`
— the same `baseDir` the run already uses for `results/`). Example: a file
`potentially_unstable_amp.s2p` placed in the workspace root, referenced as `File="potentially_unstable_amp.s2p"`,
loads. Absolute paths are used unchanged. Cross-platform (Windows/Linux/macOS).

Root cause / today: `Elaborator.ResolveSnpParameters` stores the `File` string raw; `CreateSnpModel`
passes it straight to `SnpModel` as `absoluteFilePath`; `SnpModel.LoadSnp` does
`File.Exists(_filePath)` / `TouchstoneIO.ReadFile(_filePath)` — so a relative path resolves against the
process CWD (unpredictable), not the workspace.

Approach: resolve relative→absolute at the natural point — `Elaborator.ResolveSnpParameters` — so the
factory still receives an absolute path (contract unchanged). The Elaborator learns the workspace root
via a new `init` property (no constructor-signature break). Thread the root from the UI run entry
through `SchematicRunService.RunNetlist` and `ParametricSweepEngine.Run` (it re-elaborates per sweep
point, so it must pass the root too). When no root is supplied (CLI / no workspace), behavior is
unchanged (legacy CWD resolution).

Scope: `src/Core/Elaboration/Elaborator.cs`, `src/Engine/ParametricSweepEngine.cs`,
`src/Ui/Schematic/SchematicRunService.cs`, `src/Ui/ViewModels/WorkspaceViewModel.cs` (one call site) +
tests + a design note. Architectural firewall stays clean: only a plain path **string** crosses into
Core — no UI dependency. Build 0W/0E (`TreatWarningsAsErrors=true`); tests green.

Read first: `Elaborator.ResolveSnpParameters` + `_snpStringParams`; `ParametricSweepEngine.Run`
(the `new Elaborator(lib).Elaborate(tb)` loop); `SchematicRunService.RunNetlist` +
`RunTypedAnalysis` (the `ParametricSweepAnalysis` case); and in `WorkspaceViewModel`, the run method
that calls `SchematicRunService.RunNetlist` and `RunResultsWriter.WriteRun(baseDir, …)`.

## 1. `Elaborator.cs` — base directory + relative-path resolution

Add an `init` property (place near the `_libraries` field / constructor):
```csharp
    /// <summary>
    /// Workspace root for resolving relative file-path parameters (e.g. SnP File).
    /// Null → relative paths are left as-authored (legacy CWD resolution) for CLI / no-workspace runs.
    /// Only a path string crosses into Core here — no UI dependency.
    /// </summary>
    public string? BaseDirectory { get; init; }
```

In `ResolveSnpParameters`, resolve the `File` param. Replace the string-param store block:
```csharp
            if (_snpStringParams.Contains(ov.Name))
            {
                // CNL string params are stored with surrounding quotes (e.g. File="path").
                var raw = ov.Expression;
                if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
                    raw = raw[1..^1];

                // File: resolve a relative path against the workspace root (cross-platform).
                if (ov.Name.Equals("File", StringComparison.OrdinalIgnoreCase))
                    raw = ResolveSnpFilePath(raw);

                result[ov.Name] = new Value(raw);
            }
```
Add the helper (instance method — it reads `BaseDirectory`):
```csharp
    // Resolves a relative SnP File path against BaseDirectory (the workspace root); absolute paths and
    // the no-root case pass through unchanged. Cross-platform: Path.* honor the host separator rules,
    // and we tolerate a Windows-authored '\' in a relative path so a netlist ports across OSes.
    private string ResolveSnpFilePath(string file)
    {
        if (string.IsNullOrWhiteSpace(file))       return file;
        if (Path.IsPathRooted(file))               return file;   // absolute on this OS → unchanged
        if (string.IsNullOrEmpty(BaseDirectory))   return file;   // no workspace root → legacy behavior
        var rel = file.Replace('\\', '/');                        // tolerate Windows-authored separators
        return Path.GetFullPath(Path.Combine(BaseDirectory, rel));
    }
```
`System.IO` is implicitly used elsewhere via `Path` — confirm `Path` resolves (add `using System.IO;` only if the build complains; ImplicitUsings is on). The factory and `SnpModel` are untouched — they still receive an absolute path.

## 2. `ParametricSweepEngine.cs` — thread the root into per-point elaboration

`Run` re-elaborates each sweep point, so it must carry the root. Add an optional trailing param and
apply it to the internal Elaborator:
```csharp
    public static DataSet Run(
        ParametricSweepAnalysis sweep,
        Library lib,
        TestBench tb,
        AnalysisSettings? settings = null,
        string? baseDirectory = null)
```
In the loop, change:
```csharp
                var netlist = new Elaborator(lib).Elaborate(tb);
```
to:
```csharp
                var netlist = new Elaborator(lib) { BaseDirectory = baseDirectory }.Elaborate(tb);
```
`RunInner` recurses into `Run` for nested sweeps — thread it through. Change its signature:
```csharp
    private static DataSet RunInner(
        Analysis inner,
        Library lib,
        TestBench tb,
        ElaboratedNetlist netlist,
        AnalysisSettings? settings,
        string? baseDirectory)
```
the recursive case:
```csharp
            case ParametricSweepAnalysis psa:
                return Run(psa, lib, tb, settings, baseDirectory);
```
and the single call site inside `Run`:
```csharp
                datasets.Add(RunInner(inner, lib, tb, netlist, settings, baseDirectory));
```

## 3. `SchematicRunService.cs` — accept + forward the root

`RunNetlist` gains an optional root; the top elaboration and the sweep dispatch use it.
```csharp
    public static RunResult RunNetlist(string netlistPath, string? baseDirectory = null)
```
Top elaboration:
```csharp
            nl = new Elaborator(lib) { BaseDirectory = baseDirectory }.Elaborate(tb);
```
`RunTypedAnalysis` is the only dispatcher that constructs a sweep — give it the root and forward:
```csharp
    private static DataSet? RunTypedAnalysis(
        Analysis          analysis,
        ElaboratedNetlist nl,
        TestBench         tb,
        Library           lib,
        List<string>      notes,
        string?           baseDirectory)
```
the sweep case:
```csharp
            case ParametricSweepAnalysis psa:
            {
                notes.Add($"Parametric sweep '{psa.Name}': {psa.SweepValues.Length} pt(s) over {psa.SweepVarName}");
                return ParametricSweepEngine.Run(psa, lib, tb, baseDirectory: baseDirectory);
            }
```
and the one call site in `RunNetlist` (inside the roots loop):
```csharp
                var ds = RunTypedAnalysis(top, nl, tb, lib, notes, baseDirectory);
```
(Raw S-param directives don't load SnP files, so they need nothing.)

## 4. `WorkspaceViewModel.cs` — pass the workspace root (one call site)

Find the run method (the one that calls `SchematicRunService.RunNetlist(...)` and, after, builds the
grouped DataSet for `RunResultsWriter.WriteRun(baseDir, …)`). That `baseDir` **is** the workspace root.
Pass the same value into `RunNetlist`:
```csharp
// workspaceRoot is the same value already used as WriteRun's baseDir:
//   CurrentWorkspacePath is not null ? Path.GetDirectoryName(CurrentWorkspacePath) : null
var runResult = SchematicRunService.RunNetlist(netlistPath, baseDirectory: workspaceRoot);
```
If the run currently computes that base inline for `WriteRun`, hoist it into a local
`string? workspaceRoot` and use it in both places. When no workspace is open
(`CurrentWorkspacePath is null`) pass `null` → SnP relative paths fall back to legacy behavior (there is
no workspace to be relative to).

## 5. CLI (optional, parity — do only if trivial)

If the CLI constructs an Elaborator from a `.cnl` path, it may pass
`new Elaborator(lib) { BaseDirectory = Path.GetDirectoryName(Path.GetFullPath(cnlPath)) }` so relative
SnP paths resolve against the `.cnl`'s directory. Not required by this feature; leave `null` if it adds
friction. Note it in the design doc as a follow-up either way.

## Tests (`tests/Core.Tests` for resolution; an engine test for the sweep path)
Use a temp dir as the workspace root with a real `.s2p` placed in it.
1. **Relative_ResolvesAgainstRoot:** `Elaborator` with `BaseDirectory = root`, SnP `File="amp.s2p"`
   → the elaborated SnP loads (no `FileNotFoundException`); the resolved param is the absolute
   `root/amp.s2p`.
2. **Absolute_Unchanged:** `File="<abs path>"` resolves/loads regardless of `BaseDirectory`.
3. **NoBaseDirectory_Legacy:** `BaseDirectory = null`, relative `File` is left as-authored (resolves
   against CWD — assert the stored param equals the input, no rooting).
4. **Subdir_Relative:** `File="touchstone/amp.s2p"` (forward slash) resolves under the root; a
   Windows-authored `File="touchstone\\amp.s2p"` resolves to the same place (separator tolerance).
5. **MissingFile_ClearError:** relative `File` that doesn't exist → `FileNotFoundException` whose
   message contains the **resolved absolute** path (so the user sees where it looked).
6. **SweptSnP_Resolves:** SnP inside a parametric sweep (`ParametricSweepEngine.Run(..., baseDirectory:
   root)`) loads at every sweep point — confirms the root threads through re-elaboration.

## Gate (manual)
Place `potentially_unstable_amp.s2p` in the workspace root; an SnP with `File="potentially_unstable_amp.s2p"`
runs an S-param (and a parametric sweep over it) with no path error. Move the file out → the error names
the resolved absolute path under the workspace root.

## On completion
Add a short note to the SnP/Touchstone reference (and `docs/design/` where file-path params are
described): SnP `File` may be relative; it resolves against the **workspace root**
(`Path.GetDirectoryName(CurrentWorkspacePath)`), absolute paths are used as-is, resolution is
cross-platform (`Path.IsPathRooted`/`Combine`/`GetFullPath`, with `\`→`/` tolerance), and CLI/no-workspace
runs fall back to legacy CWD behavior. If the CLI base-dir parity (step 5) landed, note it.
```
