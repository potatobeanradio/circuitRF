namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Generates a default symbol for a cell that has no symbol view.
/// Framework-free — no Avalonia or SkiaSharp dependency.
/// Implements the §9 layout rules from docs/design/symbol-editor.md:
/// outer+inner rectangles, odd ports LEFT / even ports RIGHT (N=3 special),
/// short stub leads, pin tips on the P=100 connection grid.
/// </summary>
internal static class AutoSymbolGenerator
{
    private const double HalfW       = 80;
    private const double LeadLength  = 120;
    private const double PinX        = HalfW + LeadLength;   // 200 — on P grid
    private const double InnerInset  = 10;
    private const double PortSpacing = 200;

    /// <summary>
    /// Port labels are the SAME size as every other dynamic symbol's — SnP's "1".."N", SDD/ZPort's
    /// "1+"/"2−" — by REFERENCING that constant rather than restating it (owner, 2026-08-17: the
    /// auto-generated symbol's pins "are smaller than the font size for SNP components"). This file
    /// carried a private 12.0 and so quietly kept the value <see cref="BuiltInSymbols"/> was raised
    /// away from; sharing the constant is what stops the two drifting apart a second time.
    /// </summary>
    private const double FontSize = BuiltInSymbols.SddPortLabelFontSize;

    /// <summary>
    /// Label inset from the OUTER rect's edge, matching what <c>BuildSnpSymbol</c> insets its own
    /// labels from its body edge — so a label sits the same distance inside its box in both. That
    /// leaves <see cref="InnerInset"/>-worth of clear space between the text and the inner rect it
    /// sits within, which the previous 5-from-the-INNER-rect did not once the glyph grew.
    /// </summary>
    private const double TextInset = 20;

    /// <summary>
    /// Generates a default symbol for <paramref name="cellName"/>.
    /// <paramref name="numPorts"/> ≤ 0 defaults to 2.
    /// </summary>
    public static Symbol Generate(string cellName, int numPorts)
    {
        if (numPorts <= 0) numPorts = 2;

        var primitives = new List<SymbolPrimitive>();
        var pins       = new List<SymbolPin>();

        var portLayout = BuildPortLayout(numPorts);

        int maxPerSide = Math.Max(1, Math.Max(
            portLayout.Count(p => p.IsLeft),
            portLayout.Count(p => !p.IsLeft)));
        double halfH = (maxPerSide - 1) * 100.0 + 80.0;

        // Outer rect — Normal stroke
        primitives.Add(new RectPrimitive
        {
            ColorRole  = SymbolColorRole.SymbolLine,
            StrokeTier = SymbolStrokeTier.Normal,
            Cx = 0, Cy = 0, W = HalfW * 2, H = halfH * 2,
        });

        // Inner rect — Thin stroke
        primitives.Add(new RectPrimitive
        {
            ColorRole  = SymbolColorRole.SymbolLine,
            StrokeTier = SymbolStrokeTier.Thin,
            Cx = 0, Cy = 0, W = (HalfW - InnerInset) * 2, H = (halfH - InnerInset) * 2,
        });

        foreach (var (portNum, isLeft, portY) in portLayout)
        {
            double outerX  = isLeft ? -HalfW : HalfW;
            double pinTipX = isLeft ? -PinX  : PinX;

            // Stub lead from outer rect edge to pin tip
            primitives.Add(new LinePrimitive(
                SymbolColorRole.SymbolLine, SymbolStrokeTier.Normal,
                outerX, portY, pinTipX, portY));

            // Pin at tip (portIndex is 0-based)
            pins.Add(new SymbolPin(pinTipX, portY, portNum - 1, portNum.ToString()));

            // Port-number text inside the inner rect, near the stub.
            double textX = isLeft
                ? -(HalfW - TextInset)   // Left-aligned → flows toward center
                :  (HalfW - TextInset);  // Right-aligned → flows toward center
            primitives.Add(new TextPrimitive
            {
                Content  = portNum.ToString(),
                AnchorX  = textX,
                AnchorY  = portY,
                FontSize = FontSize,
                Align    = isLeft ? SymbolTextAlign.Left : SymbolTextAlign.Right,
                // MIDDLE, as SnP's labels are. TextPrimitive defaults to Baseline, which anchors the
                // glyph's baseline on portY and so hangs the whole label ABOVE its own lead — a small
                // offset at the old 12 and an obvious one at 18. This is what centres the number on
                // the stub it names rather than merely making it bigger.
                VAlign   = SymbolTextVAlign.Middle,
            });
        }

        return new Symbol(primitives, pins, numPorts);
    }

    // ── Port layout ───────────────────────────────────────────────────────────

    private record PortEntry(int PortNum, bool IsLeft, double Y);

    private static List<PortEntry> BuildPortLayout(int n)
    {
        // N=3 special: port 1 left y=0, ports 2+3 right y=∓100 (both on P=100 grid).
        if (n == 3)
        {
            return
            [
                new PortEntry(1, IsLeft: true,   Y:    0),
                new PortEntry(2, IsLeft: false,  Y: -100),
                new PortEntry(3, IsLeft: false,  Y:  100),
            ];
        }

        // General: odd ports (1, 3, 5, …) go left; even ports (2, 4, 6, …) go right.
        // Each side is centered vertically with PortSpacing=200 between adjacent ports.
        var leftPorts  = Enumerable.Range(1, n).Where(p => p % 2 == 1).ToList();
        var rightPorts = Enumerable.Range(1, n).Where(p => p % 2 == 0).ToList();

        var layout = new List<PortEntry>(n);
        AppendSide(layout, leftPorts,  isLeft: true);
        AppendSide(layout, rightPorts, isLeft: false);
        return layout;
    }

    private static void AppendSide(List<PortEntry> list, List<int> ports, bool isLeft)
    {
        int k = ports.Count;
        for (int i = 0; i < k; i++)
        {
            double y = (i - (k - 1) / 2.0) * PortSpacing;
            list.Add(new PortEntry(ports[i], isLeft, y));
        }
    }
}
