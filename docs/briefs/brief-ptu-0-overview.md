# Brief PTU-0 — Touchstone from Image: the dialog fixed, nothing more (overview)

**Series:** PTU-1 … PTU-6 · **Tag:** `R-ptu<n>-<m>` · **Written:** 2026-10-11
**Follows:** `brief-img-14-sparameter-plots-to-touchstone.md` (the plot reader) and `brief-img-5-dialog.md` (the dialog)

---

## 1. Why this series exists, and why it is small

A field trial of Create Touchstone from Image produced no Touchstone from any of six pictures. Some of that is the
reader (a clean two-trace dB plot reads as "no plot was found"); most of what made the session unpleasant is the
**dialog**: overlapping controls, a Replace that adds, a canvas and a table that do not know about each other, an
overlay that hides the colour the user needs to see, a click on an unread label that does nothing, and 57 rows to
assign for a plot holding four traces.

**This series fixes the dialog and adds four very simple UI-level helpers (PTU-3 … PTU-6). It does not touch the recognizers.** Every
brief here is a few hours at most. Algorithm work is listed in §5 and is deliberately NOT briefed — it gets its own
series, written later, if and when the owner asks.

## 2. The briefs

| Brief | What | Area |
|---|---|---|
| PTU-1 | Layout bugs: the plot panel under the Advanced expander, scrolling over it; Replace… replaces; output text selectable | `ImageReadingPanel.axaml`, `ImageSourceViewModel.cs`, the `Recognition/*.axaml` panels |
| PTU-2 | Toolbar-style buttons with glyphs (all three modes: schematic, layout, plot) and a Help button | `ImageSourcePane.axaml`, the dialog window |
| PTU-3 | Canvas ↔ traces table linked both ways; the overlay stops hiding the trace colour | `ImageOverlayCanvas.cs`, `PlotTracesPanel.axaml`, `PlotSessionViewModel.cs` |
| PTU-4 | Click an axis label, type its value — the axis calibrates (replaces typing pixel coordinates) | `ImageOverlayCanvas.cs`, `PlotSessionViewModel.cs` |
| PTU-5 | One row per colour by default (Simple / Detailed); pieces assigned alike joined before assembly | `PlotSessionViewModel.cs`, `PlotTracesPanel.axaml`, new `src/Design/Imaging/Plots/TraceJoin.cs` |
| PTU-6 | Drag a box: read only this plot (crop), or ignore this legend (the reader's existing exclude hints) | the canvas, `ImageSourceViewModel.cs`, `PlotSessionViewModel.cs` |

Order: PTU-1 first (it fixes the column every other brief draws into). The rest are independent.

## 3. Rules for every brief in this series (owner, 2026-10-11)

- **No recognizer changes.** Nothing under `src/Design/Imaging/Plots/` changes, except PTU-5's one new pure file
  (`TraceJoin.cs`), which runs after the reader and touches none of its files. If a fix appears to need the reader to behave differently, **stop and report** — that belongs in
  the later series.
- **Small context budget.** Look at **at most two rendered pictures per brief** (a before and an after of the dialog,
  §4), downscaled. Do not page through the field pictures, do not score a corpus, do not compare overlays image by
  image. Verification is view-model tests plus that one render.
- **Tests: minimal and targeted.** One test per claim, in a new class per brief under `tests/Ui.Tests/PlotRecognition/`
  (or the existing `ImageSource*` test class if one fits). Run **only that class** with `--filter`. Never the full
  suite, never all of `Ui.Tests`.
- **Stop and report** when a brief runs past its scope rather than widening it.
- No DocGen. Edit `docs/user/src/reference/create-from-image.md` (§ `#plots`, `#dialog`) only where a brief changes
  what the user sees. Findings go in `src/Ui/RESOLVED.md`, never CLAUDE.md. No commit unless the owner asks.

## 4. Seeing the dialog without launching the app

The GUI cannot be launched from an agent shell. The dialog renders headlessly with a ~60-line scratch console — build
it in the session scratchpad (never in the repo):

```xml
<!-- dlg.csproj -->
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><LangVersion>14</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
  <ItemGroup><ProjectReference Include="<repo>/src/Ui/CircuitRF.Ui.csproj" /></ItemGroup>
  <ItemGroup>
    <PackageReference Include="Avalonia.Headless" Version="12.0.3" />
    <PackageReference Include="Avalonia.Skia" Version="12.0.3" />
    <PackageReference Include="SkiaSharp" Version="3.119.4" />
  </ItemGroup>
</Project>
```

```csharp
// Program.cs — args: picture.png out.png
using Avalonia; using Avalonia.Controls; using Avalonia.Headless; using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading; using CircuitRF.Design.Imaging; using CircuitRF.Ui.Recognition;

sealed class DlgApp : Application
{
    public override void Initialize()   // the same resources tools/DocGen/DocsApp.cs merges
    {
        var b = new Uri("avares://CircuitRF.Ui/");
        Resources.MergedDictionaries.Add(new ResourceInclude(b) { Source = new Uri("avares://CircuitRF.Ui/Styles/CircuitRfResources.axaml") });
        Styles.Add(new StyleInclude(b) { Source = new Uri("avares://CircuitRF.Ui/Styles/CircuitRfStyles.axaml") });
        var app = new CircuitRF.Ui.App(); app.Initialize();
        foreach (var t in app.DataTemplates) DataTemplates.Add(t);
    }
}
static class Program
{
    static int Main(string[] args)
    {
        AppBuilder.Configure<DlgApp>().UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })   // false is load-bearing
            .WithInterFont().SetupWithoutStarting();
        string ws = Path.Combine(Path.GetTempPath(), "dlg-ws"); Directory.CreateDirectory(ws);
        var vm = new ImageSourceViewModel(ws, [], 0, null, true, debounce: TimeSpan.Zero,
                                          post: a => Dispatcher.UIThread.Post(a), presetDirectory: Path.Combine(ws, "presets"));
        vm.ReadPlot(ImageSource.FromFile(args[0]).Source!);
        for (int i = 0; i < 1200 && !vm.Recognition.IsCompleted; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(50); }
        Dispatcher.UIThread.RunJobs();
        var win = new CreateSchematicFromArtworkDialog(vm) { Width = 1500, Height = 950 };
        win.Show();
        for (int i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(1); }
        win.CaptureRenderedFrame()!.Save(args[1]);
        return 0;
    }
}
```

Build once, run against a picture, downscale with `sips -Z 1000` before reading the PNG. Set a view-model property
(`vm.Plots.…`, `vm.SelectedItem`) before `Show()` to photograph a state. A good picture for this series is any
rectangular dB plot with several coloured traces and a legend; draw one with `circuitrf plot … -o x.png` if none is
to hand (the owner's field pictures are local and are not committed).

## 5. Seen in the field trial, deliberately NOT briefed (a later series, if the owner asks)

Recorded so it is not lost, and so no brief above drifts into it:

- **"No plot was found" on clean pictures** — a two-trace dB plot with a full grid and a rotated coloured axis title,
  an S11/S21 dB plot with a legend in the middle, a Smith chart with harmonic annotations. Frame detection, not the
  dialog. (The CLI's `recognize` misreads the same picture, so it is the reader.)
- **Fragmentation at the source** — one drawn curve split into many tracks where it crosses grid lines, markers or
  another curve (57 tracks for a 4-trace plot). PTU-5 hides it in the table; the reader still produces it.
- **Axis label OCR** — `-70` and `2.25` read as one word `-72 25` at the corner; a y axis whose labels are boxed but
  left uncalibrated. PTU-4 lets the user answer it; the reader still misses it.
- **Legends** — reading a legend's colour → name (`dB(S11)`, `S21 - Simulated`) to pre-assign entries. The schematic
  series' word and region recognizers (IMF-4, IMF-7) are the likely starting point.
- **Picking a plot automatically** when a picture holds several (PTU-5's box is the manual answer).
- **Speed** — one 1816 × 1348 screenshot was still reading after 60 s in the headless harness. Measure before
  guessing.
- **Machine learning** — the owner expects the recognizer may eventually need a learned model rather than more
  hand-written image heuristics. Not now; it is the question to settle first when the later series is written.
