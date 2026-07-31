using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CircuitRF.Core.Pdk;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Turns the parts an imported kit reports into entries the Library Palette can show and place.
///
/// <para><b>Why this installs cells rather than inventing a new component species.</b> circuitRF
/// already has a component whose artwork lives in a file outside the schematic and is resolved at
/// render time — a cell reference. A kit part is exactly that shape, so installing each readable
/// symbol as an ordinary cell means placement, rendering, pin geometry, hit-testing and the symbol
/// editor all work on kit parts with no new machinery, and the user can open and inspect the
/// generated symbol like any other. The alternative — a parallel "external part" render path —
/// would duplicate all of it and drift.</para>
///
/// <para>Nothing here knows anything about any particular kit. It reads what the importer found.</para>
/// </summary>
public static class PdkPartInstaller
{
    /// <summary>Folder inside the workspace that holds cells generated from imported kits.</summary>
    public const string InstallFolderName = "pdk";

    /// <param name="Items">Entries for the Library Palette — PLACEABLE parts only.</param>
    /// <param name="OmittedNotPlaceable">
    /// Parts the kit declares that got no readable symbol, so nothing could be placed for them.
    /// These are almost always a kit's internal building blocks — the helper subcircuits its real
    /// parts are assembled from — which a component browser should not be cluttered with. They are
    /// counted rather than hidden: the import report still lists every one of them.
    /// </param>
    public sealed record InstallOutcome(
        IReadOnlyList<PaletteItem> Items,
        int SymbolsInstalled,
        int IconsFound,
        IReadOnlyList<string> Diagnostics,
        int OmittedNotPlaceable = 0);

    /// <summary>
    /// Install every part the report lists. Returns one palette entry per part — including parts
    /// whose symbol could not be read, which still appear (with their icon, if any) so the user can
    /// see what the kit contains rather than silently losing it.
    /// </summary>
    /// <param name="report">The importer's own findings. Never modified.</param>
    /// <param name="workspaceRootDir">
    /// Workspace to install generated cells into. When null — no workspace is open — nothing is
    /// written and the parts are still listed, icons and all, just not placeable yet.
    /// </param>
    public static InstallOutcome Install(PdkImportReport report, string? workspaceRootDir)
    {
        var items  = new List<PaletteItem>();
        var diags  = new List<string>();
        int syms    = 0;
        int icons   = 0;
        int omitted = 0;

        // A kit imported from an archive has no directory to resolve its own asset paths against,
        // so its artwork cannot be reached without extracting it first. Say so once, not per part.
        bool haveRoot = !string.IsNullOrEmpty(report.RootPath) && Directory.Exists(report.RootPath);
        if (!haveRoot && report.Parts.Count > 0)
            diags.Add("This kit was read from an archive, so its artwork could not be opened. " +
                      "Extract it to a folder and import that to get symbols and palette icons.");

        string kit = string.IsNullOrWhiteSpace(report.KitName) ? "Kit" : report.KitName;

        string? kitInstallDir = null;
        if (haveRoot && workspaceRootDir is not null)
            kitInstallDir = Path.Combine(workspaceRootDir, InstallFolderName, SanitizeFolderName(kit));

        foreach (var part in report.Parts)
        {
            string? iconPath = null;
            if (haveRoot && part.IconRelativePath is { Length: > 0 } rel)
            {
                string abs = Resolve(report.RootPath, rel);
                if (File.Exists(abs)) { iconPath = abs; icons++; }
            }

            string? cellDir = null;
            if (kitInstallDir is not null && part.SymbolArtwork is { } art)
            {
                cellDir = TryInstallSymbol(kitInstallDir, kit, part,
                                           Resolve(report.RootPath, art.RelativePath), diags, iconPath);
                if (cellDir is not null) syms++;
            }

            // Only placeable parts reach the palette. A part with no readable symbol is a kit's
            // internal building block, not something to browse for and click — and a tile that
            // cannot place anything is worse than no tile. The report still lists every part.
            if (cellDir is null) { omitted++; continue; }

            items.Add(new PaletteItem(
                Kind:            SymbolKind.Generic,
                PortCount:       0,
                DisplayName:     string.IsNullOrWhiteSpace(part.DisplayName) ? part.Id : part.DisplayName,
                Category:        ComponentCategory.Other,
                SearchTerms:     BuildSearchTerms(part, kit),
                IsCommon:        false,
                ExtraCategories: null,
                Pdk:             new PdkPartRef(kit, part.Id, iconPath, cellDir)));
        }

        return new InstallOutcome(items, syms, icons, diags, omitted);
    }

    /// <summary>
    /// Rebuilds palette entries from the kits already installed in a workspace.
    ///
    /// <para>Called when a workspace opens. Without it a kit vanishes from the palette on reopen
    /// even though its cells are still on disk and its placed components still resolve — the parts
    /// were only ever held in session memory. The installed cells ARE the record; nothing needs to
    /// be re-imported.</para>
    /// </summary>
    public static IReadOnlyList<PaletteItem> LoadInstalled(string? workspaceRootDir)
    {
        var items = new List<PaletteItem>();
        if (string.IsNullOrEmpty(workspaceRootDir)) return items;

        string root = Path.Combine(workspaceRootDir, InstallFolderName);
        if (!Directory.Exists(root)) return items;

        IEnumerable<string> kitDirs;
        try { kitDirs = Directory.EnumerateDirectories(root); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return items; }

        foreach (var kitDir in kitDirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> cellDirs;
            try { cellDirs = Directory.EnumerateDirectories(kitDir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { continue; }

            foreach (var cellDir in cellDirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                CcellFile ccell;
                try
                {
                    string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
                    if (!File.Exists(ccellPath)) continue;
                    ccell = CellPersistence.LoadFromFile(ccellPath);
                }
                catch { continue; }   // a cell we cannot read simply does not reappear

                if (string.IsNullOrWhiteSpace(ccell.ExternalProvider)) continue;

                string kit    = ccell.ExternalProvider!;
                string partId = ccell.ExternalType ?? Path.GetFileName(cellDir);

                items.Add(new PaletteItem(
                    Kind:            SymbolKind.Generic,
                    PortCount:       0,
                    DisplayName:     partId,
                    Category:        ComponentCategory.Other,
                    SearchTerms:     [partId, kit],
                    IsCommon:        false,
                    ExtraCategories: null,
                    Pdk:             new PdkPartRef(kit, partId, ccell.ExternalIconPath, cellDir)));
            }
        }

        return items;
    }

    // ── Symbol installation ───────────────────────────────────────────────────

    /// <summary>
    /// Reads one symbol description and writes it out as a cell. Returns the cell folder, or null
    /// when the file could not be read — in which case the reason is recorded, never swallowed.
    /// </summary>
    private static string? TryInstallSymbol(string kitInstallDir, string kitName, PdkPart part,
                                            string symbolAbsPath, List<string> diags, string? iconPath)
    {
        if (!File.Exists(symbolAbsPath)) return null;

        // Only the text symbol-description format has a reader today. Anything else (a binary cell
        // view, for instance) is left alone; the importer already reports it as a known gap.
        if (!symbolAbsPath.EndsWith(".dsn", StringComparison.OrdinalIgnoreCase)) return null;

        DsnSymbolReadResult read;
        try
        {
            read = DsnSymbolReader.ReadFile(symbolAbsPath);
        }
        catch (Exception ex)
        {
            diags.Add($"'{part.DisplayName}': reading its symbol failed — {ex.Message}");
            return null;
        }

        if (!read.Success || read.Symbol is null)
        {
            string why = read.Diagnostics.Count > 0 ? read.Diagnostics[0] : "the file could not be understood";
            diags.Add($"'{part.DisplayName}': no symbol was installed — {why}");
            return null;
        }

        foreach (var d in read.Diagnostics)
            diags.Add($"'{part.DisplayName}': {d}");

        try
        {
            Directory.CreateDirectory(kitInstallDir);

            string cellName = SanitizeFolderName(part.Id);
            string cellDir  = Path.Combine(kitInstallDir, cellName);

            if (!Directory.Exists(cellDir))
                cellDir = CellFolder.CreateCellFolder(kitInstallDir, cellName);

            string symDir = CellFolder.SubFolderPath(cellDir, ViewType.Symbol);
            Directory.CreateDirectory(symDir);

            string fileName = cellName + CellFolder.ViewExtension(ViewType.Symbol);
            SymbolPersistence.SaveToFile(Path.Combine(symDir, fileName), read.Symbol);

            // Name the symbol as the cell's primary, and record the pin count, so placement resolves
            // it the same way it resolves any hand-authored cell.
            string ccellPath = Path.Combine(cellDir, CellFolder.CcellFileName);
            var ccell = File.Exists(ccellPath) ? CellPersistence.LoadFromFile(ccellPath) : new CcellFile();
            ccell.PrimarySymbol = fileName;
            ccell.NumPorts      = read.Symbol.Pins.Count;

            // A kit part is a LEAF backed by a provider, not a hierarchy: it has a symbol and no
            // schematic on purpose, so extraction must emit one external-device instance rather
            // than trying to descend into it.
            ccell.ExternalProvider = kitName;
            ccell.ExternalType     = part.Id;
            ccell.ExternalIconPath = iconPath;

            // The part's declared parameters become the cell's published interface, which is what
            // seeds a placed instance and drives the ordinary Parameter Editor — no separate
            // parameter-editing surface is needed for kit parts.
            ccell.Parameters              = BuildDeclaredParameters(part);
            ccell.ExternalFixedParameters = BuildFixedParameters(part);

            CellPersistence.SaveToFile(ccellPath, ccell);

            return cellDir;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diags.Add($"'{part.DisplayName}': its symbol could not be written — {ex.Message}");
            return null;
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// The part's declared parameters, as the cell's published interface — carrying the KIT's own
    /// defaults verbatim. circuitRF never invents a default: where the kit stated none, the field is
    /// left blank so whatever supplies the part's behaviour keeps ownership of it.
    /// </summary>
    private static List<CcellParameter> BuildDeclaredParameters(PdkPart part)
    {
        var list = new List<CcellParameter>();
        if (part.Parameters is null) return list;

        foreach (var p in part.Parameters)
        {
            if (string.IsNullOrWhiteSpace(p.Name) || p.IsText) continue;   // text = infrastructure
            list.Add(new CcellParameter
            {
                Name              = p.Name,
                DefaultExpression = p.DefaultExpression ?? "",
                Unit              = "",
                ShowOnSchematic   = false,
            });
        }
        return list;
    }

    /// <summary>
    /// The kit's infrastructure parameters — declared as text rather than a number. Kept off the
    /// editable interface (a user pointing one instance at a different data folder is a mistake, not
    /// a design choice) but still emitted, so the provider receives what the kit specified.
    /// </summary>
    private static Dictionary<string, string>? BuildFixedParameters(PdkPart part)
    {
        if (part.Parameters is null) return null;

        var fixedParams = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var p in part.Parameters)
            if (p.IsText && !string.IsNullOrWhiteSpace(p.Name))
                fixedParams[p.Name] = p.DefaultExpression ?? "";

        return fixedParams.Count > 0 ? fixedParams : null;
    }

    private static string Resolve(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

    private static IReadOnlyList<string> BuildSearchTerms(PdkPart part, string kit)
    {
        var terms = new List<string> { part.Id, kit };
        if (!string.IsNullOrWhiteSpace(part.DisplayName)) terms.Add(part.DisplayName);
        if (!string.IsNullOrWhiteSpace(part.Category))    terms.Add(part.Category);
        return terms.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Makes a kit or part name safe to use as a folder name on every platform. Path separators are
    /// stripped on ALL platforms regardless of what the local runtime reports as invalid, so a name
    /// that is harmless here cannot become a path traversal somewhere else.
    /// </summary>
    internal static string SanitizeFolderName(string name)
    {
        var sb = new System.Text.StringBuilder(name.Length);
        var invalid = Path.GetInvalidFileNameChars();

        foreach (char c in name)
        {
            bool bad = c is '/' or '\\' or ':' || Array.IndexOf(invalid, c) >= 0 || char.IsControl(c);
            sb.Append(bad ? '_' : c);
        }

        string s = sb.ToString().Trim().Trim('.');
        return s.Length == 0 ? "part" : s;
    }
}
