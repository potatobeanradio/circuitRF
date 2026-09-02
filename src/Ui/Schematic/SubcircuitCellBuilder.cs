using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using CircuitRF.Design.Cells;

namespace CircuitRF.Ui.Schematic;

/// <summary>Where a subcircuit's cells landed, and what is worth saying about them.</summary>
/// <param name="CellDir">The folder of the cell that was asked for.</param>
/// <param name="SchematicPath">Its schematic — what to open afterwards.</param>
/// <param name="AlsoCreated">
/// Cell folders written for the subcircuits this one calls, leaf-first. Empty for the ordinary flat
/// definition. <b>Reported rather than left to be discovered</b> — an import that silently adds four
/// cells to a workspace is one nobody can undo without knowing which four.
/// </param>
/// <param name="Report">The lines to post to Messages. Never empty.</param>
public sealed record SubcircuitCellResult(
    string                CellDir,
    string                SchematicPath,
    IReadOnlyList<string> AlsoCreated,
    IReadOnlyList<string> Report);

/// <summary>
/// Builds a circuitRF cell around one SPICE <c>.subckt</c> — a schematic holding the definition's
/// own components, wired to each other exactly as the file wires them, one <c>Pin</c> per declared
/// port, and a generic box for a symbol.
///
/// <para><b>The wiring is the deliverable, and it is the hard half.</b> A <c>.model</c> card becomes
/// a cell with one device in it, so <see cref="ModelCardCellBuilder"/> can place its pins at fixed
/// offsets and be done. A subcircuit is a netlist: the components have to go somewhere, and then be
/// CONNECTED — and in circuitRF a connection is a geometric fact, so a wire drawn carelessly does
/// not look wrong, it silently joins two nets. <see cref="SchematicAutoRouter"/> owns that contract;
/// this file owns where things go.</para>
///
/// <para><b>The symbol is the SnP box, reused rather than reinvented.</b>
/// <see cref="AutoSymbolGenerator"/> already draws a generic N-port body with numbered pins on the
/// connection grid — which is exactly, and for exactly the same reason, what a subcircuit needs:
/// circuitRF does not know what the user's subcircuit IS, so any glyph more specific than a box
/// would assert something untrue about it.</para>
///
/// <para><b>Ground is drawn, not routed.</b> Net <c>0</c> is every SPICE netlist's busiest net and
/// routing it would lay a rail across the whole sheet; each terminal on it gets its own ground
/// symbol on a short lead instead, which is both what a person would draw and what extraction reads
/// back as net <c>0</c>.</para>
/// </summary>
public static class SubcircuitCellBuilder
{
    /// <summary>The connection grid. Everything electrical sits on it, as it must to connect.</summary>
    private const double P = SchematicAutoRouter.P;

    /// <summary>Centre-to-centre spacing of placed components — four free grid columns between two
    /// adjacent devices, which is what the router needs to get past them.</summary>
    private const double Pitch = 1000.0;

    /// <summary>How far the port pins stand off the block of components.</summary>
    private const double PortStandoff = 1200.0;

    /// <summary>Vertical spacing of the port pins within their own column.</summary>
    private const double PortPitch = 400.0;

    /// <summary>A ground's lead. One component pitch would be a wire; two grid squares is a stub.</summary>
    private const double GroundLead = 200.0;

    /// <summary>SPICE's global ground.</summary>
    private const string GroundNet = "0";

    // ─────────────────────────────────────────────────────────────────────────
    //  What the router is told about one placed thing
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class Placed
    {
        public required EditableComponent    Component     { get; init; }
        public required (float X, float Y)[] LocalPorts    { get; init; }

        /// <summary>Per port, the LOCAL direction its lead points. See <see cref="KeepOut"/>.</summary>
        public required (int X, int Y)[]     LocalApproach { get; init; }

        /// <summary>Per port, the net it sits on — null for a terminal the netlist leaves open.</summary>
        public required string?[]            Nets          { get; init; }

        public (double X, double Y) PortWorld(int k)
            => SchematicGeometry.LocalToWorld(
                LocalPorts[k].X, LocalPorts[k].Y,
                Component.X, Component.Y, Component.Rotation, Component.MirrorX);

        public (int X, int Y) ApproachWorld(int k) => Rotate(LocalApproach[k], Component.Rotation);

        private static (int X, int Y) Rotate((int X, int Y) d, SymbolRotation r) => r switch
        {
            SymbolRotation.R90  => (-d.Y,  d.X),
            SymbolRotation.R180 => (-d.X, -d.Y),
            SymbolRotation.R270 => ( d.Y, -d.X),
            _                   => d,
        };

        /// <summary>
        /// The world cells no wire may enter — the component's own footprint, one grid square proud
        /// of everything it draws.
        ///
        /// <para><b>Each port's own cell and the cell immediately outside it are exempt</b>, and that
        /// pair is what makes the terminal reachable from exactly one direction: the one its lead
        /// points. Without the exemption the pin is walled in and nothing can reach it; without the
        /// footprint around it a wire creeps along the device body and arrives at the pin sideways,
        /// drawn straight across the glyph.</para>
        /// </summary>
        public IReadOnlyList<(double X, double Y)> KeepOut()
        {
            double minX = 0, maxX = 0, minY = 0, maxY = 0;
            foreach (var (lx, ly) in LocalPorts)
            {
                minX = Math.Min(minX, lx); maxX = Math.Max(maxX, lx);
                minY = Math.Min(minY, ly); maxY = Math.Max(maxY, ly);
            }

            var exempt = new HashSet<(double, double)>();
            for (int k = 0; k < LocalPorts.Length; k++)
            {
                var (px, py) = PortWorld(k);
                var (ax, ay) = ApproachWorld(k);
                exempt.Add((px, py));
                exempt.Add((px + ax * P, py + ay * P));
            }

            var cells = new List<(double X, double Y)>();
            for (double lx = minX - P; lx <= maxX + P + 0.5; lx += P)
                for (double ly = minY - P; ly <= maxY + P + 0.5; ly += P)
                {
                    var w = SchematicGeometry.LocalToWorld(
                        (float)lx, (float)ly, Component.X, Component.Y,
                        Component.Rotation, Component.MirrorX);
                    if (!exempt.Contains(w)) cells.Add(w);
                }
            return cells;
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  The schematic
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The cell's schematic: every element of the definition placed, every net drawn, and one
    /// <c>Pin</c> per declared port in the order the <c>.subckt</c> line declares them.
    /// </summary>
    /// <param name="cellRefFor">
    /// Maps a called subcircuit's name to the <c>CellRef</c> its placed instance carries. Consulted
    /// only for an element that calls one, so a flat definition never reaches it.
    /// </param>
    /// <param name="report">Receives a line for anything the reader of the cell must be told.</param>
    public static SchematicEditModel BuildSchematic(
        SubcircuitTranslation translation,
        string                cellName,
        Func<string, string>  cellRefFor,
        ICollection<string>   report)
    {
        ArgumentNullException.ThrowIfNull(translation);
        ArgumentNullException.ThrowIfNull(cellRefFor);
        ArgumentNullException.ThrowIfNull(report);

        if (translation.Refusal is { } refusal)
            throw new InvalidOperationException(refusal);

        var model  = new SchematicEditModel();
        var placed = new List<Placed>();

        // Ground is substituted for net 0 UNLESS the definition declares it as a port — in which
        // case it is a net with a name like any other, and the Pin has to be able to reach it.
        bool groundIsAPort = translation.Definition.Ports
            .Any(p => p.Equals(GroundNet, StringComparison.Ordinal));

        var order   = PlacementOrder(translation);
        int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(order.Count)));

        for (int slot = 0; slot < order.Count; slot++)
            placed.Add(PlaceElement(
                model, translation.Elements[order[slot]], cellRefFor,
                x: slot % columns * Pitch,
                y: slot / columns * Pitch));

        double rows    = Math.Max(1, Math.Ceiling(order.Count / (double)columns));
        double centreY = (rows - 1) * Pitch * 0.5;
        double leftX   = -PortStandoff;
        double rightX  = (columns - 1) * Pitch + PortStandoff;

        placed.AddRange(PlacePins(model, translation.Definition.Ports, leftX, rightX, centreY));

        if (!groundIsAPort)
            placed.AddRange(PlaceGrounds(model, placed));

        // ── the wires ─────────────────────────────────────────────────────────
        var blocks = placed
            .Select(p => new SchematicAutoRouter.Block(
                [.. Enumerable.Range(0, p.LocalPorts.Length)
                     .Select(k => { var (x, y) = p.PortWorld(k); return (x, y, p.Nets[k]); })],
                p.KeepOut()))
            .ToList();

        var routed = SchematicAutoRouter.Route(blocks);

        foreach (var path in routed.Wires)
        {
            var wire = new EditableWire();
            foreach (var pt in path) wire.Points.Add(pt);
            model.Wires.Add(wire);
        }

        // A terminal the router could not reach is connected BY NAME instead. A net label is a real
        // connection — same-name labels are one net — so the cell is still the circuit the file
        // wrote; only the drawing suffered, and that is worth saying rather than hiding.
        foreach (var (x, y, net) in routed.Unrouted)
            model.NetLabels.Add(new EditableNetLabel { X = x, Y = y, Name = net });

        if (routed.Unrouted.Count > 0)
            report.Add(
                $"{routed.Unrouted.Count} connection(s) could not be drawn as a wire without "
                + "crossing something, and are made by net label instead: "
                + string.Join(", ", routed.Unrouted.Select(u => u.Net).Distinct(StringComparer.Ordinal))
                + ". The circuit is the one the file states; the drawing is what suffered.");

        model.CanvasObjects.Add(new EditableText
        {
            Text     = Annotation(translation, cellName),
            X        = leftX,
            Y        = (rows - 1) * Pitch + Pitch,
            Width    = 3200,
            Height   = 500,
            FontSize = 11f,
        });

        return model;
    }

    /// <summary>
    /// The order elements are laid down in: a breadth-first walk of the net graph, seeded from the
    /// declared ports.
    ///
    /// <para>Nothing here optimises anything — it puts things that are connected near each other,
    /// which is most of what makes an auto-drawn netlist readable and is what keeps the router's
    /// paths short enough to find. Ground is not walked through: it touches nearly every element, so
    /// following it would make one hop out of any two components in the circuit. Elements no port
    /// reaches follow in file order, so a definition with a disconnected piece still lays out
    /// deterministically.</para>
    /// </summary>
    private static List<int> PlacementOrder(SubcircuitTranslation translation)
    {
        var elements = translation.Elements;

        var byNet = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        for (int i = 0; i < elements.Count; i++)
            foreach (var net in elements[i].Nets)
            {
                if (net.Equals(GroundNet, StringComparison.Ordinal)) continue;
                if (!byNet.TryGetValue(net, out var l)) byNet[net] = l = [];
                l.Add(i);
            }

        var order = new List<int>(elements.Count);
        var seen  = new bool[elements.Count];
        var queue = new Queue<int>();

        void Push(int i) { if (!seen[i]) { seen[i] = true; queue.Enqueue(i); } }

        foreach (var port in translation.Definition.Ports)
            if (byNet.TryGetValue(port, out var touching))
                foreach (int i in touching) Push(i);

        for (int start = 0; start < elements.Count; start++)
        {
            Push(start);
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                order.Add(i);
                foreach (var net in elements[i].Nets)
                    if (byNet.TryGetValue(net, out var neighbours))
                        foreach (int n in neighbours) Push(n);
            }
        }

        return order;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Placing one of each thing
    // ─────────────────────────────────────────────────────────────────────────

    private static Placed PlaceElement(
        SchematicEditModel model, SubcircuitElement element,
        Func<string, string> cellRefFor, double x, double y)
    {
        var comp = new EditableComponent
        {
            InstanceName = element.InstanceName,
            X            = x,
            Y            = y,
        };

        (float X, float Y)[] ports;

        if (element.SubcircuitName is { } called)
        {
            // A cell instance is drawn from the cell it references, so the placeholder kind is the
            // same one interactive placement uses; CellRef is what decides the artwork and the pins.
            comp.Symbol           = SymbolKind.Generic;
            comp.CellRef          = cellRefFor(called);
            comp.ShowTypeLabel    = true;
            comp.ShowInstanceName = true;
            ports = [.. AutoSymbolGenerator.Generate(called, element.Nets.Count)
                        .Pins.OrderBy(p => p.PortIndex)
                        .Select(p => ((float)p.LocalX, (float)p.LocalY))];
        }
        else
        {
            comp.Symbol = element.Symbol!.Value;
            // The PARAMETERS go on first, because a variadic symbol reads its own port count off
            // one of them (NumPorts). Asking for the pins before they are there gives an SDD of any
            // width the two-port default — four pins for a device the netlist binds six nets to,
            // which is a wiring failure and not a drawing one.
            foreach (var q in element.Parameters) comp.Parameters.Add(q);
            ports = [.. SymbolPortDefs.For(comp.Symbol, comp.PortCount).Select(p => (p.LocalX, p.LocalY))];
            return Finish();
        }

        foreach (var p in element.Parameters) comp.Parameters.Add(p);
        return Finish();

        Placed Finish()
        {
            model.Components.Add(comp);
            return new Placed
            {
                Component     = comp,
                LocalPorts    = ports,
                LocalApproach = [.. ports.Select(Outward)],
                Nets          = [.. element.Nets],
            };
        }
    }

    /// <summary>
    /// One <c>Pin</c> per declared port, odd ports left and even ports right — the same side rule
    /// <see cref="AutoSymbolGenerator"/> uses, so a port drawn on the left of the cell's box is on
    /// the left of the cell's schematic too.
    /// </summary>
    private static List<Placed> PlacePins(
        SchematicEditModel model, IReadOnlyList<string> ports,
        double leftX, double rightX, double centreY)
    {
        var made  = new List<Placed>(ports.Count);
        int left  = ports.Count - ports.Count / 2;
        int right = ports.Count / 2;
        int li = 0, ri = 0;

        var local = SymbolPortDefs.For(SymbolKind.Pin);

        for (int i = 0; i < ports.Count; i++)
        {
            bool onLeft = i % 2 == 0;
            int  k      = onLeft ? li++ : ri++;
            int  count  = onLeft ? left : right;

            double y = centreY + (k - (count - 1) / 2.0) * PortPitch;
            y = Math.Round(y / P) * P;

            var pin = new EditableComponent
            {
                Symbol   = SymbolKind.Pin,
                X        = onLeft ? leftX : rightX,
                Y        = y,
                // R0 puts a Pin's connection point to its right and R180 to its left, so each column
                // faces the components between them rather than away off the sheet.
                Rotation = onLeft ? SymbolRotation.R0 : SymbolRotation.R180,
            };
            pin.Parameters.Add(new EditableParameter
            {
                Name = "Num", Expression = (i + 1).ToString(CultureInfo.InvariantCulture),
            });
            // The port's own net name, from the .subckt line. A cell whose pins are called 1, 2 and 3
            // is one whose user has to remember which was the gate — and here the file already said.
            pin.Parameters.Add(new EditableParameter
            {
                Name = "Name", Expression = ports[i], ShowOnSchematic = false,
            });
            model.Components.Add(pin);

            made.Add(new Placed
            {
                Component     = pin,
                LocalPorts    = [(local[0].LocalX, local[0].LocalY)],
                LocalApproach = [Outward((local[0].LocalX, local[0].LocalY))],
                Nets          = [ports[i]],
            });
        }

        return made;
    }

    /// <summary>
    /// One ground symbol per terminal sitting on net <c>0</c>, on a short lead pointing the way that
    /// terminal already points.
    ///
    /// <para><b>Each gets its own private net name, and that is what makes this work.</b> Handing
    /// every ground the net <c>"0"</c> would ask the router to join them all to one another across
    /// the sheet — the rail this exists to avoid. Extraction gives them all net <c>0</c> regardless,
    /// because a <c>Ground</c> component NAMES its net, so the circuit is unchanged.</para>
    /// </summary>
    private static List<Placed> PlaceGrounds(SchematicEditModel model, List<Placed> placed)
    {
        var made = new List<Placed>();
        int n = 0;

        foreach (var owner in placed.ToList())
            for (int k = 0; k < owner.Nets.Length; k++)
            {
                if (owner.Nets[k] is not GroundNet) continue;

                var (tx, ty) = owner.PortWorld(k);
                var (ux, uy) = owner.ApproachWorld(k);

                // Not a name any SPICE net can have, so it cannot collide with one the file wrote.
                string net = $"{GroundNet}#{n++}";
                owner.Nets[k] = net;

                var ground = new EditableComponent
                {
                    Symbol   = SymbolKind.Ground,
                    X        = tx + ux * GroundLead,
                    Y        = ty + uy * GroundLead,
                    // A Ground's stem is drawn along local +Y, so the rotation is chosen to point it
                    // AWAY from the terminal — otherwise the glyph is drawn back over its own lead.
                    Rotation = (ux, uy) switch
                    {
                        (0, > 0) => SymbolRotation.R0,
                        (< 0, 0) => SymbolRotation.R90,
                        (0, < 0) => SymbolRotation.R180,
                        _        => SymbolRotation.R270,
                    },
                };
                model.Components.Add(ground);

                made.Add(new Placed
                {
                    Component  = ground,
                    LocalPorts = [(0f, 0f)],
                    // A ground's connection point IS its origin, so its own geometry says nothing
                    // about which way a wire leaves it. It leaves back along the stem, always.
                    LocalApproach = [(0, -1)],
                    Nets          = [net],
                });
            }

        return made;
    }

    /// <summary>
    /// The direction a lead points, from the sign of its own offset. Every built-in terminal sits
    /// off-centre on exactly one axis, which is what makes this well defined everywhere it is used.
    /// </summary>
    private static (int X, int Y) Outward((float X, float Y) local)
        => (Math.Sign(local.X), Math.Sign(local.Y));

    // ─────────────────────────────────────────────────────────────────────────
    //  Writing
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the cell folder — and one more folder for every subcircuit this one calls, because a
    /// circuitRF cell instance references a cell FOLDER and a nested definition has nowhere else to
    /// live.
    /// </summary>
    public static SubcircuitCellResult Write(
        string                               parentDir,
        string                               cellName,
        SubcircuitTranslation                top,
        IReadOnlyList<SubcircuitTranslation> all,
        string?                              sourceFile = null)
        => WriteMany(parentDir, [(top, cellName)], all, sourceFile);

    /// <summary>
    /// Writes several chosen definitions from one file in ONE gesture, over ONE shared dependency
    /// plan — so a core two of them are built from is written once and neither blocks the other.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is not N calls to <see cref="Write"/>.</b> Two top-level parts in a library
    /// file routinely share a core (measured: 4 shared cells in one file, 1 in another). Importing A
    /// writes that core; importing B then plans the same folder, finds it there, and — under the
    /// never-overwrite rule, which is right — refuses. The second variant was permanently
    /// unimportable. One plan across every chosen definition makes the collision impossible rather
    /// than merely recoverable.</para>
    ///
    /// <para><b>All or nothing across every folder</b>, not merely the ones that were asked for: a
    /// nested import that half-succeeded would leave a parent cell pointing at a child that is not
    /// there, which the workspace scanner lists and a user places.</para>
    ///
    /// <para><b>An EXISTING folder is still never written over.</b> It is REUSED when the cell in it
    /// can be PROVEN to be the same definition — its recorded provenance hash matches both what this
    /// import would write and what is on disk right now — and refused otherwise, with a sentence that
    /// says which of the two it was. "Identical" is proven, never assumed from the name.</para>
    /// </remarks>
    public static SubcircuitCellResult WriteMany(
        string                                                       parentDir,
        IReadOnlyList<(SubcircuitTranslation Top, string CellName)>  tops,
        IReadOnlyList<SubcircuitTranslation>                         all,
        string?                                                      sourceFile = null)
    {
        ArgumentNullException.ThrowIfNull(tops);
        ArgumentNullException.ThrowIfNull(all);
        if (tops.Count == 0) throw new InvalidOperationException("Nothing was chosen to import.");

        foreach (var (t, _) in tops)
            if (t.Refusal is { } refusal)
                throw new InvalidOperationException($"'{t.Name}' was refused; nothing to write. {refusal}");

        var byName = new Dictionary<string, SubcircuitTranslation>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in all) byName.TryAdd(t.Name, t);

        // ── The plan ──────────────────────────────────────────────────────────
        // Every cell this import creates, leaf-first, with the folder name each gets. A CHOSEN
        // definition takes the name the user typed; one that is only a dependency takes its own,
        // because there is nobody to ask and its .subckt name is what the file already calls it.
        //
        // The chosen names are assigned FIRST, so a definition that is both chosen and a dependency
        // of another chosen one is written once, under the name the user gave it.
        var folderName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (t, name) in tops) folderName[t.Name] = name;

        var plan = new List<(SubcircuitTranslation T, string Name, bool Chosen)>();
        var planned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(SubcircuitTranslation t, bool chosen)
        {
            if (!planned.Add(t.Name)) return;
            plan.Add((t, folderName.TryGetValue(t.Name, out var n) ? n : SafeCellName(t.Name), chosen));
        }

        foreach (var (t, _) in tops)
        {
            foreach (string dep in t.Dependencies)
            {
                if (!byName.TryGetValue(dep, out var d))
                    throw new InvalidOperationException($"'{dep}' is called but was not translated.");
                Add(d, chosen: folderName.ContainsKey(dep));
            }
            Add(t, chosen: true);
        }

        var byFolder = new Dictionary<string, SubcircuitTranslation>(StringComparer.OrdinalIgnoreCase);
        foreach (var (t, name, _) in plan)
            if (!byFolder.TryAdd(name, t))
                throw new IOException(
                    $"Two of the subcircuits this import needs would both be called '{name}'. "
                    + "Rename one in the file, or import them separately.");

        // Every cell lands in the same folder, so a parent's schematic reaches a child by climbing
        // out of its own schematic/ sub-folder and its own cell folder. Written with forward slashes
        // because that is what a stored CellRef is.
        var refByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (t, name, _) in plan) refByName[t.Name] = "../../" + name;

        // ── What each cell WOULD be, before anything touches the disk ─────────
        var built = new List<(SubcircuitTranslation T, string Name, bool Chosen, CellContent Content)>();
        foreach (var (t, name, chosen) in plan)
            built.Add((t, name, chosen, BuildContent(name, t, n => refByName[n], sourceFile)));

        // ── Existing folders: reuse what is provably the same, refuse the rest ─
        var reuse = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (t, name, _, content) in built)
        {
            string dir = Path.Combine(parentDir, name);
            if (!Directory.Exists(dir)) continue;

            if (ExistingCellVerdict(dir, name, content) is { } why) throw new IOException(why);
            reuse.Add(name);
        }

        var written = new List<string>();
        var report  = new List<string>();
        string primaryName = folderName[tops[0].Top.Name];

        try
        {
            string? firstDir = null, firstSchematic = null;

            foreach (var (t, name, chosen, content) in built)
            {
                string dir, schematic;

                if (reuse.Contains(name))
                {
                    dir       = Path.Combine(parentDir, name);
                    schematic = Path.Combine(
                        CellFolder.SubFolderPath(dir, ViewType.Schematic), content.SchematicFile);
                    report.Add(
                        $"'{name}' is already in this workspace and is the same definition, so it was "
                        + "reused rather than written again.");
                }
                else
                {
                    (dir, schematic) = WriteOne(parentDir, name, content);
                    written.Add(dir);
                    report.Add(Summary(t, name));
                    report.AddRange(content.Report);
                    report.AddRange(NotCarried(t));
                }

                // The cell the caller opens is the FIRST one the user chose, not the first chosen
                // entry in plan order — a chosen definition that another chosen one depends on is
                // planned ahead of it, and opening the core instead of the part is the wrong answer.
                if (string.Equals(name, primaryName, StringComparison.OrdinalIgnoreCase))
                    { firstDir = dir; firstSchematic = schematic; }
            }

            int extra = built.Count - tops.Count;
            if (extra > 0)
                report.Add(
                    $"{string.Join(", ", tops.Select(p => $"'{p.CellName}'"))} "
                    + $"call{(tops.Count == 1 ? "s" : "")} {extra} other subcircuit(s), so a cell was "
                    + "created for each: "
                    + string.Join(", ", built.Where(b => !b.Chosen).Select(b => b.Name)) + ".");

            // Everything created that is not the cell the caller will open — the shared cores and
            // the OTHER chosen definitions alike. Reported rather than left to be discovered: an
            // import that silently adds cells to a workspace is one nobody can undo without knowing
            // which ones.
            var also = built.Select(b => Path.Combine(parentDir, b.Name))
                            .Where(d => !string.Equals(d, firstDir, StringComparison.Ordinal))
                            .ToList();

            return new SubcircuitCellResult(firstDir!, firstSchematic!, also, report);
        }
        catch
        {
            foreach (string dir in written) TryDeleteFolder(dir);
            throw;
        }
    }

    /// <summary>
    /// Why an existing cell folder cannot be reused, or null when it provably can.
    ///
    /// <para>Three outcomes, and the difference between them is the whole point of recording
    /// provenance: a cell this import already wrote (reuse), a cell that was written from this
    /// definition and has been EDITED since (refuse, and say so — overwriting would discard the
    /// user's work), and a cell that is simply something else under the same name (refuse, today's
    /// sentence).</para>
    /// </summary>
    private static string? ExistingCellVerdict(string cellDir, string name, CellContent content)
    {
        string generic =
            $"A cell named '{name}' already exists here, and this import needs that name. Importing a "
            + "subcircuit never writes over a cell that is already in the workspace.";

        CcellFile existing;
        try { existing = CellPersistence.LoadFromFile(Path.Combine(cellDir, CellFolder.CcellFileName)); }
        catch { return generic; }

        if (existing.ImportedFrom is not { } provenance || provenance.ContentHash.Length == 0)
            return generic;

        string? onDisk = HashOnDisk(cellDir, existing, content);

        if (onDisk is null || !string.Equals(onDisk, provenance.ContentHash, StringComparison.Ordinal))
            return
                $"A cell named '{name}' already exists here and has been edited since it was imported "
                + $"from {Describe(provenance)}, so it is no longer that definition. Importing never "
                + "writes over a cell in the workspace — rename or remove it first if the edit is no "
                + "longer wanted.";

        if (!string.Equals(content.Hash, provenance.ContentHash, StringComparison.Ordinal))
            return
                $"A cell named '{name}' already exists here and was imported from "
                + $"{Describe(provenance)}, which is a different definition from the one being "
                + "imported now. Importing never writes over a cell that is already in the workspace.";

        return null;

        static string Describe(CcellImportProvenance p)
            => p.Source.Length > 0 ? $"'{p.Definition}' in {p.Source}" : $"'{p.Definition}'";
    }

    /// <summary>
    /// The hash of what is on disk RIGHT NOW, over exactly the same three inputs the recorded hash
    /// was taken over. Null when a piece of it cannot be read — which is itself a difference.
    /// </summary>
    private static string? HashOnDisk(string cellDir, CcellFile existing, CellContent content)
    {
        try
        {
            string schematic = Path.Combine(
                CellFolder.SubFolderPath(cellDir, ViewType.Schematic),
                existing.PrimarySchematic ?? content.SchematicFile);
            string symbol = Path.Combine(
                CellFolder.SubFolderPath(cellDir, ViewType.Symbol),
                existing.PrimarySymbol ?? content.SymbolFile);

            if (!File.Exists(schematic) || !File.Exists(symbol)) return null;

            return HashOf(File.ReadAllText(schematic), File.ReadAllText(symbol), existing.Parameters);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  One cell's content, built before anything is written
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Everything one cell folder will hold, as text — built with no disk access at all, so an import
    /// can decide what it would write BEFORE it starts writing any of it.
    /// </summary>
    private sealed record CellContent(
        string                SchematicFile,
        string                SymbolFile,
        string                SchematicJson,
        string                SymbolJson,
        CcellFile             Ccell,
        string                Hash,
        IReadOnlyList<string> Report);

    private static CellContent BuildContent(
        string cellName, SubcircuitTranslation translation,
        Func<string, string> cellRefFor, string? sourceFile)
    {
        var lines     = new List<string>();
        var schematic = BuildSchematic(translation, cellName, cellRefFor, lines);

        string schematicFile = cellName + CellFolder.ViewExtension(ViewType.Schematic);
        string symbolFile    = cellName + CellFolder.ViewExtension(ViewType.Symbol);

        int ports = translation.Definition.Ports.Count;

        string schematicJson = SchematicPersistence.Serialize(schematic, cellName);
        string symbolJson    = SymbolPersistence.Serialize(AutoSymbolGenerator.Generate(cellName, ports));

        var ccell = new CcellFile
        {
            PrimarySchematic = schematicFile,
            PrimarySymbol    = symbolFile,
            NumPorts         = ports,
        };

        // The .subckt line's own parameter defaults become the cell's published interface — which is
        // what an instance of it is seeded from, and what a caller's overrides bind against.
        foreach (var p in translation.Definition.Parameters)
            ccell.Parameters.Add(new CcellParameter
            {
                Name              = p.Name,
                DefaultExpression = p.DefaultExpression,
                ShowOnSchematic   = false,
            });

        string hash = HashOf(schematicJson, symbolJson, ccell.Parameters);

        ccell.ImportedFrom = new CcellImportProvenance
        {
            Source      = sourceFile is null ? "" : Path.GetFileName(sourceFile),
            Definition  = translation.Definition.Name,
            ContentHash = hash,
        };

        return new CellContent(
            schematicFile, symbolFile, schematicJson, symbolJson, ccell, hash, lines);
    }

    /// <summary>
    /// The identity of a written cell: its schematic, its symbol and its declared interface.
    ///
    /// <para><b>The provenance record itself is deliberately not in it</b> — it CARRIES the hash, so
    /// including it would be circular; and the source file name is not part of what the cell IS.
    /// Two imports of the same definition from two copies of one file are the same cell.</para>
    /// </summary>
    private static string HashOf(string schematicJson, string symbolJson, IEnumerable<CcellParameter> parameters)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(schematicJson).Append('\u0000').Append(symbolJson).Append('\u0000');
        foreach (var p in parameters)
            sb.Append(p.Name).Append('=').Append(p.DefaultExpression).Append('\u0000');

        var bytes = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        return Convert.ToHexStringLower(bytes);
    }

    private static (string CellDir, string SchematicPath) WriteOne(
        string parentDir, string cellName, CellContent content)
    {
        string cellDir = CellFolder.CreateCellFolder(parentDir, cellName);

        string schematicPath = Path.Combine(
            CellFolder.SubFolderPath(cellDir, ViewType.Schematic), content.SchematicFile);
        AtomicFile.WriteAllText(schematicPath, content.SchematicJson);

        string symbolPath = Path.Combine(
            CellFolder.SubFolderPath(cellDir, ViewType.Symbol), content.SymbolFile);
        AtomicFile.WriteAllText(symbolPath, content.SymbolJson);

        CellPersistence.SaveToFile(Path.Combine(cellDir, CellFolder.CcellFileName), content.Ccell);

        return (cellDir, schematicPath);
    }

    /// <summary>
    /// A <c>.subckt</c> name made safe to be a folder. Only what a path component cannot hold is
    /// replaced — the name is otherwise the file's own, because that is what the user looks for in
    /// the tree afterwards.
    /// </summary>
    public static string SafeCellName(string subcircuitName)
    {
        var cleaned = new string([.. subcircuitName.Select(
            c => c <= 0x1F || c is '<' or '>' or ':' or '"' or '/' or '\\' or '|' or '?' or '*' ? '_' : c)])
            .Trim().TrimEnd('.');
        return NameValidator.IsValid(cleaned) ? cleaned : "Subcircuit";
    }

    /// <summary>
    /// Removes a folder this class created. <b>Best effort by design</b>: the caller is already
    /// reporting a failure, and a cleanup that threw would replace that report with a less useful one.
    /// </summary>
    private static void TryDeleteFolder(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* leave it */ }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Reporting
    // ─────────────────────────────────────────────────────────────────────────

    private static string Summary(SubcircuitTranslation translation, string cellName)
        => $"'{cellName}' built from .subckt {translation.Definition.Name} — "
         + $"{translation.Elements.Count} component(s), "
         + $"{translation.Definition.Ports.Count} port(s)"
         + (translation.Definition.Parameters.Count > 0
                ? $", {translation.Definition.Parameters.Count} parameter(s)"
                : "")
         + ".";

    /// <summary>
    /// What did not survive the import, per element. <b>Never suppressed.</b> A card's substrate
    /// junction and its flicker-noise coefficients are real, silently absent from the built cell,
    /// and discoverable in no other way than an answer that is wrong by an amount nobody can
    /// attribute to anything.
    /// </summary>
    private static IEnumerable<string> NotCarried(SubcircuitTranslation translation)
    {
        foreach (var e in translation.Elements)
        {
            if (e.Unmapped.Count > 0)
                yield return
                    $"NOT carried — {e.InstanceName} names model '{e.Reference}', and circuitRF has "
                    + "no parameter for these, so they are absent from the cell: "
                    + string.Join(", ", e.Unmapped) + ".";

            foreach (string note in e.Notes)
                yield return $"{e.InstanceName}: {note}";
        }
    }

    /// <summary>The annotation written onto the cell's own schematic.</summary>
    private static string Annotation(SubcircuitTranslation translation, string cellName)
    {
        string text =
            $"{cellName} — imported from SPICE subcircuit '{translation.Definition.Name}' "
            + $"({translation.Elements.Count} component(s), "
            + $"{translation.Definition.Ports.Count} port(s)).";

        foreach (var e in translation.Elements)
        {
            if (e.Unmapped.Count > 0)
                text += $"\n{e.InstanceName} ({e.Reference}) — not carried: "
                      + string.Join(", ", e.Unmapped) + ".";
            foreach (string note in e.Notes)
                text += $"\n{e.InstanceName}: {note}";
        }

        return text;
    }
}
