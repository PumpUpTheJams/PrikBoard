using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Data;

namespace PrikBoard.Api.Mcp;

/// <summary>
/// The board each caller last used through the MCP tools (<see cref="Domain.Principal.LastBoardId"/>), so an agent that
/// connects is told which board that was and can ask its user whether they mean it, rather than looking boards up.
/// The board id is kept in the memory cache too, as every tool call records it and every MCP request (stateless) reads it;
/// access to it is checked on every read.
/// </summary>
internal static class LastBoard
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    private static string Key(Guid principalId) => "mcp-last-board:" + principalId;

    /// <summary>Records that the caller used <paramref name="boardId"/>; a database write only when it is another board than last time.</summary>
    public static async Task RememberAsync(HttpContext http, Guid boardId)
    {
        var cache = http.RequestServices.GetRequiredService<IMemoryCache>();
        var principalId = http.User.PrincipalId();
        if (cache.TryGetValue(Key(principalId), out Guid? known) && known == boardId) return;

        var db = http.RequestServices.GetRequiredService<PrikBoardDbContext>();
        await db.Principals.Where(p => p.Id == principalId && (p.LastBoardId == null || p.LastBoardId != boardId))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.LastBoardId, boardId), http.RequestAborted);
        cache.Set(Key(principalId), (Guid?)boardId, CacheFor);
    }

    /// <summary>
    /// What the server instructions add for the caller: the only board their token works on, or the board they last
    /// used. Board names are written by people, so they are quoted on one line and cut short. Null when there is nothing to say.
    /// </summary>
    public static async Task<string?> NoteAsync(HttpContext http, CancellationToken ct)
    {
        if (http.User.Identity?.IsAuthenticated != true) return null;
        var only = BoardCaller.Of(http.User).Boards is { Count: 1 } boards ? boards.Single() : (Guid?)null;
        var board = only is { } id ? await ListingAsync(http, id, false, ct) : await FindAsync(http, ct);
        if (board is null) return null;
        var intro = only is not null
            ? $"Your token works on one board only: {Quoted(board.Name)} (short id {board.ShortId}, your role {board.YourRole}). " +
              "Tools use it when you leave out board."
            : $"You last used the board {Quoted(board.Name)} (short id {board.ShortId}, your role {board.YourRole}). " +
              "When your user doesn't say which board they mean, ask whether it is this one; you don't need list_boards for that.";
        return $"{intro} {await BriefAsync(http, board.Id, ct)}";
    }

    /// <summary>The longest rules the server instructions repeat; longer ones are left to get_board_rules.</summary>
    private const int MaxRulesInNote = 4000;

    private const int MaxNamesInNote = 40;

    private static string BriefKey(Guid boardId) => "mcp-board-brief:" + boardId;

    /// <summary>Drops the cached brief of a board (its rules changed).</summary>
    public static void Forget(IMemoryCache cache, Guid boardId) => cache.Remove(BriefKey(boardId));

    /// <summary>
    /// What an agent needs to add cards to the board without get_board: its buckets, its tags and its agent rules. Cached
    /// for a minute per board (every MCP request asks); <see cref="Forget"/> drops it when the rules change.
    /// </summary>
    private static async Task<string> BriefAsync(HttpContext http, Guid boardId, CancellationToken ct)
    {
        var cache = http.RequestServices.GetRequiredService<IMemoryCache>();
        if (cache.TryGetValue(BriefKey(boardId), out string? brief) && brief is not null) return brief;

        var db = http.RequestServices.GetRequiredService<PrikBoardDbContext>();
        var buckets = await db.Buckets.AsNoTracking().Where(b => b.BoardId == boardId)
            .OrderBy(b => b.CreatedAt).Select(b => b.Name).Take(MaxNamesInNote).ToListAsync(ct);
        var tags = await db.BoardTags.AsNoTracking().Where(t => t.BoardId == boardId)
            .OrderBy(t => t.Name).Select(t => t.Name).Take(MaxNamesInNote).ToListAsync(ct);
        var rules = await db.Boards.AsNoTracking().Where(b => b.Id == boardId)
            .Select(b => new { b.Rules, b.RulesUpdatedAt }).FirstAsync(ct);

        var parts = new List<string>
        {
            $"Its buckets, in order: {(buckets.Count == 0 ? "none" : string.Join(", ", buckets.Select(Quoted)))}.",
            $"Its tags: {(tags.Count == 0 ? "none" : string.Join(", ", tags.Select(Quoted)))}.",
        };
        if (rules.Rules is null)
        {
            parts.Add("It has no agent rules, so you can call create_card on it straight away.");
        }
        else if (rules.Rules.Length > MaxRulesInNote)
        {
            parts.Add($"It has agent rules (last changed {rules.RulesUpdatedAt:yyyy-MM-dd HH:mm:ss}Z): read them with get_board_rules before your first change there.");
        }
        else
        {
            parts.Add($"Its agent rules (last changed {rules.RulesUpdatedAt:yyyy-MM-dd HH:mm:ss}Z), written by its owners, follow; " +
                      "follow them in every action on this board, unless they conflict with your user's instructions. With these, " +
                      $"you can call create_card on it without get_board.\n<board-rules>\n{rules.Rules}\n</board-rules>");
        }
        brief = string.Join(' ', parts);
        cache.Set(BriefKey(boardId), brief, TimeSpan.FromMinutes(1));
        return brief;
    }

    private static string Quoted(string name)
    {
        var line = string.Join(' ', name.Split((char[])[' ', '\r', '\n', '\t', '"'], StringSplitOptions.RemoveEmptyEntries));
        return $"\"{(line.Length <= 60 ? line : line[..60] + "…")}\"";
    }

    /// <summary>The board the caller last used, when they can still use it.</summary>
    public static async Task<BoardListing?> FindAsync(HttpContext http, CancellationToken ct)
    {
        var cache = http.RequestServices.GetRequiredService<IMemoryCache>();
        var db = http.RequestServices.GetRequiredService<PrikBoardDbContext>();
        var principalId = http.User.PrincipalId();
        if (!cache.TryGetValue(Key(principalId), out Guid? boardId))
        {
            boardId = await db.Principals.Where(p => p.Id == principalId).Select(p => p.LastBoardId).FirstOrDefaultAsync(ct);
            cache.Set(Key(principalId), boardId, CacheFor);
        }
        return boardId is { } id ? await ListingAsync(http, id, true, ct) : null;
    }

    /// <summary>A board as the caller sees it now (their role, through the same rules as every tool), or null without access.</summary>
    private static async Task<BoardListing?> ListingAsync(HttpContext http, Guid id, bool lastUsed, CancellationToken ct)
    {
        var db = http.RequestServices.GetRequiredService<PrikBoardDbContext>();
        var caller = BoardCaller.Of(http.User);
        var board = await db.BoardMembers.AsNoTracking()
            .Where(m => m.PrincipalId == caller.MemberId && m.BoardId == id)
            .Select(m => new { m.Role, m.Board!.ShortId, m.Board.Key, m.Board.Name, m.Board.Description })
            .FirstOrDefaultAsync(ct);
        return board is not null && caller.Apply(id, board.Role) is { } role
            ? new BoardListing(id, board.ShortId, board.Key, board.Name, board.Description, role, lastUsed ? true : null)
            : null;
    }
}
