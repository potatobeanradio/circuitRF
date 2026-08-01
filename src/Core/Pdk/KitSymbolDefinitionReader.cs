using System.Text;

namespace CircuitRF.Core.Pdk;

/// <summary>One parameter a kit's symbol definition declares.</summary>
/// <param name="Name">The kit's own name for it — what a user should see, not one circuitRF invents.</param>
/// <param name="Description">The kit's own one-line description, or empty. Shown as the field's tooltip.</param>
/// <param name="IsText">
/// True when the kit declares the value as a string rather than a number. A part that offers a choice
/// of formulation names that choice with one of these, so this is what identifies the selector.
/// </param>
public sealed record KitSymbolParameter(string Name, string Description, bool IsText);

/// <summary>What a kit's symbol definition was found to declare.</summary>
/// <param name="Parameters">In declaration order — the order the kit meant them to be read in.</param>
/// <param name="ReferencedNames">
/// Other identifiers the definition mentions, in order. A part's definition names the subcircuit
/// family it is built from, which is how a part is tied to formulations found in the netlist.
/// </param>
public sealed record KitSymbolDefinition(
    IReadOnlyList<KitSymbolParameter> Parameters,
    IReadOnlyList<string>             ReferencedNames);

/// <summary>
/// Recovers what a kit's symbol definition declares about a part's parameters.
///
/// <para><b>Why bother, when the netlist already yields a working part.</b> The netlist gives the
/// formulations and which one to default to, but not what the kit CALLS that choice. Falling back to
/// a name circuitRF invents puts a word in the dialog that appears nowhere in the kit's own
/// documentation, which is a worse experience than it looks — the user cannot search for it. The
/// kit's own name and its own one-line description are both here.</para>
///
/// <para><b>This reads a FORMAT.</b> <c>create_parm</c> and the <c>PARM_*</c> flags are the
/// definition language's own API names, not any kit's. Nothing here names a supplier, a library, a
/// part or a model family, and nothing depends on a naming convention an author chose.</para>
///
/// <para><b>Best-effort by design.</b> The file is compiled, and only its identifier and text
/// constants survive in a form anything can read. So this recovers names and descriptions and
/// nothing else: no defaults, no expressions, no behaviour. Everything that decides what a part DOES
/// still comes from the netlist, where it can be read properly.</para>
/// </summary>
public static class KitSymbolDefinitionReader
{
    /// <summary>The call a definition makes to declare one parameter.</summary>
    private const string DeclareParameter = "create_parm";

    /// <summary>Marks a parameter's value as a string rather than a number.</summary>
    private const string TextValueFlag = "PARM_STRING";

    private const int MinRunLength     = 2;
    private const long MaxFileBytes    = 4 * 1024 * 1024;

    public static KitSymbolDefinition? TryReadFile(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > MaxFileBytes) return null;
            return Read(File.ReadAllBytes(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static KitSymbolDefinition Read(ReadOnlySpan<byte> bytes)
    {
        var runs = PrintableRuns(bytes);

        var parameters = new List<KitSymbolParameter>();
        var referenced = new List<string>();

        for (int i = 0; i < runs.Count; i++)
        {
            if (!runs[i].Equals(DeclareParameter, StringComparison.Ordinal))
            {
                if (IsIdentifier(runs[i]) && !IsApiWord(runs[i])) referenced.Add(runs[i]);
                continue;
            }

            // Declaration order is the file's own: the name follows the call, its description
            // follows the name, and the flags follow that until the next declaration.
            if (i + 1 >= runs.Count) break;
            string name = runs[++i];
            if (!IsIdentifier(name)) continue;

            string description = "";
            if (i + 1 < runs.Count && IsProse(runs[i + 1])) description = runs[++i];

            bool isText = false;
            for (int j = i + 1; j < runs.Count && !runs[j].Equals(DeclareParameter, StringComparison.Ordinal); j++)
                if (runs[j].Equals(TextValueFlag, StringComparison.Ordinal)) { isText = true; break; }

            parameters.Add(new KitSymbolParameter(name, description, isText));
        }

        return new KitSymbolDefinition(parameters, referenced);
    }

    /// <summary>
    /// The file's printable text runs, in order. A compiled definition keeps its identifiers and
    /// text constants as plain bytes; everything else is structure this deliberately does not try to
    /// interpret.
    /// </summary>
    private static List<string> PrintableRuns(ReadOnlySpan<byte> bytes)
    {
        var runs = new List<string>();
        var sb   = new StringBuilder();

        foreach (byte b in bytes)
        {
            if (b >= 0x20 && b < 0x7F) { sb.Append((char)b); continue; }
            if (sb.Length >= MinRunLength) runs.Add(sb.ToString());
            sb.Clear();
        }
        if (sb.Length >= MinRunLength) runs.Add(sb.ToString());

        return runs;
    }

    private static bool IsIdentifier(string s)
        => s.Length > 0 && (char.IsLetter(s[0]) || s[0] == '_')
        && s.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>A run with spaces in it is a description, not an identifier or a flag.</summary>
    private static bool IsProse(string s) => s.Contains(' ') && !s.StartsWith("PARM_", StringComparison.Ordinal);

    /// <summary>
    /// A word belonging to the definition language rather than to the kit. Kept out of the referenced
    /// names so what remains is the kit's own vocabulary.
    /// </summary>
    private static bool IsApiWord(string s)
        => s.StartsWith("PARM_", StringComparison.Ordinal)
        || s.EndsWith("_UNIT",  StringComparison.Ordinal)
        || s.Contains("_dialog", StringComparison.Ordinal)
        || s.Contains("_symbol", StringComparison.Ordinal)
        || s.Contains("_form",   StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("create_", StringComparison.Ordinal)
        || s.StartsWith("no_",     StringComparison.Ordinal);
}
