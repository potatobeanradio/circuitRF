// Export: the composed model as a .cmodel a schematic can place (brief-modelcomposer-11 R-mc11-1, R-mc11-2; overview D9,
// D12, D13).
//
// Assemble builds the whole ComposedModel in memory from the document's recorded steps — the network with its values in
// use and each variable's scaling rule, the current and charge constituents with their domains, G_x and arrays, the
// thermal network, the trap block, the temperature mode, N0/W0, the symbol and the provenance — and validates it with
// MC-1's validator. Export then writes it by a temporary file and a rename, so a refusal, a failure or a cancellation
// leaves no file and never a partial one.
//
// Refusals name what is missing: no N0; a constituent no step has fitted (or whose arrays are not in the cache); a stale
// step (CmcStamps), each one named. The charge step's record is what export needs, fitted or typed (MC-18): a document with
// DC I–V alone exports, its charges the typed capacitances or zero. The symbol choice is stored in the .cmc (Steps.Export) as well as in the model.

using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Engine;
using RfCore.Data;

namespace CircuitRF.Design.Modeling;

/// <summary>What the document records about export: the chosen symbol (a library name, or a <c>.csym</c> relative to the
/// <c>.cmc</c>); null is the standard FET symbol.</summary>
public sealed record ExportSettings(string? Symbol = null)
{
    public static ExportSettings Default { get; } = new();
}

/// <summary>An export: where it went, what was written, and one line per thing worth saying.</summary>
public sealed record CmodelExportResult(string Path, ComposedModel Model, IReadOnlyList<string> Lines);

public static class CmodelExport
{
    public const string StepName = "Export";

    /// <summary>The network's define-block name in <c>model.json</c>.</summary>
    public const string NetworkName = "extrinsic";

    // ── The document ──────────────────────────────────────────────────────────

    public static ExportSettings Settings(CmcDocument doc) =>
        Step(doc)?["Settings"] is JsonObject o && o["Symbol"] is JsonValue v && v.TryGetValue<string>(out var s)
            ? new ExportSettings(s) : ExportSettings.Default;

    /// <summary>The block's keys checked — <see cref="CmcFile.Validate"/>'s call.</summary>
    public static void Validate(CmcDocument doc)
    {
        if (Step(doc) is not { } o) return;
        CmcExtrinsic.Only(o, $"Steps.{StepName}", ["Settings"]);
        if (o["Settings"] is { } sn)
        {
            var so = CmcExtrinsic.Obj(sn, $"Steps.{StepName}.Settings");
            CmcExtrinsic.Only(so, $"Steps.{StepName}.Settings", ["Symbol"]);
            CmcExtrinsic.Str(so, "Symbol", $"Steps.{StepName}.Settings.Symbol", required: false);
        }
    }

    /// <summary>The document with <paramref name="settings"/> recorded; a null symbol removes the block.</summary>
    public static CmcDocument WithSettings(CmcDocument doc, ExportSettings settings)
    {
        var steps = doc.Steps is JsonObject s ? (JsonObject)JsonNode.Parse(s.ToJsonString())! : new JsonObject();
        if (settings.Symbol is { Length: > 0 } sym) steps[StepName] = new JsonObject { ["Settings"] = new JsonObject { ["Symbol"] = sym } };
        else steps.Remove(StepName);
        return doc with { Steps = steps.Count == 0 ? null : steps };
    }

    private static JsonObject? Step(CmcDocument doc) =>
        doc.Steps is JsonObject s && s[StepName] is { } n ? CmcExtrinsic.Obj(n, $"Steps.{StepName}") : null;

    // ── Assembly ──────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole model, assembled and validated in memory (R-mc11-1). <paramref name="symbol"/> (a library name, or a
    /// <c>.csym</c> path, absolute or relative to the <c>.cmc</c>) overrides the document's choice; <paramref name="outDir"/>
    /// is the folder the <c>.cmodel</c> will live in, which a <c>.csym</c> is stored relative to. Throws
    /// <see cref="ModelingException"/> for a refusal.
    /// </summary>
    public static ComposedModel Assemble(string cmcPath, CmcDocument doc, string outDir, string? symbol = null,
                                         string? created = null, RunControl? rc = null)
    {
        cmcPath = Path.GetFullPath(cmcPath);
        string cmcDir = Path.GetDirectoryName(cmcPath)!;
        if (doc.Kind != CmcKind.Active)
            throw new ModelingException("Kind", "a Passive document exports as a cell, not a .cmodel.");
        if (doc.BasePeriphery is not { } periphery)
            throw new ModelingException("BasePeriphery", "the base finger count N0 is not set; state the measured device's fingers and unit width.");

        // The cache opens before the stale check: the data digest reads each source's hash from its index, and only an open
        // brings that up to date with a source edited on disk since — the case the stale check exists for.
        var data = new CmcCache(cmcPath).Open(doc, rc).Data;
        var stale = CmcStamps.StaleSteps(cmcPath, doc);
        if (stale.Count > 0) throw new ModelingException("Steps", CmcStamps.Describe(stale));

        var current = Fitted(CurrentFit.Record(doc), CurrentFit.Load(cmcPath, doc), CurrentFit.StepName, "Id and Ig");
        var charge = Fitted(ChargeFit.Record(doc), ChargeFit.Load(cmcPath, doc), ChargeFit.StepName, "Qg and Qd");
        if (TrapFit.Settings(doc).Modelled && TrapFit.Record(doc) is null)
            throw new ModelingException(TrapFit.StepName, "traps are modelled and the step has not been run.");
        var heating = ThermalFit.Heating(doc);
        var thermal = heating.SelfHeating ? ThermalFit.Network(ThermalFit.Record(doc)) : null;
        if (heating.SelfHeating && thermal is null)
            throw new ModelingException(ThermalFit.StepName, "self-heating is on and the thermal network has not been extracted.");

        // The temperature mode: what the constituents read, at the temperature the current fit's data was taken at.
        var r = current.Record;
        Constituent?[] all = [r.Id, r.Ig, r.IdRF, charge.Record.Qg, charge.Record.Qd];
        var cs = all.OfType<Constituent>().ToList();
        double t0 = CurrentFit.Temperatures(CurrentFit.ChosenIv(doc, current.Settings), data) is { Count: > 0 } ts ? ts[0] : 25.0;
        var plan = ThermalFit.Plan(doc, data);
        var temperature = cs.Any(c => c.Inputs.Contains(ComposedCoreProgram.T))
            ? plan.IsSingle ? ChargeFit.TemperatureOf(cs, t0) : ThermalFit.ModelTemperature(plan, cs)
            : ModelTemperature.Single(t0);

        var arrays = new Dictionary<string, DataCube>(StringComparer.Ordinal);
        foreach (var (k, v) in current.Arrays) arrays[k] = v;
        foreach (var (k, v) in charge.Arrays) arrays[k] = v;

        var model = new ComposedModel
        {
            BasePeriphery = periphery,
            Temperature = temperature,
            Thermal = thermal,
            // Under a shift coupling the current step's record carries no block: Id₀ is trap-free, the strengths the trap step's.
            Traps = r.Traps ?? TrapFit.ShiftBlock(doc),
            Network = Network(cmcDir, doc),
            Core = new CoreConstituents(r.Id, r.Ig, charge.Record.Qg, charge.Record.Qd, r.IdRF)
            {
                Symmetry = current.Settings.Symmetry,
                Dispersion = r.Tau is { } tau ? new ModelDispersion(tau) : null,
            },
            Extrapolation = r.Gx.Count > 0 ? ModelExtrapolation.Local(r.Gx) : null,
            Symbol = SymbolFor(symbol ?? Settings(doc).Symbol, cmcDir, outDir, thermal is not null),
            Provenance = Provenance(cmcPath, doc, created) with
            {
                Charges = ChargeFit.LinearProvenance(charge.Settings, charge.Record) ?? "",
            },
            Arrays = arrays,
        };
        try { CmodelFile.Validate(model); }
        catch (CmodelFormatException ex) { throw new ModelingException(ex.Field, ex.Message[(ex.Field.Length + 2)..]); }
        return model;
    }

    private static T Fitted<T>(object? record, T? loaded, string step, string what) where T : class =>
        loaded ?? throw new ModelingException(step, record is null
            ? $"{what} are not fitted; run the {step} step."
            : $"the fitted arrays are not in the cache; run the {step} step again.");

    /// <summary>The network block as <c>model.json</c> carries it: the six-pin define block at the values in use, and every
    /// variable's rule that is not <c>fixed</c>. No network: each pin joined to its intrinsic one through 1 µΩ.</summary>
    internal static ExtrinsicNetwork Network(string cmcDir, CmcDocument doc)
    {
        var n = CmcExtrinsic.Network(doc);
        if (n.Schematic is null) return new ExtrinsicNetwork(ChargeFit.IntrinsicNetwork, []);
        var net = SixPinNetwork.Load(DataImport.ResolveSource(cmcDir, n.Schematic));
        var rules = net.Variables
            .Select(v => new ScalingRule(v.Name, n.Variables.GetValueOrDefault(v.Name)?.Scaling ?? PeripheryScaling.Fixed))
            .Where(s => s.Rule != PeripheryScaling.Fixed)
            .ToList();
        return new ExtrinsicNetwork(net.DefineBlock(NetworkName, n.Values), rules);
    }

    /// <summary>The symbol as the <c>.cmodel</c> names it — a <c>.csym</c> relative to the model's folder — or a refusal.</summary>
    internal static string? SymbolFor(string? symbol, string cmcDir, string outDir, bool thermal)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return null;
        string s = symbol.Trim();
        if (ModelSymbols.Refusal(s, cmcDir, thermal) is { } why) throw new ModelingException("Symbol", why);
        if (!s.EndsWith(".csym", StringComparison.OrdinalIgnoreCase)) return s;
        string full = Path.IsPathRooted(s) ? s : Path.GetFullPath(Path.Combine(cmcDir, s));
        return Path.GetRelativePath(outDir, full).Replace('\\', '/');
    }

    private static ModelProvenance Provenance(string cmcPath, CmcDocument doc, string? created)
    {
        string dir = Path.GetDirectoryName(cmcPath)!;
        var known = new CmcCache(cmcPath).SourceHashes();
        var sources = new List<ModelSource>();
        foreach (var set in doc.DataSets.Where(CmcStamps.FeedsFits))
            foreach (string src in set.Sources)
                if (known.TryGetValue((set.Name, src), out var sha) && sources.All(x => x.Path != src))
                    sources.Add(new ModelSource(src, sha));
        return new ModelProvenance
        {
            Created = created ?? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            CircuitRfVersion = Version,
            Sources = sources,
            SettingsDigest = SettingsDigest(doc),
        };
    }

    /// <summary>The digest a <c>.cmodel</c>'s provenance records of the document it was exported from: its network and
    /// steps, so a model whose document has changed since can be told from one that has not (brief vendorlib-17 D6).</summary>
    public static string SettingsDigest(CmcDocument doc)
    {
        string settings = (doc.Network?.ToJsonString() ?? "") + "\n" + (doc.Steps?.ToJsonString() ?? "");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(settings)))[..16];
    }

    /// <summary>The version every assembly carries (the repo's one <c>VERSION</c> file), without a build suffix.</summary>
    internal static string Version =>
        (typeof(CmodelExport).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "")
        .Split('+')[0];

    // ── Export ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reads the <c>.cmc</c>, assembles and validates the model, then writes it to <paramref name="outPath"/> atomically
    /// (R-mc11-1) — what the window and the <c>model</c> verb call. A given <paramref name="symbol"/> is recorded in the
    /// <c>.cmc</c> after the model is written. Refused, failed or cancelled, nothing is written.
    /// </summary>
    public static CmodelExportResult Export(string cmcPath, string outPath, string? symbol = null, string? created = null,
                                            RunControl? rc = null)
    {
        rc?.BeginStage("Export", 1);
        var doc = CmcFile.Read(cmcPath);
        outPath = Path.GetFullPath(outPath);
        string outDir = Path.GetDirectoryName(outPath)!;
        var model = Assemble(cmcPath, doc, outDir, symbol, created, rc);

        using var buffer = new MemoryStream();
        CmodelFile.Write(model, buffer);
        rc?.ThrowIfCancellationRequested();
        Directory.CreateDirectory(outDir);
        string tmp = outPath + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            File.WriteAllBytes(tmp, buffer.ToArray());
            File.Move(tmp, outPath, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }

        // The .cmc records a .csym relative to itself, as Settings documents it.
        string? stored = symbol?.Trim() is { Length: > 0 } sym
            ? sym.EndsWith(".csym", StringComparison.OrdinalIgnoreCase) && Path.IsPathRooted(sym)
                ? Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(cmcPath))!, sym).Replace('\\', '/') : sym
            : null;
        if (symbol is not null && stored != Settings(doc).Symbol)
        {
            CmcFile.Write(cmcPath, WithSettings(doc, new ExportSettings(stored)));
            // The model is as new as the document it was exported from: recording the symbol is part of this export, and
            // a .cmodel left older than its .cmc reads as stale to Export to Library (brief vendorlib-17 D6).
            File.SetLastWriteTimeUtc(outPath, File.GetLastWriteTimeUtc(cmcPath));
        }

        var lines = new List<string>
        {
            $"{Path.GetFileName(outPath)}: N0 {periphery(model)} · {Temperature(model.Temperature)}" +
            (model.Thermal is { } th ? $" · Rth {th.Rth.ToString("G4", CultureInfo.InvariantCulture)} °C/W" : "") +
            (model.Traps is { } tr ? $" · traps {tr.Model} ({string.Join(", ", tr.States)}){(tr.Shifted ? ", threshold shift" : "")}" : ""),
        };
        // R-mc18-2: a model whose charges are zero is legitimate, and said so.
        if (ChargeFit.Record(doc)?.Typed is { IsZero: true }) lines.Add(ChargeFit.ZeroLine);
        rc?.TickStage();
        return new CmodelExportResult(outPath, model, lines);

        static string periphery(ComposedModel m) => $"{m.BasePeriphery.Fingers} × {m.BasePeriphery.UnitWidth}";
    }

    private static string Temperature(ModelTemperature t) => t.Mode == TemperatureMode.Single
        ? $"{t.T0.ToString("G4", CultureInfo.InvariantCulture)} °C only"
        : $"{t.Tmin.ToString("G4", CultureInfo.InvariantCulture)} to {t.Tmax.ToString("G4", CultureInfo.InvariantCulture)} °C";
}
