using System.Text.Json;
using CircuitRF.Core.Devices.External;

namespace CircuitRF.Ui.Schematic;

/// <summary>One device type a compiled model file declares, as far as the parameter dialog needs it.</summary>
/// <param name="TypeId">The module name, exactly as the artefact declares it — what the component's
/// <c>Model</c> parameter is set to.</param>
/// <param name="PinCount">External terminals. This is the answer to "how many Pins should I set?" —
/// the model states it, so nobody should be guessing.</param>
/// <param name="ParameterCount">How many parameters the module declares. Reported only so the user
/// can see the file was genuinely read; circuitRF interprets none of them.</param>
/// <param name="TerminalLabels">
/// The model's own name for each external terminal, in declaration order — <c>d</c>, <c>g</c>,
/// <c>s</c>, <c>b</c>, <c>dt</c> rather than 1..5.
///
/// <para><b>The model has already said which is which, and on a five-terminal part numbers are the
/// largest single source of mis-wiring.</b> Empty entries are ordinary: a model that names no
/// terminal falls back to the number, per terminal rather than all-or-nothing.</para>
/// </param>
/// <param name="ThermalTerminals">
/// Which of those terminals the model declares as THERMAL rather than electrical, by index.
///
/// <para>Only expressible since the worker began reporting a node's discipline; before that every
/// OSDI node was read as electrical. It is what lets the dialog tell a deliberate four-pin placement
/// of a five-terminal self-heating model from a mistake.</para>
/// </param>
public sealed record VerilogAModelInfo(
    string                TypeId,
    int                   PinCount,
    int                   ParameterCount,
    IReadOnlyList<string> TerminalLabels   = null!,
    IReadOnlyList<int>    ThermalTerminals = null!)
{
    public IReadOnlyList<string> TerminalLabels   { get; init; } = TerminalLabels   ?? [];
    public IReadOnlyList<int>    ThermalTerminals { get; init; } = ThermalTerminals ?? [];
}

/// <summary>One parameter a device type declares, as the picker shows it.</summary>
/// <param name="Name">The model's own spelling — what gets written onto the component. The worker
/// matches with <c>strcmp</c>, so this is the only spelling that reaches the model.</param>
/// <param name="Kind">Real, integer or string, as the model declares it.</param>
/// <param name="Units">The model's own units, or "".</param>
/// <param name="Description">The model's own one-line description, or "".</param>
/// <param name="DefaultText">
/// What the model itself defaults this parameter to, or null when it could not be read.
///
/// <para><b>Read from a probe model, not from the descriptor</b> — this ABI has no default field, so
/// the worker stands a model up with nothing set and reads the value back. Null means exactly "the
/// model did not say"; it is never stood in for with a zero, because a zero nobody chose is
/// indistinguishable from a real default and would be acted on as one.</para>
/// </param>
public sealed record VerilogAParameterInfo(
    string  Name,
    string  Kind,
    string  Units,
    string  Description,
    string? DefaultText)
{
    /// <summary>Name, units and description on one line, for a searchable list.</summary>
    public string Summary =>
        (Units.Length > 0 ? $"{Name} [{Units}]" : Name)
        + (Description.Length > 0 ? $" — {Description}" : "");
}

/// <summary>
/// What a compiled model file (<c>.osdi</c>) declares, read for the parameter dialog so the
/// component's <c>Model</c> and <c>Pins</c> can be filled in from the file rather than typed from
/// the user's memory of it.
///
/// <para><b>It goes through the SAME provider the engine uses at Run</b>
/// (<see cref="ExternalDeviceRegistry.Find"/> against <see cref="VerilogAFileResolver"/>'s own
/// composed name), not a second reader of the artefact. So what the dialog reports and what Run
/// accepts cannot disagree — and because the registry keeps a resolved provider, the worker started
/// to answer this question is the same one Run then reuses rather than a second process.</para>
///
/// <para><b>Never throws.</b> A file that is missing, half-built, or built for another architecture
/// is an ordinary thing to have just picked; it comes back as an empty list plus a reason, and the
/// dialog leaves the parameters exactly as the user typed them.</para>
/// </summary>
public static class VerilogAModelIntrospection
{
    // Keyed by (absolute path, last-write-time) so re-picking the same file costs nothing while a
    // recompile in place is picked up — the same freshness rule CellLayoutResolver already uses.
    private static readonly Dictionary<(string Path, long Ticks), IReadOnlyList<VerilogAModelInfo>> _cache =
        new();

    /// <summary>
    /// The device types <paramref name="modelFilePath"/> declares, or an empty list with
    /// <paramref name="error"/> set. A blank path is simply "nothing chosen yet" — no error.
    /// </summary>
    public static IReadOnlyList<VerilogAModelInfo> Describe(string? modelFilePath, out string? error)
    {
        error = null;
        // Cleared FIRST, so the note always describes THIS call and not some earlier file's. Its
        // reader posts it to the Messages panel, and a note left standing across a cache hit would
        // be re-posted on every parameter edit — the same line, over and over, about a compile that
        // did not happen.
        LastCompileNote = "";
        if (string.IsNullOrWhiteSpace(modelFilePath)) return [];

        string path;
        long   ticks;
        try
        {
            path = Path.GetFullPath(modelFilePath.Trim());
            if (!File.Exists(path))
            {
                error = $"'{path}' does not exist.";
                return [];
            }
            ticks = File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return [];
        }

        lock (_cache)
            if (_cache.TryGetValue((path, ticks), out var hit)) return hit;

        IReadOnlyList<VerilogAModelInfo> result;
        try
        {
            // Catch what the compile step had to say, if this file was Verilog-A SOURCE. The note
            // names the compiler that ran and where the artefact went, and its reader posts it to
            // the Messages panel — through the OWNING SCHEMATIC's sink, not a process-global one.
            // That distinction is the whole reason this is a return value rather than a call: a
            // static sink would post into whichever window registered last, which is the
            // multi-window defect MW1 exists to have fixed.
            string? note = null;
            VerilogAFileResolver.CompileNote = n => note = n;

            IExternalDeviceProvider? provider;
            try { provider = ExternalDeviceRegistry.Find(VerilogAFileResolver.ProviderNameFor(path)); }
            finally { VerilogAFileResolver.CompileNote = null; }

            LastCompileNote = note ?? "";
            if (provider is null)
            {
                error = $"'{path}' could not be opened as a compiled model.";
                return [];
            }

            result = [.. provider.Describe()
                                 .Where(d => !string.IsNullOrWhiteSpace(d.TypeId))
                                 .Select(d => new VerilogAModelInfo(
                                     d.TypeId, d.ExternalPinCount, d.Parameters.Count,
                                     TerminalsOf(d), ThermalTerminalsOf(d)))];

            if (result.Count == 0)
            {
                error = $"'{path}' loaded but declares no device type.";
                return [];
            }
        }
        catch (Exception ex)
        {
            // Includes ExternalDeviceException — a missing helper, a file for another architecture.
            error = ex.Message;
            return [];
        }

        lock (_cache) _cache[(path, ticks)] = result;
        RememberLabels(modelFilePath, path, ticks, result);
        return result;
    }

    /// <summary>The external terminals' own names, in declaration order.</summary>
    private static IReadOnlyList<string> TerminalsOf(ExternalDeviceDescriptor d)
        => [.. d.Nodes.Where(n => n.External)
                      .OrderBy(n => n.Index)
                      .Select(n => n.Label ?? "")];

    /// <summary>Which external terminals are thermal, by position in that same order.</summary>
    private static IReadOnlyList<int> ThermalTerminalsOf(ExternalDeviceDescriptor d)
    {
        var external = d.Nodes.Where(n => n.External).OrderBy(n => n.Index).ToList();
        return [.. external.Select((n, i) => (n, i))
                           .Where(t => t.n.QuantityKind == NodeQuantityKind.Thermal)
                           .Select(t => t.i)];
    }

    // ── What the SYMBOL is allowed to read ────────────────────────────────────

    /// <summary>
    /// Terminal labels for a (file, model) pair that has ALREADY been described, keyed on the
    /// parameter values exactly as the component carries them.
    ///
    /// <para><b>Separate from <see cref="_cache"/>, and deliberately not keyed on the file's
    /// mtime.</b> Its one reader is the symbol, which is rebuilt whenever a parameter changes — so a
    /// lookup there must cost a dictionary probe and nothing else. Keying on mtime would put a file
    /// stat on that path; describing on a miss would put a WORKER LAUNCH on it, which is a process
    /// start during a redraw.</para>
    ///
    /// <para>That is why the symbol falls back to numbers rather than blocking: labels appear once
    /// the file has been read, which is what opening the component's parameters does. Nothing
    /// renders wrongly in the meantime — a numbered lead is what the symbol has always drawn.</para>
    ///
    /// <para><b>It is backed by a file, because a process-lifetime cache meant the labels were lost
    /// on every restart.</b> A user who closed circuitRF and reopened the workspace found their
    /// model drawn with numbers again, and the only way back was to open each component's parameters
    /// — a step that reads as "the design forgot something". The backing store is
    /// <see cref="StoreFileName"/> under the per-user cache directory: derived data, disposable, and
    /// rebuilt by the next describe if deleted.</para>
    ///
    /// <para><b>The mtime rule is kept, and still costs nothing per probe.</b> Each stored entry
    /// carries the file's resolved path and last-write ticks, and they are checked ONCE when the
    /// store is loaded — an entry whose file has since been edited, recompiled or removed is dropped
    /// there. A probe stays a dictionary lookup, which is the constraint this whole class exists
    /// under.</para>
    /// </summary>
    private static readonly Dictionary<(string File, string Model), IReadOnlyList<string>> _labels =
        new();

    /// <summary>One remembered set of terminal names, as it is written to disk.</summary>
    /// <param name="File">The component's <c>File</c> parameter EXACTLY as it carries it, which is
    /// the half of the probe key — a component looks itself up by what it holds, not by a resolved
    /// path it never sees.</param>
    /// <param name="Model">The component's <c>Model</c> value, or "" for the single-type case.</param>
    /// <param name="Path">The resolved absolute path, kept only so the entry can be validated.</param>
    /// <param name="Ticks">That file's last-write time when the labels were read from it.</param>
    /// <param name="Labels">The model's own terminal names, in declaration order.</param>
    private sealed record LabelEntry(string File, string Model, string Path, long Ticks, string[] Labels);

    /// <summary>The store's file name, inside the per-user <c>cache</c> directory.</summary>
    private const string StoreFileName = "verilog-a-terminal-labels.json";

    /// <summary>The entries behind <see cref="_labels"/>, kept so the store can be rewritten whole.</summary>
    private static readonly Dictionary<(string File, string Model), LabelEntry> _store = new();

    /// <summary>Whether the store has been read this session. Guarded by <see cref="_labels"/>.</summary>
    private static bool _storeLoaded;

    private static string StorePath => Path.Combine(AppDataRoot.SubDir("cache"), StoreFileName);

    /// <summary>
    /// Reads the store once, dropping every entry whose file has changed or gone. Caller holds the
    /// <see cref="_labels"/> lock.
    ///
    /// <para><b>Never throws.</b> A cache that cannot be read is a cache miss — the labels are
    /// re-derived by the next describe, which is exactly what happened before the store existed.</para>
    /// </summary>
    private static void LoadStoreLocked()
    {
        if (_storeLoaded) return;
        _storeLoaded = true;   // set FIRST: a failed read must not be retried on every glyph rebuild

        try
        {
            string path = StorePath;
            if (!File.Exists(path)) return;

            var entries = JsonSerializer.Deserialize<LabelEntry[]>(File.ReadAllText(path));
            if (entries is null) return;

            foreach (var e in entries)
            {
                if (e.Labels.Length == 0) continue;
                // The one place the mtime is checked. An entry for a model that has since been
                // edited or recompiled describes terminals that may no longer exist.
                if (!File.Exists(e.Path)) continue;
                if (File.GetLastWriteTimeUtc(e.Path).Ticks != e.Ticks) continue;

                _labels[(e.File, e.Model)] = e.Labels;
                _store[(e.File, e.Model)]  = e;
            }
        }
        catch (Exception)
        {
            // Unreadable, half-written, or from a future shape of this record. Start empty.
        }
    }

    /// <summary>Writes the store out. Best effort — see <see cref="LoadStoreLocked"/>.</summary>
    private static void SaveStoreLocked()
    {
        try
        {
            string path = StorePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(_store.Values.ToArray()));
        }
        catch (Exception)
        {
            // A read-only or missing cache directory costs the restart-survival and nothing else.
        }
    }

    /// <summary>
    /// Records one file's terminal names, in memory and in the backing store.
    ///
    /// <para><b>Internal rather than private for the test seam</b>: a real <c>.osdi</c> cannot be
    /// stood up in the UI test project (it needs a compiled artefact and the model-hosting worker),
    /// so the store's round trip is exercised through the same entry point <see cref="Describe"/>
    /// uses rather than through a reimplementation of it.</para>
    /// </summary>
    internal static void RememberLabels(
        string? modelFilePath, string resolvedPath, long ticks, IReadOnlyList<VerilogAModelInfo> models)
    {
        string file = modelFilePath?.Trim() ?? "";
        if (file.Length == 0) return;

        lock (_labels)
        {
            LoadStoreLocked();

            bool changed = false;
            void Remember(string model, IReadOnlyList<string> labels)
            {
                _labels[(file, model)] = labels;
                _store[(file, model)]  = new LabelEntry(file, model, resolvedPath, ticks, [.. labels]);
                changed = true;
            }

            foreach (var m in models)
            {
                if (m.TerminalLabels.Count == 0) continue;
                Remember(m.TypeId, m.TerminalLabels);
                // Also under the blank model name, which is what a component carries when the file
                // declares exactly one type and nothing had to be chosen — by far the common case.
                if (models.Count == 1) Remember("", m.TerminalLabels);
            }

            if (changed) SaveStoreLocked();
        }
    }

    /// <summary>
    /// Terminal labels for a component's own <c>File</c>/<c>Model</c> values, or null when this file
    /// has not been read yet. <b>Never launches anything</b> — see <see cref="_labels"/>.
    /// </summary>
    public static IReadOnlyList<string>? CachedTerminalLabels(string? modelFilePath, string? modelValue)
    {
        string file = modelFilePath?.Trim() ?? "";
        if (file.Length == 0) return null;

        lock (_labels)
        {
            LoadStoreLocked();
            return _labels.TryGetValue((file, modelValue?.Trim() ?? ""), out var hit) ? hit : null;
        }
    }

    /// <summary>
    /// What the last <see cref="Describe"/> had to say about COMPILING, or "" when no compiler ran
    /// on that call — the file was already a compiled artefact, or the answer came from the cache.
    ///
    /// <para>Reported rather than left silent because the two questions a user has here are "did it
    /// actually rebuild" and "which compiler did it use" — and a cache that answers invisibly is
    /// indistinguishable from one that is stale.</para>
    ///
    /// <para><b>Valid only for the call that just returned.</b> Every <see cref="Describe"/> clears
    /// it on entry, which is what lets a caller post it exactly once per compile rather than on
    /// every refresh that re-describes the same file.</para>
    /// </summary>
    public static string LastCompileNote { get; private set; } = "";

    /// <summary>Drops the remembered labels. For tests, which stand up different models under one
    /// path.
    ///
    /// <para>It marks the backing store as ALREADY READ rather than reloading it. A test that
    /// rebuilds a model under a path it has used before must see nothing; re-reading the file here
    /// would hand it the previous model's terminals, which is the exact staleness this method is
    /// called to remove.</para>
    /// </summary>
    public static void ForgetCachedLabels()
    {
        lock (_labels)
        {
            _labels.Clear();
            _store.Clear();
            _storeLoaded = true;
        }
        lock (_cache) _cache.Clear();
    }

    /// <summary>
    /// Forgets which store was read, so the next lookup reads the one at the CURRENT per-user
    /// directory. Called when that directory moves — see <see cref="AppDataRoot.RedirectTo"/>, for
    /// the same reason the compiled-model cache is refreshed there: a redirected process must not
    /// answer from, or write into, the real user's cache.
    /// </summary>
    public static void RefreshLabelStore()
    {
        lock (_labels)
        {
            _labels.Clear();
            _store.Clear();
            _storeLoaded = false;
        }
    }

    /// <summary>
    /// The parameters one device type inside <paramref name="modelFilePath"/> declares, each with
    /// the model's own units, description and default. Empty when the file or the type cannot be
    /// read — the caller shows no picker rather than an empty one.
    ///
    /// <para>Op-vars are already excluded by the worker: they are model OUTPUTS, and offering one as
    /// settable would put a writable box on a value the model computes.</para>
    ///
    /// <para><b>Not cached here.</b> <see cref="Describe"/> is on the path of every dialog open, so
    /// it caches; this runs only when the picker is actually opened, and its cost (one probe model
    /// stood up inside the worker) belongs to that gesture.</para>
    /// </summary>
    public static IReadOnlyList<VerilogAParameterInfo> DescribeParameters(
        string? modelFilePath, string? modelValue, out string? error)
    {
        error = null;
        var declared = Describe(modelFilePath, out error);
        if (Select(declared, modelValue) is not { } chosen) return [];

        try
        {
            var provider = ExternalDeviceRegistry.Find(
                VerilogAFileResolver.ProviderNameFor(Path.GetFullPath(modelFilePath!.Trim())));
            if (provider is null) return [];

            var descriptor = provider.Describe().FirstOrDefault(d => d.TypeId == chosen.TypeId);
            if (descriptor is null) return [];

            // Null when this worker cannot answer — an ordinary outcome, and the picker simply shows
            // no default rather than treating it as a failure.
            var defaults = (provider as DeviceWorkerProvider)?.DeclaredDefaults(chosen.TypeId);

            return [.. descriptor.Parameters.Select(p => new VerilogAParameterInfo(
                p.Name,
                p.Kind.ToString(),
                p.Units,
                p.Description,
                defaults is not null && defaults.TryGetValue(p.Name, out var d) ? d : p.DefaultText))];
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return [];
        }
    }

    /// <summary>
    /// Which of <paramref name="declared"/> the component's <c>Model</c> value selects: the named
    /// one, or the only one when the value is blank. Null when the value names nothing declared —
    /// the same rule the factory applies at Run, so the dialog cannot promise a device Run refuses.
    /// </summary>
    /// <summary>
    /// Which of the declared types to offer as the DEFAULT when the component names none — the
    /// "highest level" model in the file. Null only for an empty set.
    ///
    /// <para><b>Ranked by external terminal count, tie-broken by declared parameter count.</b> A
    /// model family ships its variants in one file, and the fuller formulation is the one carrying
    /// the extra terminals: a substrate node, a self-heating node, or both. On the published
    /// families this picks the variant a user means by "the model" — the 5-terminal
    /// substrate-plus-thermal bipolar over the 3-terminal reduced one, and the surface-potential
    /// MOSFET over the junction diode that ships beside it to model its own drain junction.</para>
    ///
    /// <para><b>It is a DEFAULT, not a determination.</b> Every declared type stays on the picker;
    /// this only decides which one a component starts on instead of starting on nothing. That is
    /// worth doing because "nothing" is not a neutral state — <c>CreateVerilogAModel</c> refuses a
    /// blank <c>Model</c> outright once a file declares more than one type, so a component left
    /// unset is a component that fails at Run.</para>
    ///
    /// <para>The final tie-break is the type name, so a file declaring two equally-ranked variants
    /// defaults to the same one on every machine rather than to whatever order the artefact happened
    /// to enumerate in.</para>
    ///
    /// <para><b>Why terminal count rather than the module hierarchy.</b> The intuitive rule — prefer
    /// the module that INSTANTIATES others in the same file — has no signal to read here. An
    /// <c>ExternalDeviceDescriptor</c> carries nodes and parameters and no sub-instances, and the
    /// hierarchy is gone before this code sees anything: the compiler rejects a structural instance
    /// in Verilog-A source outright (verified directly against the shipped compiler — a module
    /// instantiated inside another fails to parse, with or without a parameter override, and no
    /// artefact is produced at all). So a file whose modules call each other never reaches this
    /// method; what does reach it is a family shipping several INDEPENDENT variants side by side,
    /// and there "highest level" is the one carrying the extra terminals.</para>
    /// </summary>
    public static VerilogAModelInfo? Default(IReadOnlyList<VerilogAModelInfo> declared)
        => declared.Count == 0
            ? null
            : declared.OrderByDescending(m => m.PinCount)
                      .ThenByDescending(m => m.ParameterCount)
                      .ThenBy(m => m.TypeId, StringComparer.Ordinal)
                      .First();

    public static VerilogAModelInfo? Select(IReadOnlyList<VerilogAModelInfo> declared, string? modelValue)
    {
        if (declared.Count == 0) return null;

        string wanted = modelValue?.Trim() ?? "";
        if (wanted.Length == 0) return declared.Count == 1 ? declared[0] : null;

        foreach (var m in declared)
            if (m.TypeId.Equals(wanted, StringComparison.Ordinal)) return m;
        return null;
    }
}
