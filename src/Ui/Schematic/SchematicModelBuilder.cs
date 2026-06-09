namespace CircuitRF.Ui.Schematic;

/// <summary>
/// Builds SchematicModel instances:
///  - GenerateStressTest(n)  — n-component grid for performance validation
///  - BuildHero2PA()         — simplified GaN PA to demonstrate correct rendering
///
/// All positions are in world units (100 = 1 grid square).
/// Components are placed on a 400-unit horizontal pitch (standard EDA: 4 grid squares).
/// </summary>
public static class SchematicModelBuilder
{
    // Standard 2-terminal component: body ±60, leads ±150 → total span ±175 with margin
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

        // Component pitch
        const double pitchX = 400.0;  // horizontal spacing (component center to center)
        const double pitchY = 350.0;  // vertical row spacing

        SymbolKind[] kinds = [SymbolKind.Resistor, SymbolKind.Capacitor, SymbolKind.Inductor, SymbolKind.VoltageSource];

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

                var comp = MakeComponent(name, kind, cx, cy, SymbolRotation.R0, DemoParams(kind, count));
                int compIdx = components.Count;
                components.Add(comp);

                // Wire from previous component's right port to this component's left port
                if (prevIdx.HasValue)
                {
                    var prev = components[prevIdx.Value];
                    // prev port2 at (prev.X + 150, prev.Y), this port1 at (cx - 150, cy)
                    double wireX0 = prev.X + 150;
                    double wireY0 = prev.Y;
                    double wireX1 = cx - 150;
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
    /// Builds a simplified version of the Hero 2 GaN PA schematic for visual testing:
    /// ToneSource → Resistor(match) → Capacitor(block) → FetSdd → Inductor(choke) → Port
    /// with gate bias and drain bias stubs below.
    /// </summary>
    public static SchematicModel BuildHero2PA()
    {
        // Signal path at y=0, components spaced 500 units apart (wider for clarity)
        const double pitch = 500.0;
        const double signalY = 0.0;
        const double biasY   = 400.0;

        var components = new List<SchematicComponent>();
        var wires      = new List<SchematicWire>();
        var dots       = new List<SchematicDot>();

        // ── Signal path ────────────────────────────────────────────────────────

        // Drive source (left port connected to n_src, right port unconnected toward ground)
        components.Add(MakeComponent("Vdrive", SymbolKind.ToneSource, 0, signalY,
            SymbolRotation.R0,
            [("V", "1", "V"), ("Freq", "2", "GHz")],      // V amplitude + 2 GHz frequency
            port0State: PortConnectionState.Connected,
            port1State: PortConnectionState.Unconnected));

        components.Add(MakeComponent("Zsource", SymbolKind.ZPort, pitch, signalY,
            SymbolRotation.R0, [("Z[1,1]", "25", "ohm")], portCount: 1));

        components.Add(MakeComponent("Cblock_g", SymbolKind.Capacitor, 2 * pitch, signalY,
            SymbolRotation.R0, [("C", "1", "µF")]));

        // FET — SDD model (3-port: gate L, drain R-top, source R-bottom; using 2-port layout)
        components.Add(MakeComponent("FET1", SymbolKind.FetSdd, 3 * pitch, signalY,
            SymbolRotation.R0));

        components.Add(MakeComponent("Lchoke_d", SymbolKind.Inductor, 4 * pitch, signalY,
            SymbolRotation.R0, [("L", "1", "µH")]));

        components.Add(MakeComponent("Zload", SymbolKind.ZPort, 5 * pitch, signalY,
            SymbolRotation.R0, [("Z[1,1]", "160", "ohm")], portCount: 1));

        components.Add(MakeComponent("P2", SymbolKind.Port, 6 * pitch, signalY,
            SymbolRotation.R0));

        // ── Gate bias ──────────────────────────────────────────────────────────

        // Lchoke_g stacked above the FET gate node
        components.Add(MakeComponent("Lchoke_g", SymbolKind.Inductor,
            3 * pitch - 200, -biasY, SymbolRotation.R90, [("L", "1", "µH")]));

        components.Add(MakeComponent("Vgate", SymbolKind.VoltageSource,
            3 * pitch - 200, -biasY - 400, SymbolRotation.R90, [("Vac", "-3.05", "V")]));

        components.Add(MakeComponent("GND1", SymbolKind.Ground,
            3 * pitch - 200, -biasY - 800, SymbolRotation.R0));

        // ── Drain bias ─────────────────────────────────────────────────────────

        components.Add(MakeComponent("Vdrain", SymbolKind.VoltageSource,
            4 * pitch, biasY, SymbolRotation.R90, [("Vac", "48", "V")]));

        components.Add(MakeComponent("GND2", SymbolKind.Ground,
            4 * pitch, biasY + 400, SymbolRotation.R0));

        // Drive source ground
        components.Add(MakeComponent("GND3", SymbolKind.Ground,
            0, biasY, SymbolRotation.R0));

        // ── Signal-path wires ─────────────────────────────────────────────────

        void AddWire(double x0, double y0, double x1, double y1) =>
            wires.Add(new SchematicWire
            {
                Points  = [(x0, y0), (x1, y1)],
                BbMinX  = Math.Min(x0, x1) - 5,
                BbMinY  = Math.Min(y0, y1) - 5,
                BbMaxX  = Math.Max(x0, x1) + 5,
                BbMaxY  = Math.Max(y0, y1) + 5,
            });

        void AddPolyWire(params (double X, double Y)[] pts)
        {
            for (int i = 0; i < pts.Length - 1; i++)
                AddWire(pts[i].X, pts[i].Y, pts[i + 1].X, pts[i + 1].Y);
        }

        // Vdrive left port → (circuit input, unconnected — already shown via red box)
        // Vdrive right port → Zsource left port
        AddWire(Port2X(0, 0), signalY, Port1X(pitch, 0), signalY);
        // Zsource → Cblock_g
        AddWire(Port2X(pitch, 0), signalY, Port1X(2 * pitch, 0), signalY);
        // Cblock_g → FET gate
        AddWire(Port2X(2 * pitch, 0), signalY, Port1X(3 * pitch, 0), signalY);
        // FET drain → Lchoke_d
        AddWire(Port2X(3 * pitch, 0), signalY, Port1X(4 * pitch, 0), signalY);
        // Lchoke_d → Zload
        AddWire(Port2X(4 * pitch, 0), signalY, Port1X(5 * pitch, 0), signalY);
        // Zload → P2
        AddWire(Port2X(5 * pitch, 0), signalY, Port1X(6 * pitch, 0), signalY);

        // Gate bias: FET gate node → Lchoke_g (vertical) → Vgate → GND1
        double gateNodeX = Port1X(3 * pitch, 0);
        AddPolyWire((gateNodeX, signalY), (gateNodeX, -150),
                    (3 * pitch - 200, -150), (3 * pitch - 200, -biasY + 150));
        AddWire(3 * pitch - 200, -biasY - 150, 3 * pitch - 200, -biasY - 250);
        AddWire(3 * pitch - 200, -biasY - 550, 3 * pitch - 200, -biasY - 650);

        // Drain bias: Lchoke_d mid-node → Vdrain → GND2
        AddPolyWire((4 * pitch, signalY + 150), (4 * pitch, biasY - 150));
        AddWire(4 * pitch, biasY + 150, 4 * pitch, biasY + 250);

        // Drive source → GND3
        AddWire(0, signalY + 150, 0, biasY - 150);

        // Junction dots at Lchoke_d mid-node and FET gate node
        dots.Add(new SchematicDot(4 * pitch, signalY));
        dots.Add(new SchematicDot(gateNodeX, signalY));

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

    // Right port of a standard 2-terminal component at origin x on y-row
    private static double Port2X(double cx, double _cy) => cx + 150;
    private static double Port1X(double cx, double _cy) => cx - 150;

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

        // Type label uses N directly — ports.Count is N+1 for variadic types.
        var labels = new List<string> { ComponentTypeRegistry.DisplayName(kind, n > 0 ? n : ports.Count), name };
        foreach (var (pName, pExpr, pUnit, show) in merged)
        {
            if (!show || string.IsNullOrEmpty(pExpr)) continue;
            string val = string.IsNullOrEmpty(pUnit) ? pExpr : $"{pExpr} {pUnit}";
            labels.Add(string.IsNullOrEmpty(pName) ? val : $"{pName} = {val}");
        }

        var (gMinX, gMinY, gMaxX, gMaxY) = ComputeGlyphBbLocal(kind, cx, cy, n);

        // FullBb: glyph BB unioned with default label positions (no offsets — builder creates
        // components without saved LabelOffsets).
        double fullMinX = cx - HalfBound, fullMinY = cy - HalfBound;
        double fullMaxX = cx + HalfBound, fullMaxY = cy + HalfBound;
        for (int li = 0; li < labels.Count; li++)
        {
            if (string.IsNullOrEmpty(labels[li])) continue;
            double lx  = cx + SchematicComponent.LabelBaseOffsetX;
            double ly  = cy + SchematicComponent.LabelBaseY + li * SchematicComponent.LabelWorldStep;
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

    // Glyph BB for variadic types extends to port tips; fixed types use the standard box.
    private static (double MinX, double MinY, double MaxX, double MaxY) ComputeGlyphBbLocal(
        SymbolKind kind, double cx, double cy, int n)
    {
        if (n > 0 && kind is SymbolKind.ZPort or SymbolKind.Sdd)
        {
            var portDefs = SymbolPortDefs.For(kind, n);
            float minX = -70f, maxX = 70f;   // body bounds (ZPort ±70, Sdd ±80)
            float minY = -50f, maxY = 50f;
            foreach (var (_, lx, ly) in portDefs)
            {
                if (lx < minX) minX = lx; if (lx > maxX) maxX = lx;
                if (ly < minY) minY = ly; if (ly > maxY) maxY = ly;
            }
            const float pad = 15f;
            return (cx + minX - pad, cy + minY - pad, cx + maxX + pad, cy + maxY + pad);
        }
        return (cx - 160, cy - 60, cx + 160, cy + 60);
    }

    private static IReadOnlyList<SchematicPortDef> BuildPorts(
        SymbolKind kind, SymbolRotation rotation,
        PortConnectionState p0, PortConnectionState p1,
        int portCount = -1)
    {
        return kind switch
        {
            SymbolKind.Ground => [new SchematicPortDef("1", 0, 0, p0)],
            SymbolKind.Port   => [new SchematicPortDef("1", -150, 0, p0)],
            SymbolKind.FetSdd => [
                new SchematicPortDef("gate",   -150, 0,    p0),
                new SchematicPortDef("drain",   150, -100, p1),
                new SchematicPortDef("source",  150,  100, PortConnectionState.Unconnected),
            ],
            SymbolKind.ZPort or SymbolKind.Sdd =>
                GenerateVariadicPorts(portCount > 0 ? portCount : 2),
            _ => [
                new SchematicPortDef("1", -150, 0, p0),
                new SchematicPortDef("2",  150, 0, p1),
            ],
        };
    }

    // Mirrors EditableSchematic.GeneratePorts — N+1 pins: N signal ports + 1 reference.
    private static IReadOnlyList<SchematicPortDef> GenerateVariadicPorts(int n)
    {
        int nLeft  = (n + 1) / 2;
        int nRight = n / 2 + 1;
        var ports  = new SchematicPortDef[n + 1];
        for (int i = 0; i < nLeft; i++)
        {
            float localY = nLeft > 1 ? (i - (nLeft - 1) * 0.5f) * 200f : 0f;
            ports[i] = new SchematicPortDef($"{i + 1}", -150f, localY, PortConnectionState.Connected);
        }
        for (int i = 0; i < nRight; i++)
        {
            float localY = nRight > 1 ? (i - (nRight - 1) * 0.5f) * 200f : 0f;
            bool isRef   = i == nRight - 1;
            ports[nLeft + i] = new SchematicPortDef(
                isRef ? "ref" : $"{nLeft + i + 1}", 150f, localY, PortConnectionState.Connected);
        }
        return ports;
    }

    private static string KindPrefix(SymbolKind k) => k switch
    {
        SymbolKind.Resistor      => "R",
        SymbolKind.Capacitor     => "C",
        SymbolKind.Inductor      => "L",
        SymbolKind.VoltageSource => "V",
        SymbolKind.ToneSource    => "V1T",
        SymbolKind.Ground        => "GND",
        SymbolKind.Port          => "P",
        SymbolKind.FetSdd        => "X",
        SymbolKind.ZPort         => "Z",
        _                        => "X",
    };

    private static (string Name, string Expr, string Unit)[] DemoParams(SymbolKind k, int idx) => k switch
    {
        SymbolKind.Resistor      => [("R",   $"{50 + (idx % 5) * 10}", "ohm")],
        SymbolKind.Capacitor     => [("C",   "1",                        "pF")],
        SymbolKind.Inductor      => [("L",   "1",                        "nH")],
        SymbolKind.VoltageSource => [("Vac", "1",                        "V"), ("Freq", "", "Hz")],
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
