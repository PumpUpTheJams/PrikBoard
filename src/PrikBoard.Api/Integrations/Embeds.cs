using Discord;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Integrations;

/// <summary>
/// Every embed the Discord bot posts: link previews, board updates, the board snapshot and saved cards. All of them carry
/// the PrikBoard icon and name as author, so they read as one thing, and as the anonymous link preview chat apps build
/// from index.html. No images and no avatars.
/// </summary>
public sealed class Embeds(string publicUrl)
{
    /// <summary>The logo's yellow, also the anonymous preview's theme-color.</summary>
    public static readonly Color Brand = new(0xffd54f);

    /// <summary>For what is gone.</summary>
    private static readonly Color Muted = new(0x5c6370);

    /// <summary>Lines one update post takes; the rest is counted.</summary>
    public const int MaxUpdateLines = 15;

    private readonly string _site = publicUrl.TrimEnd('/');

    public Embed For(LinkPreview preview, string source) => preview.Kind switch
    {
        LinkPreviewKind.Card => Card(preview),
        LinkPreviewKind.Board => Board(preview),
        _ => Invite(preview, source),
    };

    /// <summary>A posted preview whose card or board this channel can't see any more (deleted, or disconnected).</summary>
    public Embed Unavailable() =>
        Base()
            .WithTitle("No longer available")
            .WithDescription("This card or board was deleted, or is no longer previewed here.")
            .WithColor(Muted)
            .Build();

    private Embed Card(LinkPreview card)
    {
        var embed = Base()
            .WithTitle(Cut($"{card.ReadableId} · {card.Title}", EmbedBuilder.MaxTitleLength))
            .WithUrl(_site + card.Path)
            .WithDescription(card.Description)
            .WithFooter(Cut(card.BoardName, EmbedFooterBuilder.MaxFooterTextLength))
            .WithColor(Fill(card.Color));
        if (card.WorkingFor is { } workingFor)
        {
            embed.AddField("🤖 Agent at work", workingFor.Length > 0 ? $"for {Format.Sanitize(workingFor)}" : "now", inline: true);
        }
        if (card.Bucket is { } bucket) embed.AddField("Bucket", Cut(Format.Sanitize(bucket), 100), inline: true);
        if (card.Assignees is { Count: > 0 } people) embed.AddField("Assigned", Cut(string.Join(", ", people.Select(Format.Sanitize)), 300), inline: true);
        if (card.Tags is { Count: > 0 } tags) embed.AddField("Tags", Cut(string.Join(" ", tags.Select(t => $"`{t}`")), 300), inline: true);
        if (card.Checklist is { } checklist) embed.AddField("Checklist", $"{checklist} done", inline: true);
        return embed.Build();
    }

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

    /// <summary>What happened on a board, one line per card, for a channel that follows its updates.</summary>
    public Embed Updates(string boardName, string boardPath, IReadOnlyList<string> lines)
    {
        var shown = lines.Take(MaxUpdateLines).ToList();
        if (lines.Count > shown.Count) shown.Add($"…and {lines.Count - shown.Count} more");
        return Base()
            .WithTitle(Cut(boardName, EmbedBuilder.MaxTitleLength))
            .WithUrl(_site + boardPath)
            .WithDescription(Cut(string.Join('\n', shown), EmbedBuilder.MaxDescriptionLength))
            .WithColor(Brand)
            .Build();
    }

    /// <summary>A card as a line in an update or the snapshot: its readable id linking to it, and its title.</summary>
    public string CardLine(string readableId, string path, string title) =>
        $"[{readableId}]({_site}{path}) {Format.Sanitize(Cut(title, 80))}";

    /// <summary>One line of a board's updates, for <see cref="Updates"/>.</summary>
    public string UpdateLine(ChatUpdateLine line)
    {
        var link = CardLine(line.ReadableId, line.Path, line.Title);
        var by = Format.Sanitize(line.By);
        return line.Kind switch
        {
            ChatUpdates.NewCards => $"🆕 {link} added{(line.Into is { } into ? $" to **{Format.Sanitize(into)}**" : "")} by {by}",
            ChatUpdates.Moves => $"➡️ {link} {Bucket(line.From)} → **{Bucket(line.To)}** · {by}",
            ChatUpdates.Assignments => $"👤 {link} assigned to {string.Join(", ", (line.People ?? []).Select(Format.Sanitize))} by {by}",
            _ => line.Started ? $"🤖 {by} started working on {link}" : $"✅ {by} stopped working on {link}",
        };

        static string Bucket(string? name) => name is null ? "loose" : Format.Sanitize(name);
    }

    /// <summary>The board's snapshot for /prikboard board.</summary>
    public Embed Snapshot(BoardSnapshot board)
    {
        var embed = Base()
            .WithTitle(Cut(board.Name, EmbedBuilder.MaxTitleLength))
            .WithUrl(_site + board.Path)
            .WithColor(Brand)
            .WithFooter($"Last 24 hours: {board.AddedToday} added, {board.MovedToday} moved");
        foreach (var bucket in board.Buckets.Take(EmbedBuilder.MaxFieldCount - 1))
        {
            var lines = bucket.Cards.Take(5).Select(c => CardLine(c.ReadableId, c.Path, c.Title)).ToList();
            if (bucket.Count > lines.Count) lines.Add($"…and {bucket.Count - lines.Count} more");
            embed.AddField(Cut($"{bucket.Name} ({bucket.Count})", EmbedFieldBuilder.MaxFieldNameLength),
                Cut(lines.Count == 0 ? "Empty" : string.Join('\n', lines), EmbedFieldBuilder.MaxFieldValueLength));
        }
        if (board.Working.Count > 0)
        {
            embed.AddField("🤖 Agents at work", Cut(string.Join('\n', board.Working.Select(w =>
                $"{CardLine(w.ReadableId, w.Path, w.Title)}{(w.For is { Length: > 0 } name ? $" · for {Format.Sanitize(name)}" : "")}")), EmbedFieldBuilder.MaxFieldValueLength));
        }
        return embed.Build();
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

/// <summary>What /prikboard board shows: the board's buckets with their cards, and who is at work.</summary>
public sealed record BoardSnapshot(
    string Name, string Path, IReadOnlyList<BoardSnapshot.BucketCards> Buckets, IReadOnlyList<BoardSnapshot.WorkingCard> Working,
    int AddedToday, int MovedToday)
{
    public sealed record CardRef(string ReadableId, string Path, string Title);
    public sealed record BucketCards(string Name, int Count, IReadOnlyList<CardRef> Cards);
    public sealed record WorkingCard(string ReadableId, string Path, string Title, string? For);
}
