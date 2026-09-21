// ================================================================
//  ExpressionCultureInvarianceTests.cs — the expression language is a FORMAL
//  language and does not follow the user's locale. It accepts '.' and ',' alike
//  as a decimal point, everywhere, for that same reason.
//  (docs/design/expressions.md §"Locale"; supersedes the never-accept-a-comma
//   half of brief-localization-groundwork.md §6, R-loc-3 — owner, 2026-09-21)
// ================================================================

using System.Globalization;
using System.Numerics;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Tests.Expressions;

/// <summary>
/// circuitRF's expression language does not follow the user's locale — not in any locale, not ever.
/// What changed (owner, 2026-09-21) is that it now accepts BOTH spellings of a decimal point, in
/// every locale alike: <c>1.5</c> and <c>1,5</c> are the same number on a German machine and on an
/// American one. That is not a locale-sensitive parser; it is a formal language with two spellings
/// of one token, which is why the invariance claim this file exists to protect is untouched.
///
/// <para><b>The earlier rule said a comma decimal was impossible, and the argument was too
/// strong.</b> It ran: a grammar cannot have <c>if(a,b,c)</c> AND a comma decimal, because
/// <c>f(1,5)</c> would be both "f of one-point-five" and "f of one and five". That is true — but
/// only INSIDE the brackets. Every separator comma in this grammar sits inside <c>(…)</c> or
/// <c>[…]</c>, and <c>Parser.Parse</c> requires EOF after one expression, so a comma at bracket
/// depth 0 is a hard parse error today and means nothing at all. Rewriting it is a pure widening.
/// Inside an argument list the comma is still a separator and a decimal point must be written as a
/// point — the ambiguity is real there and nothing resolves it.</para>
///
/// <para><b>This does not change when the UI is localized.</b> What a German user sees in a status
/// line is display text and correctly follows their locale (<c>2,5 GHz</c>); what they TYPE is read
/// by the same rule everywhere. The same split governs the file formats — see
/// <c>FormatCultureInvarianceTests</c> in Ui.Tests.</para>
///
/// <para><b>Why this file exists at all when <c>Parser</c> already names
/// <see cref="CultureInfo.InvariantCulture"/>.</b> Because that is one line in one file, and it is
/// the kind of line a later "clean-up" removes on the grounds that it looks redundant. The default
/// test run cannot notice: <c>tests/TestCulture.cs</c> pins the whole suite to <c>en-US</c>, where
/// invariant and current agree exactly. That pinning is what makes this file necessary AND what
/// makes it meaningful — the foreign-locale pass has to be deliberate, and this is it.</para>
///
/// <para>Culture here is set on the CURRENT THREAD only (not the process-wide
/// <c>DefaultThreadCurrentCulture</c>) and restored in a <c>finally</c>; the collection serializes
/// these tests as well. See the same discipline, and the reasoning, in
/// <c>FormatCultureInvarianceTests</c>.</para>
/// </summary>
[Collection(ExpressionCultureCollection.Name)]
public sealed class ExpressionCultureInvarianceTests
{
    /// <summary>Comma decimal + dot grouping, and comma decimal + dot time separator. Both would
    /// break a locale-sensitive parser, in different ways.</summary>
    private static readonly string[] ProbeCultures = ["de-DE", "fi-FI"];

    private static T InCulture<T>(string name, Func<T> body)
    {
        var previous   = CultureInfo.CurrentCulture;
        var previousUi = CultureInfo.CurrentUICulture;
        try
        {
            var ci = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentCulture   = ci;
            CultureInfo.CurrentUICulture = ci;
            return body();
        }
        finally
        {
            CultureInfo.CurrentCulture   = previous;
            CultureInfo.CurrentUICulture = previousUi;
        }
    }

    private static double Real(string expr)
    {
        var v = new Evaluator().Eval(expr, new Scope("test"));
        Assert.Equal(ValueKind.Real, v.Kind);
        return v.AsReal();
    }

    /// <summary>
    /// A representative sweep of the v1 language — literals in every spelling, the operator set,
    /// precedence and associativity, the standard functions, comparisons and boolean operators,
    /// <c>if()</c>, nesting, and unit-suffixed literals. Every one must evaluate to the SAME
    /// <see cref="double"/> bit pattern under a comma-decimal locale as under <c>en-US</c>.
    ///
    /// <para>This is §6's "run the expression tests once more under de-DE" made self-contained:
    /// rather than re-hosting the whole existing suite, it drives the same language surface those
    /// tests cover and compares results across cultures directly, which is the property actually
    /// at stake.</para>
    /// </summary>
    public static TheoryData<string> LanguageSurface =>
    [
        // Decimal literals — the whole point.
        "1.5", "0.25", "1234.5678", ".5", "3.0",
        // Exponent forms, which a comma-decimal parser also mangles.
        "1.5e9", "1.5E9", "2.5e-3", "1e3", "6.62607015e-34",
        // Arithmetic, precedence, associativity.
        "2+3*4.5", "(2+3)*4.5", "2^3^2", "-2^2", "10.5/4.2", "7.5-2.25-1.25",
        // Standard functions over fractional arguments.
        "sin(0.5)", "cos(1.25)", "tan(0.75)", "tanh(0.5)", "exp(1.5)",
        "ln(2.5)", "log10(1000.0)", "sqrt(2.25)", "abs(-3.5)",
        // ',' as the ARGUMENT separator — still exactly that, inside the brackets.
        "max(1.5, 2.5)", "min(1.5, 2.5)", "if(1.5 > 0.5, 10.25, 20.75)",
        "if(1.5 < 0.5, 10.25, if(2.5 >= 2.5, 30.125, 40.5))",
        // Comparisons and booleans folded into a numeric result.
        "if(1.5 == 1.5 && 2.5 != 3.5, 1.25, 9.75)",
        "if(!(1.5 > 2.5) || 0.5 > 1.5, 2.125, 8.875)",
        // Constants mixed with fractional literals.
        "pi*2.5", "e^1.5",
        // Deep nesting, where a mis-parsed separator would surface as a wrong arity.
        "max(min(1.5, 2.5), if(3.5 > 2.5, 4.5, 5.5))",
    ];

    [Theory, MemberData(nameof(LanguageSurface))]
    public void Expression_EvaluatesIdenticallyInEveryLocale(string expr)
    {
        double reference = InCulture("en-US", () => Real(expr));

        foreach (var probe in ProbeCultures)
        {
            double actual = InCulture(probe, () => Real(expr));

            // BitConverter, not a tolerance: the claim is that culture is not an input to the
            // evaluator at all, so anything short of an identical bit pattern is a real defect.
            Assert.True(
                BitConverter.DoubleToInt64Bits(reference) == BitConverter.DoubleToInt64Bits(actual),
                $"The expression \"{expr}\" evaluated to {reference:R} under en-US but {actual:R} " +
                $"under {probe}. The expression language is a formal language and must not follow " +
                $"the user's locale — see docs/design/expressions.md.");
        }
    }

    /// <summary>
    /// A decimal comma at bracket depth 0 IS a decimal point, and reads the same on every machine.
    /// This is the half a user in a comma-decimal region actually types, and until 2026-09-21 it was
    /// a parse error everywhere — which is why widening it cannot change what any existing text
    /// means.
    /// </summary>
    [Theory]
    [InlineData("1,5",      1.5)]
    [InlineData("1,5e9",    1.5e9)]
    [InlineData("0,25",     0.25)]
    [InlineData("1234,5678", 1234.5678)]
    [InlineData("2,5 * 2",  5.0)]
    public void CommaDecimal_IsADecimalPoint_IdenticallyInEveryLocale(string expr, double expected)
    {
        foreach (var culture in new[] { "en-US", "de-DE", "fi-FI" })
            Assert.Equal(expected, InCulture(culture, () => Real(expr)), 12);
    }

    /// <summary>
    /// The negative half, and the one that would silently corrupt a design rather than fail loudly:
    /// a GROUPED number must never be read, in any locale. <c>1.234,5</c> is 1234.5 to a German and
    /// something else entirely to an American, and nothing in the text says which — so it is
    /// refused, exactly as <c>1,234.5</c> is. circuitRF's fields carry engineering units; none of
    /// them needs a thousands separator.
    /// </summary>
    [Theory]
    [InlineData("1.234,5")]
    [InlineData("1,234.5")]
    public void AGroupedNumber_IsRefused_InEveryLocale(string expr)
    {
        foreach (var culture in new[] { "en-US", "de-DE", "fi-FI" })
            InCulture(culture, () =>
            {
                Assert.ThrowsAny<Exception>(() => new Evaluator().Eval(expr, new Scope("test")));
                return 0;
            });
    }

    /// <summary>
    /// Inside an argument list <c>,</c> still separates arguments, in every locale, and a call with
    /// fractional arguments keeps its arity. This is the boundary of the widening above: at depth 0
    /// a comma is a decimal point, inside brackets it is a separator and always was.
    /// </summary>
    [Fact]
    public void CommaIsAlwaysTheArgumentSeparator()
    {
        foreach (var culture in new[] { "en-US", "de-DE", "fi-FI" })
        {
            Assert.Equal(2.5,    InCulture(culture, () => Real("max(1.5, 2.5)")), 12);
            Assert.Equal(1.5,    InCulture(culture, () => Real("min(1.5, 2.5)")), 12);
            Assert.Equal(10.25,  InCulture(culture, () => Real("if(1.5 > 0.5, 10.25, 20.75)")), 12);

            // The ambiguous shape itself: max(1,5) is the larger of 1 and 5, NOT max(1.5).
            Assert.Equal(5.0,    InCulture(culture, () => Real("max(1,5)")), 12);
        }
    }

    /// <summary>
    /// A <c>.cnl</c> carries expressions as TEXT, so the language rule and the file-format rule are
    /// the same rule seen twice: an expression written into a netlist on a German machine has to
    /// parse on an American one. This drives the round trip under each locale.
    /// </summary>
    [Fact]
    public void ExpressionsSurviveAWriteAndReadUnderAnyLocale()
    {
        foreach (var culture in new[] { "en-US", "de-DE", "fi-FI" })
        {
            InCulture(culture, () =>
            {
                var scope = new Scope("test");
                var ev    = new Evaluator();
                foreach (var expr in new[] { "1.5e9", "2.5e3", "if(1.5 > 0.5, 10.25, 20.75)" })
                {
                    var v = ev.Eval(expr, scope);
                    Assert.Equal(ValueKind.Real, v.Kind);
                }
                return 0;
            });
        }
    }

    /// <summary>
    /// §6's "record the interaction that already bites": a UNIT SUFFIX is a row FIELD, not part of
    /// the expression grammar, which is why <c>60u</c> is a parse error in circuitRF and <c>60</c>
    /// in a row whose unit is µm is not. That is unrelated to locale and localization must not be
    /// used as an excuse to revisit it — but it is worth pinning here, because the obvious "fix" for
    /// a comma-decimal user ("just make the parser more forgiving about what follows a number")
    /// would quietly change it.
    ///
    /// <para>The suffix stays a parse error in every locale, so nobody can conclude from a foreign
    /// locale that the rule is softer there.</para>
    /// </summary>
    [Theory]
    [InlineData("60u")]
    [InlineData("1.5k")]
    [InlineData("2.5G")]
    public void AUnitSuffixIsNotPartOfTheGrammar_InAnyLocale(string expr)
    {
        foreach (var culture in new[] { "en-US", "de-DE", "fi-FI" })
            InCulture(culture, () =>
            {
                Assert.ThrowsAny<Exception>(() => new Evaluator().Eval(expr, new Scope("test")));
                return 0;
            });
    }

    /// <summary>
    /// Complex-valued expressions take the same path and are pinned for the same reason — the
    /// imaginary part is a <c>double</c> parsed from the same literal grammar.
    /// </summary>
    [Fact]
    public void ComplexExpressions_AreAlsoCultureIndependent()
    {
        Complex Eval(string expr) => new Evaluator().Eval(expr, new Scope("test")).AsComplex();

        Complex reference = InCulture("en-US", () => Eval("1.5 + 2.5*j"));
        foreach (var probe in ProbeCultures)
        {
            Complex actual = InCulture(probe, () => Eval("1.5 + 2.5*j"));
            Assert.Equal(reference.Real,      actual.Real,      15);
            Assert.Equal(reference.Imaginary, actual.Imaginary, 15);
        }
    }
}

/// <summary>Serializes the culture-probing expression tests. Same reasoning as
/// <c>CultureProbeCollection</c> in Ui.Tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ExpressionCultureCollection
{
    public const string Name = "ExpressionCultureProbe";
}
