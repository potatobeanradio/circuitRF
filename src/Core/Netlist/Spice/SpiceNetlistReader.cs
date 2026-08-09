using System.Text;
using CircuitRF.Core.Design;
using CircuitRF.Core.Elaboration;
using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Netlist.Spice;

/// <summary>
/// Reads a netlist written in the SPICE dialect into the same <see cref="Library"/> of
/// <see cref="Cell"/>s circuitRF's own <c>.cnl</c> produces — so a subcircuit read from one is
/// treated exactly like a cell the user drew.
///
/// <para><b>This reads a FORMAT, not a kit.</b> Nothing here names a supplier, a product, a kit or a
/// model family. It is deliberately not a general-purpose importer either: it reads what a file
/// needs in order to DEFINE A DEVICE, and everything else is reported by file and line and skipped.
/// Analysis directives, plot commands and simulator options are all recognised well enough to be
/// named and passed over — a file full of them must not read as a file full of mysteries.</para>
///
/// <para><b>It is a sibling of <see cref="KitNetlistReader"/>, not an extension of it.</b> That
/// reader takes a different format; what carries across is its SHAPE — an honest note per line it
/// could not use, an explicit set of cells whose definition was only partly read, and no supplier
/// named anywhere. Bending one reader to cover two formats would put two grammars in one state
/// machine and lose exactly those properties.</para>
///
/// <para><b>The distinction that decides what "incomplete" means.</b> A skipped analysis directive
/// does not make a cell incomplete — the circuit is still the one the file wrote. A line of the
/// definition itself that could not be read does, because what is left is a plausible-looking
/// different circuit. An unfamiliar device type is NOT incompleteness either: it is very often a
/// device a provider supplies, and marking it would report the working case as broken.</para>
/// </summary>
public static class SpiceNetlistReader
{
    /// <summary>Nesting limit for inclusion. A cycle is caught by identity; this catches depth.</summary>
    private const int MaxIncludeDepth = 32;

    public static SpiceNetlistResult ReadFile(string path)
    {
        string full  = Path.GetFullPath(path);
        var    lines = File.ReadAllLines(full);

        var session = new Session();
        // The root file is registered as open BEFORE it is read, so a file that includes its way
        // back to the top is caught by the same rule as any other cycle. Registering only what
        // inclusion opens would leave the root as the one file that can be entered twice.
        session.MarkOpen(full);
        session.Run(lines, full, Path.GetDirectoryName(full), section: null, depth: 0);
        return session.Finish(lines.Length);
    }

    /// <summary>
    /// Reads from text. Inclusion needs a directory to resolve against, so a caller reading from a
    /// string may supply one; without it an inclusion is reported rather than attempted, because
    /// resolving against the process's working directory would make the result depend on where
    /// circuitRF happened to be started.
    /// </summary>
    public static SpiceNetlistResult Read(
        string text, string? sourceDirectory = null, string fileLabel = "<text>")
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var session = new Session();
        session.Run(lines, fileLabel, sourceDirectory, section: null, depth: 0);
        return session.Finish(lines.Length);
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  the reader's state
    // ─────────────────────────────────────────────────────────────────────────

    private sealed class Session
    {
        private readonly Library                    _library    = new("spice");
        private readonly List<SpiceNetlistNote>     _notes      = [];
        private readonly List<Variable>             _globals    = [];
        private readonly List<UserFunction>         _functions  = [];
        private readonly List<SpiceModelCard>       _cards      = [];
        private readonly HashSet<string>            _incomplete = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<SpiceStatisticalUse>  _statistics = [];
        private readonly List<string>               _filesRead  = [];

        /// <summary>Section names per file, in declaration order. See SpiceNetlistResult.Sections.</summary>
        private readonly Dictionary<string, List<string>> _sections =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string>            _open       = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Open subcircuits, innermost last. The dialect permits nesting.</summary>
        private readonly List<Cell> _cells = [];

        /// <summary>
        /// Every parameter whose value is known so far, for evaluating a conditional. Flat by
        /// design: a conditional is nearly always guarded on a top-level constant, and a scope chain
        /// here would imply a resolution order this reader does not otherwise have.
        /// </summary>
        private readonly Scope _conditionScope = new("spice");

        private Cell?  Current   => _cells.Count > 0 ? _cells[^1] : null;
        private string CurrentName => _cells.Count > 0 ? _cells[^1].Name : "";

        public void MarkOpen(string path) => _open.Add(path);

        public SpiceNetlistResult Finish(int lastLine)
        {
            if (Current is not null)
                throw new SpiceNetlistException(
                    _filesRead.Count > 0 ? _filesRead[0] : "<text>", lastLine,
                    $"'{CurrentName}' has no '.ends'.");

            // A card may be declared after — or in a different file from — the subcircuit that uses
            // it, so this cannot be done at the element line without making the result depend on
            // read order. See SpicePassiveModelBinding.
            SpicePassiveModelBinding.Bind(
                _library, _cards,
                note:           m => Note(_filesRead.Count > 0 ? _filesRead[0] : "<text>", 0, m),
                markIncomplete: n => _incomplete.Add(n));

            SpicePassiveModelBinding.AlignSubcircuitParameterCase(_library);

            return new SpiceNetlistResult(
                _library, _notes, _globals, _cards, _incomplete, _statistics, _filesRead)
            {
                Functions = _functions,
                Sections  = [.. _sections.Where(kv => kv.Value.Count > 0)
                                         .Select(kv => new SpiceSectionSet(kv.Key, kv.Value))],
            };
        }

        // ── the line loop ─────────────────────────────────────────────────────

        public void Run(
            IReadOnlyList<string> lines, string file, string? directory, string? section, int depth)
        {
            _filesRead.Add(file);

            // Conditional frames are per file. A construct opened in one file and closed in another
            // is not something this dialect expresses, and pretending otherwise would let an
            // unbalanced include silently disable the rest of a netlist.
            var conditions = new List<Condition>();

            // Section state. A file read FOR a section skips everything outside it; a file read
            // whole skips every section, because sections are alternatives and choosing one nobody
            // asked for is a guess.
            bool inSomeSection  = false;
            bool inWantedSection = section is null;

            // Whether the section the caller ASKED for was ever found. A request for one the file
            // does not declare otherwise reads nothing at all and reports nothing at all — and a
            // design elaborated with none of its process constants bound is not an error anywhere,
            // just a set of plausible numbers computed from defaults nobody chose.
            bool foundWanted = section is null;

            foreach (var (text, number) in Join(lines))
            {
                string s = StripComment(text).Trim();
                if (s.Length == 0) continue;

                // ── section framing, which outranks everything including conditionals ──
                if (StartsWithWord(s, ".endl"))
                {
                    inSomeSection = false;
                    inWantedSection = section is null;
                    continue;
                }
                if (StartsWithWord(s, ".lib") && Words(s).Count == 2)
                {
                    string named = Unquote(Words(s)[1]);
                    if (section is not null &&
                        named.Equals(section, StringComparison.OrdinalIgnoreCase)) foundWanted = true;

                    // Recorded whether or not this is the section being read. A file read WHOLE skips
                    // every section deliberately, and that is exactly the pass during which circuitRF
                    // needs to learn what the alternatives ARE — so the names are collected here
                    // rather than anywhere downstream of the skip.
                    if (!_sections.TryGetValue(file, out var namesInFile))
                        _sections[file] = namesInFile = [];
                    if (!namesInFile.Contains(named, StringComparer.OrdinalIgnoreCase))
                        namesInFile.Add(named);

                    inSomeSection = true;
                    inWantedSection = section is not null
                                   && named.Equals(section, StringComparison.OrdinalIgnoreCase);
                    if (section is null)
                        Note(file, number,
                             $"section '{named}' is one of several alternatives in this file and none was " +
                             "requested; skipped rather than chosen.");
                    continue;
                }
                if (inSomeSection && !inWantedSection) continue;

                // ── conditionals, which must be tracked even where nothing is being read ──
                if (StartsWithWord(s, ".if") || StartsWithWord(s, ".elseif") ||
                    StartsWithWord(s, ".else") || StartsWithWord(s, ".endif"))
                {
                    HandleConditional(s, file, number, conditions);
                    continue;
                }
                if (conditions.Any(c => !c.Active)) continue;

                Dispatch(s, file, number, directory, depth);
            }

            if (!foundWanted)
            {
                var offered = _sections.TryGetValue(file, out var names) && names.Count > 0
                    ? $" It offers: {string.Join(", ", names)}."
                    : " It declares no sections at all.";
                Note(file, lines.Count,
                     $"section '{section}' was requested and this file does not declare it, so nothing " +
                     $"was read from it.{offered}");
                MarkIncomplete();
            }

            if (conditions.Count > 0)
                Note(file, lines.Count, $"{conditions.Count} unclosed '.if' — the rest of this file was read as though closed.");
        }

        private void Dispatch(string s, string file, int number, string? directory, int depth)
        {
            if (s[0] == '.')
            {
                DispatchDirective(s, file, number, directory, depth);
                return;
            }

            if (Current is null)
            {
                // An element outside any subcircuit belongs to a test deck, not to a device
                // definition. Named rather than read: reading it would put somebody's testbench into
                // the library as an anonymous cell.
                Note(file, number, $"element '{FirstWord(s)}' is outside any '.subckt'; skipped.");
                return;
            }

            ReadElement(s, file, number);
        }

        private void DispatchDirective(string s, string file, int number, string? directory, int depth)
        {
            var words = Words(s);
            string head = DirectiveHead(s);

            switch (head)
            {
                case ".subckt":
                    ReadSubcktHeader(s, words, file, number);
                    return;

                case ".ends":
                case ".eom":
                    CloseSubckt(words, file, number);
                    return;

                case ".param":
                case ".params":
                case ".parameter":
                    ReadParams(s, file, number);
                    return;

                case ".func":
                case ".function":
                    ReadFunction(s, file, number);
                    return;

                case ".model":
                    ReadModelCard(s, words, file, number);
                    return;

                case ".include":
                case ".inc":
                    ReadInclude(words, file, number, directory, depth, section: null);
                    return;

                case ".lib":
                    // The one-word form was handled as section framing before dispatch, so this can
                    // only be the two-argument form: a file plus the section wanted from it.
                    if (words.Count >= 3) ReadInclude(words, file, number, directory, depth, section: Unquote(words[2]));
                    else Note(file, number, $"'{s}' names no file to read; skipped.");
                    return;
            }

            if (Ignorable.Contains(head))
            {
                Note(file, number, $"'{head}' is a simulator directive, not part of a device definition; skipped.");
                return;
            }

            Note(file, number, $"unrecognised directive, skipped: {Shorten(s)}");
            MarkIncomplete();
        }

        // ── .subckt / .ends ───────────────────────────────────────────────────

        private void ReadSubcktHeader(string s, List<string> words, string file, int number)
        {
            if (words.Count < 2) throw new SpiceNetlistException(file, number, "'.subckt' names no cell.");

            var cell = new Cell(words[1]);

            // Ports run until the first parameter binding. `params:` is a separator some files write
            // between the two and is not a port.
            var (bare, assignments) = SplitBareAndAssignments(words.Skip(2));
            foreach (string p in bare)
            {
                if (p.Equals("params:", StringComparison.OrdinalIgnoreCase)) continue;
                cell.Ports.Add(p);
            }

            foreach (var (name, value) in assignments)
            {
                string expr = Rewrite(value);
                cell.Parameters.Add(new ParameterDeclaration(name, expr));
                _conditionScope.Bind(name, expr);
            }

            _cells.Add(cell);
        }

        private void CloseSubckt(List<string> words, string file, int number)
        {
            if (Current is null) throw new SpiceNetlistException(file, number, "'.ends' without '.subckt'.");

            // A named '.ends' whose name does not match is REPORTED, not refused.
            //
            // This was a hard error, on the reasoning that the file is not nested the way it appears
            // and every later cell would be attributed wrongly. That reasoning is wrong: '.ends'
            // closes the innermost open subcircuit whatever name follows it — that is the dialect's
            // own rule, and every simulator reads it that way — so the nesting is not in doubt and
            // the name is decoration. Refusing lost an entire model library over one stray suffix,
            // measured whose own '.ends diodevdd_4kv_mod' closes 'diodevdd_4kv'. The
            // kit is wrong; nothing had noticed, because nothing else reads that name either.
            if (words.Count > 1 && !words[1].Equals(CurrentName, StringComparison.OrdinalIgnoreCase))
                Note(file, number,
                     $"'.ends {words[1]}' closes '{CurrentName}', which is a different name. The " +
                     "subcircuit is closed anyway, as the name after '.ends' carries no meaning.");

            _library.Cells.Add(Current);
            _cells.RemoveAt(_cells.Count - 1);
        }

        // ── .param / .func / .model ───────────────────────────────────────────

        private void ReadParams(string s, string file, int number)
        {
            var (_, assignments) = SplitBareAndAssignments(Words(s).Skip(1));
            if (assignments.Count == 0)
            {
                Note(file, number, $"'.param' binds nothing; skipped: {Shorten(s)}");
                return;
            }

            foreach (var (name, value) in assignments)
            {
                // The function form: `.param f(a,b) = expr`. It is a declaration, not a binding, and
                // reading it as one would put a name with brackets in it into the scope.
                if (TryReadFunctionForm(name, value, file, number)) continue;

                string expr = Rewrite(value);
                _conditionScope.Bind(name, expr);

                if (Current is null) { _globals.Add(new Variable(name, expr)); continue; }

                // A '.param' INSIDE a subcircuit is a declaration with a default, not an internal
                // variable — this dialect lets a call site override one, and a kit relies on it: the
                // MIM capacitor states its width and length exactly this way and there is no other
                // way to set them. Read as a variable the geometry is sealed shut, so a placed part
                // has one size forever, which is the whole point of the part gone.
                //
                // The '.subckt' line's own bindings are the more specific statement of the same
                // thing and win; a name declared twice is the file's own contradiction and is said
                // out loud rather than resolved silently.
                if (Current.Parameters.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                {
                    Note(file, number,
                         $"'{name}' was already declared on the '.subckt' line of '{CurrentName}'; " +
                         "that declaration is the one kept.");
                    continue;
                }

                Current.Parameters.Add(new ParameterDeclaration(name, expr));
            }
        }

        private void ReadFunction(string s, string file, int number)
        {
            var (bare, assignments) = SplitBareAndAssignments(Words(s).Skip(1));

            // Both spellings occur: `.func f(a,b) = expr` and `.func f(a,b) {expr}`.
            if (assignments.Count == 1 && TryReadFunctionForm(assignments[0].Name, assignments[0].Value, file, number))
                return;
            if (bare.Count >= 2 && TryReadFunctionForm(bare[0], string.Join(' ', bare.Skip(1)), file, number))
                return;

            Note(file, number, $"'.func' could not be read; skipped: {Shorten(s)}");
            MarkIncomplete();
        }

        private bool TryReadFunctionForm(string lhs, string body, string file, int number)
        {
            int open = lhs.IndexOf('(');
            if (open <= 0 || !lhs.EndsWith(')')) return false;

            string name = lhs[..open].Trim();
            var args = lhs[(open + 1)..^1]
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.Trim())
                .ToArray();
            if (args.Length == 0 || !args.All(IsIdentifier) || !IsIdentifier(name)) return false;

            // The body is the file's, not ours — an unreadable one is reported, never approximated.
            try { _functions.Add(new UserFunction(name, args, Rewrite(body))); }
            catch (Exception ex)
            {
                Note(file, number, $"'{name}' could not be read as a function ({ex.Message}); skipped.");
                MarkIncomplete();
            }
            return true;
        }

        private void ReadModelCard(string s, List<string> words, string file, int number)
        {
            if (words.Count < 2)
            {
                Note(file, number, $"'.model' names nothing; skipped: {Shorten(s)}");
                MarkIncomplete();
                return;
            }

            string name = words[1];

            // The type and its parameter block are read off the RAW text rather than off the word
            // list, because the block's opening bracket is routinely glued to the type — `nmos(...)`
            // — and the tokeniser deliberately keeps a bracketed run whole. Splitting the word on the
            // bracket afterwards would leave the type spelled `nmos(level` on every such card.
            //
            // The brackets themselves are optional in this dialect and carry no meaning: they group
            // nothing, so they are removed rather than parsed.
            int nameAt = s.IndexOf(name, StringComparison.Ordinal);
            string rest = nameAt < 0 ? "" : s[(nameAt + name.Length)..].Trim();

            // THE BRACKET ONLY OPENS THE PARAMETER BLOCK WHEN IT IS PART OF THE TYPE'S OWN WORD.
            // Searching the whole line for the first '(' is what a bracket-less card gets wrong, and
            // it gets it wrong silently: a kit writes `.model X mdla_va type=+1 … dlq =
            // '5.2202e-08-((1-pre_layout)*0.0)'`, whose first bracket is hundreds of characters into
            // a parameter VALUE. Everything before it then becomes the "type" and the card is left
            // with NO parameters at all — a model reference that resolves to nothing and a parameter
            // set that vanished, neither of which reports itself.
            var parts = Words(rest);
            string head = parts.Count > 0 ? parts[0] : "";

            string type, body;
            int    open = head.IndexOf('(');
            if (open >= 0)
            {
                type = head[..open].Trim();
                body = rest[(rest.IndexOf('(') + 1)..].TrimEnd();
                if (body.EndsWith(')')) body = body[..^1];
            }
            else
            {
                type = parts.Count > 0 ? parts[0] : "";
                body = parts.Count > 1 ? string.Join(' ', parts.Skip(1)) : "";

                // `type (a=1 b=2)` — the bracket detached from the type. The tokeniser keeps a
                // bracketed run whole, so it arrives as one word that no assignment split can read.
                body = body.Trim();
                if (body.StartsWith('(') && body.EndsWith(')')) body = body[1..^1];
            }

            if (type.Length == 0)
            {
                Note(file, number, $"'.model {name}' names no type; skipped.");
                MarkIncomplete();
                return;
            }

            var (_, assignments) = SplitBareAndAssignments(Words(body));

            var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in assignments) parameters[k] = Rewrite(v);

            var existing = _cards.FindIndex(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (existing >= 0)
                Note(file, number,
                     $"model '{name}' was already defined; the later definition is the one kept, " +
                     "and the two are not necessarily the same parameter set.");

            var card = new SpiceModelCard(name, type, parameters);
            if (existing >= 0) _cards[existing] = card; else _cards.Add(card);
        }

        // ── inclusion ─────────────────────────────────────────────────────────

        private void ReadInclude(
            List<string> words, string file, int number, string? directory, int depth, string? section)
        {
            if (words.Count < 2)
            {
                Note(file, number, "no file named; skipped.");
                return;
            }

            string named = Unquote(words[1]);

            if (directory is null)
            {
                Note(file, number,
                     $"'{named}' cannot be read: this netlist was read from text, so there is no " +
                     "directory to resolve it against.");
                MarkIncomplete();
                return;
            }

            if (depth >= MaxIncludeDepth)
            {
                Note(file, number, $"'{named}' is nested more than {MaxIncludeDepth} deep; not read.");
                MarkIncomplete();
                return;
            }

            string path = Path.IsPathRooted(named) ? named : Path.Combine(directory, named);
            path = Path.GetFullPath(path);

            // Identity, not name: the same file reached by two different relative paths is one file,
            // and a cycle through it would otherwise recurse until the depth limit rather than being
            // reported as what it is.
            if (!_open.Add(path))
            {
                Note(file, number, $"'{named}' includes itself, directly or through another file; not read again.");
                return;
            }

            try
            {
                if (!File.Exists(path))
                {
                    Note(file, number, $"'{named}' was not found; not read.");
                    MarkIncomplete();
                    return;
                }

                var lines = File.ReadAllLines(path);
                Run(lines, path, Path.GetDirectoryName(path), section, depth + 1);
            }
            catch (IOException ex)
            {
                Note(file, number, $"'{named}' could not be read ({ex.Message}); skipped.");
                MarkIncomplete();
            }
            finally { _open.Remove(path); }
        }

        // ── conditionals ──────────────────────────────────────────────────────

        private sealed class Condition
        {
            public bool Active;       // are lines in this branch being read
            public bool AnyTaken;     // has a branch of this construct already been taken
            public bool Unreadable;   // the condition could not be evaluated: take NO branch
        }

        private void HandleConditional(string s, string file, int number, List<Condition> stack)
        {
            switch (DirectiveHead(s))
            {
                case ".if":
                {
                    bool enclosingActive = stack.All(c => c.Active);
                    bool? taken = enclosingActive ? Evaluate(ConditionText(s), file, number) : false;

                    stack.Add(taken is null
                        ? new Condition { Active = false, AnyTaken = true, Unreadable = true }
                        : new Condition { Active = taken.Value, AnyTaken = taken.Value });
                    return;
                }

                case ".elseif":
                {
                    if (stack.Count == 0) { Note(file, number, "'.elseif' without '.if'; skipped."); return; }
                    var top = stack[^1];
                    if (top.Unreadable || top.AnyTaken) { top.Active = false; return; }

                    bool? taken = Evaluate(ConditionText(s), file, number);
                    if (taken is null) { top.Active = false; top.Unreadable = true; return; }
                    top.Active   = taken.Value;
                    top.AnyTaken = taken.Value;
                    return;
                }

                case ".else":
                {
                    if (stack.Count == 0) { Note(file, number, "'.else' without '.if'; skipped."); return; }
                    var top = stack[^1];
                    top.Active = !top.AnyTaken && !top.Unreadable;
                    return;
                }

                default:   // .endif
                    if (stack.Count == 0) { Note(file, number, "'.endif' without '.if'; skipped."); return; }
                    stack.RemoveAt(stack.Count - 1);
                    return;
            }
        }

        /// <summary>The text between the brackets of <c>.if(...)</c>, or the rest of the line.</summary>
        private static string ConditionText(string s)
        {
            int open = s.IndexOf('(');
            int close = s.LastIndexOf(')');
            if (open > 0 && close > open) return s[(open + 1)..close];

            int space = s.IndexOf(' ');
            return space > 0 ? s[(space + 1)..] : "";
        }

        /// <summary>
        /// Evaluates a conditional, or answers null when it cannot be — an unresolved name, a
        /// construct outside this reader's grammar.
        ///
        /// <para>Null is NOT read as false. A false answer silently deletes the guarded block; the
        /// caller of this method skips the whole construct instead, takes no branch at all, and marks
        /// the cell incomplete — so the outcome is "circuitRF could not read this", which is true,
        /// rather than "the file said no", which is a claim nothing here can make.</para>
        /// </summary>
        private bool? Evaluate(string condition, string file, int number)
        {
            string text = Rewrite(condition);
            if (text.Length == 0) return null;

            try
            {
                var value = new Evaluator().Eval(text, _conditionScope);
                switch (value.Kind)
                {
                    case ValueKind.Bool: return value.AsBool();
                    case ValueKind.Real: return value.AsReal() != 0.0;
                    default:
                        Note(file, number,
                             $"the condition '{Shorten(condition)}' is {value.Kind}, which is neither " +
                             "true nor false; no branch of this conditional was read.");
                        MarkIncomplete();
                        return null;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Note(file, number,
                     $"the condition '{Shorten(condition)}' could not be evaluated ({ex.Message}); " +
                     "no branch of this conditional was read.");
                MarkIncomplete();
                return null;
            }
        }

        // ── elements ──────────────────────────────────────────────────────────

        /// <summary>How many nets an element's leading letter requires, and whether it names a model.</summary>
        private static readonly Dictionary<char, (int Nets, bool NamesModel)> Elements = new()
        {
            ['R'] = (2, false),
            ['C'] = (2, false),
            ['L'] = (2, false),
            ['D'] = (2, true),
            ['Q'] = (3, true),      // a fourth net is the substrate; handled below
            ['M'] = (4, true),
            // How a device backed by a COMPILED model is instantiated. Its terminal count is the
            // model's, not the letter's, so the minimum is the smallest a device can have and the
            // name-is-the-last-bare-word rule covers the rest — which is why that rule was written
            // that way. Observed on a kit as a four-terminal resistor with a thermal node.
            ['N'] = (2, true),
            ['X'] = (0, true),      // a subcircuit call: as many nets as the definition has ports
        };

        private void ReadElement(string s, string file, int number)
        {
            var words = Words(s);
            string name = words[0];
            char letter = char.ToUpperInvariant(name[0]);

            if (!Elements.TryGetValue(letter, out var shape))
            {
                Note(file, number,
                     $"element '{name}' is of a kind this reader does not read ('{letter}'); skipped.");
                MarkIncomplete();
                return;
            }

            var (bare, assignments) = SplitBareAndAssignments(words.Skip(1));

            string? area = null;
            if (shape.NamesModel && letter != 'X' && bare.Count > shape.Nets + 1 &&
                SpiceNumber.TryParse(bare[^1], out double areaValue))
            {
                // A bare number in the last position is the device's area, which this dialect allows
                // to be written positionally as well as by name.
                area = SpiceNumber.Normalise(areaValue);
                bare.RemoveAt(bare.Count - 1);
            }

            string reference;
            var    overrides = new List<ParameterAssignment>();
            List<string> nets;

            if (letter is 'R' or 'C' or 'L')
            {
                if (bare.Count < 2)
                {
                    Note(file, number, $"'{name}' gives {bare.Count} net(s), needs 2; skipped.");
                    MarkIncomplete();
                    return;
                }

                nets = bare.Take(2).ToList();
                var rest = bare.Skip(2).ToList();

                // The third word is the VALUE when it reads as one and the name of a model card when
                // it does not. Both spellings are ordinary, and nothing but the word itself
                // distinguishes them.
                if (rest.Count > 0 && LooksLikeValue(rest[0]))
                {
                    reference = letter.ToString();
                    overrides.Add(new ParameterAssignment(letter.ToString(), Rewrite(rest[0])));
                    rest.RemoveAt(0);
                }
                else if (rest.Count > 0)
                {
                    reference = rest[0];
                    rest.RemoveAt(0);
                }
                else
                {
                    // No positional value: it has to have arrived as `R=…`, and if it did not, the
                    // component has no value at all and saying so beats emitting a default.
                    reference = letter.ToString();
                    if (!assignments.Any(a => a.Name.Equals(letter.ToString(), StringComparison.OrdinalIgnoreCase)))
                    {
                        Note(file, number, $"'{name}' has no value and names no model; skipped.");
                        MarkIncomplete();
                        return;
                    }
                }

                if (rest.Count > 0)
                    Note(file, number, $"'{name}' has {rest.Count} unexpected extra word(s), ignored: {string.Join(' ', rest)}");
            }
            else
            {
                // Everything else ends with the name of what implements it, whether that is a model
                // card or a subcircuit. Taking it from the END rather than by position is what lets
                // one rule cover a three- and a four-terminal device, and a subcircuit call of any
                // width, without guessing.
                if (bare.Count < shape.Nets + 1)
                {
                    Note(file, number,
                         $"'{name}' gives {bare.Count} word(s) where {shape.Nets + 1} are needed " +
                         $"({shape.Nets} net(s) and a name); skipped.");
                    MarkIncomplete();
                    return;
                }

                reference = bare[^1];
                nets = bare.Take(bare.Count - 1).ToList();
            }

            if (area is not null) overrides.Add(new ParameterAssignment("area", area));
            foreach (var (k, v) in assignments)
            {
                string spelling = NormaliseInstanceParameter(k);
                if (letter is 'R' or 'C' or 'L') spelling = NormalisePassiveParameter(letter, spelling);
                overrides.Add(new ParameterAssignment(spelling, Rewrite(v)));
            }

            Current!.Instances.Add(new Instance(name, reference, nets, overrides));
        }

        /// <summary>
        /// Whether a passive's third word is its VALUE rather than the name of a model card.
        ///
        /// <para><b>Nothing in the word itself settles this, and both spellings are ordinary.</b> A
        /// bracketed expression and a number are values beyond doubt. A bare identifier is the hard
        /// case: strictly it names a model, but a parameter written bare in the value position is
        /// extremely common and reading it as a model produces a component with no value at all,
        /// pointing at a card that does not exist.</para>
        ///
        /// <para>So the deciding question is one this reader can actually answer: <b>is a parameter
        /// of that name in scope?</b> If it is, the word is a reference to it. If it is not, it can
        /// only be a model name. The remaining collision — a parameter and a model card sharing a
        /// name — resolves to the parameter, and is a spelling nobody writes on purpose.</para>
        /// </summary>
        private bool LooksLikeValue(string w)
            => w.Length > 0
            && (w[0] is '\'' or '{'
                || SpiceNumber.TryParse(w, out _)
                || _conditionScope.Lookup(w) is not null);

        /// <summary>
        /// Spells an instance parameter the way circuitRF does where the two dialects disagree on a
        /// name whose CASE carries meaning.
        ///
        /// <para><b>The device multiplier is the whole of it, and it is a genuine trap.</b> This
        /// dialect is case-insensitive, so <c>M=4</c> and <c>m=4</c> on an instance both mean four
        /// copies in parallel. circuitRF compares parameter names ordinally, and reserves upper-case
        /// <c>M</c> for the junction diode's grading coefficient — on a component that can carry
        /// both. Passing the spelling through verbatim would give a diode written <c>M=4</c> a
        /// grading coefficient of 4 and no multiplier at all, and it would simulate.</para>
        ///
        /// <para>Only the INSTANCE spelling is normalised. On a model card <c>M</c> is the grading
        /// coefficient and means exactly what circuitRF means by it, so a card is left alone.</para>
        /// </summary>
        private static string NormaliseInstanceParameter(string name)
            => name.Equals(Elaborator.MultiplierParamName, StringComparison.OrdinalIgnoreCase)
                ? Elaborator.MultiplierParamName
                : name;

        /// <summary>
        /// circuitRF's own spelling for a parameter of a passive it implements natively.
        ///
        /// <para><b>The same case-insensitivity trap as the multiplier, one letter along.</b> This
        /// dialect writes <c>R1 a b r=55m</c> as readily as <c>R=55m</c>, and circuitRF's resistor
        /// reads <c>R</c> ordinally — so passing the spelling through verbatim gives a resistor with
        /// no value at all. Measured: its MIM capacitor's series resistance is written
        /// lower-case, and every part built on it failed to elaborate.</para>
        ///
        /// <para>Applied to the PASSIVES only. A subcircuit's parameters are the subcircuit's own,
        /// and are aligned to the spelling its definition declared instead — see
        /// <see cref="SpicePassiveModelBinding"/>.</para>
        /// </summary>
        private static string NormalisePassiveParameter(char letter, string name)
        {
            if (name.Equals(letter.ToString(), StringComparison.OrdinalIgnoreCase))
                return char.ToUpperInvariant(letter).ToString();

            foreach (string canonical in NativePassiveParameters)
                if (name.Equals(canonical, StringComparison.OrdinalIgnoreCase)) return canonical;

            return name;
        }

        private static readonly string[] NativePassiveParameters =
            ["TC1", "TC2", "Tnom", "Temp", "Dtemp"];

        // ── shared plumbing ───────────────────────────────────────────────────

        private string Rewrite(string value) => SpiceExpression.Rewrite(value, _statistics);

        private void Note(string file, int line, string message)
            => _notes.Add(new SpiceNetlistNote(file, line, message));

        /// <summary>
        /// Records that the innermost open subcircuit holds something unreadable. Outside any
        /// subcircuit there is no cell to mark, and the note alone carries it.
        /// </summary>
        private void MarkIncomplete()
        {
            if (Current is not null) _incomplete.Add(CurrentName);
        }

        // Directives that belong to a simulator run rather than to a device's definition. Named and
        // skipped: a file full of them must read as understood, not as a file full of mysteries, and
        // none of them makes a cell incomplete.
        private static readonly HashSet<string> Ignorable = new(StringComparer.OrdinalIgnoreCase)
        {
            ".end", ".title", ".global", ".temp", ".option", ".options", ".op", ".dc", ".ac",
            ".tran", ".noise", ".print", ".plot", ".save", ".probe", ".meas", ".measure", ".ic",
            ".nodeset", ".width", ".four", ".disto", ".pz", ".sens", ".tf", ".data", ".enddata",
            ".protect", ".unprotect", ".alter", ".del", ".step", ".control", ".endc", ".csparam",
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  line assembly and tokenising — static, and shared by the session
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Joins continued lines, reporting each joined line at the number it STARTED on — which is
    /// where a reader of the file would look for it.
    ///
    /// <para>Continuation is a leading <c>+</c> on the FOLLOWING line, which is the dialect's rule
    /// and is the opposite way round from a trailing marker. Full-line comments between a line and
    /// its continuation do not break it, because that is where people put them.</para>
    /// </summary>
    internal static IEnumerable<(string Text, int Number)> Join(IReadOnlyList<string> lines)
    {
        var sb = new StringBuilder();
        int start = 0;

        for (int i = 0; i < lines.Count; i++)
        {
            string trimmed = lines[i].Trim();

            if (trimmed.Length == 0 || trimmed[0] == '*') continue;

            if (trimmed[0] == '+')
            {
                if (sb.Length == 0) { start = i + 1; sb.Append(trimmed[1..].Trim()); }
                else sb.Append(' ').Append(trimmed[1..].Trim());
                continue;
            }

            if (sb.Length > 0) yield return (sb.ToString(), start);
            sb.Clear();
            start = i + 1;
            sb.Append(trimmed);
        }

        if (sb.Length > 0) yield return (sb.ToString(), start);
    }

    /// <summary>
    /// Removes a trailing comment. <c>$</c> and <c>;</c> both start one, and neither is honoured
    /// inside a quoted string — a file path is data.
    /// </summary>
    internal static string StripComment(string s)
    {
        bool inQuotes = false;
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '"' || s[i] == '\'') inQuotes = !inQuotes;
            else if (!inQuotes && (s[i] == '$' || s[i] == ';')) return s[..i];
        }
        return s;
    }

    /// <summary>
    /// Splits on whitespace, keeping quoted strings and bracketed expressions whole — an expression
    /// in this dialect routinely contains spaces, and splitting one produces a value plus several
    /// words that look exactly like net names.
    /// </summary>
    internal static List<string> Words(string s)
    {
        var result = new List<string>();
        var sb = new StringBuilder();
        char quote = '\0';
        int depth = 0;

        foreach (char c in s)
        {
            if (quote != '\0')
            {
                sb.Append(c);
                if (c == quote) quote = '\0';
                continue;
            }

            if (c is '"' or '\'') { quote = c; sb.Append(c); continue; }
            if (c is '(' or '{')  { depth++;  sb.Append(c); continue; }
            if (c is ')' or '}')  { depth--;  sb.Append(c); continue; }

            if (depth <= 0 && char.IsWhiteSpace(c))
            {
                if (sb.Length > 0) { result.Add(sb.ToString()); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) result.Add(sb.ToString());
        return result;
    }

    /// <summary>
    /// Separates bare words from <c>name=value</c> bindings, accepting the value split from its name
    /// by spaces (<c>w = 1u</c>) as well as glued to it.
    ///
    /// <para>Bare words are returned in order and a binding never becomes one, so a caller reading
    /// positional fields can rely on the leading run being exactly what the file wrote there.</para>
    /// </summary>
    internal static (List<string> Bare, List<(string Name, string Value)> Assignments)
        SplitBareAndAssignments(IEnumerable<string> words)
    {
        var list = words.ToList();
        var bare = new List<string>();
        var assignments = new List<(string, string)>();

        for (int i = 0; i < list.Count; i++)
        {
            string w = list[i];
            int eq = IndexOfBinding(w);

            if (eq < 0)
            {
                // `name = value` written with spaces: three words that are one binding.
                if (i + 2 < list.Count && list[i + 1] == "=" && IndexOfBinding(list[i + 2]) < 0)
                {
                    assignments.Add((w, list[i + 2]));
                    i += 2;
                    continue;
                }

                // `name =value` — the '=' glued to the VALUE rather than to the name. Two words, one
                // binding. Measured this is how it writes every statistical parameter,
                // 75 of them, and without this both halves fall through as bare words and the
                // binding vanishes: the file reads cleanly and declares nothing.
                if (i + 1 < list.Count && list[i + 1].StartsWith('=') && list[i + 1].Length > 1)
                {
                    assignments.Add((w, list[i + 1][1..].Trim()));
                    i++;
                    continue;
                }

                bare.Add(w);
                continue;
            }

            string name  = w[..eq].Trim();
            string value = w[(eq + 1)..].Trim();

            // `name= value`: the value is the next word.
            if (value.Length == 0 && i + 1 < list.Count && IndexOfBinding(list[i + 1]) < 0)
                value = list[++i];

            if (name.Length == 0) { bare.Add(w); continue; }
            assignments.Add((name, value));
        }

        return (bare, assignments);
    }

    /// <summary>Index of a binding <c>=</c> — never one belonging to <c>==</c>, <c>&lt;=</c>, <c>!=</c>.</summary>
    internal static int IndexOfBinding(string s)
    {
        char quote = '\0';
        int depth = 0;

        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; continue; }
            if (c is '(' or '{') { depth++; continue; }
            if (c is ')' or '}') { depth--; continue; }
            if (depth > 0 || c != '=') continue;

            if (i + 1 < s.Length && s[i + 1] == '=') { i++; continue; }
            if (i > 0 && s[i - 1] is '=' or '<' or '>' or '!') continue;
            return i;
        }
        return -1;
    }

    /// <summary>
    /// The directive's own name, lower-cased: the leading dot and the letters after it, and nothing
    /// else.
    ///
    /// <para><b>Not the first whitespace-separated word.</b> A condition is written <c>.if(x==1)</c>
    /// as readily as <c>.if (x==1)</c>, and the tokeniser deliberately keeps a bracketed run whole —
    /// so the first "word" of the glued spelling is the whole directive, matches nothing, and falls
    /// through to whatever the last arm of the switch happens to be.</para>
    /// </summary>
    internal static string DirectiveHead(string s)
    {
        int i = 1;                                    // past the leading '.'
        while (i < s.Length && char.IsAsciiLetter(s[i])) i++;
        return s[..i].ToLowerInvariant();
    }

    internal static bool StartsWithWord(string s, string word)
        => s.StartsWith(word, StringComparison.OrdinalIgnoreCase)
        && (s.Length == word.Length || !char.IsAsciiLetterOrDigit(s[word.Length]));

    internal static string FirstWord(string s)
    {
        int i = s.IndexOfAny([' ', '\t']);
        return i < 0 ? s : s[..i];
    }

    internal static string Unquote(string s)
        => s.Length >= 2 && (s[0] == '"' || s[0] == '\'') && s[^1] == s[0] ? s[1..^1] : s;

    internal static bool IsIdentifier(string s)
        => s.Length > 0
        && (char.IsLetter(s[0]) || s[0] == '_')
        && s.All(c => char.IsLetterOrDigit(c) || c == '_');

    internal static string Shorten(string s) => s.Length <= 80 ? s : s[..77] + "…";
}
