namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Builds SchematicModel instances:
///  - GenerateStressTest(n)  — n-component grid for performance validation
///  - BuildHero2PA()         — simplified GaN PA to demonstrate correct rendering
///
/// All positions are in world units (100 = 1 grid square).
/// 2-terminal vertical symbols (R/L/C/V/Tone/Port/GND) have pins at (0,±200) in local
/// coords.  Place them at R90 in horizontal signal paths (pin1 right, pin2 left).
/// Box symbols (FET/ZPort/Sdd) stay horizontal (pins ±200 on the x-axis).
/// </summary>
public static class SchematicModelBuilder
{
    // Standard 2-terminal component: body ±60, leads ±200 → total span ±215 with margin
    private const double HalfBound = 200.0;

    // ---------------------------------------------------------------------------
    //  Stress test — n components in a rows×cols grid, connected in rows
    // ---------------------------------------------------------------------------

    /// <summary>Generates a stress-test schematic with approximately <paramref name="n"/> components.</summary>
    public static SchematicModel GenerateStressTest(int n = 10_000)
    {
        int cols = (int)Math.Ceiling(Math.Sqrt(n));
        int rows = (int)Math.Ceiling((double)n / cols);
        // Actually limit to n components total
        int total = Math.Min(n, rows * cols);

        // Component pitch — pins are at ±200, so use 500 to keep 100-unit wire segments
        const double pitchX = 500.0;  // horizontal spacing (component center to center)
        const double pitchY = 400.0;  // vertical row spacing

        SymbolKind[] kinds = [SymbolKind.Resistor, SymbolKind.Capacitor, SymbolKind.Inductor, SymbolKind.Vdc];

        var components = new List<SchematicComponent>(total);
        var wires      = new List<SchematicWire>(total);
        var dots       = new List<SchematicDot>();

        int count = 0;
        for (int row = 0; row < rows && count < total; row++)
        {
            int? prevIdx = null;
            for (int col = 0; col < cols && count < total; col++, count++)
            {
                double cx = col * pitchX;
                double cy = row * pitchY;
                SymbolKind kind = kinds[(col + row) % kinds.Length];
                string name = $"{KindPrefix(kind)}{count + 1}";

                // R90: vertical 2-terminal passive lies horizontal (pin1 right, pin2 left)
                var comp = MakeComponent(name, kind, cx, cy, SymbolRotation.R90, DemoParams(kind, count));
                int compIdx = components.Count;
                components.Add(comp);

                // Wire from previous component's right connector (cx_prev+200) to this left (cx-200).
                // At R90 pin1 is at cx+200 (right) and pin2 is at cx-200 (left); same x-coords as before.
                if (prevIdx.HasValue)
                {
                    var prev = components[prevIdx.Value];
                    double wireX0 = prev.X + 200;
                    double wireY0 = prev.Y;
                    double wireX1 = cx - 200;
                    double wireY1 = cy;

                    wires.Add(new SchematicWire
                    {
                        Points  = [(wireX0, wireY0), (wireX1, wireY1)],
                        BbMinX  = Math.Min(wireX0, wireX1) - 5,
                        BbMinY  = Math.Min(wireY0, wireY1) - 5,
                        BbMaxX  = Math.Max(wireX0, wireX1) + 5,
                        BbMaxY  = Math.Max(wireY0, wireY1) + 5,
                    });

                    // Connection dot at each junction point
                    if (col > 1)
                        dots.Add(new SchematicDot(wireX0, wireY0));
                }

                prevIdx = compIdx;
            }
        }

        ComputeOverallBounds(components, wires, out double minX, out double minY, out double maxX, out double maxY);

        return new SchematicModel
        {
            Components     = components,
            Wires          = wires,
            ConnectionDots = dots,
            GridSize       = 100.0,
            BbMinX = minX, BbMinY = minY,
            BbMaxX = maxX, BbMaxY = maxY,
        };
    }

    // ---------------------------------------------------------------------------
    //  Hero 2 PA — simplified gate + drain bias-tee with SDD FET
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Builds a simplified version of the Hero 2 GaN PA schematic for visual testing.
    /// Signal path (left → right, all at y=0):
    ///   ToneSource(R90) → ZPort → Cap(R90) → FetSdd → Inductor(R90) → ZPort → Port(R270)
    /// Gate bias (vertical, x=gateNodeX): Inductor(R0) → Vdc(R0) → Ground
    /// Drain bias (vertical, x=drainNodeX): Vdc(R0) → Ground
    /// Drive source return: Ground below Vdrive left pin.
    /// </summary>
    public static SchematicModel BuildHero2PA()
    {
        // Signal path at y=0, components spaced 500 units apart
        const double pitch       = 500.0;
        const double signalY     = 0.0;
        const double biasY       = 400.0;   // vertical offset for bias components below signal

        var components = new List<SchematicComponent>();
        var wires      = new List<SchematicWire>();
        var dots       = new List<SchematicDot>();

        // ── Signal path ─────────────────────────────────────────────────────────

        // Vdrive: vertical ToneSource at R90 → pin1 at (+200,0), pin2 at (-200,0)
        components.Add(MakeComponent("Vdrive", SymbolKind.ToneSource, 0, signalY,
            SymbolRotation.R90,
            [("V", "1", "V"), ("Freq", "2", "GHz")]));

        components.Add(MakeComponent("Zsource", SymbolKind.ZPort, pitch, signalY,
            SymbolRotation.R0, [("Z[1,1]", "25", "ohm")], portCount: 1));

        // Cblock_g: vertical Cap at R90 → connects horizontally in signal path
        components.Add(MakeComponent("Cblock_g", SymbolKind.Capacitor, 2 * pitch, signalY,
            SymbolRotation.R90, [("C", "1", "µF")]));

        // FET — SDD model (3-port: gate left, drain right-top, source right-bottom; horizontal box)
        components.Add(MakeComponent("FET1", SymbolKind.FetSdd, 3 * pitch, signalY,
            SymbolRotation.R0));

        // Lchoke_d: vertical Inductor at R90 → connects horizontally; drain junction at left pin
        components.Add(MakeComponent("Lchoke_d", SymbolKind.Inductor, 4 * pitch, signalY,
            SymbolRotation.R90, [("L", "1", "µH")]));

        components.Add(MakeComponent("Zload", SymbolKind.ZPort, 5 * pitch, signalY,
            SymbolRotation.R0, [("Z[1,1]", "160", "ohm")], portCount: 1));

        // Term P2: R270 → "+" pin at world(-200,0) = signal, "−" pin at world(+200,0) = ref.
        components.Add(MakeComponent("P2", SymbolKind.Term, 6 * pitch, signalY,
            SymbolRotation.R270, [("Num", "2", ""), ("Z", "50", "Ω")]));

        // ── Gate bias (vertical stack above signal path, x = gateNodeX = 3*pitch-200) ──

        double gateNodeX = Port1X(3 * pitch, 0);   // FET gate world x = 3*pitch - 200

        // Lchoke_g at R0 (vertical native): pin2 at (gateNodeX,-200), pin1 at (gateNodeX,-600)
        components.Add(MakeComponent("Lchoke_g", SymbolKind.Inductor,
            gateNodeX, -biasY, SymbolRotation.R0, [("L", "1", "µH")]));

        // Vgate at R0 (vertical native): pin2 at (gateNodeX,-700), pin1 at (gateNodeX,-1100)
        components.Add(MakeComponent("Vgate", SymbolKind.Vdc,
            gateNodeX, -biasY - 500, SymbolRotation.R0, [("Vdc", "-3.05", "V")]));

        components.Add(MakeComponent("GND1", SymbolKind.Ground,
            gateNodeX, -biasY - 1000, SymbolRotation.R0));

        // ── Drain bias (vertical stack below signal path, x = drainNodeX = Lchoke_d left pin) ──

        double drainNodeX = Port1X(4 * pitch, 0);  // Lchoke_d left pin x at R90 = 4*pitch - 200

        // Vdrain at R0 (vertical): pin1 at (drainNodeX, biasY-100), pin2 at (drainNodeX, biasY+300)
        components.Add(MakeComponent("Vdrain", SymbolKind.Vdc,
            drainNodeX, biasY + 100, SymbolRotation.R0, [("Vdc", "48", "V")]));

        // GND2 below Vdrain
        components.Add(MakeComponent("GND2", SymbolKind.Ground,
            drainNodeX, biasY + 500, SymbolRotation.R0));

        // ── Drive source return (below Vdrive left pin at (-200,0)) ─────────────

        components.Add(MakeComponent("GND3", SymbolKind.Ground,
            -200, 200, SymbolRotation.R0));

        // ── Signal-path wires ────────────────────────────────────────────────────

        void AddWire(double x0, double y0, double x1, double y1) =>
            wires.Add(new SchematicWire
            {
                Points  = [(x0, y0), (x1, y1)],
                BbMinX  = Math.Min(x0, x1) - 5,
                BbMinY  = Math.Min(y0, y1) - 5,
                BbMaxX  = Math.Max(x0, x1) + 5,
                BbMaxY  = Math.Max(y0, y1) + 5,
            });

        // At R90 the vertical passives have their connectors at cx±200 on the x-axis —
        // same world coordinates as the old horizontal layout, so Port1X/Port2X are unchanged.
        AddWire(Port2X(0, 0),           signalY, Port1X(pitch, 0),       signalY); // Vdrive → Zsource
        AddWire(Port2X(pitch, 0),       signalY, Port1X(2 * pitch, 0),   signalY); // Zsource → Cblock_g
        AddWire(Port2X(2 * pitch, 0),   signalY, Port1X(3 * pitch, 0),   signalY); // Cblock_g → FET gate
        AddWire(Port2X(3 * pitch, 0),   signalY, Port1X(4 * pitch, 0),   signalY); // FET drain → Lchoke_d
        AddWire(Port2X(4 * pitch, 0),   signalY, Port1X(5 * pitch, 0),   signalY); // Lchoke_d → Zload
        AddWire(Port2X(5 * pitch, 0),   signalY, Port1X(6 * pitch, 0),   signalY); // Zload → P2

        // ── Gate bias wires ─────────────────────────────────────────────────────

        // FET gate node → Lchoke_g pin2 (Lchoke_g at R0: pin2 = local(0,+200) → y=-biasY+200=-200)
        AddWire(gateNodeX, signalY,              gateNodeX, -biasY + 200);
        // Lchoke_g pin1 (y=-biasY-200=-600) → Vgate pin2 (Vgate at R0: pin2 = y=-biasY-500+200=-700)
        AddWire(gateNodeX, -biasY - 200,         gateNodeX, -biasY - 300);
        // Vgate pin1 (y=-biasY-500-200=-1100) → GND1 (y=-biasY-1000=-1400)
        AddWire(gateNodeX, -biasY - 700,         gateNodeX, -biasY - 1000);

        // ── Drain bias wires ────────────────────────────────────────────────────

        // Lchoke_d left-pin junction → Vdrain pin1 (y=biasY+100-200=biasY-100)
        AddWire(drainNodeX, signalY,             drainNodeX, biasY - 100);
        // Vdrain pin2 (y=biasY+100+200=biasY+300) → GND2 (y=biasY+500)
        AddWire(drainNodeX, biasY + 300,         drainNodeX, biasY + 500);

        // ── Drive source return wire ─────────────────────────────────────────────

        // Vdrive left pin at (-200,0) → GND3 at (-200,200)
        AddWire(-200, signalY, -200, 200);

        // ── Junction dots ────────────────────────────────────────────────────────

        // FET gate node: Cblock_g right wire + FET gate port + gate bias stub
        dots.Add(new SchematicDot(gateNodeX, signalY));
        // Lchoke_d left-pin drain node: FET drain wire + Lchoke_d pin2 + drain bias stub
        dots.Add(new SchematicDot(drainNodeX, signalY));

        ComputeOverallBounds(components, wires, out double minX, out double minY, out double maxX, out double maxY);

        return new SchematicModel
        {
            Components     = components,
            Wires          = wires,
            ConnectionDots = dots,
            GridSize       = 100.0,
            BbMinX = minX - 200, BbMinY = minY - 200,
            BbMaxX = maxX + 200, BbMaxY = maxY + 200,
        };
    }

    // ---------------------------------------------------------------------------
    //  Helpers
    // ---------------------------------------------------------------------------

    // Right/left connection point x-coordinate for a component centered at cx.
    // For vertical 2-terminal passives at R90: pin1 is at cx+200 (right), pin2 at cx-200 (left).
    // For horizontal box symbols at R0: port2 is at cx+200 (right), port1 at cx-200 (left).
    // Either way, the connection x-coordinate is cx±200.
    private static double Port2X(double cx, double _cy) => cx + 200;
    private static double Port1X(double cx, double _cy) => cx - 200;

    private static SchematicComponent MakeComponent(
        string name, SymbolKind kind, double cx, double cy,
        SymbolRotation rot,
        (string Name, string Expr, string Unit)[]? parameters = null,
        PortConnectionState port0State = PortConnectionState.Connected,
        PortConnectionState port1State = PortConnectionState.Connected,
        int portCount = -1)
    {
        // N = network port count for variadic types; 0 for fixed types.
        int n = portCount > 0 ? portCount
              : kind is SymbolKind.ZPort or SymbolKind.Sdd ? 2 : 0;

        // Seed from the registry template so hidden params (e.g. NumPorts="1") are present
        // and correct. Merge caller-supplied values by name, leaving unmentioned slots at default.
        var tpl = ComponentTypeRegistry.DefaultParameters(kind, n);
        var merged = tpl.Select(tp => (tp.Name, Expr: tp.Expression, tp.Unit, tp.ShowOnSchematic)).ToList();
        if (parameters is not null)
        {
            foreach (var (oName, oExpr, oUnit) in parameters)
            {
                int idx = merged.FindIndex(p => p.Name == oName);
                if (idx >= 0) merged[idx] = (oName, oExpr, oUnit, merged[idx].ShowOnSchematic);
            }
        }

        var ports = BuildPorts(kind, rot, port0State, port1State, n);

        // Type label uses N directly — ports.Count is 2N for both ZPort and SDD.
        var labels = new List<string> { ComponentTypeRegistry.DisplayName(kind, n > 0 ? n : ports.Count), name };
        foreach (var (pName, pExpr, pUnit, show) in merged)
        {
            if (!show || string.IsNullOrEmpty(pExpr)) continue;
            string val = string.IsNullOrEmpty(pUnit) ? pExpr : $"{pExpr} {pUnit}";
            labels.Add(string.IsNullOrEmpty(pName) ? val : $"{pName} = {val}");
        }

        var (gMinX, gMinY, gMaxX, gMaxY) = ComputeGlyphBbLocal(kind, cx, cy, n, rot);

        // FullBb: glyph BB unioned with default label positions (no offsets — builder creates
        // components without saved LabelOffsets).
        // Also union with the glyph BB so tall symbols (SDD/ZPort with many ports) don't vanish
        // when only their center scrolls off screen.
        double fullMinX = Math.Min(cx - HalfBound, gMinX), fullMinY = Math.Min(cy - HalfBound, gMinY);
        double fullMaxX = Math.Max(cx + HalfBound, gMaxX), fullMaxY = Math.Max(cy + HalfBound, gMaxY);

        for (int li = 0; li < labels.Count; li++)
        {
            if (string.IsNullOrEmpty(labels[li])) continue;
            double lx  = cx + SchematicComponent.LabelBaseOffsetX;
            double ly  = cy + SchematicComponent.LabelBaseYFor(kind, n, gMaxY - cy) + li * SchematicComponent.LabelWorldStep;
            fullMinX = Math.Min(fullMinX, lx);
            fullMinY = Math.Min(fullMinY, ly - SchematicComponent.LabelWorldHeight);
            fullMaxX = Math.Max(fullMaxX, lx + SchematicComponent.LabelWidthEstimate);
            fullMaxY = Math.Max(fullMaxY, ly + 20.0);
        }

        return new SchematicComponent
        {
            InstanceName  = name,
            Symbol        = kind,
            X = cx, Y = cy,
            Rotation      = rot,
            Ports         = ports,
            Labels        = labels,
            BbMinX        = cx - HalfBound,
            BbMinY        = cy - HalfBound,
            BbMaxX        = cx + HalfBound,
            BbMaxY        = cy + HalfBound,
            GlyphBbMinX   = gMinX,
            GlyphBbMinY   = gMinY,
            GlyphBbMaxX   = gMaxX,
            GlyphBbMaxY   = gMaxY,
            FullBbMinX    = fullMinX,
            FullBbMinY    = fullMinY,
            FullBbMaxX    = fullMaxX,
            FullBbMaxY    = fullMaxY,
        };
    }

    // Glyph BB (world coords) for the built-in symbols.
    // Vertical 2-terminal symbols (R/L/C/V/Tone/Port/GND) span ±200 in local y, ~60 in x.
    // At R90/R270 the axes are swapped (lying horizontal).
    // Box symbols (FET/ZPort/Sdd/Generic) keep the legacy horizontal box.
    private static (double MinX, double MinY, double MaxX, double MaxY) ComputeGlyphBbLocal(
        SymbolKind kind, double cx, double cy, int n, SymbolRotation rot)
    {
        if (n > 0 && kind is SymbolKind.ZPort or SymbolKind.Sdd)
        {
            var portDefs = SymbolPortDefs.For(kind, n);
            float minX = -70f, maxX = 70f;
            float minY = -50f, maxY = 50f;
            foreach (var (_, lx, ly) in portDefs)
            {
                if (lx < minX) minX = lx; if (lx > maxX) maxX = lx;
                if (ly < minY) minY = ly; if (ly > maxY) maxY = ly;
            }
            const float pad = 15f;
            return (cx + minX - pad, cy + minY - pad, cx + maxX + pad, cy + maxY + pad);
        }

        // Box symbols stay horizontal regardless of rotation.
        if (kind is SymbolKind.FetSdd or SymbolKind.Generic)
            return (cx - 210, cy - 110, cx + 210, cy + 110);

        // Vertical 2-terminal symbols: local x ≈ ±65, local y ≈ ±210.
        // At R90/R270 the axes swap (component lies horizontal).
        bool isHorizontal = rot is SymbolRotation.R90 or SymbolRotation.R270;
        return isHorizontal
            ? (cx - 210, cy - 65, cx + 210, cy + 65)
            : (cx -  65, cy - 210, cx +  65, cy + 210);
    }

    private static IReadOnlyList<SchematicPortDef> BuildPorts(
        SymbolKind kind, SymbolRotation rotation,
        PortConnectionState p0, PortConnectionState p1,
        int portCount = -1)
    {
        return kind switch
        {
            // Ground: single pin at local origin (unchanged)
            SymbolKind.Ground => [new SchematicPortDef("1", 0, 0, p0)],
            // Term: two pins — "+" signal at (0,-200) and "−" reference at (0,+200).
            SymbolKind.Term   => [new SchematicPortDef("+", 0, -200, p0),
                                  new SchematicPortDef("−", 0, +200, p1)],
            // Pin: one connection terminal at the lead tip — carries the interface port number.
            SymbolKind.Pin    => [new SchematicPortDef("1", 100, 0, p0)],
            // FetSdd: horizontal box, pins unchanged
            SymbolKind.FetSdd => [
                new SchematicPortDef("gate",   -200, 0,    p0),
                new SchematicPortDef("drain",   200, -100, p1),
                new SchematicPortDef("source",  200,  100, PortConnectionState.Unconnected),
            ],
            // Variadic box symbols — both use 2N ± pair generator
            SymbolKind.ZPort => GenerateSddVariadicPorts(portCount > 0 ? portCount : 2),
            SymbolKind.Sdd   => GenerateSddVariadicPorts(portCount > 0 ? portCount : 2),
            // 2-terminal vertical: pins at local top (0,-200) and bottom (0,+200)
            _ => [
                new SchematicPortDef("1", 0, -200, p0),
                new SchematicPortDef("2", 0,  200, p1),
            ],
        };
    }

    // Mirrors EditableSchematic.GenerateSddPorts — 2N pins, left/right port split, same pin-index contract.
    private static IReadOnlyList<SchematicPortDef> GenerateSddVariadicPorts(int n)
    {
        var ports  = new SchematicPortDef[2 * n];
        int nLeft  = (n + 1) / 2;
        int nRight = n       / 2;
        const float halfDiff    = 100f;
        const float portSpacing = 300f;

        for (int p = 0; p < nLeft; p++)
        {
            float cy = nLeft == 1 ? 0f : (p - (nLeft - 1) * 0.5f) * portSpacing;
            int   pn = p + 1;
            ports[2 * p]     = new SchematicPortDef($"{pn}+", -200f, cy - halfDiff, PortConnectionState.Connected);
            ports[2 * p + 1] = new SchematicPortDef($"{pn}-", -200f, cy + halfDiff, PortConnectionState.Connected);
        }
        for (int q = 0; q < nRight; q++)
        {
            float cy = nRight == 1 ? 0f : (q - (nRight - 1) * 0.5f) * portSpacing;
            int   pn = nLeft + q + 1;
            int   i  = 2 * (nLeft + q);
            ports[i]     = new SchematicPortDef($"{pn}+", +200f, cy - halfDiff, PortConnectionState.Connected);
            ports[i + 1] = new SchematicPortDef($"{pn}-", +200f, cy + halfDiff, PortConnectionState.Connected);
        }
        return ports;
    }

    private static string KindPrefix(SymbolKind k) => k switch
    {
        SymbolKind.Resistor      => "R",
        SymbolKind.Capacitor     => "C",
        SymbolKind.Inductor      => "L",
        SymbolKind.Vdc           => "V",
        SymbolKind.ToneSource    => "V1T",
        SymbolKind.Ground        => "GND",
        SymbolKind.Term          => "Term",
        SymbolKind.FetSdd        => "X",
        SymbolKind.ZPort         => "Z",
        _                        => "X",
    };

    private static (string Name, string Expr, string Unit)[] DemoParams(SymbolKind k, int idx) => k switch
    {
        SymbolKind.Resistor      => [("R",   $"{50 + (idx % 5) * 10}", "ohm")],
        SymbolKind.Capacitor     => [("C",   "1",                        "pF")],
        SymbolKind.Inductor      => [("L",   "1",                        "nH")],
        SymbolKind.Vdc           => [("Vdc", "0",                        "V")],
        _                        => [],
    };

    private static void ComputeOverallBounds(
        List<SchematicComponent> comps, List<SchematicWire> wires,
        out double minX, out double minY, out double maxX, out double maxY)
    {
        minX = minY = double.MaxValue;
        maxX = maxY = double.MinValue;

        foreach (var c in comps)
        {
            minX = Math.Min(minX, c.BbMinX);
            minY = Math.Min(minY, c.BbMinY);
            maxX = Math.Max(maxX, c.BbMaxX);
            maxY = Math.Max(maxY, c.BbMaxY);
        }

        foreach (var w in wires)
        {
            minX = Math.Min(minX, w.BbMinX);
            minY = Math.Min(minY, w.BbMinY);
            maxX = Math.Max(maxX, w.BbMaxX);
            maxY = Math.Max(maxY, w.BbMaxY);
        }

        if (minX == double.MaxValue)
        {
            minX = minY = -100;
            maxX = maxY = 100;
        }
    }
}
