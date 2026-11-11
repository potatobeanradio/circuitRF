# Line Section — a C# plugin with its own window

A circuitRF `window` plugin written in C#, published with NativeAOT to one native program per platform. It sizes a
microstrip line of a chosen impedance and electrical length with circuitRF's own line model (the `lineModel` host
call) and places a cell holding the line's layout (a port label at each end), its circuit and an EM setup naming those
ports — so the placed cell opens ready for an EM run. It also has a **headless form**, so `circuitRF plugin run` and MCP
can run it with no window.

It is the template for an author who already has a .NET desktop tool and wants it to place cells in circuitRF instead
of exporting interchange files. The contract it implements is the user guide's
[Writing a Plugin](../../../docs/user/src/reference/writing-plugins.md); this folder references nothing else in the
circuitRF repository, and copied anywhere it builds the same.

| File | What it holds |
|---|---|
| `Frame.cs` | The frame codec, BCL only. Copy it into your program unchanged |
| `Protocol.cs` | The messages (source-generated JSON), `hello`, the reader thread, and host calls awaited by id |
| `LineSection.cs` | The computation: parameters and circuitRF's line model in, three documents out. Window and headless both call it |
| `MainWindow.cs` | The Avalonia window, built in code (no XAML). The UI thread never waits on the pipe |
| `Program.cs` | `Main`: stdout kept for frames and `Console.Out` pointed at stderr first; the headless form; `--write-golden-frames` |
| `plugin.json` | `kind: window`, the headless form's parameters and readouts, a `run` entry per platform |
| `publish.sh`, `publish.ps1` | NativeAOT publish into `bin/<rid>/` |
| `golden/` | This program's encoding of three fixed messages; circuitRF's tests read them back (see below) |

## Build

You need the .NET 10 SDK and the platform's native toolchain: Xcode's command-line tools on macOS
(`xcode-select --install`), clang and the zlib development headers on Linux, Visual Studio's "Desktop development with
C++" workload on Windows.

```sh
./publish.sh               # macOS or Linux: every RID of this operating system
./publish.sh osx-arm64     # one RID
.\publish.ps1              # Windows: win-x64 and win-arm64
```

**NativeAOT cannot cross operating systems**: each OS's programs are published on that OS. The scripts refuse a RID of
another OS rather than skip it, because a `run` entry with nothing behind it fails for whoever installs the plugin.
Each `bin/<rid>/` holds the program and the native libraries the UI toolkit loads; ship the whole folder.

## Link and run

1. In circuitRF, **Settings ▸ Plugins ▸ Link Folder…** and choose this folder. Allow it.
2. **Tools ▸ Plugins ▸ Examples ▸ Line Section** opens the window. Choose a conductor, type a frequency, impedance and
   electrical length; **Place in workspace** places a cell called `line`; **Open in circuitRF** opens its layout.
3. Headless, with the same parameters:

```sh
circuitRF plugin run example.line-section --set f0="2.4 GHz" --set z0="50 Ohm" --set degrees=90 --workspace <ws> -o out
circuitRF plugin run example.line-section --set f0="2.4 GHz" --place <ws>
```

Every number can be checked by hand: the length is (degrees / 360) · c / (f · √εeff) with circuitRF's εeff at the
drawn width — 18.44 mm for a 90° line at 2.4 GHz on 30 mil RO4350B.

## Making it yours

1. Rename the project file, `AssemblyName`, the manifest's `id`, `name` and every `run` path.
2. Replace `LineSection.cs` with your tool's computation. Keep it a function of its inputs: the window and the headless
   form must stage the same bytes for the same parameters.
3. Replace `MainWindow.cs`'s controls with your own window. Await every message to circuitRF; never block the UI thread
   on the pipe.
4. Keep `Frame.cs` and the message classes. For each new message you send or read, add a class and a
   `[JsonSerializable]` line on `PluginJson` — reflection-based JSON is trimmed away by NativeAOT and fails only at run
   time.
5. Declare in `plugin.json`'s `params` what the headless form needs, or drop the headless form (and `params`) if your
   tool cannot work without its window — `plugin run` then refuses it, saying so.

## The golden frames

`golden/*.bin` are this program's own encoding of three messages — one with no payload, one whose payload includes both
int64 extremes, and one whose JSON is non-ASCII — and `golden/expected.json` says what each holds. circuitRF's routine
tests decode each with its own frame readers and with the Python helper's, and check circuitRF writes the same bytes, so
a change that would break a compiled plugin fails there without publishing one. After changing `Frame.cs`:

```sh
bin/<rid>/line-section --write-golden-frames golden
```
