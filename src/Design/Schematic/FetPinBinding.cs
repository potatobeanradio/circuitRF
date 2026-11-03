namespace CircuitRF.Design.Schematic;

/// <summary>What a transistor glyph's pin is: one of the three it always has, or the fourth.</summary>
public enum FetPinRole { Gate, Drain, Source, Thermal, Bulk }

/// <summary>
/// Which of a model's terminals each pin of a transistor glyph stands for (brief-vendorlib-7 R-vl7-2a) — the
/// gate, drain and source, and a fourth when the model has one. One rule in one place: a placed Verilog-A model
/// binds its FET symbol through it, and a cell's symbol picker binds a cell's pin names through it.
///
/// <para><b>It matches the model's own terminal NAMES</b>, case-insensitively — <c>g</c>/<c>gate</c>,
/// <c>d</c>/<c>drain</c>, <c>s</c>/<c>source</c>. A model whose names do not say which is the gate has not
/// said, and <see cref="For"/> answers null rather than guessing: the caller keeps the box, or binds by
/// position and labels every pin so the guess can be seen.</para>
///
/// <para><b>The fourth pin takes whatever is left</b>, and only when it is one thing a transistor's fourth pin
/// can be — a thermal node (declared thermal by the model, or named <c>t</c>, <c>dt</c>, <c>tj</c>,
/// <c>thermal</c>) or a bulk (<c>b</c>, <c>bulk</c>). Anything else, or more than one terminal left, does not
/// bind: a glyph has nowhere honest to put it.</para>
/// </summary>
/// <param name="Gate">The model terminal (0-based) the glyph's gate pin binds to.</param>
/// <param name="Drain">The drain's terminal.</param>
/// <param name="Source">The source's terminal.</param>
/// <param name="Fourth">The fourth pin's terminal, or null for a three-terminal model.</param>
/// <param name="FourthRole"><see cref="FetPinRole.Thermal"/> or <see cref="FetPinRole.Bulk"/> when there is a
/// fourth; null otherwise.</param>
public sealed record FetPinBinding(int Gate, int Drain, int Source, int? Fourth, FetPinRole? FourthRole)
{
    /// <summary>
    /// The binding for a model whose terminals are <paramref name="terminalNames"/> (in the model's own order)
    /// and of which <paramref name="thermal"/> (0-based) are declared thermal; null when the names do not
    /// identify gate, drain and source, or leave a terminal no transistor pin can stand for.
    /// </summary>
    public static FetPinBinding? For(IReadOnlyList<string> terminalNames, IReadOnlyList<int> thermal)
    {
        ArgumentNullException.ThrowIfNull(terminalNames);
        thermal ??= [];
        int n = terminalNames.Count;
        if (n is < 3 or > 4) return null;

        int? gate = null, drain = null, source = null, fourth = null;
        FetPinRole? fourthRole = null;
        for (int i = 0; i < n; i++)
        {
            var role = thermal.Contains(i) ? FetPinRole.Thermal : RoleOf(terminalNames[i]);
            switch (role)
            {
                case FetPinRole.Gate   when gate   is null: gate   = i; break;
                case FetPinRole.Drain  when drain  is null: drain  = i; break;
                case FetPinRole.Source when source is null: source = i; break;
                case FetPinRole.Thermal or FetPinRole.Bulk when fourth is null:
                    fourth = i; fourthRole = role; break;
                // A second gate, an unnamed terminal, a second fourth: nothing to bind it to.
                default: return null;
            }
        }
        return gate is int g && drain is int d && source is int s
            ? new FetPinBinding(g, d, s, fourth, fourthRole)
            : null;
    }

    /// <summary>What a pin or terminal called <paramref name="name"/> is, or null when the name does not say.</summary>
    public static FetPinRole? RoleOf(string? name) => (name ?? "").Trim().ToLowerInvariant() switch
    {
        "g" or "gate"                        => FetPinRole.Gate,
        "d" or "drain"                       => FetPinRole.Drain,
        "s" or "source"                      => FetPinRole.Source,
        "t" or "dt" or "tj" or "thermal"     => FetPinRole.Thermal,
        "b" or "bulk"                        => FetPinRole.Bulk,
        _                                    => null,
    };

    /// <summary>The model terminal a glyph pin of <paramref name="role"/> stands for. A thermal or bulk pin
    /// takes the fourth terminal whichever it is — the glyph's fourth pin is the model's fourth.</summary>
    public int? TerminalFor(FetPinRole role) => role switch
    {
        FetPinRole.Gate   => Gate,
        FetPinRole.Drain  => Drain,
        FetPinRole.Source => Source,
        _                 => Fourth,
    };

    /// <summary>How many terminals this binding covers: three, or four with a fourth.</summary>
    public int Count => Fourth is null ? 3 : 4;
}
