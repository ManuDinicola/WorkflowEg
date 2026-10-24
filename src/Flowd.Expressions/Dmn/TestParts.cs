namespace Flowd.Expressions.Dmn;

/// <summary>Splits comma separated unary tests and output values, ignoring commas inside quotes, brackets and parentheses.</summary>
internal static class TestParts
{
    public static IEnumerable<string> Split(string text)
    {
        var depth = 0;
        var quoted = false;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            quoted ^= c == '"';
            depth += quoted ? 0 : Depth(c);
            if (c == ',' && !quoted && depth == 0)
            {
                yield return text[start..i].Trim();
                start = i + 1;
            }
        }

        if (start < text.Length || text.Length == 0)
        {
            yield return text[start..].Trim();
        }
    }

    private static int Depth(char c) => c switch
    {
        '(' or '[' or '{' => 1,
        ')' or ']' or '}' => -1,
        _ => 0, // non-union: character
    };
}
