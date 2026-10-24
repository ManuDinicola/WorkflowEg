using System.Globalization;
using Flowd.Expressions.Values;

namespace Flowd.Expressions.Parsing;

/// <summary>Recursive-descent parser for the flowd-FEEL subset (section 8.2).</summary>
internal sealed class Parser
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "or", "and", "in", "between", "if", "then", "else", "true", "false", "null",
    };

    private static readonly string[] Comparisons = ["=", "!=", "<=", ">=", "<", ">"];

    private readonly IReadOnlyList<Token> _tokens;
    private int _position;

    private Parser(IReadOnlyList<Token> tokens) => _tokens = tokens;

    private Token Current => _tokens[_position];

    public static Expr Parse(string text)
    {
        var parser = new Parser(Lexer.Tokenize(text));
        var expression = parser.ParseExpression();
        return parser.Current.Kind == TokenKind.End
            ? expression
            : throw new SyntaxException($"Unexpected '{parser.Current.Text}' at {parser.Current.Position}.");
    }

    private Expr ParseExpression()
    {
        if (IsKeyword("if"))
        {
            return ParseIf();
        }

        return IsQuantifierStart() ? ParseQuantified() : ParseOr();
    }

    /// <summary><c>some</c>, <c>every</c> and <c>for</c> are only keywords when followed by <c>name in</c>, so variables with those names keep working.</summary>
    private bool IsQuantifierStart() =>
        Current.Kind == TokenKind.Name
        && Current.Text is "some" or "every" or "for"
        && _tokens.Count > _position + 2
        && _tokens[_position + 1].Kind == TokenKind.Name
        && _tokens[_position + 2] is { Kind: TokenKind.Name, Text: "in" };

    private QuantifiedExpr ParseQuantified()
    {
        var kind = Advance().Text;
        var variable = Advance().Text;
        Advance(); // in
        var source = ParseOr();
        ExpectKeyword(kind == "for" ? "return" : "satisfies");
        return new QuantifiedExpr(kind, variable, source, ParseExpression());
    }

    private IfExpr ParseIf()
    {
        Advance();
        var condition = ParseExpression();
        ExpectKeyword("then");
        var then = ParseExpression();
        ExpectKeyword("else");
        return new IfExpr(condition, then, ParseExpression());
    }

    private Expr ParseOr()
    {
        var left = ParseAnd();
        while (IsKeyword("or"))
        {
            Advance();
            left = new BinaryExpr("or", left, ParseAnd());
        }

        return left;
    }

    private Expr ParseAnd()
    {
        var left = ParseComparison();
        while (IsKeyword("and"))
        {
            Advance();
            left = new BinaryExpr("and", left, ParseComparison());
        }

        return left;
    }

    private Expr ParseComparison()
    {
        var left = ParseAdditive();
        if (IsKeyword("between"))
        {
            Advance();
            var low = ParseAdditive();
            ExpectKeyword("and");
            return new BetweenExpr(left, low, ParseAdditive());
        }

        if (IsKeyword("in"))
        {
            Advance();
            return new InExpr(left, LooksLikeRange() ? ParseRange() : ParseAdditive());
        }

        var op = Comparisons.FirstOrDefault(IsSymbol);
        if (op is null)
        {
            return left;
        }

        Advance();
        return new BinaryExpr(op, left, ParseAdditive());
    }

    /// <summary>True when the bracket group that starts here contains a top-level <c>..</c>, i.e. it is an interval and not a list.</summary>
    private bool LooksLikeRange()
    {
        if (!(IsSymbol("[") || IsSymbol("(")))
        {
            return false;
        }

        var depth = 0;
        for (var i = _position; i < _tokens.Count && _tokens[i].Kind != TokenKind.End; i++)
        {
            var token = _tokens[i];
            depth += token.Kind == TokenKind.Symbol ? token.Text switch { "(" or "[" or "{" => 1, ")" or "]" or "}" => -1, _ => 0 } : 0; // non-union: token text
            if (depth == 1 && token.Kind == TokenKind.Symbol && token.Text == "..")
            {
                return true;
            }

            if (depth == 0)
            {
                return false;
            }
        }

        return false;
    }

    private RangeExpr ParseRange()
    {
        var lowInclusive = Advance().Text == "[";
        var low = ParseAdditive();
        ExpectSymbol("..");
        var high = ParseAdditive();
        var closing = Advance().Text;
        return new RangeExpr(low, high, lowInclusive, closing == "]");
    }

    private Expr ParseAdditive()
    {
        var left = ParseMultiplicative();
        while (IsSymbol("+") || IsSymbol("-"))
        {
            var op = Advance().Text;
            left = new BinaryExpr(op, left, ParseMultiplicative());
        }

        return left;
    }

    private Expr ParseMultiplicative()
    {
        var left = ParsePower();
        while (IsSymbol("*") || IsSymbol("/"))
        {
            var op = Advance().Text;
            left = new BinaryExpr(op, left, ParsePower());
        }

        return left;
    }

    private Expr ParsePower()
    {
        var left = ParseUnary();
        if (!IsSymbol("**"))
        {
            return left;
        }

        Advance();
        return new BinaryExpr("**", left, ParsePower());
    }

    private Expr ParseUnary()
    {
        if (!IsSymbol("-"))
        {
            return ParsePostfix();
        }

        Advance();
        return new UnaryExpr("-", ParseUnary());
    }

    private Expr ParsePostfix()
    {
        var expression = ParsePrimary();
        while (true)
        {
            if (IsSymbol("."))
            {
                Advance();
                expression = new PathExpr(expression, ExpectName());
            }
            else if (IsSymbol("["))
            {
                Advance();
                var selector = ParseExpression();
                ExpectSymbol("]");
                expression = new SubscriptExpr(expression, selector);
            }
            else
            {
                return expression;
            }
        }
    }

    private Expr ParsePrimary()
    {
        var token = Current;
        switch (token.Kind)
        {
            case TokenKind.Number:
                Advance();
                return new LiteralExpr(decimal.Parse(token.Text, CultureInfo.InvariantCulture));
            case TokenKind.String:
                Advance();
                return new LiteralExpr(token.Text);
            case TokenKind.Name:
                return ParseNameOrCall();
            case TokenKind.Symbol when token.Text == "(":
                Advance();
                var inner = ParseExpression();
                ExpectSymbol(")");
                return inner;
            case TokenKind.Symbol when token.Text == "[":
                return ParseList();
            case TokenKind.Symbol when token.Text == "{":
                return ParseContext();
            default:
                throw new SyntaxException($"Unexpected '{token.Text}' at {token.Position}.");
        }
    }

    private Expr ParseNameOrCall()
    {
        var name = Advance().Text;
        switch (name)
        {
            case "true":
                return new LiteralExpr(true);
            case "false":
                return new LiteralExpr(false);
            case "null":
                return new LiteralExpr(NullValue.Instance);
        }

        if (Keywords.Contains(name))
        {
            throw new SyntaxException($"Unexpected keyword '{name}'.");
        }

        if (!IsSymbol("("))
        {
            return new NameExpr(name);
        }

        Advance();
        var arguments = new List<Expr>();
        while (!IsSymbol(")"))
        {
            arguments.Add(ParseExpression());
            if (!IsSymbol(")"))
            {
                ExpectSymbol(",");
            }
        }

        Advance();
        return new CallExpr(name, arguments);
    }

    private ListExpr ParseList()
    {
        Advance();
        var items = new List<Expr>();
        while (!IsSymbol("]"))
        {
            items.Add(ParseExpression());
            if (!IsSymbol("]"))
            {
                ExpectSymbol(",");
            }
        }

        Advance();
        return new ListExpr(items);
    }

    private ContextExpr ParseContext()
    {
        Advance();
        var entries = new List<KeyValuePair<string, Expr>>();
        while (!IsSymbol("}"))
        {
            var key = Current.Kind == TokenKind.String ? Advance().Text : ExpectName();
            ExpectSymbol(":");
            entries.Add(new KeyValuePair<string, Expr>(key, ParseExpression()));
            if (!IsSymbol("}"))
            {
                ExpectSymbol(",");
            }
        }

        Advance();
        return new ContextExpr(entries);
    }

    private Token Advance()
    {
        var token = Current;
        if (token.Kind != TokenKind.End)
        {
            _position++;
        }

        return token;
    }

    private bool IsSymbol(string text) => Current.Kind == TokenKind.Symbol && Current.Text == text;

    private bool IsKeyword(string text) => Current.Kind == TokenKind.Name && Current.Text == text;

    private void ExpectSymbol(string text)
    {
        if (!IsSymbol(text))
        {
            throw new SyntaxException($"Expected '{text}' at {Current.Position}.");
        }

        Advance();
    }

    private void ExpectKeyword(string text)
    {
        if (!IsKeyword(text))
        {
            throw new SyntaxException($"Expected '{text}' at {Current.Position}.");
        }

        Advance();
    }

    private string ExpectName()
    {
        if (Current.Kind != TokenKind.Name)
        {
            throw new SyntaxException($"Expected a name at {Current.Position}.");
        }

        return Advance().Text;
    }
}
