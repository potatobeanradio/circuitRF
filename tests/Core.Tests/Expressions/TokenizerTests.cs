using CircuitRF.Core.Expressions;

namespace CircuitRF.Core.Tests.Expressions;

public class TokenizerTests
{
    private static Token[] Tok(string s) => new Tokenizer(s).Tokenize();
    private static TokenKind Kind(string s, int idx) => Tok(s)[idx].Kind;

    [Fact] public void Numbers()
    {
        var t = Tok("1.5e-3");
        Assert.Equal(TokenKind.Number, t[0].Kind);
        Assert.Equal("1.5e-3", t[0].Text);
    }

    [Fact] public void Identifiers()
    {
        var t = Tok("L1");
        Assert.Equal(TokenKind.Identifier, t[0].Kind);
        Assert.Equal("L1", t[0].Text);
    }

    [Fact] public void TwoCharOps()
    {
        Assert.Equal(TokenKind.LessEqual,    Kind("<=", 0));
        Assert.Equal(TokenKind.GreaterEqual, Kind(">=", 0));
        Assert.Equal(TokenKind.EqualEqual,   Kind("==", 0));
        Assert.Equal(TokenKind.BangEqual,    Kind("!=", 0));
        Assert.Equal(TokenKind.AmpAmp,       Kind("&&", 0));
        Assert.Equal(TokenKind.PipePipe,     Kind("||", 0));
    }

    [Fact] public void OneCharOps()
    {
        Assert.Equal(TokenKind.Plus,     Kind("+", 0));
        Assert.Equal(TokenKind.Minus,    Kind("-", 0));
        Assert.Equal(TokenKind.Star,     Kind("*", 0));
        Assert.Equal(TokenKind.Slash,    Kind("/", 0));
        Assert.Equal(TokenKind.Caret,    Kind("^", 0));
        Assert.Equal(TokenKind.Bang,     Kind("!", 0));
        Assert.Equal(TokenKind.Less,     Kind("<", 0));
        Assert.Equal(TokenKind.Greater,  Kind(">", 0));
        Assert.Equal(TokenKind.Question, Kind("?", 0));
        Assert.Equal(TokenKind.Colon,    Kind(":", 0));
        Assert.Equal(TokenKind.Comma,    Kind(",", 0));
        Assert.Equal(TokenKind.LParen,   Kind("(", 0));
        Assert.Equal(TokenKind.RParen,   Kind(")", 0));
    }

    [Fact] public void EofAlwaysLast()
    {
        var t = Tok("1+2");
        Assert.Equal(TokenKind.Eof, t[^1].Kind);
    }

    [Fact] public void WhitespaceIgnored()
    {
        var t = Tok("  1  +  2  ");
        Assert.Equal(TokenKind.Number,  t[0].Kind);
        Assert.Equal(TokenKind.Plus,    t[1].Kind);
        Assert.Equal(TokenKind.Number,  t[2].Kind);
        Assert.Equal(TokenKind.Eof,     t[3].Kind);
    }

    [Fact] public void UnexpectedCharThrows()
        => Assert.Throws<ParseException>(() => Tok("1 @ 2"));
}
