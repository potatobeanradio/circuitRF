using System.Text.RegularExpressions;
using Xunit;

namespace CircuitRF.Ui.Tests.Em;

/// <summary>
/// Owner report, 2026-09-11: a lot of the text inside the EM Setup panel's comboboxes does not sit
/// in the vertical middle of the control.
///
/// <para>It is NOT <c>VerticalContentAlignment</c> — the Fluent ComboBox theme already template-binds
/// that and it already says Center, which is why the one combobox here that had been given an
/// explicit Center (the core cap) was no better than its neighbours. It is the theme's own default
/// PADDING, <c>12,1,0,3</c>: an optical nudge sized for a 32 px control carrying 14 px text,
/// and at this application's compact density (24 px, 12 px text) it simply pushes the line up.</para>
///
/// <para>Measured on the live <c>EmSetupEditorView</c> before and after rather than assumed. Before:
/// the five combos left on the theme default rendered their text 3 px from the top and 6 px from the
/// bottom — 1.5 px high — while the TextBox beside them was exact, and the five that had been given a
/// hand-written <c>4,3</c> were a rounded half-pixel the other way. After: all eleven sit at 4 / 5,
/// which is as close as integer layout gets (a 15 px line in a 24 px box wants 4.5).</para>
///
/// <para>A source scan because the assertion is about the STYLE, and the rendered geometry it
/// produces cannot be measured without an Avalonia app host — the same fallback every other
/// view-level claim in this suite uses.</para>
/// </summary>
public sealed class ComboBoxTextIsVerticallyCentredTests
{
    private static string RepoFile(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "circuitrf.slnx")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!.FullName, relative));
    }

    /// <summary>Top and bottom of a XAML Thickness, whichever of the 1/2/4-number spellings it uses.</summary>
    private static (double Top, double Bottom) Vertical(string thickness)
    {
        double[] n = [.. thickness.Split(',').Select(p => double.Parse(p.Trim(),
                         System.Globalization.CultureInfo.InvariantCulture))];
        return n.Length switch
        {
            1 => (n[0], n[0]),
            2 => (n[1], n[1]),
            4 => (n[1], n[3]),
            _ => throw new FormatException($"'{thickness}' is not a Thickness"),
        };
    }

    [Fact]
    public void TheApplicationWideComboBoxPadding_IsVerticallyEven()
    {
        string styles = RepoFile("src/Ui/Styles/CircuitRfStyles.axaml");
        int at = styles.IndexOf("<Style Selector=\"ComboBox\">", StringComparison.Ordinal);
        Assert.True(at > 0,
            "the application-wide ComboBox style is gone — the theme's own 12,1,0,3 is back, and with "
          + "it the 1.5 px lift on every combobox that does not override Padding by hand.");

        string style = styles[at..styles.IndexOf("</Style>", at, StringComparison.Ordinal)];
        var pad = Regex.Match(style, @"Property=""Padding"" Value=""([^""]+)""");
        Assert.True(pad.Success, "the global ComboBox style no longer sets Padding");

        var (top, bottom) = Vertical(pad.Groups[1].Value);
        Assert.Equal(bottom, top);
        Assert.Equal(0, top % 2);   // odd halves round apart; see the class remarks
    }

    [Fact]
    public void EveryComboBoxPadding_InTheEmSetupPanel_IsVerticallyEven()
    {
        string xaml = RepoFile("src/Ui/Views/Layout/EmSetupEditorView.axaml");

        // Both spellings: the ComboBox.cell style's setter, and any Padding written on a ComboBox
        // element itself. A combobox that states no Padding at all is fine — it takes the
        // application-wide one the test above holds.
        var setters = Regex.Matches(
            xaml, @"<Style Selector=""ComboBox[^""]*"">(?<body>.*?)</Style>", RegexOptions.Singleline);
        var elements = Regex.Matches(
            xaml, @"<ComboBox\b(?<body>[^>]*)>", RegexOptions.Singleline);

        int checkedCount = 0;
        foreach (var m in setters.Concat(elements))
        {
            var pad = Regex.Match(m.Groups["body"].Value, @"Padding(?:="")?""?\s*(?:Value="")?([\d.,\s]+)""");
            if (!pad.Success) continue;
            var (top, bottom) = Vertical(pad.Groups[1].Value);
            Assert.Equal(bottom, top);
            Assert.Equal(0, top % 2);
            checkedCount++;
        }

        Assert.True(checkedCount > 0, "no ComboBox padding was found to check — the scan has gone stale");
    }
}
