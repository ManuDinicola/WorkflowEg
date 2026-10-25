using System.Text.RegularExpressions;

namespace Flowd.Application.Execution;

/// <summary>Rules for the tags of a process instance (FR-EXEC-020): at most 10, each starting with a letter, up to 100 characters of letters, digits and <c>_ - : .</c>.</summary>
public static partial class InstanceTags
{
    public const int MaxTags = 10;

    /// <summary>The first problem found, or null when the tags are acceptable.</summary>
    public static string? Check(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return null;
        }

        if (tags.Count > MaxTags)
        {
            return $"At most {MaxTags} tags are allowed.";
        }

        var bad = tags.FirstOrDefault(t => !Valid().IsMatch(t));
        return bad is null ? null : $"Tag '{bad}' is not valid: start with a letter, then up to 99 letters, digits or _ - : .";
    }

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9_\-:.]{0,99}$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex Valid();
}
