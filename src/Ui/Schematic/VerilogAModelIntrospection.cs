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
            // names the compiler that ran and where the artefact went, and it is shown in the
            // parameter dialog rather than posted to the Messages panel: there is no process-global
            // message sink, and inventing one would post into whichever window registered last —
            // the multi-window defect MW1 exists to have fixed. The dialog is also simply where the
            // user is standing at the moment they choose the file.
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
        RememberLabels(modelFilePath, result);
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
    /// </summary>
    private static readonly Dictionary<(string File, string Model), IReadOnlyList<string>> _labels =
        new();

    private static void RememberLabels(string? modelFilePath, IReadOnlyList<VerilogAModelInfo> models)
    {
        string file = modelFilePath?.Trim() ?? "";
        if (file.Length == 0) return;

        lock (_labels)
        {
            foreach (var m in models)
            {
                if (m.TerminalLabels.Count == 0) continue;
                _labels[(file, m.TypeId)] = m.TerminalLabels;
                // Also under the blank model name, which is what a component carries when the file
                // declares exactly one type and nothing had to be chosen — by far the common case.
                if (models.Count == 1) _labels[(file, "")] = m.TerminalLabels;
            }
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
            return _labels.TryGetValue((file, modelValue?.Trim() ?? ""), out var hit) ? hit : null;
    }

    /// <summary>
    /// What the last <see cref="Describe"/> had to say about COMPILING, or "" when the file was
    /// already a compiled artefact and no compiler was involved.
    ///
    /// <para>Reported rather than left silent because the two questions a user has here are "did it
    /// actually rebuild" and "which compiler did it use" — and a cache that answers invisibly is
    /// indistinguishable from one that is stale.</para>
    /// </summary>
    public static string LastCompileNote { get; private set; } = "";

    /// <summary>Drops the remembered labels. For tests, which stand up different models under one
    /// path.</summary>
    public static void ForgetCachedLabels()
    {
        lock (_labels) _labels.Clear();
        lock (_cache)  _cache.Clear();
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
