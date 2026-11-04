// Export to Library from Model Composer (brief vendorlib-17): the cell a maker would have drawn around a model, written
// for them, so the model reaches a library without a schematic of the maker's own.
//
// THERE IS NO EXPORT HERE. What is written is an ordinary cell folder, and the export runs ModelLibraryExport.Plan and
// Export on it unchanged (D1, D2) — so the library part is byte-for-byte what a hand-drawn cell would have produced. What
// is here is the cell (D3), the defaults the dialog opens on (D4), and every reason not to (D6), shared by the window's
// button and `circuitrf export part <file.cmc|file.cmodel>`.

using System.Globalization;
using CircuitRF.Core.Devices.Composed;
using CircuitRF.Design.Cells;
using CircuitRF.Design.Modeling;
using CircuitRF.Design.Modeling.Passive;
using CircuitRF.Design.Schematic;

namespace CircuitRF.Design.VendorLibraries.ModelLibrary;

/// <summary>
/// What Export to Library publishes from a model document (brief vendorlib-17 D3, D4).
/// </summary>
/// <param name="DocumentPath">The <c>.cmc</c>, or the <c>.cmodel</c> when that is what was given.</param>
/// <param name="CmodelPath">An active model's exported <c>.cmodel</c>; null for a passive one.</param>
/// <param name="PassiveCellDir">A passive model's exported cell, used as it is; null for an active one.</param>
/// <param name="PartNumber">The default part number: the <c>.cmodel</c>'s file stem, or the passive cell's name.</param>
/// <param name="FrequencyLow">The measured data's lowest frequency, as the dialog spells one; empty when unknown.</param>
public sealed record ModelCellSource(
    string DocumentPath, string? CmodelPath, string? PassiveCellDir, string PartNumber,
    string FrequencyLow = "", string FrequencyHigh = "")
{
    public bool IsActive => CmodelPath is not null;
}

/// <summary>A cell made to be exported: deleted on <see cref="Dispose"/> when it was written for the export (an active
/// model's), and left alone when it is the maker's own (a passive model's).</summary>
public sealed class StagedModelCell(string cellDir, string? temporaryRoot) : IDisposable
{
    /// <summary>The cell folder the export runs on.</summary>
    public string CellDir { get; } = cellDir;

    /// <summary>The <c>.export-&lt;part&gt;</c> folder deleted afterwards; null when nothing was written.</summary>
    public string? TemporaryRoot { get; } = temporaryRoot;

    public void Dispose()
    {
        if (TemporaryRoot is { } t) ModelCellFromModel.TryDelete(t);
    }
}

public static class ModelCellFromModel
{
    /// <summary>The temporary folder's prefix, beside the document: <c>.export-&lt;part&gt;/&lt;part&gt;</c>. The leading
    /// dot keeps a workspace scan from listing it as a cell.</summary>
    public const string TemporaryPrefix = ".export-";

    /// <summary>The instance the cell places, and the pins it has, in order (D3): gate, drain, source, and the thermal node
    /// when the model self-heats.</summary>
    public const string FetInstance = "Q1";
    public static readonly IReadOnlyList<string> FetPins = ["g", "d", "s"];
    public const string ThermalPin = "t";

    /// <summary>The cell parameter a placed part sets its device temperature with. Its default is the design's ambient
    /// <c>temp</c>, which is what a blank <c>Temp</c> on a ComposedFET means — a blank default is a value the netlist
    /// cannot carry.</summary>
    public const string TempParameter = "Temp";
    public const string TempDefault = "temp";

    /// <summary>The <c>.cmodel</c> an active document's Export step writes when no other path is chosen: beside the
    /// <c>.cmc</c>, with its name.</summary>
    public static string DefaultCmodelPath(string cmcPath) => Path.ChangeExtension(Path.GetFullPath(cmcPath), CmodelFile.Extension);

    // ── Why not (D6) ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Why <paramref name="doc"/>, saved at <paramref name="cmcPath"/>, cannot be published, or null when it can: an active
    /// model not exported to <paramref name="cmodelPath"/> (null: <see cref="DefaultCmodelPath"/>) or exported before the
    /// document last changed; a passive document with no Export step settings, or whose cell is not written yet. The
    /// sentence is the button's tooltip and the CLI's refusal.
    /// </summary>
    public static string? BlockedReason(CmcDocument doc, string cmcPath, string? cmodelPath = null)
    {
        cmcPath = Path.GetFullPath(cmcPath);
        if (doc.Kind == CmcKind.Passive)
        {
            if (PassiveStep.Settings(doc).Export is not { } e)
                return "The Export step names no cell and library yet; set them and export the cell first.";
            string cell = PassiveCellDir(cmcPath, e);
            if (!Directory.Exists(cell))
                return $"The cell {e.Cell} has not been exported yet; run the Export step first.";
            return null;
        }

        string cmodel = Path.GetFullPath(cmodelPath ?? DefaultCmodelPath(cmcPath));
        string name = Path.GetFileName(cmodel);
        if (!File.Exists(cmodel))
            return $"The model has not been exported to {name} yet; run the Export step first.";
        if (IsStale(doc, cmcPath, cmodel))
            return $"{name} is older than {Path.GetFileName(cmcPath)}; run the Export step again so the library gets the model as it is now.";
        return null;
    }

    /// <summary>The <c>.cmodel</c> is older than the document and the document's settings are no longer the ones it was
    /// exported from. A save that changed nothing the model is built from (a view, a plot) leaves it current.</summary>
    private static bool IsStale(CmcDocument doc, string cmcPath, string cmodel)
    {
        if (File.GetLastWriteTimeUtc(cmodel) >= File.GetLastWriteTimeUtc(cmcPath)) return false;
        try { return CmodelFile.Read(cmodel).Provenance.SettingsDigest != CmodelExport.SettingsDigest(doc); }
        catch (Exception ex) when (ex is IOException or CmodelFormatException) { return true; }
    }

    private static string PassiveCellDir(string cmcPath, PassiveExportSettings e)
    {
        string dir = Path.GetDirectoryName(cmcPath)!;
        string library = Path.IsPathRooted(e.Library) ? e.Library : Path.GetFullPath(Path.Combine(dir, e.Library));
        return Path.Combine(library, e.Cell);
    }

    // ── What is published (D3, D4) ───────────────────────────────────────────────────────────────

    /// <summary>
    /// What publishing <paramref name="path"/> — a <c>.cmc</c> or a <c>.cmodel</c> — would publish, with the dialog's
    /// defaults; null, with the <paramref name="refusal"/> the button and the CLI show, when it cannot be. A <c>.cmc</c>'s
    /// active model is <paramref name="cmodelPath"/> (null: its Export step's default path).
    /// </summary>
    public static ModelCellSource? Open(string path, out string? refusal, string? cmodelPath = null)
    {
        path = Path.GetFullPath(path);
        bool isModel = path.EndsWith(CmodelFile.Extension, StringComparison.OrdinalIgnoreCase);
        refusal = !isModel && !path.EndsWith(CmcFile.Extension, StringComparison.OrdinalIgnoreCase)
                ? $"'{Path.GetFileName(path)}' is neither a Model Composer document ({CmcFile.Extension}) nor a model ({CmodelFile.Extension})."
                : !File.Exists(path) ? $"{path} does not exist."
                : null;
        if (refusal is not null) return null;
        if (isModel)
            return ReadModel(path, out refusal) is null ? null
                 : new ModelCellSource(path, path, null, Path.GetFileNameWithoutExtension(path));

        CmcDocument doc;
        try { doc = CmcFile.Read(path); }
        catch (CmcFormatException ex) { refusal = $"{Path.GetFileName(path)} could not be read ({ex.Message})."; return null; }
        if ((refusal = BlockedReason(doc, path, cmodelPath)) is not null) return null;

        var (low, high) = MeasuredRange(path, doc);
        if (doc.Kind == CmcKind.Passive)
        {
            string cell = PassiveCellDir(path, PassiveStep.Settings(doc).Export!);
            return new ModelCellSource(path, null, cell, Path.GetFileName(cell), low, high);
        }
        string cmodel = Path.GetFullPath(cmodelPath ?? DefaultCmodelPath(path));
        return ReadModel(cmodel, out refusal) is null ? null
             : new ModelCellSource(path, cmodel, null, Path.GetFileNameWithoutExtension(cmodel), low, high);
    }

    private static ComposedModel? ReadModel(string cmodel, out string? refusal)
    {
        refusal = null;
        try { return CmodelFile.Read(cmodel); }
        catch (Exception ex) when (ex is IOException or CmodelFormatException)
        {
            refusal = $"{Path.GetFileName(cmodel)} could not be read ({ex.Message}).";
            return null;
        }
    }

    /// <summary>D4: the measured S-parameters' frequency range, or empty when the document has none (or its data cannot be
    /// opened, which is not a reason to refuse — the range is the dialog's to change).</summary>
    private static (string Low, string High) MeasuredRange(string cmcPath, CmcDocument doc)
    {
        if (doc.DataSets.Count == 0) return ("", "");
        try
        {
            var data = new CmcCache(cmcPath).Open(doc).Data;
            double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
            foreach (var d in data.Values)
                if (d.FrequenciesHz is { Length: > 0 } f) { lo = Math.Min(lo, f.Min()); hi = Math.Max(hi, f.Max()); }
            return double.IsFinite(lo) && hi > 0
                ? (ModelLibraryExport.FormatFrequency(lo), ModelLibraryExport.FormatFrequency(hi))
                : ("", "");
        }
        catch (Exception ex) when (ex is not OperationCanceledException) { return ("", ""); }
    }

    // ── The cell (R-vl17-1) ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the cell an active model is published from into <paramref name="cellDir"/> (whose name is the cell's): one
    /// ComposedFET placing <paramref name="cmodelPath"/>, Pins g, d, s — and t when the model self-heats — and the cell
    /// parameters N (the model's N0) and Temp, forwarded to the FET. No <c>.csym</c>: the dialog's symbol choice decides.
    /// Deterministic — the same model at the same path writes the same bytes. A model that cannot be read throws
    /// <see cref="CmodelFormatException"/>; <see cref="Open"/> has already refused one.
    /// </summary>
    public static void Write(string cmodelPath, string cellDir)
    {
        cmodelPath = Path.GetFullPath(cmodelPath);
        cellDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cellDir));
        var model = CmodelFile.Read(cmodelPath);
        string cellName = Path.GetFileName(cellDir);
        IReadOnlyList<string> pins = model.Thermal is null ? FetPins : [.. FetPins, ThermalPin];

        // Drawn here rather than through NetlistSchematic, which cannot draw a three-net ComposedFET: the built-in symbol
        // has the optional thermal pin as its fourth, and the netlist drawing matches nets to pins one for one. The FET
        // names its glyph outright — the FET family's, or the thermal one — so its pins are where the labels are even when
        // the model names a .csym of its own that does not travel with the copy the export makes.
        var fet = new EditableComponent { InstanceName = FetInstance, Symbol = SymbolKind.ComposedFET, X = 0, Y = 0 };
        foreach (var (name, value) in new[]
                 {
                     (ComposedFetSymbolProvider.FileParameter, cmodelPath.Replace('\\', '/')),
                     ("N", "N"),
                     (ComposedFetSymbolProvider.SymbolParameter, model.Thermal is null ? nameof(SymbolKind.FetCurtice) : ComposedFetSymbolProvider.ThermalGlyph),
                     (TempParameter, TempParameter),
                 })
            fet.Parameters.Add(new EditableParameter { Name = name, Expression = value, ShowOnSchematic = name is "File" or "N" });
        var sch = new SchematicEditModel();
        sch.Components.Add(fet);
        for (int k = 0; k < pins.Count; k++)
        {
            var (x, y) = fet.GetPortWorldCoord(k);
            sch.NetLabels.Add(new EditableNetLabel { X = x, Y = y, Name = pins[k] });
        }
        ModelLibraryExport.AddPins(sch, pins);

        CellCreate.Create(Path.GetDirectoryName(cellDir)!, cellName, CellViews.Schematic, sch);
        string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
        var ccell = CellPersistence.LoadFromFile(ccellPath);
        ccell.NumPorts = pins.Count;
        ccell.Parameters.Clear();
        ccell.Parameters.Add(new CcellParameter
        {
            Name = "N", DefaultExpression = model.BasePeriphery.Fingers.ToString(CultureInfo.InvariantCulture), ShowOnSchematic = true,
            Description = $"Finger count; the model was measured at N0 = {model.BasePeriphery.Fingers}.",
        });
        ccell.Parameters.Add(new CcellParameter
        {
            Name = TempParameter, DefaultExpression = TempDefault, Unit = "", ShowOnSchematic = false,
            Description = "The device's temperature in °C; temp follows the design's.",
        });
        CellPersistence.SaveToFile(ccellPath, ccell);
    }

    /// <summary>
    /// The cell the export runs on (D2): an active model's written to <c>&lt;document folder&gt;/.export-&lt;part&gt;/&lt;part&gt;</c>
    /// and deleted when the result is disposed; a passive model's own cell, used as it is. Any <c>.export-*</c> folder a
    /// crash left beside the document is deleted first (R-vl17-4). Throws what the file system or the model reader
    /// throws, having deleted what it wrote.
    /// </summary>
    public static StagedModelCell Stage(ModelCellSource source)
    {
        if (source.PassiveCellDir is { } passive) return new StagedModelCell(passive, null);
        string docDir = Path.GetDirectoryName(source.DocumentPath)!;
        DeleteLeftovers(docDir);
        string root = Path.Combine(docDir, TemporaryPrefix + source.PartNumber);
        string cell = Path.Combine(root, source.PartNumber);
        try
        {
            Directory.CreateDirectory(root);
            Write(source.CmodelPath!, cell);
            return new StagedModelCell(cell, root);
        }
        catch
        {
            TryDelete(root);
            throw;
        }
    }

    /// <summary>Deletes every <c>.export-*</c> folder in <paramref name="folder"/>.</summary>
    public static void DeleteLeftovers(string folder)
    {
        try
        {
            foreach (string d in Directory.EnumerateDirectories(folder, TemporaryPrefix + "*")) TryDelete(d);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    internal static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
