using System.Globalization;

namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Validation for the pin port-number entry (owner, 2026-08-17: "make sure the text is properly
/// validated"). Framework-free and deliberately separate from the dialog: the rules are the part worth
/// testing, and a headless test cannot construct an Avalonia <c>Window</c>.
///
/// <para><b>Why a text box and an explicit parse rather than a spinner.</b> A <c>NumericUpDown</c>
/// answers bad text by silently reverting it or by going null, which reads as the dialog ignoring what
/// was typed — and a null on OK would have pushed a "changed to nothing" through the undo stack. Every
/// rejection here names what is wrong with the text the user actually typed, and the OK button stays
/// disabled until it parses, so no invalid value can reach <c>SymbolEditorViewModel.SetPinPortNumber</c>
/// in the first place. That method re-checks the lower bound anyway — the dialog is not the only
/// caller, and a validator that is also the only guard is one refactor away from being neither.</para>
/// </summary>
public static class SymbolPinPortInput
{
    /// <summary>Outcome of validating one typed port number.</summary>
    /// <param name="IsValid">True when <paramref name="PortNumber"/> may be committed.</param>
    /// <param name="PortNumber">The parsed 1-based port number; 0 when invalid.</param>
    /// <param name="Error">Why it was rejected, or null when valid.</param>
    /// <param name="Note">A non-blocking remark about an otherwise valid value — currently a number
    /// past the owning cell's declared port count. Never a reason to refuse: a symbol is routinely
    /// authored before its <c>.ccell</c> declares how many ports the cell has, and the canvas's own
    /// unmapped-port overlay is what keeps the mismatch visible afterwards.</param>
    public readonly record struct Result(bool IsValid, int PortNumber, string? Error, string? Note);

    /// <summary>The largest port number accepted. Not a physical limit — a guard against a typed
    /// value so large it is meaningless, chosen well above any real symbol so it can never be the
    /// thing that stops ordinary work.</summary>
    public const int MaxPortNumber = 9999;

    /// <param name="text">Exactly what is in the box, including whitespace and null.</param>
    /// <param name="declaredPortCount">The owning cell's port count, or null for an orphan symbol.</param>
    public static Result Validate(string? text, int? declaredPortCount = null)
    {
        string s = (text ?? "").Trim();

        if (s.Length == 0)
            return new Result(false, 0, "Enter a port number.", null);

        // Digits only — no sign, no decimal point, no thousands separator, no exponent. Parsing
        // leniently and then range-checking would accept "+2" and "2.0" and quietly turn "2.7" into
        // something; a port number is an ordinal, and the strict rule is the honest one to state.
        foreach (char c in s)
            if (!char.IsAsciiDigit(c))
                return new Result(false, 0, $"\"{s}\" is not a whole number — enter digits only.", null);

        // Only after the shape is known to be digits, so an overflow is reported as too large rather
        // than as "not a number" — the same text, two very different corrections.
        if (!int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out int n) || n > MaxPortNumber)
            return new Result(false, 0, $"Port number must be {MaxPortNumber} or less.", null);

        if (n < 1)
            return new Result(false, 0, "Port number must be 1 or greater.", null);

        string? note = declaredPortCount is { } count && count > 0 && n > count
            ? $"This cell declares {count} port{(count == 1 ? "" : "s")}, so port {n} is not connected to anything yet."
            : null;

        return new Result(true, n, null, note);
    }
}
