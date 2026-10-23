using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;
using PrikBoard.Api.Endpoints;

namespace PrikBoard.Api.Mcp;

// How the tools find what they work on: boards, cards, buckets, components, tags and people by the names and ids they
// take, checked against the caller's access before any handler runs.
public sealed partial class BoardTools
{
    /// <summary>A tool call on a board the caller may use with the role it needs.</summary>
    private sealed record BoardCall(HttpContext Http, PrikBoardDbContext Db, Guid BoardId);

    /// <summary>A tool call on a card, and its board, that the caller may use with the role it needs.</summary>
    private sealed record CardCall(HttpContext Http, PrikBoardDbContext Db, Guid BoardId, Guid CardId);

    private static PrikBoardDbContext Db(HttpContext http) => http.RequestServices.GetRequiredService<PrikBoardDbContext>();

    private static FileStore Files(HttpContext http) => http.RequestServices.GetRequiredService<FileStore>();

    /// <summary>The board (see <see cref="BoardAsync"/>) with the caller's role on it checked.</summary>
    private static async Task<BoardCall> BoardCallAsync(IHttpContextAccessor accessor, string? board, BoardRole minimum, CancellationToken ct)
    {
        var http = McpCall.Http(accessor);
        var boardId = await BoardAsync(http, board, minimum, ct);
        return new BoardCall(http, Db(http), boardId);
    }

    /// <summary>
    /// The card (see <see cref="ResolveCardAsync"/>, which also says what <paramref name="deleted"/> does), found before the
    /// caller's role on its board is checked.
    /// </summary>
    private static async Task<CardCall> CardCallAsync(
        IHttpContextAccessor accessor, string card, BoardRole minimum, CancellationToken ct, bool deleted = false)
    {
        var http = McpCall.Http(accessor);
        var cardId = await ResolveCardAsync(http, card, ct, deleted);
        var boardId = await McpCall.AuthorizeAsync(http, BoardScope.Card, cardId, minimum);
        return new CardCall(http, Db(http), boardId, cardId);
    }

    /// <summary>The board of a bucket, with the Editor role on it checked (the bucket tools all change it).</summary>
    private static async Task<BoardCall> BucketCallAsync(IHttpContextAccessor accessor, Guid bucketId)
    {
        var http = McpCall.Http(accessor);
        var boardId = await McpCall.AuthorizeAsync(http, BoardScope.Bucket, bucketId, BoardRole.Editor);
        return new BoardCall(http, Db(http), boardId);
    }

    /// <summary>The board an image goes on and the card it is attached to, if any, checking the caller may add it there.</summary>
    private static async Task<(Guid BoardId, Guid? CardId)> ImageTargetAsync(HttpContext http, string? card, string? board, CancellationToken ct)
    {
        if (card is null && board is null && BoardCaller.Of(http.User).Boards is not { Count: 1 })
        {
            throw new McpException("Give card to attach the image to a card, or board to put it on the board.");
        }
        Guid? cardId = card is null ? null : await ResolveCardAsync(http, card, ct);
        // With neither card nor board, this is the only board the token works on.
        Guid? boardId = card is null || board is not null ? await ResolveBoardAsync(http, board, ct) : null;
        var target = cardId is { } attachTo
            ? await McpCall.AuthorizeAsync(http, BoardScope.Card, attachTo, BoardRole.Editor)
            : await McpCall.AuthorizeAsync(http, BoardScope.Board, boardId!.Value, BoardRole.Editor);
        if (boardId is { } given && given != target) throw new McpException("The card is not on that board; leave out board.");
        return (target, cardId);
    }

    private const string NotFound = "Not found, or you have no access to its board.";

    /// <summary>
    /// A board by id or short id (or, left out, the only board the caller's token works on), checking the caller's role
    /// on it in the same query: a missing board and one the caller cannot see give the same "Not found".
    /// </summary>
    private static async Task<Guid> BoardAsync(HttpContext http, string? board, BoardRole minimum, CancellationToken ct)
    {
        var value = board?.Trim();
        if (value is null || Guid.TryParse(value, out _)) return await McpCall.AuthorizeAsync(http, BoardScope.Board, await ResolveBoardAsync(http, value, ct), minimum);
        var shortId = value.ToLowerInvariant();
        if (!Board.IsShortId(shortId)) throw new McpException($"\"{board}\" is not a board id or short id; see list_boards.");
        return await McpCall.AuthorizeAsync(http, shortId, minimum);
    }

    /// <summary>A card for a tool result, with its bucket's name.</summary>
    private static async Task<CardView> ViewAsync(HttpContext http, CardDetail card, bool content, CancellationToken ct)
    {
        var bucket = card.BucketId is { } id
            ? (await BucketsAsync(http, Db(http), card.BoardId, ct)).Find(b => b.Id == id)?.Name
            : null;
        return CardView.From(card, bucket, content);
    }

    private sealed record BucketName(Guid Id, string Name);

    /// <summary>A board's buckets in list order, looked up once per call (create_cards and results name them often).</summary>
    private static async Task<List<BucketName>> BucketsAsync(HttpContext http, PrikBoardDbContext db, Guid boardId, CancellationToken ct)
    {
        var key = "PrikBoard.Mcp.Buckets:" + boardId;
        if (http.Items[key] is List<BucketName> known) return known;
        var buckets = await db.Buckets.AsNoTracking().Where(b => b.BoardId == boardId)
            .OrderBy(b => b.CreatedAt).Select(b => new BucketName(b.Id, b.Name)).ToListAsync(ct);
        http.Items[key] = buckets;
        return buckets;
    }

    /// <summary>
    /// A board by id or short id. Runs before the access check, which then gives the same "Not found" for a board that
    /// doesn't exist and one the caller can't see, so callers can't tell which boards exist.
    /// </summary>
    private static async Task<Guid> ResolveBoardAsync(HttpContext http, string? board, CancellationToken ct)
    {
        if (board is null)
        {
            // A token limited to one board needs no board named.
            return BoardCaller.Of(http.User).Boards is { Count: 1 } only
                ? only.Single()
                : throw new McpException("Give the board: " + BoardForms + " See list_boards.");
        }
        var value = board.Trim();
        if (Guid.TryParse(value, out var id)) return id;
        var shortId = value.ToLowerInvariant();
        if (!Board.IsShortId(shortId)) throw new McpException($"\"{board}\" is not a board id or short id; see list_boards.");
        var db = Db(http);
        var found = await db.Boards.AsNoTracking().Where(b => b.ShortId == shortId).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(ct);
        return found ?? throw new McpException(NotFound);
    }

    /// <summary>
    /// A card by id, path (either form, or a full link to it) or readable id. A readable id is looked up among the boards
    /// the caller can reach, as keys are not unique; when it matches cards on several of them, the caller is asked for the
    /// path instead. Runs before the access check, like <see cref="ResolveBoardAsync"/>. A readable id or a path by number
    /// finds a deleted card only with <paramref name="deleted"/> (restore_card, purge_card); an id, also in the older path
    /// form, is returned as it is.
    /// </summary>
    private static async Task<Guid> ResolveCardAsync(HttpContext http, string card, CancellationToken ct, bool deleted = false)
    {
        var value = card.Trim();
        if (Guid.TryParse(value, out var id)) return id;

        var db = Db(http);
        IQueryable<Card> cards = deleted ? db.Cards.WithDeleted() : db.Cards;
        // A full link (https://host/#/boards/…) holds the path from its "/#/" on.
        var start = value.IndexOf("/#/boards/", StringComparison.Ordinal);
        if (start >= 0 && CardLinks.TryParse(value[start..], out var linked))
        {
            if (!linked.ByNumber) return linked.CardId;
            var found = await cards.AsNoTracking()
                .Where(n => n.Board!.ShortId == linked.BoardShortId && n.Number == linked.Number)
                .Select(n => (Guid?)n.Id).FirstOrDefaultAsync(ct);
            return found ?? throw new McpException(NotFound);
        }

        var dash = value.LastIndexOf('-');
        var key = dash > 0 ? Board.NormalizeKey(value[..dash]) : "";
        if (!Board.IsKey(key) || !CardLinks.TryParseNumber(value[(dash + 1)..], out var number))
        {
            throw new McpException($"\"{card}\" is not a card: give {CardForms}");
        }

        var reachable = await ReachableBoardsAsync(http, db, ct);
        var matches = await cards.AsNoTracking()
            .Where(n => reachable.Contains(n.BoardId) && n.Board!.Key == key && n.Number == number)
            .Select(n => new { n.Id, n.Board!.ShortId, n.Board.Name })
            .ToListAsync(ct);
        return matches switch
        {
            [var only] => only.Id,
            [] => throw new McpException(NotFound),
            _ => throw new McpException($"{key}-{number} is a card on several of your boards: " +
                string.Join(", ", matches.Select(m => $"\"{m.Name}\" ({CardLinks.CardPath(m.ShortId, number)})")) +
                ". Give the path of the one you mean."),
        };
    }

    /// <summary>The boards the caller can reach, looked up once per call (place_in_bucket resolves many cards).</summary>
    private static async Task<List<Guid>> ReachableBoardsAsync(HttpContext http, PrikBoardDbContext db, CancellationToken ct)
    {
        if (http.Items[ReachableBoardsItem] is List<Guid> known) return known;
        var caller = BoardCaller.Of(http.User);
        var memberships = await db.BoardMembers.AsNoTracking().Where(m => m.PrincipalId == caller.MemberId)
            .Select(m => new { m.BoardId, m.Role }).ToListAsync(ct);
        var reachable = memberships.Where(m => caller.Apply(m.BoardId, m.Role) is not null).Select(m => m.BoardId).ToList();
        http.Items[ReachableBoardsItem] = reachable;
        return reachable;
    }

    private const string ReachableBoardsItem = "PrikBoard.Mcp.ReachableBoards";

    /// <summary>A bucket on the board by id or (case-insensitive) name.</summary>
    private static async Task<Guid> ResolveBucketAsync(HttpContext http, PrikBoardDbContext db, Guid boardId, string bucket, CancellationToken ct)
    {
        var buckets = await BucketsAsync(http, db, boardId, ct);
        var name = bucket.Trim();
        var match = Guid.TryParse(bucket, out var id)
            ? buckets.FirstOrDefault(b => b.Id == id)
            : buckets.FirstOrDefault(b => b.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return match?.Id ?? throw new McpException(
            $"No bucket \"{bucket}\" on this board. Buckets: {string.Join(", ", buckets.Select(b => $"\"{b.Name}\""))}.");
    }

    /// <summary>A component of the card by id, (case-insensitive) title, or kind when the card has exactly one component of that kind.</summary>
    private static async Task<Guid> ResolveComponentAsync(PrikBoardDbContext db, Guid cardId, string component, CancellationToken ct)
    {
        var components = await db.Cards.AsNoTracking().Where(n => n.Id == cardId).Select(n => n.Components).FirstAsync(ct);
        var matches = ComponentsNamed(components, component.Trim());
        if (matches.Count == 1) return matches[0].Id;

        var list = components.Count == 0 ? "none" : string.Join(", ", components.Select(b => b.Title is null ? $"{b.Kind} ({b.Id})" : $"\"{b.Title}\" ({b.Kind}, {b.Id})"));
        throw new McpException(matches.Count == 0
            ? $"No component \"{component}\" on this card. Components: {list}."
            : $"Several components match \"{component}\"; use the component id. Components: {list}.");
    }

    /// <summary>The components <paramref name="value"/> names: by id, else by title, else by kind.</summary>
    private static List<CardComponent> ComponentsNamed(List<CardComponent> components, string value)
    {
        if (Guid.TryParse(value, out var id)) return components.Where(b => b.Id == id).ToList();
        var titled = components.Where(b => b.Title is not null && b.Title.Equals(value, StringComparison.OrdinalIgnoreCase)).ToList();
        if (titled.Count > 0) return titled;
        if (Enum.TryParse<CardComponentKind>(value, ignoreCase: true, out var kind)) return components.Where(b => b.Kind == kind).ToList();
        return [];
    }

    /// <summary>A tag on the board by id or name (normalized like card tags).</summary>
    private static async Task<Guid> ResolveTagAsync(PrikBoardDbContext db, Guid boardId, string tag, CancellationToken ct)
    {
        var tags = await db.BoardTags.AsNoTracking().Where(t => t.BoardId == boardId)
            .OrderBy(t => t.Name).Select(t => new { t.Id, t.Name }).ToListAsync(ct);
        var name = CardTags.Normalize(tag);
        var match = Guid.TryParse(tag, out var id)
            ? tags.FirstOrDefault(t => t.Id == id)
            : tags.FirstOrDefault(t => t.Name == name);
        return match?.Id ?? throw new McpException(
            $"No tag \"{tag}\" on this board. Tags: {(tags.Count == 0 ? "none" : string.Join(", ", tags.Select(t => $"\"{t.Name}\"")))}.");
    }

    /// <summary>A deleted tag on the board by id or name; with a name, the most recently deleted one.</summary>
    private static async Task<Guid> ResolveDeletedTagAsync(PrikBoardDbContext db, Guid boardId, string tag, CancellationToken ct)
    {
        var tags = await db.BoardTags.WithDeleted().AsNoTracking()
            .Where(t => t.BoardId == boardId && t.DeletedAt != null)
            .OrderByDescending(t => t.DeletedAt).Select(t => new { t.Id, t.Name }).ToListAsync(ct);
        var name = CardTags.Normalize(tag);
        var match = Guid.TryParse(tag, out var id)
            ? tags.FirstOrDefault(t => t.Id == id)
            : tags.FirstOrDefault(t => t.Name == name);
        return match?.Id ?? throw new McpException(
            $"No deleted tag \"{tag}\" on this board. Deleted tags: {(tags.Count == 0 ? "none" : string.Join(", ", tags.Select(t => $"\"{t.Name}\"").Distinct()))}.");
    }

    /// <summary>
    /// People and agents by principal id or (case-insensitive) current name: anyone who can be assigned on the board,
    /// plus whoever is already on the card (they may stay after losing access).
    /// </summary>
    private static async Task<List<Guid>> ResolveAssigneesAsync(
        HttpContext http, PrikBoardDbContext db, Guid boardId, IEnumerable<string> values, List<Guid> current, CancellationToken ct)
    {
        // Looked up once per call: create_cards resolves people for every card.
        var key = "PrikBoard.Mcp.People:" + boardId;
        if (http.Items[key] is not List<Principal> assignable) http.Items[key] = assignable = await BoardAccess.PeopleAsync(db, boardId, ct);
        var people = assignable.ToList();
        var missing = current.Except(people.Select(p => p.Id)).ToList();
        if (missing.Count > 0) people.AddRange(await db.Principals.AsNoTracking().Where(p => missing.Contains(p.Id)).ToListAsync(ct));

        // An agent behind a person's token naming itself means the person it works for, who is assigned instead.
        var me = http.User.PrincipalId();
        var worksFor = await CardEndpoints.WorksForAsync(db, http.User, ct);
        var self = worksFor == me ? null : await db.Principals.AsNoTracking().FirstOrDefaultAsync(p => p.Id == me, ct);

        var ids = new List<Guid>();
        foreach (var value in values)
        {
            var name = value.Trim();
            if (self is not null && (name.Equals(self.ShownName, StringComparison.OrdinalIgnoreCase) || (Guid.TryParse(name, out var mine) && mine == me)))
            {
                ids.Add(worksFor);
                continue;
            }
            var matches = Guid.TryParse(name, out var id)
                ? people.Where(p => p.Id == id).ToList()
                : people.Where(p => p.ShownName.Equals(name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0)
            {
                var names = people.Select(p => p is Agent ? $"\"{p.ShownName}\" (agent)" : $"\"{p.ShownName}\"");
                throw new McpException($"No one called \"{name}\" can be assigned on this board. People: {string.Join(", ", names)}.");
            }
            if (matches.Count > 1)
            {
                throw new McpException(
                    $"Several people are called \"{name}\"; use their principal id: {string.Join(", ", matches.Select(p => p.Id))}.");
            }
            ids.Add(matches[0].Id);
        }
        return ids;
    }
}
