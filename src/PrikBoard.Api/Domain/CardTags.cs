using System.Text.RegularExpressions;

namespace PrikBoard.Api.Domain;

public static partial class CardTags
{
    public const int MaxLength = 30;

    /// <summary>Trims, lower-cases and hyphenates each tag ("Back end" becomes "back-end"), dropping blanks and duplicates.</summary>
    public static List<string> Normalize(IEnumerable<string> tags) =>
        tags.Select(Normalize).Where(t => t.Length > 0).Distinct().ToList();

    public static string Normalize(string tag) => Whitespace().Replace(tag.Trim().ToLowerInvariant(), "-");

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
