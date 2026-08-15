using System.Text;
using CircuitRF.Core.Devices;

namespace CircuitRF.Harmonica;

/// <summary>
/// R7B §3.4 — turns a resolved <see cref="DutSpec.Parameters"/> map back into the SDD editor's
/// verbatim-text SHAPE, for a document that carries no <see cref="DutSpec.SddText"/> at all (a
/// pre-R7B <c>.charm</c>, or a <see cref="CircuitModel"/> built directly in code without ever going
/// through the dialog). Variables first, then equations, each group sorted by name — there is no
/// original authored order to recover, so this only needs to be SENSIBLE, not faithful.
///
/// <para>Framework-free and living beside <see cref="CircuitModel"/> rather than in
/// <c>src/Ui/Harmonica/HarmonicaSddText.cs</c> (the validating parser) because <see cref="CharmIo"/>
/// needs it too, and <c>src/Harmonica</c> may not reference the UI-framework assembly that
/// <c>HarmonicaSddText</c> lives in.</para>
/// </summary>
public static class SddTextIo
{
    public static string Reconstruct(IReadOnlyDictionary<string, string> parameters)
    {
        var vars = parameters.Where(p => !ComponentModelFactory.IsSddEquationName(p.Key))
                              .OrderBy(p => p.Key, StringComparer.Ordinal)
                              .ToList();
        var eqs = parameters.Where(p => ComponentModelFactory.IsSddEquationName(p.Key))
                             .OrderBy(p => p.Key, StringComparer.Ordinal)
                             .ToList();

        var sb = new StringBuilder();
        foreach (var v in vars) sb.Append(v.Key).Append(" = ").Append(v.Value).Append('\n');
        if (vars.Count > 0 && eqs.Count > 0) sb.Append('\n');
        foreach (var e in eqs) sb.Append(e.Key).Append(" = ").Append(e.Value).Append('\n');

        return sb.ToString().TrimEnd('\n');
    }
}
