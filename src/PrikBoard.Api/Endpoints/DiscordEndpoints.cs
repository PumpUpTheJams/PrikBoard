using System.Buffers.Text;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;
using PrikBoard.Api.Live;

namespace PrikBoard.Api.Endpoints;

/// <summary>
/// The PrikBoard Discord bot's link previews (docs/discord.md). A Discord server is connected to boards with the VS Code
/// sign-in's paste flow: the bot links an admin to the connect page with a PKCE challenge, a board owner approves there
/// (approveDiscordConnect) and gets a code, and the admin pastes it into /prikboard finish, where the bot trades it with
/// its verifier and the server Discord vouches for (exchangeDiscordConnect). The server becomes a Viewer agent on those
/// boards with a read-only <see cref="AccessTokenKind.Discord"/> token, which the bot uses for getLinkPreview.
/// </summary>
public static class DiscordEndpoints
{
    /// <summary>Rate-limiter policy for trading codes, which anyone can call.</summary>
    public const string ExchangeLimit = "discord-connect";

    /// <summary>Rate-limiter policy for link previews, per connected server.</summary>
    public const string PreviewLimit = "link-previews";

    /// <summary>The PrikBoard origin pasted links are matched against, e.g. https://board.example.com.</summary>
    public const string PublicUrlSetting = "Discord:PublicUrl";

    public static RouteGroupBuilder MapDiscordEndpoints(this RouteGroupBuilder api)
    {
        api.MapPost("/me/discord-connects", ApproveDiscordConnect)
            .WithTags("Discord")
            .NoBoardScope()
            .WithName("approveDiscordConnect")
            .WithSummary("Approve connecting a Discord server")
            .WithDescription("Called by PrikBoard's Discord connect page when you connect a Discord server to boards you own, so " +
                             "the PrikBoard bot can preview their links there. Returns a one-time code to paste into Discord with " +
                             "/prikboard finish. Browser sessions from PrikBoard's own pages only; Owner on every board.")
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        api.MapPost("/discord/connect/token", ExchangeDiscordConnect)
            .WithTags("Discord")
            .NoBoardScope()
            .AllowAnonymous()
            .RequireRateLimiting(ExchangeLimit)
            .WithName("exchangeDiscordConnect")
            .WithSummary("Trade a Discord connect code for the server's token")
            .WithDescription("The Discord bot sends the code from approveDiscordConnect, its PKCE code verifier, and the server and " +
                             "channel from Discord's interaction. The server becomes a Viewer on the approved boards and gets a " +
                             "read-only token for 365 days, replacing its old one. Only boards the approver still owns are connected. " +
                             "Each code works once, for 5 minutes. 409 when the approver already has the maximum number of tokens.")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status429TooManyRequests);

        var bot = api.MapGroup("").WithTags("Discord").NoBoardScope().RequireAuthorization(AuthSetup.DiscordPolicy);

        bot.MapGet("/discord/status", GetDiscordStatus)
            .WithName("getDiscordStatus")
            .WithSummary("Get the Discord server's boards")
            .WithDescription("Returns the boards this server is connected to, with their channel limits, and when its token expires. " +
                             "Discord server tokens only.")
            .Produces(StatusCodes.Status403Forbidden);

        bot.MapPost("/link-previews", GetLinkPreview)
            .RequireRateLimiting(PreviewLimit)
            .WithName("getLinkPreview")
            .WithSummary("Preview a pasted PrikBoard link")
            .WithDescription("Returns what a link pasted in Discord points to: a card (readable id, title, description, color), a board " +
                             "(name, description) or an open invite (board, role, expiry; never spent). Every other link gives " +
                             "the same 404: another host, a board or card this server can't see or that doesn't exist, a channel " +
                             "the board isn't previewed in, or a used, revoked or expired invite. Discord server tokens only.")
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status429TooManyRequests);

        bot.MapDelete("/discord/token", RevokeDiscordToken)
            .WithName("revokeDiscordToken")
            .WithSummary("Revoke the Discord server's own token")
            .WithDescription("Called by /prikboard disconnect: the token stops working at once. The server stays on the boards' " +
                             "member lists until an owner removes it, and connecting again gives it a new token. Discord server tokens only.")
            .Produces(StatusCodes.Status403Forbidden);

        var board = api.MapGroup("/boards/{boardId:guid}/discord").WithTags("Discord");

        board.MapGet("/", ListDiscordConnections)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("listDiscordConnections")
            .WithSummary("List the Discord servers connected to a board")
            .WithDescription("Lists the Discord servers whose PrikBoard bot previews this board's links, with the channels it may do so in.");

        board.MapDelete("/{guildId}", RemoveDiscordConnection)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("removeDiscordConnection")
            .WithSummary("Disconnect a Discord server from a board")
            .WithDescription("Removes the server from the board's members, so its links are no longer previewed there. Previews " +
                             "already posted stay in Discord.");

        board.MapDelete("/{guildId}/channels/{channelId}", RemoveDiscordChannel)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("removeDiscordChannel")
            .WithSummary("Stop previewing a board in a Discord channel")
            .WithDescription("Removes one channel from where the server previews the board's links. Removing the last one " +
                             "disconnects the server from the board. 404 when the channel is not in the list, also when the server " +
                             "previews the board in every channel.");

        return api;
    }

    private static async Task<Results<Ok<DiscordConnectCode>, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> ApproveDiscordConnect(
        ApproveDiscordConnectRequest request, ClaimsPrincipal principal, HttpRequest http, PrikBoardDbContext db, CancellationToken ct)
    {
        // Like app sign-ins: a token cannot connect servers, and another site cannot make the person's browser do it.
        if (principal.IsToken() || !SameOrigin.Check(http)) return TypedResults.Forbid();
        var userId = principal.UserId();

        if (!Base64Url.IsValid(request.CodeChallenge))
        {
            return Problems.Invalid("codeChallenge", "The base64url SHA-256 of the code verifier (S256), from the link the bot posted.");
        }

        var boardIds = request.Boards.Select(b => b.BoardId).Distinct().ToList();
        var owned = await db.BoardMembers.CountAsync(m => m.PrincipalId == userId && m.Role == BoardRole.Owner && boardIds.Contains(m.BoardId), ct);
        if (owned != boardIds.Count || boardIds.Count != request.Boards.Count)
        {
            return Problems.Invalid("boards", "Only boards you own, each once.");
        }

        var now = DateTimeOffset.UtcNow;
        await db.DiscordConnectGrants.Where(g => g.OwnerId == userId && g.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        if (await db.DiscordConnectGrants.CountAsync(g => g.OwnerId == userId, ct) >= DiscordConnectGrant.MaxPendingPerOwner)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Too many connections waiting",
                detail: "Finish the Discord connections you started, or wait a few minutes for them to expire.");
        }

        var code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var grant = new DiscordConnectGrant
        {
            OwnerId = userId,
            CodeHash = AccessTokens.Hash(code),
            CodeChallenge = request.CodeChallenge,
            ChannelBoardIds = request.Boards.Where(b => !b.WholeServer).Select(b => b.BoardId).ToList(),
            WholeServerBoardIds = request.Boards.Where(b => b.WholeServer).Select(b => b.BoardId).ToList(),
            ExpiresAt = now.Add(DiscordConnectGrant.Lifetime),
        };
        db.DiscordConnectGrants.Add(grant);
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(new DiscordConnectCode(code, grant.ExpiresAt));
    }

    private static async Task<Results<Created<DiscordConnected>, ValidationProblem, ProblemHttpResult>> ExchangeDiscordConnect(
        ExchangeDiscordConnectRequest request, PrikBoardDbContext db, IMemoryCache cache, BoardEvents events, CancellationToken ct)
    {
        if (!DiscordGuild.IsId(request.GuildId) || !DiscordGuild.IsId(request.ChannelId))
        {
            return Problems.Invalid(DiscordGuild.IsId(request.GuildId) ? "channelId" : "guildId", "A Discord id: digits only.");
        }

        var hash = AccessTokens.Hash(request.Code);
        var grant = await db.DiscordConnectGrants.AsNoTracking().FirstOrDefaultAsync(g => g.CodeHash == hash, ct);

        // Spent on any attempt, right or wrong, and only once even when two requests race.
        var spent = grant is not null && await db.DiscordConnectGrants.Where(g => g.Id == grant.Id).ExecuteDeleteAsync(ct) == 1;
        if (grant is null || !spent || grant.ExpiresAt <= DateTimeOffset.UtcNow || !AppSignInEndpoints.Matches(request.CodeVerifier, grant.CodeChallenge))
        {
            return Problems.Invalid("code", "Unknown, used or expired code, or a code started in another server. Run /prikboard connect again.");
        }

        var guildName = AppSignInEndpoints.CleanName(request.GuildName);
        var channel = new DiscordChannel(request.ChannelId, AppSignInEndpoints.CleanName(request.ChannelName));

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Only boards the person still owns: their role may have changed in the minutes since they approved.
        var wanted = grant.ChannelBoardIds.Concat(grant.WholeServerBoardIds).ToList();
        foreach (var boardId in wanted.Order()) await MemberEndpoints.LockBoard(db, boardId, ct);
        var stillOwned = await db.BoardMembers
            .Where(m => m.PrincipalId == grant.OwnerId && m.Role == BoardRole.Owner && wanted.Contains(m.BoardId))
            .Select(m => m.BoardId).ToListAsync(ct);
        if (stillOwned.Count == 0)
        {
            return Problems.Invalid("code", "Whoever approved this no longer owns any of its boards.");
        }

        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Issuer == Agent.DiscordIssuer && a.ClientId == request.GuildId, ct);
        if (agent is null)
        {
            agent = new Agent { Issuer = Agent.DiscordIssuer, ClientId = request.GuildId };
            db.Agents.Add(agent);
        }
        agent.DisplayName = guildName;
        agent.LastSeenAt = DateTimeOffset.UtcNow;

        var guild = await db.DiscordGuilds.FirstOrDefaultAsync(g => g.GuildId == request.GuildId, ct);
        if (guild is null)
        {
            guild = new DiscordGuild { GuildId = request.GuildId, AgentId = agent.Id };
            db.DiscordGuilds.Add(guild);
        }
        guild.ConnectedById = grant.OwnerId;
        guild.ConnectedAt = DateTimeOffset.UtcNow;

        var members = await db.BoardMembers.Where(m => m.PrincipalId == agent.Id && stillOwned.Contains(m.BoardId)).ToListAsync(ct);
        var limits = await db.DiscordBoardChannels.Where(c => c.GuildId == request.GuildId && stillOwned.Contains(c.BoardId)).ToListAsync(ct);
        List<Guid> joined = [];
        foreach (var boardId in stillOwned)
        {
            var wholeServer = grant.WholeServerBoardIds.Contains(boardId);
            var member = members.FirstOrDefault(m => m.BoardId == boardId);
            var limit = limits.FirstOrDefault(c => c.BoardId == boardId);
            if (member is null)
            {
                db.BoardMembers.Add(new BoardMember { BoardId = boardId, PrincipalId = agent.Id, Role = BoardRole.Viewer });
                joined.Add(boardId);
            }

            if (limit is null)
            {
                db.DiscordBoardChannels.Add(new DiscordBoardChannels
                {
                    GuildId = request.GuildId, BoardId = boardId, Channels = wholeServer ? null : [channel],
                });
            }
            else if (member is null || wholeServer)
            {
                // Left behind by a removed membership, so this connection starts afresh; or widened to the whole server.
                limit.Channels = wholeServer ? null : [channel];
            }
            else if (limit.Channels is { } channels)
            {
                // Another channel, or the same one under a new name.
                limit.Channels = [.. channels.Where(c => c.Id != channel.Id), channel];
            }
        }

        // One token per server: a new connection replaces the old one.
        var old = await db.AccessTokens.Where(t => t.Kind == AccessTokenKind.Discord && t.AgentId == agent.Id).ToListAsync(ct);
        db.AccessTokens.RemoveRange(old);
        var ownerTokens = await db.AccessTokens.CountAsync(t => t.OwnerId == grant.OwnerId, ct) - old.Count(t => t.OwnerId == grant.OwnerId);
        if (ownerTokens >= AccessToken.MaxPerOwner)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Too many access tokens",
                detail: $"Whoever approved this has {AccessToken.MaxPerOwner} access tokens already; they can revoke some in PrikBoard under Account.");
        }

        var (token, hint) = AccessTokens.Generate();
        var record = new AccessToken
        {
            Kind = AccessTokenKind.Discord,
            OwnerId = grant.OwnerId,
            AgentId = agent.Id,
            Name = guildName,
            TokenHash = AccessTokens.Hash(token),
            Hint = hint,
            MaxRole = BoardRole.Viewer,
        };
        record.ExpiresAt = record.CreatedAt.AddDays(AccessToken.MaxDays);
        db.AccessTokens.Add(record);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        foreach (var t in old) AccessTokenHandler.Forget(cache, t.TokenHash);
        foreach (var boardId in joined) await events.AccessChangedAsync(boardId, agent.Id, BoardRole.Viewer);
        // The members lists changed (a new member, or the server's new name).
        foreach (var boardId in stillOwned) await events.ChangedAsync(boardId, grant.OwnerId);
        if (joined.Count > 0) await events.BoardsChangedAsync([agent.Id]);

        return TypedResults.Created("/api/discord/status",
            new DiscordConnected(token, record.ExpiresAt, await ConnectionsOfAsync(db, agent.Id, request.GuildId, ct)));
    }

    private static async Task<Results<Ok<DiscordStatus>, NotFound>> GetDiscordStatus(ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        var agentId = principal.PrincipalId();
        var guild = await db.DiscordGuilds.AsNoTracking().Include(g => g.Agent).FirstOrDefaultAsync(g => g.AgentId == agentId, ct);
        if (guild is null) return TypedResults.NotFound();

        var tokenId = principal.TokenId();
        var expiresAt = await db.AccessTokens.Where(t => t.Id == tokenId).Select(t => t.ExpiresAt).FirstAsync(ct);
        return TypedResults.Ok(new DiscordStatus(guild.GuildId, guild.Agent!.ShownName, expiresAt,
            await ConnectionsOfAsync(db, agentId, guild.GuildId, ct)));
    }

    private static async Task<Results<Ok<LinkPreview>, NotFound>> GetLinkPreview(
        LinkPreviewRequest request, ClaimsPrincipal principal, PrikBoardDbContext db, IConfiguration config, CancellationToken ct)
    {
        var agentId = principal.PrincipalId();
        var guildId = await db.DiscordGuilds.Where(g => g.AgentId == agentId).Select(g => g.GuildId).FirstOrDefaultAsync(ct);
        if (guildId is null || AppPath(request.Url, config[PublicUrlSetting]) is not { } path) return TypedResults.NotFound();

        // Anyone holding an invite link is meant to see the board's name, so invites need no membership or channel.
        if (path.StartsWith(InvitePrefix, StringComparison.Ordinal))
        {
            var token = path[InvitePrefix.Length..];
            if (token.Length == 0 || token.Length > 100 || !token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')) return TypedResults.NotFound();
            if (await InviteEndpoints.FindOpenAsync(db, token, ct) is not { } found) return TypedResults.NotFound();
            var (invite, invited) = found;
            return TypedResults.Ok(new LinkPreview(LinkPreviewKind.Invite, invited.Name,
                Role: invite.Role, InvitedBy: invite.CreatedBy?.ShownName, ExpiresAt: invite.ExpiresAt));
        }

        Board? board;
        Card? card = null;
        if (CardLinks.TryParse(path, out var linked))
        {
            var cards = db.Cards.AsNoTracking().Include(c => c.Board);
            card = linked.ByNumber
                ? await cards.FirstOrDefaultAsync(c => c.Board!.ShortId == linked.BoardShortId && c.Number == linked.Number, ct)
                : await cards.FirstOrDefaultAsync(c => c.Id == linked.CardId, ct);
            if (card is null) return TypedResults.NotFound();
            board = card.Board;
        }
        else if (CardLinks.TryParseBoard(path, out var shortId, out var boardId))
        {
            board = await db.Boards.AsNoTracking().FirstOrDefaultAsync(b => shortId == null ? b.Id == boardId : b.ShortId == shortId, ct);
        }
        else
        {
            return TypedResults.NotFound();
        }

        if (board is null || await BoardAccess.RoleOnBoardAsync(db, board.Id, principal, ct) is null) return TypedResults.NotFound();
        var limit = await db.DiscordBoardChannels.AsNoTracking().FirstOrDefaultAsync(c => c.GuildId == guildId && c.BoardId == board.Id, ct);
        if (limit is null || !limit.Allows(request.ChannelId)) return TypedResults.NotFound();

        return TypedResults.Ok(card is null
            ? new LinkPreview(LinkPreviewKind.Board, board.Name, Description: CardPreview.Describe(board), Path: CardLinks.BoardPath(board.ShortId))
            : new LinkPreview(LinkPreviewKind.Card, board.Name,
                ReadableId: CardLinks.ReadableId(board.Key, card.Number),
                Title: card.Title,
                Description: CardPreview.Describe(card),
                Color: card.Color,
                Path: CardLinks.CardPath(board.ShortId, card.Number)));
    }

    private static async Task<NoContent> RevokeDiscordToken(ClaimsPrincipal principal, PrikBoardDbContext db, IMemoryCache cache, CancellationToken ct)
    {
        var tokenId = principal.TokenId();
        var token = await db.AccessTokens.FirstOrDefaultAsync(t => t.Id == tokenId && t.Kind == AccessTokenKind.Discord, ct);
        if (token is not null)
        {
            db.AccessTokens.Remove(token);
            await db.SaveChangesAsync(ct);
            AccessTokenHandler.Forget(cache, token.TokenHash);
        }
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<DiscordConnection>>> ListDiscordConnections(Guid boardId, PrikBoardDbContext db, CancellationToken ct)
    {
        var rows = await (
                from member in db.BoardMembers
                where member.BoardId == boardId
                join guild in db.DiscordGuilds on member.PrincipalId equals guild.AgentId
                select new { member.AddedAt, guild.GuildId, guild.AgentId, guild.Agent, guild.ConnectedById })
            .AsNoTracking().ToListAsync(ct);
        var guildIds = rows.Select(r => r.GuildId).ToList();
        var limits = await db.DiscordBoardChannels.AsNoTracking()
            .Where(c => c.BoardId == boardId && guildIds.Contains(c.GuildId)).ToDictionaryAsync(c => c.GuildId, ct);
        var byIds = rows.Where(r => r.ConnectedById != null).Select(r => r.ConnectedById!.Value).Distinct().ToList();
        var names = await db.Principals.AsNoTracking().Where(p => byIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.ShownName, ct);

        return TypedResults.Ok(rows
            .OrderBy(r => r.Agent!.ShownName, StringComparer.OrdinalIgnoreCase)
            .Select(r => new DiscordConnection(r.GuildId, r.Agent!.ShownName, r.AgentId,
                // A server without its row previews nothing here; show it as limited to no channels.
                limits.TryGetValue(r.GuildId, out var limit) ? DiscordChannelDetail.From(limit.Channels) : [],
                r.AddedAt,
                r.ConnectedById is { } by && names.TryGetValue(by, out var name) ? name : null))
            .ToList());
    }

    private static async Task<Results<NoContent, NotFound>> RemoveDiscordConnection(
        Guid boardId, string guildId, PrikBoardDbContext db, BoardEvents events, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await MemberEndpoints.LockBoard(db, boardId, ct);
        var agentId = await DisconnectAsync(db, boardId, guildId, ct);
        if (agentId is null) return TypedResults.NotFound();
        await transaction.CommitAsync(ct);
        await events.AccessChangedAsync(boardId, agentId.Value, null);
        await events.BoardsChangedAsync([agentId.Value]);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> RemoveDiscordChannel(
        Guid boardId, string guildId, string channelId, PrikBoardDbContext db, BoardEvents events, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await MemberEndpoints.LockBoard(db, boardId, ct);
        var limit = await db.DiscordBoardChannels.FirstOrDefaultAsync(c => c.GuildId == guildId && c.BoardId == boardId, ct);
        if (limit?.Channels is not { } channels || channels.All(c => c.Id != channelId)) return TypedResults.NotFound();

        var left = channels.Where(c => c.Id != channelId).ToList();
        if (left.Count > 0)
        {
            limit.Channels = left;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return TypedResults.NoContent();
        }

        var agentId = await DisconnectAsync(db, boardId, guildId, ct);
        await transaction.CommitAsync(ct);
        if (agentId is { } id)
        {
            await events.AccessChangedAsync(boardId, id, null);
            await events.BoardsChangedAsync([id]);
        }
        return TypedResults.NoContent();
    }

    /// <summary>Removes the server's membership and channel limits from the board; its agent id, or null when it wasn't connected.</summary>
    private static async Task<Guid?> DisconnectAsync(PrikBoardDbContext db, Guid boardId, string guildId, CancellationToken ct)
    {
        var agentId = await db.DiscordGuilds.Where(g => g.GuildId == guildId).Select(g => (Guid?)g.AgentId).FirstOrDefaultAsync(ct);
        if (agentId is null) return null;
        var removed = await db.BoardMembers.Where(m => m.BoardId == boardId && m.PrincipalId == agentId).ExecuteDeleteAsync(ct);
        var limits = await db.DiscordBoardChannels.Where(c => c.GuildId == guildId && c.BoardId == boardId).ExecuteDeleteAsync(ct);
        return removed + limits > 0 ? agentId : null;
    }

    /// <summary>The boards a server's agent is a member of, with where it may preview them.</summary>
    private static async Task<List<DiscordBoardConnection>> ConnectionsOfAsync(PrikBoardDbContext db, Guid agentId, string guildId, CancellationToken ct)
    {
        var boards = await db.BoardMembers.AsNoTracking()
            .Where(m => m.PrincipalId == agentId)
            .Select(m => new { m.Board!.Id, m.Board.ShortId, m.Board.Name })
            .ToListAsync(ct);
        var limits = await db.DiscordBoardChannels.AsNoTracking().Where(c => c.GuildId == guildId).ToDictionaryAsync(c => c.BoardId, ct);
        return boards
            .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .Select(b => new DiscordBoardConnection(b.Id, b.ShortId, b.Name,
                limits.TryGetValue(b.Id, out var limit) ? DiscordChannelDetail.From(limit.Channels) : []))
            .ToList();
    }

    private const string InvitePrefix = "/#/invite/";

    /// <summary>
    /// The web app's path a link holds ("/#/boards/…"), when it is a link to this PrikBoard: same scheme and host as the
    /// configured public URL, at its path. Null for anything else, and for every link while no public URL is configured.
    /// </summary>
    internal static string? AppPath(string url, string? publicUrl)
    {
        if (!Uri.TryCreate(publicUrl, UriKind.Absolute, out var site)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var link)
            || link.Scheme != site.Scheme
            || !string.Equals(link.Authority, site.Authority, StringComparison.OrdinalIgnoreCase)
            || link.UserInfo.Length > 0
            || link.Query.Length > 0
            || link.AbsolutePath.TrimEnd('/') != site.AbsolutePath.TrimEnd('/')
            || !link.Fragment.StartsWith("#/", StringComparison.Ordinal))
        {
            return null;
        }
        return "/" + link.Fragment;
    }
}
