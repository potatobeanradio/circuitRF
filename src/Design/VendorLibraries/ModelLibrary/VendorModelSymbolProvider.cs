using CircuitRF.Design.Cells;
using CircuitRF.Design.Schematic;
using CircuitRF.Design.Workspace;

namespace CircuitRF.Design.VendorLibraries.ModelLibrary
{
    // See SpiceModelSymbolProvider for why this alias sits inside the namespace.
    using Symbol = global::CircuitRF.Design.Symbol.Symbol;

    /// <summary>
    /// The <c>vendormodel://</c> cell reference a placed model folder carries (brief vendorlib-9 R-vl9-2a):
    /// <c>vendormodel://&lt;library&gt;/&lt;model folder, library-relative&gt;</c>. A distinct scheme from a SpiceModel's
    /// <c>vendorlib://</c> <c>File</c>, so neither is ever mistaken for the other.
    ///
    /// <para><b>The glyph travels beside it, not in it.</b> What is stored is the reference as the <c>.csch</c>'s
    /// <c>CellRef</c> and a <c>Glyph</c> instance parameter, as a SpiceModel vendor part stores its glyph. The
    /// SYMBOL reference (<see cref="SymbolRef"/>) adds the glyph after a <c>|</c>, so a placed model whose library
    /// is missing still draws its shape; a folder name cannot hold <c>|</c> on every system, so the split is
    /// unambiguous.</para>
    /// </summary>
    public static class VendorModelRefs
    {
        public const string Scheme = "vendormodel://";

        /// <summary>The instance parameter a placed model stores <c>part.cpart</c>'s <c>Glyph</c> in. It is the
        /// symbol's, never the model's: extraction does not pass it to the cell.</summary>
        public const string GlyphParameter = "Glyph";

        private const char GlyphSeparator = '|';

        public static bool IsRef(string? cellRef)
            => cellRef is not null && cellRef.TrimStart().StartsWith(Scheme, StringComparison.OrdinalIgnoreCase);

        /// <summary>The stored reference for one model folder of one library, with <c>/</c> separators.</summary>
        public static string RefFor(string library, string modelFolderRel)
            => $"{Scheme}{library}/{modelFolderRel.Replace('\\', '/').Trim('/')}";

        /// <summary>The symbol reference: <paramref name="cellRef"/> with the instance's glyph after it.</summary>
        public static string SymbolRef(string cellRef, string? glyph)
        {
            string g = (glyph ?? "").Trim();
            string bare = Bare(cellRef);
            return g.Length == 0 ? bare : bare + GlyphSeparator + g;
        }

        /// <summary>The reference without any glyph suffix.</summary>
        public static string Bare(string cellRef)
        {
            string t = cellRef.Trim();
            int bar = t.IndexOf(GlyphSeparator);
            return bar < 0 ? t : t[..bar];
        }

        /// <summary>Splits a reference (stored or symbol form) into library, model folder and glyph.</summary>
        public static bool TryParse(string? reference, out string library, out string modelFolderRel, out string glyph)
        {
            library = modelFolderRel = glyph = "";
            if (!IsRef(reference)) return false;
            string rest = reference!.Trim()[Scheme.Length..];
            int bar = rest.IndexOf(GlyphSeparator);
            if (bar >= 0) { glyph = rest[(bar + 1)..].Trim(); rest = rest[..bar]; }
            int slash = rest.IndexOf('/');
            if (slash <= 0 || slash == rest.Length - 1) return false;
            library        = rest[..slash];
            modelFolderRel = rest[(slash + 1)..].Trim('/');
            return modelFolderRel.Length > 0;
        }

        /// <summary>A placed model, resolved: the library's folder, the model folder and its index entry.</summary>
        public sealed record Resolved(string WorkspaceRoot, string Library, string LibraryRoot, string ModelDir,
                                      VendorLibraryEntry Entry);

        /// <summary>
        /// The model folder a reference names, through the workspace the document belongs to — the walk a
        /// <c>vendorlib://</c> part takes — or null with a refusal naming the part and the library (brief
        /// vendorlib-3's rule, R-vl9-3c): a library the workspace does not reference, a library folder that is
        /// missing, and a part the library no longer holds each say so differently.
        /// </summary>
        public static Resolved? Resolve(string? reference, string? documentDir, string? workspaceRoot, out string? refusal)
        {
            refusal = null;
            if (!TryParse(reference, out string library, out string rel, out _))
            {
                refusal = $"'{reference}' is not a model reference ({Scheme}<library>/<model folder>).";
                return null;
            }
            string part = rel.Contains('/') ? rel[(rel.LastIndexOf('/') + 1)..] : rel;

            string? ws = WorkspaceRootFinder.WorkspaceDirOf(documentDir)
                         ?? (string.IsNullOrWhiteSpace(workspaceRoot) ? null : WorkspaceRootFinder.Normalize(workspaceRoot));
            if (ws is null)
            {
                refusal = $"'{part}' comes from the vendor library '{library}', and this document belongs to no " +
                          "workspace to look the library up in.";
                return null;
            }
            if (VendorLibraryRefs.ResolveLibraryRoot(ws, library, out string? why) is not { } root)
            {
                refusal = $"'{part}' cannot be read. {why}";
                return null;
            }

            string dir;
            try { dir = Path.GetFullPath(Path.Combine(root, CircuitRF.Core.RefPath.ToNative(rel))); }
            catch (Exception ex) { refusal = $"'{reference}' is not a usable path: {ex.Message}"; return null; }
            if (!ModelFolder.Is(dir))
            {
                refusal = $"The vendor library '{library}' no longer holds the model '{part}' ({rel}).";
                return null;
            }
            if (VendorLibraryIndex.ModelEntry(root, rel) is not { } entry)
            {
                refusal = $"'{part}' in the vendor library '{library}': its {ModelFolder.PartFileName} could not be read.";
                return null;
            }
            return new Resolved(ws, library, root, dir, entry);
        }
    }

    /// <summary>
    /// The symbol of a placed model (brief vendorlib-9 R-vl9-2b): the glyph <c>part.cpart</c> names, bound to the
    /// model's pins, or — with no glyph — the model folder's primary <c>.csym</c> through <see cref="CellFolder"/>'s
    /// primacy rule, exactly as a cell's symbol is drawn.
    ///
    /// <para><b>Missing library:</b> the stored glyph is drawn with the NotFound mark, as a vendor SPICE part's is,
    /// and the existing Repair applies; with no glyph the placeholder is drawn. Only the model's <c>part.cpart</c>
    /// is consulted to draw, through the index's stat-keyed cache.</para>
    /// </summary>
    public static class VendorModelSymbolProvider
    {
        public static CellSymbolResolution Resolve(string symbolRef, string? baseDir, string? workspaceRoot)
        {
            VendorModelRefs.TryParse(symbolRef, out _, out _, out string storedGlyph);
            if (VendorModelRefs.Resolve(symbolRef, baseDir, workspaceRoot, out _) is not { } r)
                return new CellSymbolResolution
                {
                    State  = CellSymbolState.NotFound,
                    Symbol = storedGlyph.Length > 0 ? GlyphSymbol(storedGlyph, null) : null,
                };

            string caption = r.Entry.Header.ValueText();
            string glyph = r.Entry.Model!.Glyph;
            if (glyph.Length > 0)
                return new CellSymbolResolution
                {
                    State = CellSymbolState.Resolved, Symbol = GlyphSymbol(glyph, r.Entry.Ports),
                    Caption = caption.Length > 0 ? caption : null,
                };

            var own = CellSymbolResolver.Resolve(Path.GetFileName(r.ModelDir), Path.GetDirectoryName(r.ModelDir));
            return caption.Length == 0 ? own
                : new CellSymbolResolution { State = own.State, Symbol = own.Symbol, Caption = caption };
        }

        /// <summary>
        /// A glyph with its pins numbered onto the model's: R, C or L is the built-in two-terminal part (pin k is
        /// model pin k); FET is the transistor glyph bound to the model's pin NAMES, by the rule a Verilog-A part's
        /// FET glyph follows (brief vendorlib-7). <paramref name="pins"/> null — the library is missing — draws
        /// the glyph with its own pin order.
        /// </summary>
        public static Symbol GlyphSymbol(string glyph, IReadOnlyList<string>? pins)
        {
            int count = pins?.Count ?? 0;
            SymbolKind? two = glyph.Trim().ToUpperInvariant() switch
            {
                "R" => SymbolKind.Resistor,
                "C" => SymbolKind.Capacitor,
                "L" => SymbolKind.Inductor,
                _   => null,
            };
            if (two is { } kind && (pins is null || count == 2)) return BuiltInSymbols.Primitives(kind, 2);

            if (glyph.Trim().Equals("FET", StringComparison.OrdinalIgnoreCase))
            {
                if (pins is null) return BuiltInSymbols.Primitives(SymbolKind.FetCurtice, 3);
                string terminals = VerilogASymbolProvider.FormatTerminals(pins, []);
                if (VerilogASymbolProvider.RefFor(count, terminals, "") is { } fet
                    && VerilogASymbolProvider.Resolve(fet, null).Symbol is { } bound)
                    return bound;
            }
            return BuiltInSymbols.PrimitivesForNamedPortBox(pins is { Count: > 0 } ? pins : ["1", "2"],
                                                            global::CircuitRF.Design.Symbol.SnpPinConfig.Standard, global::CircuitRF.Design.Symbol.SnpPitch.Loose);
        }

        /// <summary>
        /// The placed model's parameter interface as a <c>.ccell</c> would publish it (R-vl9-2c): <c>part.cpart</c>'s
        /// parameters and defaults, read from the index. What the Parameter Editor seeds and lists a cell
        /// instance's rows from.
        /// </summary>
        public static CcellFile? Ccell(string cellRef, string? baseDir, string? workspaceRoot)
        {
            if (VendorModelRefs.Resolve(cellRef, baseDir, workspaceRoot, out _) is not { } r) return null;
            var ccell = new CcellFile();
            foreach (var p in r.Entry.Model!.Parameters) ccell.Parameters.Add(ParameterOf(p));
            return ccell;
        }

        /// <summary>One <c>part.cpart</c> parameter as a cell's declared parameter: <c>"3 V"</c> → <c>3</c> in V, and an
        /// R, L or C unit in the spelling (and dimension) the Parameter Editor's unit list uses.</summary>
        public static CcellParameter ParameterOf(ModelPartParameter p)
        {
            var (expr, unit) = CircuitRF.Core.Expressions.Units.LiftInlineUnit(p.Default.Trim());
            var known = unit is null ? null : BomTablePaste.ParseUnit(unit);
            return new CcellParameter
            {
                Name = p.Name, DefaultExpression = expr,
                Unit = known?.Unit ?? unit ?? "", Dimension = known?.Dim ?? UnitDimension.None,
            };
        }
    }
}
