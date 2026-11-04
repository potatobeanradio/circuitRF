using System.Numerics;
using System.Text;
using CircuitRF.Core;
using CircuitRF.Core.Devices;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Netlist;

namespace CircuitRF.Design.VendorLibraries.ModelLibrary.Protect;

/// <summary>
/// Brief vendorlib-19 D2: which side of the S-parameter block each flattened element lands on, decided by the engine and
/// not by a table. The cell is elaborated once in a bench of its own, and each element's elaborated components are read:
/// every one linear and none a source or a probe puts the element inside; every one a device (a model reporting
/// <see cref="ModelKind.Nonlinear"/>), an independent source or a probe keeps it outside. A linear model added to
/// circuitRF later therefore goes into the block with no change here.
/// </summary>
public static class ProtectClassify
{
    /// <summary>The bench instance the cell is placed as, and the prefix of every one of its components' paths.</summary>
    public const string BenchInstance = "ENCX1";

    /// <summary>The engine reference of a placed SPICE subcircuit.</summary>
    private const string SpiceSubcircuit = "SpiceModel";

    /// <summary>The bench net for pin <paramref name="k"/> (1-based).</summary>
    public static string PinNet(int k) => $"__enc_n{k}";

    /// <summary>The cell placed alone, each pin on its own bench net: the netlist both the partition and the block's
    /// sampling read. Throws with the elaborator's own message when the cell does not elaborate.</summary>
    public static ElaboratedNetlist Elaborate(string cnlText, string cell, int pins, string? baseDirectory)
    {
        var sb = new StringBuilder(cnlText).Append('\n').Append(cell).Append(':').Append(BenchInstance);
        for (int k = 1; k <= pins; k++) sb.Append(' ').Append(PinNet(k));
        sb.Append('\n');
        var (lib, tb) = new CnlReader().Read(sb.ToString(), "encapsulate", baseDirectory);
        return new Elaborator(lib) { BaseDirectory = baseDirectory }.Elaborate(tb);
    }

    /// <summary>The elaborated components an element built: its own path, or below it for one that expands (a SPICE
    /// subcircuit).</summary>
    public static IEnumerable<ElaboratedComponent> ComponentsOf(ElaboratedNetlist nl, string path)
    {
        string own = BenchInstance + "." + path;
        return nl.Components.Where(c => c.InstancePath == own || c.InstancePath.StartsWith(own + ".", StringComparison.Ordinal));
    }

    /// <summary>A component the block cannot hold: a device, an independent source, or a probe.</summary>
    public static bool StaysOutside(ElaboratedComponent c)
        => c.IsNonlinear || c.Model is SeriesProbeModelBase or IDriveScalable || DrivesSomething(c);

    internal static IReadOnlyDictionary<string, ProtectSide> Sides(string cnlText, string cell, IReadOnlyList<string> pins, IReadOnlyList<ProtectElement> elements,
                                                                   string? baseDirectory, List<string> refusals,
                                                                   Dictionary<string, string[]> terminals)
    {
        var sides = new Dictionary<string, ProtectSide>(StringComparer.Ordinal);
        ElaboratedNetlist nl;
        try { nl = Elaborate(cnlText, cell, pins.Count, baseDirectory); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            refusals.Add($"The cell could not be elaborated to sort its elements: {ex.Message}");
            return sides;
        }
        using (nl)
            foreach (var e in elements)
            {
                var comps = ComponentsOf(nl, e.Path).ToList();
                // Nothing elaborated (a Term inside a placed cell, which every run ignores): left as written.
                if (comps.Count == 0) { sides[e.Path] = ProtectSide.Outside; continue; }
                // Its terminal names, from the component that sits on the element's own nets in order (the one component,
                // or a composed device's core when it is wired straight to them).
                int[] own = [.. e.Nets.Select(n => NodeOf(nl, pins, n))];
                if (comps.FirstOrDefault(c => c.Nodes.Length >= own.Length && c.Nodes.Take(own.Length).SequenceEqual(own)) is { } named
                    && named.Model.TerminalNames.Length >= own.Length)
                    terminals[e.Path] = named.Model.TerminalNames;
                int outside = comps.Count(StaysOutside);
                if (outside == 0) { sides[e.Path] = ProtectSide.Inside; continue; }
                // A device is one instance however many components its model builds (a composed FET's core and its own
                // parasitics): it stays outside whole. A SPICE subcircuit is the exception D2 names — its primitives are
                // classified one by one — and the block cannot take part of one placed instance.
                if (outside < comps.Count && e.Type.Equals(SpiceSubcircuit, StringComparison.OrdinalIgnoreCase))
                {
                    var devices = comps.Where(StaysOutside).Select(c => c.InstancePath[(BenchInstance.Length + 1)..]);
                    refusals.Add($"{e.Path} is a SPICE subcircuit holding linear elements and devices ({string.Join(", ", devices)}). Its linear " +
                                 "elements belong in the S-parameter block, and part of one placed subcircuit cannot stay outside. Place its " +
                                 "devices and its linear elements separately, or use Compile netlist.");
                    continue;
                }
                sides[e.Path] = ProtectSide.Outside;
            }
        return sides;
    }

    /// <summary>A flattened net's node in the bench: a pin is on the bench net it was placed on, ground is 0, any other
    /// net carries the bench instance's path.</summary>
    public static int NodeOf(ElaboratedNetlist nl, IReadOnlyList<string> pins, string net)
    {
        if (net == "0") return 0;
        int k = pins.ToList().IndexOf(net);
        string name = k >= 0 ? PinNet(k + 1) : BenchInstance + "." + net;
        return nl.Nodes.TryGetIndex(name, out int idx) ? idx : -1;
    }

    /// <summary>An independent source: a linear model that writes a non-zero right-hand side, at DC or at RF. A 0 V
    /// source writes none and is the short it stands for.</summary>
    private static bool DrivesSomething(ElaboratedComponent c)
    {
        foreach (double omega in (ReadOnlySpan<double>)[0.0, 2 * Math.PI * 1e9])
        {
            var probe = new SourceProbe();
            try { c.Stamp(probe, omega); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return false; }
            if (probe.Drives) return true;
        }
        return false;
    }

    /// <summary>A stamping context that records only whether anything reached the right-hand side.</summary>
    private sealed class SourceProbe : IMnaContext
    {
        private int _branches;
        public bool Drives { get; private set; }
        public void AddAdmittance(int nodeA, int nodeB, Complex y) { }
        public void AddBlockAdmittance(int rowNode, int colNode, Complex y) { }
        public int AddBranch() => _branches++;
        public void AddBranchCurrent(int branch, int nodeFrom, int nodeTo) { }
        public void AddConstraint(int branch, int node, Complex coeff) { }
        public void AddNodeBranchCoupling(int node, int branch, Complex coeff) { }
        public void AddBranchConstraint(int branch, int otherBranch, Complex coeff) { }
        public void AddCurrentInjection(int node, Complex j) { if (j != Complex.Zero) Drives = true; }
        public void AddSourceValue(int branch, Complex value) { if (value != Complex.Zero) Drives = true; }
    }
}
