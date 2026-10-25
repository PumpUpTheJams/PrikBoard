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

    /// <summary>For what is gone.</summary>
    private const string Muted = "#5c6370";

    /// <summary>Fields a section takes in Slack.</summary>
    private const int MaxFields = 10;

    /// <summary>Characters Slack takes in a section's text, and in one of its fields.</summary>
    private const int MaxSectionText = 3000, MaxFieldText = 2000;

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

    /// <summary>A posted preview whose card or board this channel can't see any more (deleted, or disconnected).</summary>
    public Dictionary<string, object> Unavailable() => Attachment(Muted,
    [
        Section("*No longer available*\nThis card or board was deleted, or is no longer previewed here."),
        Context(null),
    ]);

    /// <summary>What happened on a board, one line per card, for a channel that follows its updates.</summary>
    public Dictionary<string, object> Updates(string boardName, string boardPath, IReadOnlyList<ChatUpdateLine> lines)
    {
        var shown = lines.Take(Embeds.MaxUpdateLines).Select(UpdateLine).ToList();
        if (lines.Count > shown.Count) shown.Add($"…and {lines.Count - shown.Count} more");
        return Attachment(Brand,
        [
            Section($"*{Link(_site + boardPath, boardName)}*"),
            Section(Cut(string.Join('\n', shown), MaxSectionText)),
            Context(null),
        ]);
    }

    /// <summary>One line of a board's updates, the same words as the Discord bot's.</summary>
    public string UpdateLine(ChatUpdateLine line)
    {
        var link = CardLine(line.ReadableId, line.Path, line.Title);
        var by = Escape(line.By);
        return line.Kind switch
        {
            ChatUpdates.NewCards => $"🆕 {link} added{(line.Into is { } into ? $" to *{Escape(into)}*" : "")} by {by}",
            ChatUpdates.Moves => $"➡️ {link} {Bucket(line.From)} → *{Bucket(line.To)}* · {by}",
            ChatUpdates.Assignments => $"👤 {link} assigned to {string.Join(", ", (line.People ?? []).Select(Escape))} by {by}",
            _ => line.Started ? $"🤖 {by} started working on {link}" : $"✅ {by} stopped working on {link}",
        };

        static string Bucket(string? name) => name is null ? "loose" : Escape(name);
    }

    /// <summary>The board's snapshot for /prikboard board: a field per bucket with its first cards, and who is at work.</summary>
    public Dictionary<string, object> Snapshot(BoardSnapshot board)
    {
        var fields = board.Buckets.Take(MaxFields).Select(bucket =>
        {
            var lines = bucket.Cards.Take(5).Select(c => CardLine(c.ReadableId, c.Path, c.Title)).ToList();
            if (bucket.Count > lines.Count) lines.Add($"…and {bucket.Count - lines.Count} more");
            return (object)Mrkdwn(Cut($"*{Escape(bucket.Name)} ({bucket.Count})*\n{(lines.Count == 0 ? "Empty" : string.Join('\n', lines))}", MaxFieldText));
        }).ToList();

        List<object> blocks = [Section($"*{Link(_site + board.Path, board.Name)}*")];
        if (fields.Count > 0) blocks.Add(new Dictionary<string, object> { ["type"] = "section", ["fields"] = fields });
        if (board.Working.Count > 0)
        {
            blocks.Add(Section(Cut("*🤖 Agents at work*\n" + string.Join('\n', board.Working.Select(w =>
                $"{CardLine(w.ReadableId, w.Path, w.Title)}{(w.For is { Length: > 0 } name ? $" · for {Escape(name)}" : "")}")), MaxSectionText)));
        }
        blocks.Add(new Dictionary<string, object>
        {
            ["type"] = "context",
            ["elements"] = new List<object> { Mrkdwn($"Last 24 hours: {board.AddedToday} added, {board.MovedToday} moved") },
        });
        return Attachment(Brand, blocks);
    }

    /// <summary>The modal's <c>callback_id</c> for Save to PrikBoard.</summary>
    public const string SaveCallback = "save_to_prikboard";

    /// <summary>Options a select takes in Slack, and characters in an option's text.</summary>
    private const int MaxOptions = 100, MaxOptionText = 75;

    /// <summary>
    /// Save to PrikBoard's modal: where (one select with a group per board and its buckets, the one picked first already
    /// chosen), the title, and whose message it is. <paramref name="draft"/> comes back with the submission
    /// (<c>private_metadata</c>), to find the message again.
    /// </summary>
    public static Dictionary<string, object> SaveView(string draft, IReadOnlyList<SaveTarget> targets, string title, string authorName, string channelName)
    {
        var options = 0;
        Dictionary<string, object>? initial = null;
        var groups = new List<object>();
        foreach (var target in targets)
        {
            List<(Guid? Id, string Name)> choices = target.Buckets.Count == 0
                ? [(null, "On the board")]
                : [.. target.Buckets.Select(b => ((Guid?)b.Id, b.Name))];
            var group = new List<object>();
            foreach (var (id, name) in choices)
            {
                if (options++ >= MaxOptions) break;
                var option = Option(name, WhereValue(target.BoardId, id));
                group.Add(option);
                if (initial is null && target == targets[0] && id == target.DefaultBucketId) initial = option;
            }
            if (group.Count > 0) groups.Add(new Dictionary<string, object> { ["label"] = Plain(Cut(target.Name, MaxOptionText)), ["options"] = group });
        }
        var select = new Dictionary<string, object> { ["type"] = "static_select", ["action_id"] = "where", ["option_groups"] = groups };
        if (initial is not null) select["initial_option"] = initial;

        return Modal(SaveCallback, draft, "Save",
        [
            new Dictionary<string, object> { ["type"] = "input", ["block_id"] = "where", ["label"] = Plain("Board and bucket"), ["element"] = select },
            new Dictionary<string, object>
            {
                ["type"] = "input",
                ["block_id"] = "title",
                ["label"] = Plain("Card title"),
                ["element"] = new Dictionary<string, object>
                {
                    ["type"] = "plain_text_input", ["action_id"] = "title", ["initial_value"] = title, ["max_length"] = 200,
                },
            },
            new Dictionary<string, object>
            {
                ["type"] = "context",
                ["elements"] = new List<object> { Mrkdwn($"From *{Escape(authorName)}* in #{Escape(channelName)}, with up to {ChatSaves.MaxImages} of its images") },
            },
        ]);
    }

    /// <summary>A select option's value for a board and bucket (null: loose on the board), and back.</summary>
    public static string WhereValue(Guid boardId, Guid? bucketId) => $"{boardId:N}:{bucketId:N}";

    public static (Guid BoardId, Guid? BucketId)? ParseWhere(string? value) =>
        value?.Split(':') is [var board, var bucket] && Guid.TryParse(board, out var boardId)
            ? (boardId, Guid.TryParse(bucket, out var bucketId) ? bucketId : null)
            : null;

    /// <summary>A modal that only says something, with a Close button.</summary>
    public static Dictionary<string, object> NoticeView(string mrkdwn) =>
        Modal(null, null, null, [Section(mrkdwn)]);

    private static Dictionary<string, object> Modal(string? callbackId, string? metadata, string? submit, List<object> blocks)
    {
        var view = new Dictionary<string, object>
        {
            ["type"] = "modal",
            ["title"] = Plain("Save to PrikBoard"),
            ["close"] = Plain(submit is null ? "Close" : "Cancel"),
            ["blocks"] = blocks,
        };
        if (callbackId is not null) view["callback_id"] = callbackId;
        if (metadata is not null) view["private_metadata"] = metadata;
        if (submit is not null) view["submit"] = Plain(submit);
        return view;
    }

    private static Dictionary<string, object> Option(string text, string value) => new() { ["text"] = Plain(Cut(text, MaxOptionText)), ["value"] = value };

    private static Dictionary<string, object> Plain(string text) => new() { ["type"] = "plain_text", ["text"] = text };

    /// <summary>A card as a line in an update or the snapshot: its readable id linking to it, and its title.</summary>
    public string CardLine(string readableId, string path, string title) =>
        $"{Link(_site + path, readableId)} {Escape(Cut(title, 80))}";

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
