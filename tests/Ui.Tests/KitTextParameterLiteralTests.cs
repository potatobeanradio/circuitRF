using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// A kit's fixed parameters are literal TEXT, but a <c>ParameterAssignment</c> is EVALUATED. Emitted
/// verbatim, an ordinary folder name is read as a variable and elaboration fails with
/// "Unresolved name 'Kit_..._Data' in scope 'global'" — reported from a real run.
/// </summary>
public sealed class KitTextParameterLiteralTests
{
    [Theory]
    [InlineData("KIT_PART_Data", "\"KIT_PART_Data\"")]  // the reported failure
    [InlineData("PROC1_15p6.mdl",      "\"PROC1_15p6.mdl\"")]        // a model data file
    [InlineData("Sub/Data",            "\"Sub/Data\"")]              // a path
    [InlineData("PROC1",               "\"PROC1\"")]                 // a bare token
    public void ABareTextValue_BecomesAStringLiteral(string value, string expected)
        => Assert.Equal(expected, NetExtractor.AsLiteralExpression(value));

    [Theory]
    // A number is a numeric literal; quoting it would silently change its kind to string.
    [InlineData("26")]
    [InlineData("-1")]
    [InlineData("15.6")]
    [InlineData("1.0e-6")]
    public void ANumber_IsLeftAlone(string value)
        => Assert.Equal(value, NetExtractor.AsLiteralExpression(value));

    [Theory]
    // Already a literal, or unquotable without an escape syntax that does not exist here. Reporting
    // a wrong value beats inventing one.
    [InlineData("\"already quoted\"")]
    [InlineData("has \" inside")]
    [InlineData("")]
    public void AnAlreadyLiteralOrUnquotableValue_IsLeftAlone(string value)
        => Assert.Equal(value, NetExtractor.AsLiteralExpression(value));
}
