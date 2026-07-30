using System.IO;
using System.Linq;
using CircuitRF.Ui.Layout;
using CircuitRF.Ui.Layout.PCells;
using CircuitRF.Ui.Schematic;
using Xunit;

namespace CircuitRF.Ui.Tests;

/// <summary>
/// docs/sonnet-briefs/brief-L5-followups-2.md §4/R-L5g-9, gate 8: generated cells are NEVER shown in
/// the Project Tree, in any form — not as individual peers (the original, never-shipped naive
/// approach) and not even as one collapsed group node (R-L5-3's original decision, which this
/// supersedes: a group node still let a user browse into a "cache," which R-L5g-9 explicitly rules
/// out — "treat the folder as infrastructure, not content"). This file previously asserted the
/// collapsed-group behavior (<c>FortyGeneratedCells_CollapseIntoOneGroupNode_NotFortyPeers</c>); that
/// test is replaced, not merely extended, because the two behaviors are mutually exclusive.
/// </summary>
public sealed class GeneratedCellsTreeGroupingTests : IDisposable
{
    private readonly string _root;

    public GeneratedCellsTreeGroupingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "crf-gencell-tree-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void NoGeneratedCells_NoGeneratedCellsFolderNodeAtAll()
    {
        var root = WorkspaceScanner.Scan(_root);
        Assert.DoesNotContain(root.Children, c =>
            c.Name.Contains("Generated", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FortyGeneratedCells_NeverAppearInTheTree_NotAsPeersAndNotAsAGroup()
    {
        for (int i = 0; i < 40; i++)
        {
            var parms = new System.Collections.Generic.Dictionary<string, double> { ["W"] = 0.001 * (i + 1), ["L"] = 0.01 };
            GeneratedCellStore.GetOrCreate(_root, "MLIN", parms, null, null, PCellLayerSelection.Default);
        }

        var root = WorkspaceScanner.Scan(_root);

        // The reserved folder itself is not scanned as an ordinary UserFolder peer...
        Assert.DoesNotContain(root.Children, c => c.Kind == NodeKind.UserFolder && c.Name == GeneratedCellStore.ReservedFolderName);
        // ...nor is any of the 40 generated cells inside it surfaced as a top-level Cell peer...
        Assert.DoesNotContain(root.Children, c => c.Kind == NodeKind.Cell);
        // ...nor is there any synthetic group node standing in for them, at any name.
        Assert.DoesNotContain(root.Children, c => c.Name.Contains("Generated", StringComparison.OrdinalIgnoreCase));

        // The tree is otherwise unaffected — nothing else got swallowed by the exclusion.
        Assert.Empty(root.Children);
    }
}
