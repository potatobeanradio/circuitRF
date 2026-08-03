using System.Runtime.InteropServices;
using System.Text.Json;

namespace CircuitRF.Core.Devices.External;

// ─────────────────────────────────────────────────────────────────────────────
//  How circuitRF learns to start a worker without being told.
//
//  The goal is that a user imports a kit, places a part, and presses Run. No
//  provider to configure, no path to paste. That needs one fact circuitRF cannot
//  derive — which program to run for this kit — and a manifest is where that fact
//  lives: DATA sitting beside the kit, not knowledge compiled into circuitRF.
//
//  Nothing here names a supplier, a library or a part. It reads a file that does.
// ─────────────────────────────────────────────────────────────────────────────

/// <summary>
/// One way of starting a worker, for one kind of machine.
/// </summary>
/// <param name="Platform">
/// Which machines this entry is for: a runtime identifier (<c>linux-x64</c>), an operating system
/// alone (<c>linux</c>), or empty/<c>any</c> for a last resort. The most specific match wins, so a
/// manifest can give a general entry and override it for one platform.
/// </param>
/// <param name="Command">
/// Program to run. Resolved relative to the manifest's own folder when it names a file there;
/// otherwise left as written, so a worker on the system path works with no path at all.
/// </param>
/// <param name="Arguments">Arguments, typically which model library the worker should load.</param>
public sealed record DeviceWorkerLaunch(
    string                Platform,
    string                Command,
    IReadOnlyList<string> Arguments);

/// <summary>
/// A parameter whose value picks WHICH model a kit part is built from, rather than a value fed to
/// one — the shape a kit uses when it ships several formulations of the same part.
///
/// <para><b>Why this is declared rather than inferred.</b> circuitRF can see that a kit's netlist
/// holds several near-identical subcircuits, but not which of them the part's own parameter selects,
/// what that parameter is called, which choice should be the default, or which choices this kit's
/// worker can actually evaluate. All four are the kit's knowledge, so all four are stated here as
/// data. Nothing about them is compiled into circuitRF.</para>
///
/// <para><b><see cref="Default"/> is what makes the first run produce results.</b> A part placed
/// from the palette arrives already set to it, so a user who imports a kit and presses Run gets an
/// answer rather than an explanation.</para>
/// </summary>
/// <param name="Parameter">The part parameter this governs — the kit's own name for it.</param>
/// <param name="Choices">Every value the part offers, in the order the kit listed them.</param>
/// <param name="Default">The value a newly placed part starts at. Must be one of <paramref name="Choices"/>.</param>
/// <param name="Unsupported">
/// Choices this kit declares but circuitRF cannot evaluate. Offered anyway — a user asking for one
/// deserves to be told it is not implemented, not to find it silently missing from the list.
/// </param>
/// <param name="Parts">
/// Which parts this applies to, or empty for all of them. A kit's parts are not alike — the same
/// folder holds real components and the helper cells they are assembled from — so a formulation
/// choice that belongs to one part must not appear on the others. Empty means the kit is saying it
/// genuinely applies throughout.
/// </param>
public sealed record DeviceWorkerVariant(
    string                Parameter,
    IReadOnlyList<string> Choices,
    string                Default,
    IReadOnlyList<string> Unsupported,
    IReadOnlyList<string> Parts,
    string                Description = "")
{
    /// <summary>Whether this variant belongs to the named part.</summary>
    public bool AppliesTo(string partId)
        => Parts.Count == 0 || Parts.Contains(partId, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// A part whose behaviour is a CIRCUIT rather than a device — a package, a matching network, an
/// assembly — pointed at the netlist that defines it.
///
/// <para><b>Why a part needs this at all.</b> A worker evaluates one device. A packaged part is
/// several of them plus the passives that connect them, which is a subcircuit, not a device model —
/// so it is emitted as an ordinary cell instance and the definition is read from a netlist the kit
/// supplies. Everything downstream (elaboration, nets, sweeps) then treats it like any other cell.</para>
/// </summary>
/// <param name="Id">The part this applies to, as the importer named it.</param>
/// <param name="NetlistFile">
/// The <c>.cnl</c> holding the definition. Resolved against the kit, so the file stays with the kit
/// it came from.
/// </param>
/// <param name="CellName">
/// Which subcircuit in that file defines the part. May name a parameter in braces —
/// <c>Part_{ModelAs}</c> — and the instance's own value is substituted, which is how one part can
/// resolve to one of several formulations.
/// </param>
public sealed record DeviceWorkerPart(string Id, string NetlistFile, string CellName);

/// <summary>
/// A kit's answer to "how do I evaluate these devices?", read from a
/// <see cref="FileName"/> file beside the kit.
///
/// <para><b>Why a file rather than a setting.</b> A setting is a click, and the click would have to
/// be repeated per kit and per machine. A manifest travels with the kit that needs it, so importing
/// the kit is the only action the user takes.</para>
/// </summary>
public sealed class DeviceWorkerManifest
{
    /// <summary>The file this is read from. Sits in a kit's own folder.</summary>
    public const string FileName = "device-provider.json";

    private DeviceWorkerManifest(
        string providerName, IReadOnlyList<DeviceWorkerLaunch> launches, string directory, string baseDirectory,
        IReadOnlyList<DeviceWorkerVariant> variants, IReadOnlyList<DeviceWorkerPart> parts,
        IReadOnlyList<string> fileParameters)
    {
        ProviderName  = providerName;
        Launches      = launches;
        Directory     = directory;
        BaseDirectory = baseDirectory;
        Variants      = variants;
        Parts         = parts;
        FileParameters = fileParameters;
    }

    /// <summary>Name a netlist refers to this provider by. Matches the kit it was imported from.</summary>
    public string ProviderName { get; }

    /// <summary>Every declared way of starting a worker, in the order the file listed them.</summary>
    public IReadOnlyList<DeviceWorkerLaunch> Launches { get; }

    /// <summary>Folder the manifest was read from. Relative paths resolve against it first.</summary>
    public string Directory { get; }

    /// <summary>
    /// A second folder to resolve relative paths against, or empty. Declared as
    /// <c>baseDirectory</c>.
    ///
    /// <para><b>Why a manifest needs two folders.</b> Importing a kit copies its manifest into the
    /// workspace, while the worker and the model files stay where the kit is installed — so a path
    /// written relative to the kit no longer resolves relative to the copy. Recording where the kit
    /// was keeps the copy working, and checking the manifest's own folder first means dropping the
    /// real files beside it still takes precedence.</para>
    /// </summary>
    public string BaseDirectory { get; }

    /// <summary>
    /// Model-selection parameters this kit's parts offer, declared as <c>variants</c>. Empty for the
    /// ordinary kit that ships one formulation per part.
    /// </summary>
    public IReadOnlyList<DeviceWorkerVariant> Variants { get; }

    /// <summary>
    /// Parts whose definition is a subcircuit in a netlist the kit ships, declared as <c>parts</c>.
    /// Empty for the ordinary kit whose parts are all single devices.
    /// </summary>
    public IReadOnlyList<DeviceWorkerPart> Parts { get; }

    /// <summary>
    /// Parameters that name a FILE, declared as <c>fileParameters</c>. The editor offers a picker for
    /// these and lists them first — which file a part is modelled from is settled before anything
    /// else about it. Empty for a kit whose parameters are all plain values.
    /// </summary>
    public IReadOnlyList<string> FileParameters { get; }

    // ── reading ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads a manifest, or returns null with a reason. Never throws: a malformed manifest beside a
    /// kit must not stop a workspace from opening — it must stop only the devices that need it, and
    /// say why when they are run.
    /// </summary>
    /// <summary>
    /// True when a workerless manifest still says something worth reading — parts, variants, file
    /// parameters, or the kit it adds to. An EMPTY manifest is still an error: a file naming
    /// neither a worker nor anything else is a mistake, and reporting it is more useful than
    /// silently importing a kit that declares nothing.
    /// </summary>
    private static bool HasNonWorkerContent(JsonElement root)
    {
        foreach (var name in new[] { "parts", "variants", "fileParameters", "baseDirectory" })
            if (root.TryGetProperty(name, out var v) &&
                v.ValueKind is JsonValueKind.Array or JsonValueKind.String &&
                (v.ValueKind != JsonValueKind.Array || v.GetArrayLength() > 0) &&
                (v.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(v.GetString())))
                return true;
        return false;
    }

    public static DeviceWorkerManifest? TryRead(string path, out string? problem)
    {
        problem = null;

        string text;
        try
        {
            if (!File.Exists(path)) { problem = $"'{path}' does not exist."; return null; }
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problem = $"'{path}' could not be read: {ex.Message}";
            return null;
        }

        return TryParse(text, Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".", path, out problem);
    }

    /// <summary>
    /// Reads a manifest from text rather than from a file, for a workspace that records its kits'
    /// settled decisions itself instead of writing a file beside each kit (see
    /// <c>docs/design/pdk-import.md</c>). Same parser, same rules — a second one would drift.
    /// </summary>
    /// <param name="json">The manifest object.</param>
    /// <param name="directory">
    /// What relative paths inside it resolve against — the kit's own folder. There is no file to
    /// take this from, so the caller supplies it.
    /// </param>
    /// <param name="origin">Named in any problem reported, so a message points somewhere real.</param>
    public static DeviceWorkerManifest? TryParse(
        string json, string directory, string origin, out string? problem)
    {
        problem = null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }); }
        catch (JsonException ex) { problem = $"'{origin}' is not valid JSON: {ex.Message}"; return null; }

        string path = origin;

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                problem = $"'{path}' should contain a JSON object.";
                return null;
            }


            string provider = root.TryGetProperty("provider", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() ?? ""
                : "";

            // A manifest that does not name itself takes the name of the folder it sits in, which is
            // the kit's own name — the same name the parts were installed under.
            if (string.IsNullOrWhiteSpace(provider))
                provider = new DirectoryInfo(directory).Name;

            var launches = new List<DeviceWorkerLaunch>();

            if (root.TryGetProperty("workers", out var workers) && workers.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in workers.EnumerateArray())
                    if (ReadLaunch(w) is { } launch) launches.Add(launch);
            }
            else if (ReadLaunch(root) is { } single)
            {
                launches.Add(single);   // a manifest with one worker need not wrap it in an array
            }

            // A manifest with NO worker is valid, and this is not a relaxation — it is the
            // difference between the two kinds of part circuitRF already distinguishes:
            //
            //   netlist-backed   a circuit of ordinary primitives (ExternalNetlistPath/Cell).
            //                    Nothing runs out of process, so there is no worker to name.
            //   provider-backed  one ExtDevice evaluated by a worker (ExternalProvider/Type).
            //
            // **Whether a kit needs a worker is therefore a property of its PARTS, not a thing the
            // manifest has to declare** — a kit whose parts are all circuits needs none, and
            // demanding one made such a kit unimportable. Rejecting here also reported the wrong
            // thing: the manifest was refused whole, `baseDirectory` was never read, and the kit
            // surfaced as "no placeable parts" with the cause several steps upstream.
            //
            // The requirement has not gone away, it has moved to where it can be stated precisely:
            // a provider-backed part that finds no worker is refused BY NAME at the point it needs
            // one (ExternalDeviceRegistry / the provider resolver), which names the part rather
            // than condemning the kit.
            if (launches.Count == 0 && !HasNonWorkerContent(root))
            {
                problem = $"'{path}' names no worker to run, and declares nothing else either.";
                return null;
            }

            string baseDirectory = root.TryGetProperty("baseDirectory", out var b) && b.ValueKind == JsonValueKind.String
                ? b.GetString() ?? ""
                : "";

            if (baseDirectory.Length > 0 && !Path.IsPathRooted(baseDirectory))
            {
                try { baseDirectory = Path.GetFullPath(Path.Combine(directory, baseDirectory)); }
                catch (ArgumentException) { baseDirectory = ""; }
            }

            var variants = new List<DeviceWorkerVariant>();
            if (root.TryGetProperty("variants", out var vs) && vs.ValueKind == JsonValueKind.Array)
            {
                foreach (var v in vs.EnumerateArray())
                    if (ReadVariant(v) is { } variant) variants.Add(variant);
            }

            var parts = new List<DeviceWorkerPart>();
            if (root.TryGetProperty("parts", out var ps) && ps.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in ps.EnumerateArray())
                    if (ReadPart(e) is { } part) parts.Add(part);
            }

            return new DeviceWorkerManifest(provider, launches, directory, baseDirectory, variants, parts,
                                            ReadStringArray(root, "fileParameters"));
        }
    }

    /// <summary>
    /// Reads one variant declaration, or null when it is not usable. A variant with no parameter
    /// name or fewer than two choices describes no choice at all, and one whose stated default is
    /// not among its own choices contradicts itself — both are dropped rather than half-applied,
    /// because a part offering a broken picker is worse than a part offering none.
    /// </summary>
    private static DeviceWorkerVariant? ReadVariant(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        string parameter = element.TryGetProperty("parameter", out var p) && p.ValueKind == JsonValueKind.String
            ? (p.GetString() ?? "").Trim()
            : "";
        if (parameter.Length == 0) return null;

        var choices = ReadStringArray(element, "choices");
        if (choices.Count < 2) return null;

        string dflt = element.TryGetProperty("default", out var d) && d.ValueKind == JsonValueKind.String
            ? (d.GetString() ?? "").Trim()
            : "";
        if (!choices.Contains(dflt, StringComparer.Ordinal)) return null;

        var unsupported = ReadStringArray(element, "unsupported")
                          .Where(u => choices.Contains(u, StringComparer.Ordinal))
                          .ToList();

        return new DeviceWorkerVariant(parameter, choices, dflt, unsupported,
                                       ReadStringArray(element, "parts"));
    }

    /// <summary>
    /// Reads one netlist-backed part declaration, or null when it names no part, no file or no
    /// subcircuit — all three are needed to find a definition, so a partial one is dropped rather
    /// than left to fail later with a worse message.
    /// </summary>
    private static DeviceWorkerPart? ReadPart(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        string Str(string name) =>
            element.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
                ? (v.GetString() ?? "").Trim()
                : "";

        string id = Str("id"), file = Str("netlist"), cell = Str("cell");
        return id.Length == 0 || file.Length == 0 || cell.Length == 0
            ? null
            : new DeviceWorkerPart(id, file, cell);
    }

    private static List<string> ReadStringArray(JsonElement element, string name)
    {
        var result = new List<string>();
        if (!element.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array) return result;

        foreach (var item in a.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s && s.Trim().Length > 0)
                result.Add(s.Trim());

        return result;
    }

    private static DeviceWorkerLaunch? ReadLaunch(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;

        string command = element.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
            ? c.GetString() ?? ""
            : "";
        if (string.IsNullOrWhiteSpace(command)) return null;

        string platform = element.TryGetProperty("platform", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? ""
            : "";

        var args = new List<string>();
        if (element.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in a.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s) args.Add(s);
        }

        return new DeviceWorkerLaunch(platform.Trim(), command.Trim(), args);
    }

    // ── choosing a worker for this machine ────────────────────────────────────

    /// <summary>
    /// The best launch for the machine circuitRF is running on, or null if the manifest describes
    /// none. A manifest listing only other platforms is a normal, explainable situation — a kit
    /// built for one operating system, opened on another — so it is reported, not thrown.
    /// </summary>
    public DeviceWorkerLaunch? LaunchForThisMachine()
    {
        DeviceWorkerLaunch? best = null;
        int bestScore = 0;

        foreach (var launch in Launches)
        {
            int score = MatchScore(launch.Platform);
            if (score > bestScore) { bestScore = score; best = launch; }
        }

        return best;
    }

    private static int MatchScore(string platform) => MatchScore(platform, CurrentRuntimeIdentifier(), CurrentOs());

    /// <summary>
    /// How well a platform string fits a machine: an exact runtime identifier beats an operating
    /// system alone, which beats a catch-all. Zero means it does not apply there at all.
    ///
    /// <para>This ordering is the manifest's own semantics, which is why it is stated as a function
    /// rather than buried in a comparison — a kit can give one general entry and override it for a
    /// single platform, and that only works if "more specific wins" is exact.</para>
    /// </summary>
    public static int MatchScore(string platform, string runtimeIdentifier, string os)
    {
        if (string.IsNullOrWhiteSpace(platform) || platform.Equals("any", StringComparison.OrdinalIgnoreCase))
            return 1;

        if (platform.Equals(os, StringComparison.OrdinalIgnoreCase))
            return 2;

        if (platform.Equals(runtimeIdentifier, StringComparison.OrdinalIgnoreCase))
            return 3;

        return 0;
    }

    /// <summary>This machine's runtime identifier, e.g. <c>linux-x64</c>. Appears in messages about a
    /// kit that does not cover it.</summary>
    public static string CurrentRuntimeIdentifier()
    {
        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64   => "x64",
            Architecture.X86   => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm   => "arm",
            _                  => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
        };
        return $"{CurrentOs()}-{arch}";
    }

    /// <summary>This machine's operating system: <c>win</c>, <c>osx</c> or <c>linux</c>.</summary>
    public static string CurrentOs()
    {
        if (OperatingSystem.IsWindows()) return "win";
        if (OperatingSystem.IsMacOS())   return "osx";
        if (OperatingSystem.IsLinux())   return "linux";
        return "unknown";
    }

    /// <summary>
    /// Turns a launch into a command that can actually be started: the program resolved against the
    /// manifest's folder when it lives there, and every relative argument likewise.
    ///
    /// <para>Resolving arguments matters as much as the command. A manifest names a model library
    /// relative to itself, so a kit stays movable — copying the folder elsewhere, or opening the
    /// same workspace on another machine, must not break it.</para>
    /// </summary>
    public (string Command, IReadOnlyList<string> Arguments) Resolve(DeviceWorkerLaunch launch)
    {
        ArgumentNullException.ThrowIfNull(launch);

        return (ResolveCommand(launch.Command),
                launch.Arguments.Select(a => ResolveAgainstDirectory(a, mustExist: true)).ToArray());
    }

    /// <summary>
    /// Finds a file the manifest names, against its own folder first and then the kit's. Returns the
    /// value unchanged when nothing is there, so a caller can report the name the kit actually wrote.
    ///
    /// <para>The order is what lets a workspace hold files a kit does not: the manifest installed
    /// into the workspace is checked before the kit it came from, so dropping a file beside it works
    /// without touching the kit — which may well be read-only.</para>
    /// </summary>
    public string ResolveFile(string value) => ResolveAgainstDirectory(value, mustExist: true);

    /// <summary>
    /// Where circuitRF's own helper programs live. Defaults to the folder its assemblies were loaded
    /// from; settable so a test can point it somewhere real without installing anything.
    /// </summary>
    public static string ToolsDirectory { get; set; } = AppContext.BaseDirectory;

    /// <summary>
    /// Resolves the program to run. Same rules as any other value the manifest names, plus one:
    /// a BARE NAME may also be a helper circuitRF itself ships.
    ///
    /// <para>This is what lets a kit ask for a tool by name — <c>crf-vmhost</c>, the Linux VM host
    /// that runs Linux-only device models on macOS — without knowing where circuitRF was installed.
    /// Without it, a bare name falls through to the system path, finds nothing, and the kit fails
    /// with a message about a missing program the user never installed and should not have to.</para>
    ///
    /// <para>Deliberately narrow in two ways. It applies only to the COMMAND, never to arguments,
    /// which name the kit's own files and have no business resolving inside circuitRF's install.
    /// And only to a bare name: anything with a separator is a path the kit meant literally. The
    /// kit's own folders are still searched first, so a kit that ships its own build of a tool keeps
    /// it.</para>
    /// </summary>
    private string ResolveCommand(string command)
    {
        string resolved = ResolveAgainstDirectory(command, mustExist: true);
        if (!ReferenceEquals(resolved, command) && resolved != command) return resolved;

        if (string.IsNullOrWhiteSpace(command) || Path.IsPathRooted(command)) return command;
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains('/')) return command;
        if (string.IsNullOrEmpty(ToolsDirectory)) return command;

        try
        {
            string candidate = Path.GetFullPath(Path.Combine(ToolsDirectory, command));
            if (File.Exists(candidate)) return candidate;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException) { /* leave it as written */ }

        return command;
    }

    /// <summary>
    /// Makes a relative path absolute against the manifest's folder — but only when something is
    /// actually there. A value that is not a path in this folder (a program on the system path, a
    /// plain flag, a number) is left exactly as written.
    /// </summary>
    private string ResolveAgainstDirectory(string value, bool mustExist)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value)) return value;

        // The manifest's own folder wins, so real files placed beside a copied manifest take
        // precedence over the kit it was copied from.
        foreach (string root in new[] { Directory, BaseDirectory })
        {
            if (string.IsNullOrEmpty(root)) continue;

            string candidate;
            try { candidate = Path.GetFullPath(Path.Combine(root, value)); }
            catch (ArgumentException) { continue; }

            if (!mustExist) return candidate;

            try
            {
                if (File.Exists(candidate) || System.IO.Directory.Exists(candidate)) return candidate;
            }
            catch (IOException) { /* try the next root */ }
        }

        return value;
    }
}
