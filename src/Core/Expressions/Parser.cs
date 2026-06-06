namespace CircuitRF.Core.Expressions;

/// <summary>
/// Pratt (precedence-climbing) parser for the expression grammar (§4).
/// Binding powers follow the precedence table exactly:
///   1  ?: (ternary, right)
///   2  ||
///   3  &&
///   4  == !=
///   5  &lt; &lt;= > >=
///   6  + - (binary)
///   7  * /
///   8  unary - + ! (right-associative prefix)
///   9  ^ (right-associative)
///   10 atoms, calls, parens
/// </summary>
public sealed class Parser
{
    private readonly Token[] _tokens;
    private int _pos;

    private Parser(Token[] tokens) => _tokens = tokens;

    public static Expr Parse(string source)
    {
        var tokens = new Tokenizer(source).Tokenize();
        var p = new Parser(tokens);
        var expr = p.ParseExpr(0);
        if (p.Current.Kind != TokenKind.Eof)
            throw new ParseException($"Unexpected token '{p.Current.Text}'", p.Current.Position);
        return expr;
    }

    // ── Core Pratt loop ──────────────────────────────────────────────────────

    private Expr ParseExpr(int minBp)
    {
        var left = ParsePrefix();

        while (true)
        {
            var (lbp, rbp, isRight) = InfixBp(Current.Kind);
            if (lbp < minBp) break;

            // ternary ? :
            if (Current.Kind == TokenKind.Question)
            {
                Advance(); // consume ?
                var then = ParseExpr(0);
                Expect(TokenKind.Colon, ":");
                var els = ParseExpr(lbp); // right-associative
                left = new ConditionalExpr(left, then, els);
                continue;
            }

            var op = Current.Text;
            var kind = Current.Kind;
            Advance();
            var right = ParseExpr(rbp);
            left = BuildInfix(kind, op, left, right);
        }

        return left;
    }

    private Expr ParsePrefix()
    {
        var t = Current;
        switch (t.Kind)
        {
            case TokenKind.Minus:
                Advance();
                // unary minus binds tighter than ^: -2^2 = -(2^2) = -4
                // so unary minus has lower rbp than ^ lbp (9)
                // prefix bp = 8 (right), we parse at rbp=9 so ^ is grabbed first
                return new UnaryExpr("-", ParseExpr(9));
            case TokenKind.Plus:
                Advance();
                return new UnaryExpr("+", ParseExpr(9));
            case TokenKind.Bang:
                Advance();
                return new UnaryExpr("!", ParseExpr(9));
            case TokenKind.LParen:
                Advance();
                var inner = ParseExpr(0);
                Expect(TokenKind.RParen, ")");
                return inner;
            case TokenKind.StringLiteral:
                Advance();
                return new StringLiteralExpr(t.Text);
            case TokenKind.Number:
                Advance();
                var numVal = double.Parse(t.Text, System.Globalization.CultureInfo.InvariantCulture);
                // implicit n*j: "10j" → Number("10") + Identifier("j")
                if (Current.Kind == TokenKind.Identifier && Current.Text == "j")
                {
                    Advance(); // consume j
                    return new BinaryExpr("*", new NumberExpr(numVal), new ConstExpr("j"));
                }
                return new NumberExpr(numVal);
            case TokenKind.Identifier:
                Advance();
                // reserved constants
                if (t.Text == "j")
                {
                    // implicit j*n: "j3" tokenizes as j + 3 (tokenizer splits on digit boundary)
                    if (Current.Kind == TokenKind.Number)
                    {
                        var n = double.Parse(Advance().Text, System.Globalization.CultureInfo.InvariantCulture);
                        return new BinaryExpr("*", new ConstExpr("j"), new NumberExpr(n));
                    }
                    return new ConstExpr("j");
                }
                if (t.Text == "pi")  return new ConstExpr("pi");
                if (t.Text == "e")   return new ConstExpr("e");
                if (t.Text == "All") return new ConstExpr("All");
                // if(...) keyword → ConditionalExpr (§5 AST spec)
                // Two forms:
                //   canonical:  if(cond, then, else)
                //   extended:   if(cond) then ... [elseif(cond) then ...] else ... endif
                if (t.Text == "if" && Current.Kind == TokenKind.LParen)
                {
                    Advance(); // consume (
                    var cond = ParseExpr(0);
                    if (Current.Kind == TokenKind.Comma)
                    {
                        // Canonical form: if(cond, then, else)
                        Advance(); // consume ,
                        var then = ParseExpr(0); Expect(TokenKind.Comma, ",");
                        var els  = ParseExpr(0); Expect(TokenKind.RParen, ")");
                        return new ConditionalExpr(cond, then, els);
                    }
                    else
                    {
                        // Extended form: if(cond) then expr [elseif(cond) then expr ...] else expr endif
                        Expect(TokenKind.RParen, ")");
                        ExpectKeyword("then");
                        return ParseIfThenChain(cond);
                    }
                }
                // qualified accessor: Analysis.Cube(args) — e.g. HB1.V("n_drain", 1, All)
                // Recognized when: current token is '.', next is Identifier, one after that is '('
                if (Current.Kind == TokenKind.Dot
                    && _pos + 1 < _tokens.Length
                    && _tokens[_pos + 1].Kind == TokenKind.Identifier
                    && _pos + 2 < _tokens.Length
                    && _tokens[_pos + 2].Kind == TokenKind.LParen)
                {
                    Advance(); // consume '.'
                    var methodName = Advance().Text; // consume method name
                    Advance(); // consume '('
                    var qArgs = new List<Expr>();
                    if (Current.Kind != TokenKind.RParen)
                    {
                        qArgs.Add(ParseExpr(0));
                        while (Current.Kind == TokenKind.Comma)
                        {
                            Advance();
                            qArgs.Add(ParseExpr(0));
                        }
                    }
                    Expect(TokenKind.RParen, ")");
                    return new CallExpr($"{t.Text}.{methodName}", [.. qArgs]);
                }
                // function call or bare ref
                if (Current.Kind == TokenKind.LParen)
                {
                    Advance(); // consume (
                    var args = new List<Expr>();
                    if (Current.Kind != TokenKind.RParen)
                    {
                        args.Add(ParseExpr(0));
                        while (Current.Kind == TokenKind.Comma)
                        {
                            Advance();
                            args.Add(ParseExpr(0));
                        }
                    }
                    Expect(TokenKind.RParen, ")");
                    return new CallExpr(t.Text, [.. args]);
                }
                return new RefExpr(t.Text);
            default:
                throw new ParseException($"Unexpected token '{t.Text}'", t.Position);
        }
    }

    // ── Precedence table ─────────────────────────────────────────────────────

    // Returns (leftBp, rightBp, isRightAssoc).
    // leftBp = the binding power of this infix op seen from the left.
    // rightBp = what we recurse with on the right.
    // For left-assoc: rbp = lbp.
    // For right-assoc: rbp = lbp - 1 (so right side can bind the same level again).
    private static (int lbp, int rbp, bool isRight) InfixBp(TokenKind kind) => kind switch
    {
        TokenKind.Question          => (1, 0,  true),   // ternary handled specially above
        // left-associative: rbp = lbp+1 so the same-precedence op on the right does NOT re-enter
        TokenKind.PipePipe          => (2, 3,  false),
        TokenKind.AmpAmp            => (3, 4,  false),
        TokenKind.EqualEqual
        or TokenKind.BangEqual      => (4, 5,  false),
        TokenKind.Less
        or TokenKind.LessEqual
        or TokenKind.Greater
        or TokenKind.GreaterEqual   => (5, 6,  false),
        TokenKind.Plus
        or TokenKind.Minus          => (6, 7,  false),
        TokenKind.Star
        or TokenKind.Slash          => (7, 8,  false),
        // ^ right-associative: rbp=lbp-1 so same-precedence ^ on the right DOES re-enter
        TokenKind.Caret             => (9, 8,  true),
        _                           => (-1, -1, false)  // not an infix op
    };

    private static Expr BuildInfix(TokenKind kind, string op, Expr left, Expr right) => kind switch
    {
        TokenKind.Plus or TokenKind.Minus
        or TokenKind.Star or TokenKind.Slash
        or TokenKind.Caret              => new BinaryExpr(op, left, right),
        TokenKind.Less or TokenKind.LessEqual
        or TokenKind.Greater or TokenKind.GreaterEqual
        or TokenKind.EqualEqual or TokenKind.BangEqual
                                        => new CompareExpr(op, left, right),
        TokenKind.AmpAmp or TokenKind.PipePipe
                                        => new LogicExpr(op, left, right),
        _                               => throw new ParseException($"Unknown infix op '{op}'", 0)
    };

    // ── Extended if/then/elseif/else/endif ───────────────────────────────────

    // Called after consuming "if(cond) then" — cond is the first condition already parsed.
    // Collects the chain and folds right into nested ConditionalExprs.
    private Expr ParseIfThenChain(Expr firstCond)
    {
        var conditions = new List<Expr> { firstCond };
        var thens      = new List<Expr> { ParseExpr(0) };   // then-expression for firstCond

        while (CurrentIsKeyword("elseif"))
        {
            Advance();  // consume elseif
            Expect(TokenKind.LParen, "(");
            conditions.Add(ParseExpr(0));
            Expect(TokenKind.RParen, ")");
            ExpectKeyword("then");
            thens.Add(ParseExpr(0));
        }

        Expr els = new NumberExpr(0);
        if (CurrentIsKeyword("else"))
        {
            Advance();  // consume else
            els = ParseExpr(0);
        }
        ExpectKeyword("endif");

        // Fold right: build if(cN, thenN, if(cN-1,..., els))
        Expr result = els;
        for (int k = thens.Count - 1; k >= 0; k--)
            result = new ConditionalExpr(conditions[k], thens[k], result);
        return result;
    }

    private bool CurrentIsKeyword(string kw)
        => Current.Kind == TokenKind.Identifier && Current.Text == kw;

    private void ExpectKeyword(string kw)
    {
        if (!CurrentIsKeyword(kw))
            throw new ParseException($"Expected '{kw}', got '{Current.Text}'", Current.Position);
        Advance();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private Token Current => _pos < _tokens.Length ? _tokens[_pos] : new Token(TokenKind.Eof, "", -1);

    private Token Advance()
    {
        var t = Current;
        if (_pos < _tokens.Length) _pos++;
        return t;
    }

    private void Expect(TokenKind kind, string text)
    {
        if (Current.Kind != kind)
            throw new ParseException($"Expected '{text}', got '{Current.Text}'", Current.Position);
        Advance();
    }
}
