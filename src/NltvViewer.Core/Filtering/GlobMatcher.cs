using System.Text;
using System.Text.RegularExpressions;

namespace NltvViewer.Core.Filtering;

public static partial class GlobMatcher
{
    public static bool IsMatch(string? value, string? pattern)
    {
        if (string.IsNullOrWhiteSpace(value) || string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var expression = new StringBuilder("^");
        foreach (var character in pattern.Trim())
        {
            expression.Append(character switch
            {
                '*' => ".*",
                '?' => ".",
                _ => Regex.Escape(character.ToString())
            });
        }

        expression.Append('$');
        return Regex.IsMatch(
            Normalize(value),
            expression.ToString(),
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(100));
    }

    public static string Normalize(string value) => WhitespaceRegex().Replace(value.Trim(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
