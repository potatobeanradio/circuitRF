using System.Globalization;

namespace CircuitRF.Text;

/// <summary>
/// Decimal-separator tolerance for USER-TYPED numeric text.
///
/// <para>Much of the world writes one and a half as <c>1,5</c>. circuitRF accepts both spellings in
/// every editable field, and it does so REGARDLESS of the machine's culture — a decimal comma is
/// not a German feature to be unlocked by a German locale, it is a second spelling of the same
/// number, and a user who prefers it should not have to change their operating system to get it.
/// The canonical form is still the point: what is STORED and what is WRITTEN to a document is
/// always <c>1.5</c>, so a file authored on one machine reads identically on every other.</para>
///
/// <para><b>Why this type lives in the leaf project.</b> Its callers are the expression parser
/// (<c>Core</c>), the layout and technology editors (<c>Design</c>, <c>Ui</c>), wBond's own input
/// (<c>WBond</c>) and the CLI. <c>WBond</c> and <c>RfCore</c> are both leaves with no common
/// ancestor but this one — the same reason <see cref="Diagnostics.Diagnostic"/> is here, recorded
/// in this project's <c>.csproj</c>.</para>
/// </summary>
public static class NumericText
{
    /// <summary>
    /// Rewrites a decimal comma to a decimal point in the ONE position where a comma cannot mean
    /// anything else, and leaves every other comma exactly as typed.
    ///
    /// <para><b>The rule: a comma between two digits, at bracket depth 0, outside a string
    /// literal.</b> Everywhere else the comma already has a job. In the expression grammar every
    /// separator comma sits inside <c>(…)</c> or <c>[…]</c> — <c>if(c,a,b)</c>, <c>max(a,b)</c>,
    /// <c>HB1.V("n",1,All)</c>, <c>X[1,2]</c> — and the Pratt parser accepts no comma at all at
    /// depth 0, so <c>1,5</c> is a parse ERROR today. Rewriting it is therefore a pure widening:
    /// no text that parses now changes meaning.</para>
    ///
    /// <para><b>What this deliberately does not do.</b> A decimal comma inside an argument list is
    /// irreducibly ambiguous — <c>max(1,5)</c> is either the larger of 1 and 5 or the single value
    /// 1.5, and nothing in the text says which. There the comma stays a separator and the decimal
    /// point must be written as a point. The same holds for any field whose own grammar separates
    /// values with commas (a List-mode sweep's <c>Values=</c>), which is why that text must never
    /// be passed through here — see <see cref="TryParseDouble(string?, out double)"/> for the
    /// single-value case, which is the one that is safe.</para>
    ///
    /// <para>A leading comma (<c>,5</c> for 0.5) is NOT accepted, though <c>.5</c> is: write
    /// <c>0,5</c>. The rewrite is one character for one character, so a caller reporting a parse
    /// error by POSITION still points at the character the user typed.</para>
    /// </summary>
    public static string NormalizeDecimalSeparator(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        if (!text.Contains(',')) return text;   // the overwhelming majority — no allocation

        char[]? buf = null;
        int  depth    = 0;
        bool inString = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (inString)
            {
                if (c == '"') inString = false;
                continue;
            }

            switch (c)
            {
                case '"':
                    inString = true;
                    break;
                case '(' or '[':
                    depth++;
                    break;
                case ')' or ']':
                    if (depth > 0) depth--;
                    break;
                case ',' when depth == 0
                           && i > 0 && char.IsDigit(text[i - 1])
                           && i + 1 < text.Length && char.IsDigit(text[i + 1]):
                    buf ??= text.ToCharArray();
                    buf[i] = '.';
                    break;
            }
        }

        return buf is null ? text : new string(buf);
    }

    /// <summary>
    /// <c>double.TryParse</c> for a field a PERSON typed into: either decimal separator, invariant
    /// otherwise, and no group separators in either culture (a value carrying thousands marks is
    /// refused rather than guessed at — <c>1.234</c> means one-point-two-three-four here, on every
    /// machine, and <c>1,234</c> means the same).
    /// </summary>
    /// <remarks>
    /// Never use this on a file's bytes. A document, a Touchstone file and a CLI argument are
    /// contracts, not typing, and they stay strictly invariant.
    /// </remarks>
    public static bool TryParseDouble(string? text, out double value)
        => TryParseDouble(text, NumberStyles.Float | NumberStyles.AllowLeadingSign, out value);

    /// <inheritdoc cref="TryParseDouble(string?, out double)"/>
    public static bool TryParseDouble(string? text, NumberStyles styles, out double value)
        => double.TryParse(NormalizeDecimalSeparator(text?.Trim()),
                           styles & ~NumberStyles.AllowThousands,
                           CultureInfo.InvariantCulture,
                           out value);

    /// <inheritdoc cref="TryParseDouble(string?, out double)"/>
    public static bool TryParseInt(string? text, out int value)
        => int.TryParse(text?.Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out value);
}
