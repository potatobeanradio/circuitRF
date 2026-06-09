using CircuitRF.Ui.Schematic;

namespace CircuitRF.Ui.Tests;

public class NameValidatorTests
{
    // ── Valid names ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData("AmpStage")]
    [InlineData("amp_v1")]
    [InlineData("MyCell123")]
    [InlineData("resistor")]
    [InlineData("R")]
    [InlineData("X")]
    [InlineData("a b")]            // space in the middle is fine
    [InlineData("cell.v2")]        // dot in the middle is fine
    [InlineData("COMPONENT")]
    [InlineData("com10")]          // COM10 is not reserved (only COM1-COM9)
    [InlineData("lpt10")]          // LPT10 is not reserved
    [InlineData("con_backup")]     // not a reserved stem (stem = "con_backup")
    [InlineData("normal.con")]     // stem = "normal" — not reserved
    public void IsValid_ReturnsTrue_ForValidNames(string name)
    {
        Assert.True(NameValidator.IsValid(name));
        Assert.Null(NameValidator.Validate(name));
    }

    // ── Empty / whitespace ────────────────────────────────────────────────────

    [Fact]
    public void Validate_RejectsEmpty()
    {
        var reason = NameValidator.Validate("");
        Assert.NotNull(reason);
        Assert.Contains("empty", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData(" ")]
    public void Validate_RejectsWhitespaceOnly(string name)
    {
        var reason = NameValidator.Validate(name);
        Assert.NotNull(reason);
    }

    // ── Disallowed characters ─────────────────────────────────────────────────

    [Theory]
    [InlineData("<cell>")]
    [InlineData("cell<")]
    [InlineData("cell>")]
    [InlineData("cell:name")]
    [InlineData("cell\"name")]
    [InlineData("cell/name")]
    [InlineData("cell\\name")]
    [InlineData("cell|name")]
    [InlineData("cell?")]
    [InlineData("cell*")]
    public void Validate_RejectsDisallowedCharacters(string name)
    {
        var reason = NameValidator.Validate(name);
        Assert.NotNull(reason);
        Assert.Contains("disallowed character", reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── Control characters ────────────────────────────────────────────────────

    [Theory]
    [InlineData("\x00")]
    [InlineData("\x01")]
    [InlineData("\x1F")]
    [InlineData("cell\x07name")]
    [InlineData("tab\x09here")]
    public void Validate_RejectsControlCharacters(string name)
    {
        var reason = NameValidator.Validate(name);
        Assert.NotNull(reason);
        Assert.Contains("control character", reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── Trailing space or dot ─────────────────────────────────────────────────

    [Theory]
    [InlineData("cell ")]
    [InlineData("amp ")]
    [InlineData("name   ")]
    public void Validate_RejectsTrailingSpace(string name)
    {
        var reason = NameValidator.Validate(name);
        Assert.NotNull(reason);
        Assert.Contains("space or dot", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("cell.")]
    [InlineData("amp.")]
    [InlineData("name..")]
    public void Validate_RejectsTrailingDot(string name)
    {
        var reason = NameValidator.Validate(name);
        Assert.NotNull(reason);
        Assert.Contains("space or dot", reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── Windows reserved names ────────────────────────────────────────────────

    [Theory]
    [InlineData("CON")]
    [InlineData("con")]
    [InlineData("Con")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("NUL")]
    [InlineData("COM1")]
    [InlineData("COM9")]
    [InlineData("com1")]
    [InlineData("LPT1")]
    [InlineData("LPT9")]
    [InlineData("lpt1")]
    public void Validate_RejectsWindowsReservedNames(string name)
    {
        var reason = NameValidator.Validate(name);
        Assert.NotNull(reason);
        Assert.Contains("reserved", reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("CON.txt")]
    [InlineData("NUL.log")]
    [InlineData("com1.csym")]
    [InlineData("LPT9.data")]
    public void Validate_RejectsWindowsReservedNamesWithExtension(string name)
    {
        var reason = NameValidator.Validate(name);
        Assert.NotNull(reason);
        Assert.Contains("reserved", reason, StringComparison.OrdinalIgnoreCase);
    }

    // ── IsValid mirrors Validate ──────────────────────────────────────────────

    [Fact]
    public void IsValid_ReturnsFalse_WhenValidateReturnsReason()
    {
        Assert.False(NameValidator.IsValid(""));
        Assert.False(NameValidator.IsValid("CON"));
        Assert.False(NameValidator.IsValid("bad*name"));
        Assert.False(NameValidator.IsValid("trailing."));
    }
}
