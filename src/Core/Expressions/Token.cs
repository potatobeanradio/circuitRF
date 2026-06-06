namespace CircuitRF.Core.Expressions;

public enum TokenKind
{
    // Atoms
    Number, Identifier,
    // Arithmetic
    Plus, Minus, Star, Slash, Caret,
    // Comparison
    Less, LessEqual, Greater, GreaterEqual, EqualEqual, BangEqual,
    // Logic
    AmpAmp, PipePipe, Bang,
    // Ternary / punctuation
    Question, Colon, Comma, Dot,
    LParen, RParen,
    // String literal: "foo"  (storage-only config params; no string operations)
    StringLiteral,
    // Sentinel
    Eof
}

public readonly struct Token(TokenKind kind, string text, int position)
{
    public TokenKind Kind     { get; } = kind;
    public string    Text     { get; } = text;
    public int       Position { get; } = position;

    public override string ToString() => $"{Kind}({Text})@{Position}";
}

public sealed class Tokenizer(string source)
{
    private readonly string _source = source;
    private int _pos;

    public Token[] Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            var t = NextToken();
            tokens.Add(t);
            if (t.Kind == TokenKind.Eof) break;
        }
        return [.. tokens];
    }

    private Token NextToken()
    {
        SkipWhitespace();
        if (_pos >= _source.Length) return Make(TokenKind.Eof, "", _pos);

        int start = _pos;
        char c = _source[_pos];

        if (char.IsDigit(c) || (c == '.' && _pos + 1 < _source.Length && char.IsDigit(_source[_pos + 1])))
            return ReadNumber(start);

        if (char.IsLetter(c) || c == '_')
            return ReadIdentifier(start);

        if (c == '"')
            return ReadStringLiteral(start);

        return c switch
        {
            '+' => Advance(TokenKind.Plus,   start),
            '-' => Advance(TokenKind.Minus,  start),
            '*' => Advance(TokenKind.Star,   start),
            '/' => Advance(TokenKind.Slash,  start),
            '^' => Advance(TokenKind.Caret,  start),
            ',' => Advance(TokenKind.Comma,  start),
            '(' => Advance(TokenKind.LParen, start),
            ')' => Advance(TokenKind.RParen, start),
            '.' => Advance(TokenKind.Dot,     start),
            '?' => Advance(TokenKind.Question, start),
            ':' => Advance(TokenKind.Colon,  start),
            '<' => _pos + 1 < _source.Length && _source[_pos + 1] == '='
                        ? Advance2(TokenKind.LessEqual,    start)
                        : Advance(TokenKind.Less,          start),
            '>' => _pos + 1 < _source.Length && _source[_pos + 1] == '='
                        ? Advance2(TokenKind.GreaterEqual, start)
                        : Advance(TokenKind.Greater,       start),
            '=' => _pos + 1 < _source.Length && _source[_pos + 1] == '='
                        ? Advance2(TokenKind.EqualEqual,   start)
                        : throw new ParseException($"Unexpected '=' (did you mean '=='?)", start),
            '!' => _pos + 1 < _source.Length && _source[_pos + 1] == '='
                        ? Advance2(TokenKind.BangEqual,    start)
                        : Advance(TokenKind.Bang,          start),
            '&' => _pos + 1 < _source.Length && _source[_pos + 1] == '&'
                        ? Advance2(TokenKind.AmpAmp,       start)
                        : throw new ParseException("Expected '&&'", start),
            '|' => _pos + 1 < _source.Length && _source[_pos + 1] == '|'
                        ? Advance2(TokenKind.PipePipe,     start)
                        : throw new ParseException("Expected '||'", start),
            _ => throw new ParseException($"Unexpected character '{c}'", start)
        };
    }

    private Token ReadStringLiteral(int start)
    {
        _pos++; // skip opening "
        int contentStart = _pos;
        while (_pos < _source.Length && _source[_pos] != '"')
            _pos++;
        if (_pos >= _source.Length)
            throw new ParseException("Unterminated string literal", start);
        var content = _source[contentStart.._pos];
        _pos++; // skip closing "
        return Make(TokenKind.StringLiteral, content, start);
    }

    private Token ReadNumber(int start)
    {
        while (_pos < _source.Length && (char.IsDigit(_source[_pos]) || _source[_pos] == '.'))
            _pos++;
        // optional exponent
        if (_pos < _source.Length && (_source[_pos] == 'e' || _source[_pos] == 'E'))
        {
            _pos++;
            if (_pos < _source.Length && (_source[_pos] == '+' || _source[_pos] == '-'))
                _pos++;
            while (_pos < _source.Length && char.IsDigit(_source[_pos]))
                _pos++;
        }
        return Make(TokenKind.Number, _source[start.._pos], start);
    }

    private Token ReadIdentifier(int start)
    {
        // Bare 'j' immediately followed by a digit: stop after 'j' so the tokenizer
        // produces two tokens (j, <number>), enabling the implicit j*n shorthand.
        if (_source[_pos] == 'j' && _pos + 1 < _source.Length && char.IsDigit(_source[_pos + 1]))
        {
            _pos++;
            return Make(TokenKind.Identifier, "j", start);
        }
        while (_pos < _source.Length && (char.IsLetterOrDigit(_source[_pos]) || _source[_pos] == '_'))
            _pos++;
        return Make(TokenKind.Identifier, _source[start.._pos], start);
    }

    private Token Advance(TokenKind kind, int start)  { _pos++;     return Make(kind, _source[start.._pos], start); }
    private Token Advance2(TokenKind kind, int start) { _pos += 2;  return Make(kind, _source[start.._pos], start); }
    private static Token Make(TokenKind kind, string text, int pos) => new(kind, text, pos);

    private void SkipWhitespace()
    {
        while (_pos < _source.Length && char.IsWhiteSpace(_source[_pos]))
            _pos++;
    }
}
