using System.Text.RegularExpressions;

namespace PrikBoard.Api.Integrations;

/// <summary>
/// Finds candidate PrikBoard links in a Discord message: anything starting with the public URL. The match is loose on
/// purpose; <see cref="LinkPreviews"/> decides what a link is.
/// </summary>
public sealed class LinkFinder
{
    /// <summary>Links previewed per message; Discord takes 10 embeds, and five is plenty.</summary>
    public const int MaxLinks = 5;

    private readonly string _site;
    private readonly Regex _links;

    public LinkFinder(string publicUrl)
    {
        _site = publicUrl.TrimEnd('/');
        // Not right after "<": Discord's way to post a link without a preview. Compiled, as it may run on every message.
        _links = new Regex($@"(?<![<\w]){Regex.Escape(_site)}(?:[/?#][^\s<>|]*)?",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    }

    /// <summary>The links in <paramref name="text"/>; most messages don't mention the site at all, which is checked first.</summary>
    public IReadOnlyList<string> Find(string text) => !text.Contains(_site, StringComparison.OrdinalIgnoreCase) ? [] :
        _links.Matches(text)
            .Select(m => m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '*', '_', '~', '`', '\'', '"'))
            .Distinct(StringComparer.Ordinal)
            .Take(MaxLinks)
            .ToList();
}
