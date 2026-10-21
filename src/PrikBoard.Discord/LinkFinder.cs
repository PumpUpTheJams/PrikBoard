using System.Text.RegularExpressions;

namespace PrikBoard.Discord;

/// <summary>
/// Finds candidate PrikBoard links in a message: anything starting with the configured public URL. The match is loose on
/// purpose; the API decides what a link is (getLinkPreview).
/// </summary>
public sealed class LinkFinder
{
    /// <summary>Links previewed per message; Discord takes 10 embeds, and five is plenty.</summary>
    public const int MaxLinks = 5;

    private readonly Regex _links;

    public LinkFinder(string publicUrl)
    {
        var site = Regex.Escape(publicUrl.TrimEnd('/'));
        // Not right after "<": Discord's way to post a link without a preview.
        _links = new Regex($@"(?<![<\w]){site}(?:[/?#][^\s<>|]*)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public IReadOnlyList<string> Find(string text) =>
        _links.Matches(text)
            .Select(m => m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '*', '_', '~', '`', '\'', '"'))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxLinks)
            .ToList();
}
