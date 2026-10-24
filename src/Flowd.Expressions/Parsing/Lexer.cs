using System.Globalization;
using System.Text;

namespace Flowd.Expressions.Parsing;

internal enum TokenKind
{
    Number,
    String,
    Name,
    Symbol,
    End,
}

internal sealed record Token(TokenKind Kind, string Text, int Position);

/// <summary>Raised inside the parser only; converted to an <c>EvaluationError</c> at the public boundary.</summary>
internal sealed class SyntaxException(string message) : Exception(message);

/// <summary>Splits expression text into tokens. Knows the multi-word built-in names of FEEL (e.g. <c>starts with</c>).</summary>
internal static class Lexer
{
    private static readonly string[] MultiWordNames =
    [
        "starts with", "ends with", "upper case", "lower case", "string length", "is defined", "date and time",
    ];

    private static readonly string[] TwoCharSymbols = ["<=", ">=", "!=", "**", ".."];

    public static IReadOnlyList<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
            }
            else if (char.IsAsciiDigit(text[i]))
            {
                i = ReadNumber(text, i, tokens);
            }
            else if (text[i] == '"')
            {
                i = ReadString(text, i, tokens);
            }
            else if (IsNameStart(text[i]))
            {
                i = ReadName(text, i, tokens);
            }
            else
            {
                i = ReadSymbol(text, i, tokens);
            }
        }

        tokens.Add(new Token(TokenKind.End, string.Empty, text.Length));
        return tokens;
    }

    private static bool IsNameStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsNamePart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static int ReadNumber(string text, int start, List<Token> tokens)
    {
        var i = start;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
        }

        // A single dot followed by a digit is a decimal point; ".." is a range operator.
        if (i + 1 < text.Length && text[i] == '.' && char.IsAsciiDigit(text[i + 1]))
        {
            i++;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
            }
        }

        tokens.Add(new Token(TokenKind.Number, text[start..i], start));
        return i;
    }

    private static int ReadString(string text, int start, List<Token> tokens)
    {
        var builder = new StringBuilder();
        var i = start + 1;
        while (i < text.Length && text[i] != '"')
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
            }

            builder.Append(text[i]);
            i++;
        }

        if (i >= text.Length)
        {
            throw new SyntaxException($"Unterminated string starting at {start}.");
        }

        tokens.Add(new Token(TokenKind.String, builder.ToString(), start));
        return i + 1;
    }

    private static int ReadName(string text, int start, List<Token> tokens)
    {
        var end = ReadWord(text, start);
        foreach (var multi in MultiWordNames)
        {
            if (string.CompareOrdinal(text, start, multi, 0, multi.Length) == 0
                && (start + multi.Length == text.Length || !IsNamePart(text[start + multi.Length])))
            {
                tokens.Add(new Token(TokenKind.Name, multi, start));
                return start + multi.Length;
            }
        }

        tokens.Add(new Token(TokenKind.Name, text[start..end], start));
        return end;
    }

    private static int ReadWord(string text, int start)
    {
        var i = start;
        while (i < text.Length && IsNamePart(text[i]))
        {
            i++;
        }

        return i;
    }

    private static int ReadSymbol(string text, int start, List<Token> tokens)
    {
        foreach (var two in TwoCharSymbols)
        {
            if (string.CompareOrdinal(text, start, two, 0, 2) == 0)
            {
                tokens.Add(new Token(TokenKind.Symbol, two, start));
                return start + 2;
            }
        }

        if ("+-*/=<>()[]{},.:".Contains(text[start], StringComparison.Ordinal))
        {
            tokens.Add(new Token(TokenKind.Symbol, text[start].ToString(CultureInfo.InvariantCulture), start));
            return start + 1;
        }

        throw new SyntaxException($"Unexpected character '{text[start]}' at {start}.");
    }
}
