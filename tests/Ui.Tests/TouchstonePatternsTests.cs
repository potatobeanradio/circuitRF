// ================================================================
//  TouchstonePatternsTests.cs — every N-port Touchstone picker offers every port count (an .s20p included), and none
//  lists port counts by hand, which is how the lists stopped at .s4p or .s12p.
// ================================================================

using System;
using System.IO;
using System.Linq;
using CircuitRF.Ui.Controls;
using Xunit;

namespace CircuitRF.Ui.Tests;

public sealed class TouchstonePatternsTests
{
    [Fact]
    public void EveryPortCount_IsOffered_AndNoPickerListsThemByHand()
    {
        foreach (string p in new[] { "*.s1p", "*.s20p", "*.s99p", "*.snp", "*.S20P" })
            Assert.Contains(p, TouchstonePatterns.Snp);

        string root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "circuitRF.slnx"))) root = Path.GetDirectoryName(root)!;
        var byHand = Directory.EnumerateFiles(Path.Combine(root, "src", "Ui"), "*.cs", SearchOption.AllDirectories)
            .Where(f => File.ReadAllText(f).Contains("\"*.s3p\"", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetRelativePath(root, f));
        Assert.Empty(byHand);
    }
}
