using Discord;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Integrations;

/// <summary>
/// The embeds the Discord bot posts. All of them carry the PrikBoard icon and name as author, so they read as the same
/// thing as the anonymous link preview chat apps build from index.html. No images and no avatars.
/// </summary>
public sealed class Embeds(string publicUrl)
{
    /// <summary>The logo's yellow, also the anonymous preview's theme-color.</summary>
    public static readonly Color Brand = new(0xffd54f);

    private readonly string _site = publicUrl.TrimEnd('/');

    public Embed For(LinkPreview preview, string pastedUrl) => preview.Kind switch
    {
        LinkPreviewKind.Card => Card(preview),
        LinkPreviewKind.Board => Board(preview),
        _ => Invite(preview, pastedUrl),
    };

    private Embed Card(LinkPreview card) =>
        Base()
            .WithTitle(Cut($"{card.ReadableId} · {card.Title}", EmbedBuilder.MaxTitleLength))
            .WithUrl(_site + card.Path)
            .WithDescription(card.Description)
            .WithFooter(Cut(card.BoardName, EmbedFooterBuilder.MaxFooterTextLength))
            .WithColor(Fill(card.Color))
            .Build();

    private Embed Board(LinkPreview board) =>
        Base()
            .WithTitle(Cut(board.BoardName, EmbedBuilder.MaxTitleLength))
            .WithUrl(_site + board.Path)
            .WithDescription(board.Description)
            .WithColor(Brand)
            .Build();

    // No board description: an invite shows what PrikBoard's own invite page shows before you join, nothing more.
    private Embed Invite(LinkPreview invite, string pastedUrl)
    {
        var who = invite.InvitedBy is { Length: > 0 } name ? $"{Format.Sanitize(name)} invited you" : "You're invited";
        var expires = invite.ExpiresAt is { } at ? $" · expires <t:{at.ToUnixTimeSeconds()}:D>" : "";
        return Base()
            .WithTitle(Cut($"You're invited to join {invite.BoardName}", EmbedBuilder.MaxTitleLength))
            .WithUrl(pastedUrl)
            .WithDescription($"{who} as {invite.Role}{expires}\nThe link works once, for whoever accepts it first.")
            .WithColor(Brand)
            .Build();
    }

    private EmbedBuilder Base() => new EmbedBuilder().WithAuthor("PrikBoard", $"{_site}/icon-512.png", _site + "/");

    /// <summary>The card fills of the web app (index.css, --fill-*).</summary>
    private static Color Fill(CardColor? color) => color switch
    {
        CardColor.Mint => new Color(0x9fe0c0),
        CardColor.Sky => new Color(0x9ccbf5),
        CardColor.Coral => new Color(0xf4a99a),
        CardColor.Lilac => new Color(0xcdb8f5),
        _ => new Color(0xf3d77a),
    };

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
