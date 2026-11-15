// brief-paa-8b R-paa8b-1 — whether a change of tuned values reaches only the weights of a Feed-mode Phased Antenna Array, so an
// evaluation may re-weigh the last elaborated netlist (Elaborator.Reweigh) instead of elaborating the bench again.
//
// A changed value reaches only the weights when every expression that reads it — directly, or through another global variable or a
// user function that reads it — is an Amp, Phase or Delay field (or an [n] override of one) of a Feed-mode array at the top of the bench,
// or a measurement. Everything else that can hold an expression is a reader that is not: a global variable's own expression is followed
// rather than refused, but a cell's parameter, variable or instance, another instance's parameter (the array's PhaseBits and AttenStep
// among them) and an analysis directive each send the evaluation back to a full elaboration. Names are read as the expression engine's
// tokenizer reads them, never by substring, so `theta0` is not `theta01`.

using CircuitRF.Core.Design;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Expressions;
using CircuitRF.Core.Netlist;

namespace CircuitRF.Core.Elaboration;

/// <summary>R-paa8b-1 — the read-set check a re-weigh needs.</summary>
public static class PhasedArrayReweigh
{
    /// <summary>
    /// Null when every name in <paramref name="changed"/> is a global variable that reaches only the weights of top-level Feed-mode arrays
    /// (and measurements); otherwise the reader that does not, in a sentence.
    /// </summary>
    public static string? Refusal(TestBench tb, Library lib, IReadOnlyCollection<string> changed)
    {
        if (changed.Count == 0) return "nothing changed";
        var globals = tb.GlobalVariables.Select(v => v.Name).ToHashSet(StringComparer.Ordinal);
        if (changed.FirstOrDefault(c => !globals.Contains(c)) is { } other) return $"'{other}' is not a global variable";

        // the changed names, and every global variable and user function that reads one
        var reach = new HashSet<string>(changed, StringComparer.Ordinal);
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var v in tb.GlobalVariables)
                if (!reach.Contains(v.Name) && Reads(v.Expression, reach)) grew |= reach.Add(v.Name);
            foreach (var f in tb.Functions)
                if (!reach.Contains(f.Name) && Reads(f.Body, reach, f.Parameters)) grew |= reach.Add(f.Name);
        }

        foreach (var inst in tb.Instances)
        {
            bool feedArray = inst.Reference.Equals(PhasedArrayModel.TypeName, StringComparison.OrdinalIgnoreCase) && PhasedArrayModel.IsFeed(inst.Overrides);
            foreach (var ov in inst.Overrides)
            {
                if (!Reads(ov.Expression, reach)) continue;
                if (feedArray && PhasedArrayModel.IsElementParameter(ov.Name, out _, out _)) continue;
                return $"{inst.InstanceName}.{ov.Name}={ov.Expression} reads it";
            }
        }
        foreach (var cell in lib.Cells)
        {
            foreach (var p in cell.Parameters) if (Reads(p.DefaultExpression, reach)) return $"cell '{cell.Name}''s parameter {p.Name} reads it";
            foreach (var v in cell.Variables) if (Reads(v.Expression, reach)) return $"cell '{cell.Name}''s variable {v.Name} reads it";
            foreach (var inst in cell.Instances)
                foreach (var ov in inst.Overrides)
                    if (Reads(ov.Expression, reach)) return $"{inst.InstanceName}.{ov.Name} in cell '{cell.Name}' reads it";
        }
        foreach (var a in tb.Analyses)
            if (DirectiveReads(CnlWriter.FormatAnalysis(a), reach)) return $"analysis '{a.Name}' reads it";
        foreach (var raw in tb.RawDirectives)
            if (!raw.Kind.Equals("measure", StringComparison.OrdinalIgnoreCase) && DirectiveReads(raw.RawLine, reach)) return $"the line '{raw.RawLine}' reads it";
        return null;
    }

    /// <summary>A directive line as the writer spells it: each <c>key=value</c>'s value read as an expression; a value the tokenizer cannot
    /// read (a file path) is read word by word, so it can only refuse more, never less.</summary>
    private static bool DirectiveReads(string line, HashSet<string> names)
    {
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(line, "(\\w[\\w\\[\\]]*)=(\"[^\"]*\"|\\S+)"))
        {
            string value = m.Groups[2].Value.Trim('"');
            if (string.IsNullOrWhiteSpace(value)) continue;
            try
            {
                if (new Tokenizer(value).Tokenize().Any(t => t.Kind == TokenKind.Identifier && names.Contains(t.Text))) return true;
            }
            catch (Exception)
            {
                if (System.Text.RegularExpressions.Regex.Matches(value, "[A-Za-z_]\\w*").Any(w => names.Contains(w.Value))) return true;
            }
        }
        return false;
    }

    private static bool Reads(string? text, HashSet<string> names, IReadOnlyCollection<string>? own = null)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            foreach (var t in new Tokenizer(text).Tokenize())
                if (t.Kind == TokenKind.Identifier && names.Contains(t.Text) && (own is null || !own.Contains(t.Text))) return true;
            return false;
        }
        catch (Exception) { return true; }   // a text the tokenizer cannot read is taken to read everything
    }
}
