using CircuitRF.Core.Design;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Netlist.Spice;

/// <summary>
/// One thing the reader could not use, named so it can be reported rather than guessed at.
/// </summary>
/// <param name="File">
/// Which file the line is in. Not decoration: an included file's line 12 and the including file's
/// line 12 are different lines, and a note that cannot tell them apart sends the reader of it to the
/// wrong place.
/// </param>
/// <param name="Line">1-based line number within that file.</param>
public sealed record SpiceNetlistNote(string File, int Line, string Message)
{
    public override string ToString() => $"{File}:{Line}: {Message}";
}

/// <summary>
/// A named parameter set that instances reference by name — a <c>.model</c> card.
///
/// <para>circuitRF has no design-layer type for this and is not given one here. A card is not a
/// circuit: it is the parameter block of whatever device eventually implements
/// <paramref name="ModelType"/>, which may be a built-in, a compiled model behind a provider, or
/// nothing yet. Binding it is the job of whatever supplies that device; carrying it faithfully is
/// this reader's.</para>
/// </summary>
/// <param name="Name">The name instances reference. Compared case-insensitively, as the dialect does.</param>
/// <param name="ModelType">What kind of device the card is for, verbatim and uninterpreted.</param>
/// <param name="Parameters">The card's own parameters, values already rewritten into circuitRF's grammar.</param>
public sealed record SpiceModelCard(
    string                              Name,
    string                              ModelType,
    IReadOnlyDictionary<string, string> Parameters);

/// <summary>What a netlist in the SPICE dialect was found to contain.</summary>
/// <param name="Library">Every subcircuit that could be read, as an ordinary circuitRF cell.</param>
/// <param name="Notes">Everything skipped or unrecognised, by file and line. Never silently dropped.</param>
/// <param name="Variables">
/// <c>.param</c> declarations outside any subcircuit. Cells reference them by bare name, so a
/// definition read without them does not resolve.
/// </param>
/// <param name="ModelCards">Every <c>.model</c> card, in the order encountered.</param>
/// <param name="IncompleteCells">
/// Cells holding something the reader could not read. This is the honest signal that circuitRF
/// cannot build them — not that a type is unfamiliar, since an unfamiliar type may well be a device
/// a provider supplies, but that a line of the definition ITSELF was skipped, so what is left is not
/// the circuit the file wrote.
/// </param>
/// <param name="Statistics">
/// Every statistical distribution that was reduced to its nominal value. Empty for the ordinary
/// case; non-empty means the numbers are a nominal-corner run and the caller must say so.
/// </param>
/// <param name="FilesRead">Every file that contributed, including those pulled in by inclusion.</param>
public sealed record SpiceNetlistResult(
    Library                             Library,
    IReadOnlyList<SpiceNetlistNote>     Notes,
    IReadOnlyList<Variable>             Variables,
    IReadOnlyList<SpiceModelCard>       ModelCards,
    IReadOnlySet<string>                IncompleteCells,
    IReadOnlyList<SpiceStatisticalUse>  Statistics,
    IReadOnlyList<string>               FilesRead)
{
    /// <summary>
    /// Expression functions declared outside any subcircuit. Cells call them by bare name, so a
    /// definition read without them does not resolve.
    /// </summary>
    public IReadOnlyList<UserFunction> Functions { get; init; } = [];
}

/// <summary>Raised when the file's structure is broken in a way that cannot be read past.</summary>
public sealed class SpiceNetlistException(string file, int line, string message)
    : Exception($"{file}:{line}: {message}")
{
    public string File { get; } = file;
    public int    Line { get; } = line;
}
