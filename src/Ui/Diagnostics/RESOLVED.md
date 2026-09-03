# Diagnostics — resolved findings (detail, off the CLAUDE.md growth path)

Same pattern as the other `RESOLVED.md` files in this repo: a completed investigation's detail lands
here, and `CLAUDE.md` stays for durable, still-true conventions only.

## The crash report says how the process EXECUTES code, not only what it runs on (2026-09-03)

Four header lines were added for one specific dead end. A field report that has survived six rounds
(`src/RfCore/RESOLVED.md`) is a managed exception whose own arithmetic says it was unreachable: an
in-range array index that faulted anyway, on an immutable object, with the read's own replay
succeeding. Once the application's state is exhausted as an explanation — and it now is, positively
rather than by failure to reproduce — what remains is the layer underneath. None of it appeared in any
report:

```
debugger    : no
gc          : workstation, Interactive
profiler    : none
codegen env : all default
modules     : 214 loaded, from elsewhere: SomeEndpointHook.dll
```

- **`profiler`** covers `CORECLR_ENABLE_PROFILING`/`CORECLR_PROFILER` and the legacy `COR_*` spelling,
  because injectors set either. A CLR profiler can rewrite IL and force rejits, so its presence
  changes what "the same code" means — and endpoint-protection and APM agents inject them routinely
  on managed corporate desktops, which is exactly the environment this report comes from.
- **`codegen env`** lists only knobs that change codegen (`DOTNET_TieredCompilation`,
  `DOTNET_TieredPGO`, `DOTNET_TC_QuickJitForLoops`, `DOTNET_ReadyToRun`, `DOTNET_JitMinOpts`,
  `DOTNET_ZapDisable`). Unset is the shipping configuration; saying "all default" explicitly is what
  makes a set one stand out.
- **`modules`** lists native modules loaded from neither the OS nor the application directory — i.e.
  injected. Classified by path, reported by **name only**: which DLLs are in the process is
  diagnostic, where they live on the reporter's disk is not ours to collect.

**The negative answer is the point.** "no / none / all default / nothing injected" narrows the field
as usefully as a named module does, which is why all five lines are written unconditionally rather
than only when something is set.

**Platform caveat, not a bug:** `Process.Modules` enumerates the full loaded-module list on Windows
and reports only the main module on macOS. The line therefore reads `1 loaded` on a Mac. Windows is
where the report comes from and where the enumeration works, so this was not worth a second
mechanism — but do not read a Mac report's `modules` line as evidence of anything.

### `Dispatcher.UIThread.CheckAccess()` is not a free read

Every trail note now carries its thread (`[07:34:56.246 t1]`, and `t7!ui` off the UI thread), because
a burst of identical notes is a different event depending on whether one thread produced it or
several did.

The obvious implementation is wrong. **Reading `Dispatcher.UIThread` has a side effect: the first
access CREATES the dispatcher, bound to whichever thread asked.** A diagnostic that consulted it could
bind the UI thread to a worker merely by noting something early in startup — a diagnostic that changes
the thing it observes, in the one direction that would be hardest to notice.

`CrashReporter.MarkUiThread()` captures the managed thread id once from
`App.OnFrameworkInitializationCompleted`, which runs on the UI thread by definition, and `Note`
compares integers. Before it has run there is no UI thread to be off, so an early note is unannotated
rather than wrongly flagged. It also keeps `CrashReporter` free of any Avalonia reference. Held by
`CrashReporterTests.EveryNote_CarriesItsThread_AndMarksTheOnesThatAreNotTheUiThread` and
`TheHeader_RecordsHowTheProcessIsExecutingCode_NotOnlyWhatItRunsOn`.
