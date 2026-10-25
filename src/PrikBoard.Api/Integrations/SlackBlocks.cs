using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Integrations;

/// <summary>
/// How link previews look in Slack: an unfurl per link, as Block Kit inside an attachment so the card's fill shows as the
/// colored bar. The counterpart of the Discord bot's <see cref="Embeds"/>, drawing the same <see cref="LinkPreview"/>, with
/// the PrikBoard icon and name in the context line so it reads as the same thing. Plain dictionaries, so the JSON is
/// exactly what Slack's documentation shows.
/// </summary>
public sealed class SlackBlocks(string publicUrl)
{
    /// <summary>The logo's yellow, also the anonymous preview's theme-color.</summary>
    public const string Brand = "#ffd54f";

    /// <summary>Fields a section takes in Slack.</summary>
    private const int MaxFields = 10;

    private readonly string _site = publicUrl.TrimEnd('/');

    /// <summary>The unfurl of one link.</summary>
    public Dictionary<string, object> For(LinkPreview preview, string url) => preview.Kind switch
    {
        LinkPreviewKind.Card => Card(preview),
        LinkPreviewKind.Board => Board(preview),
        _ => Invite(preview, url),
    };

    private Dictionary<string, object> Card(LinkPreview card)
    {
        List<object> blocks = [Section($"*{Link(_site + card.Path, $"{card.ReadableId} · {card.Title}")}*" + Paragraph(card.Description))];

        var fields = new List<object>();
        if (card.WorkingFor is { } workingFor) fields.Add(Field("🤖 Agent at work", workingFor.Length > 0 ? $"for {workingFor}" : "now"));
        if (card.Bucket is { } bucket) fields.Add(Field("Bucket", bucket));
        if (card.Assignees is { Count: > 0 } people) fields.Add(Field("Assigned", string.Join(", ", people)));
        if (card.Tags is { Count: > 0 } tags) fields.Add(Field("Tags", string.Join(" ", tags)));
        if (card.Checklist is { } checklist) fields.Add(Field("Checklist", $"{checklist} done"));
        if (fields.Count > 0) blocks.Add(new Dictionary<string, object> { ["type"] = "section", ["fields"] = fields.Take(MaxFields).ToList() });

        blocks.Add(Context(card.BoardName));
        return Attachment(Fill(card.Color), blocks);
    }

    private Dictionary<string, object> Board(LinkPreview board) =>
        Attachment(Brand, [Section($"*{Link(_site + board.Path, board.BoardName)}*" + Paragraph(board.Description)), Context(null)]);

    // No board description: an invite shows what PrikBoard's own invite page shows before you join, nothing more.
    private Dictionary<string, object> Invite(LinkPreview invite, string pastedUrl)
    {
        var who = invite.InvitedBy is { Length: > 0 } name ? $"{Escape(name)} invited you" : "You're invited";
        // Slack shows the date in each reader's own time zone, with the fallback for clients that can't.
        var expires = invite.ExpiresAt is { } at ? $" · expires <!date^{at.ToUnixTimeSeconds()}^{{date_short}}|{at:d MMM}>" : "";
        return Attachment(Brand,
        [
            Section($"*{Link(pastedUrl, $"You're invited to join {invite.BoardName}")}*\n{who} as {invite.Role}{expires}\n" +
                    "The link works once, for whoever accepts it first."),
            Context(null),
        ]);
    }

    private static Dictionary<string, object> Attachment(string color, List<object> blocks) =>
        new() { ["color"] = color, ["blocks"] = blocks };

    private static Dictionary<string, object> Section(string mrkdwn) =>
        new() { ["type"] = "section", ["text"] = Mrkdwn(mrkdwn) };

    private static Dictionary<string, object> Field(string label, string value) => Mrkdwn($"*{label}*\n{Escape(Cut(value, 300))}");

    private Dictionary<string, object> Context(string? boardName) => new()
    {
        ["type"] = "context",
        ["elements"] = new List<object>
        {
            new Dictionary<string, object> { ["type"] = "image", ["image_url"] = $"{_site}/icon-512.png", ["alt_text"] = "PrikBoard" },
            Mrkdwn(boardName is null ? "PrikBoard" : $"PrikBoard · {Escape(Cut(boardName, 150))}"),
        },
    };

    private static Dictionary<string, object> Mrkdwn(string text) => new() { ["type"] = "mrkdwn", ["text"] = text };

    private static string Paragraph(string? text) => text is { Length: > 0 } ? "\n" + Escape(text) : "";

    /// <summary>A link with its text; Slack ends the text at <c>|</c> or <c>&gt;</c>, so those are left out of it.</summary>
    private static string Link(string url, string text) =>
        $"<{url}|{Escape(Cut(text, 150)).Replace("|", "¦")}>";

    /// <summary>The three characters Slack's mrkdwn wants escaped, so nothing a person typed turns into a mention or a link.</summary>
    public static string Escape(string text) => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>The card fills of the web app (index.css, --fill-*), as <see cref="Embeds"/> uses them.</summary>
    private static string Fill(CardColor? color) => color switch
    {
        CardColor.Mint => "#9fe0c0",
        CardColor.Sky => "#9ccbf5",
        CardColor.Coral => "#f4a99a",
        CardColor.Lilac => "#cdb8f5",
        _ => "#f3d77a",
    };

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
