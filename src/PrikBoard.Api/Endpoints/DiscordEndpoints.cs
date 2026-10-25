using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;
using PrikBoard.Api.Integrations;
using PrikBoard.Api.Live;

namespace PrikBoard.Api.Endpoints;

/// <summary>
/// A board's Discord connections (docs/discord.md), set by its owners under Integrations: they pick a server the
/// instance's bot is in (<see cref="IDiscordGateway"/>), and channels of it and/or the whole server. The server becomes a
/// Viewer agent on the board; <see cref="ChatBoardChannels"/> keeps where links preview and what each channel does
/// (posting updates, Save to PrikBoard's bucket). Also the link between a person's Discord and PrikBoard accounts. An
/// admin sets the bot up (<see cref="DiscordBotEndpoints"/>).
/// </summary>
public static class DiscordEndpoints
{
    public static RouteGroupBuilder MapDiscordEndpoints(this RouteGroupBuilder api)
    {
        var board = api.MapGroup("/boards/{boardId:guid}/discord").WithTags("Discord");

        board.MapGet("/", ListDiscordConnections)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("listDiscordConnections")
            .WithSummary("List the Discord servers connected to a board")
            .WithDescription("Lists the Discord servers where PrikBoard's bot previews this board's links: the whole server and/or " +
                             "channels, and what each channel does for the board (updates it posts, where Save to PrikBoard puts cards).");

        board.MapGet("/servers", ListDiscordServers)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("listDiscordServers")
            .WithSummary("List the Discord servers a board can be connected to")
            .WithDescription("The servers this PrikBoard's Discord bot is in, each with the text channels where it may read and post, " +
                             "and whether the bot is online. State Off means no admin has set up a bot yet.");

        board.MapPost("/", ConnectDiscordChannel)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("connectDiscordChannel")
            .WithSummary("Connect a board to a Discord channel")
            .WithDescription("Connects the board to a channel of a server the bot is in, or to every channel of it when channelId is " +
                             "left out; channels and the whole server can be combined. Its links and readable ids preview there, and " +
                             "anyone in those channels can see the titles, descriptions, tags and people of the cards shown. The server " +
                             "becomes a Viewer on the board. Returns the board's Discord connections. Browser sessions only. 409 when " +
                             "the board already has the maximum number of channels (50) in that server.")
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        board.MapPut("/{guildId}/channels/{channelId}", UpdateDiscordChannel)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("updateDiscordChannel")
            .WithSummary("Set what a Discord channel does for a board")
            .WithDescription("Sets the board activity the bot posts in the channel (new cards, moves between buckets, assignments, " +
                             "agents at work; none by default) and the bucket Save to PrikBoard there puts cards in. Returns the " +
                             "board's Discord connections.")
            .ProducesValidationProblem();

        board.MapDelete("/{guildId}", RemoveDiscordConnection)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("removeDiscordConnection")
            .WithSummary("Disconnect a Discord server from a board")
            .WithDescription("Removes the server from the board's members, so its links are no longer previewed there. Previews " +
                             "already posted say the board is no longer available.");

        board.MapDelete("/{guildId}/whole-server", RemoveDiscordWholeServer)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("removeDiscordWholeServer")
            .WithSummary("Stop previewing a board in every channel of a Discord server")
            .WithDescription("Keeps the board in the channels picked for it; without any, the server is disconnected from the board. " +
                             "404 when the board isn't connected to the whole server.");

        board.MapDelete("/{guildId}/channels/{channelId}", RemoveDiscordChannel)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("removeDiscordChannel")
            .WithSummary("Disconnect a board from a Discord channel")
            .WithDescription("Removes one channel, with its settings. Removing the last one disconnects the server from the board, " +
                             "unless the board is connected to the whole server. 404 when the channel is not in the list.");

        var me = api.MapGroup("/me/discord-account").WithTags("Discord").NoBoardScope();

        me.MapGet("/", GetDiscordAccount)
            .WithName("getDiscordAccount")
            .WithSummary("Whether the Discord bot knows who you are")
            .WithDescription("Whether your Discord account is linked to you, for Save to PrikBoard: through a Discord sign-in, or a " +
                             "link you made from the bot. Sessions only.")
            .Produces(StatusCodes.Status403Forbidden);

        me.MapGet("/links/{code}", PreviewDiscordAccountLink)
            .WithName("previewDiscordAccountLink")
            .WithSummary("Show whose Discord account a link code is for")
            .WithDescription("The Discord name behind a code from the bot's \"Connect your PrikBoard account\" button, so you can " +
                             "check it is you before linking. 404 for an unknown, used or expired code. Sessions only.")
            .Produces(StatusCodes.Status403Forbidden);

        me.MapPost("/", LinkDiscordAccount)
            .WithName("linkDiscordAccount")
            .WithSummary("Link your Discord account")
            .WithDescription("Links the Discord account behind a code from the bot to you, replacing an earlier link. What that " +
                             "Discord account then does through the bot (Save to PrikBoard) is done as you, so only link your own. " +
                             "Each code works once, for 10 minutes. Browser sessions from PrikBoard's own pages only.")
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden);

        me.MapDelete("/", UnlinkDiscordAccount)
            .WithName("unlinkDiscordAccount")
            .WithSummary("Unlink your Discord account")
            .WithDescription("Removes a link you made from the bot. A Discord sign-in still says who you are; unlink that sign-in " +
                             "under Account instead. Sessions only.")
            .Produces(StatusCodes.Status403Forbidden);

        return api;
    }

    private static async Task<Ok<List<DiscordConnection>>> ListDiscordConnections(
        Guid boardId, PrikBoardDbContext db, IDiscordGateway gateway, CancellationToken ct) =>
        TypedResults.Ok(await ConnectionsAsync(db, gateway, boardId, ct));

    private static Ok<DiscordServers> ListDiscordServers(Guid boardId, IDiscordGateway gateway) =>
        TypedResults.Ok(new DiscordServers(gateway.State, gateway.Servers
            .Select(s => new DiscordServerChoice(s.Id, s.Name, s.Channels.Select(c => new DiscordChannelChoice(c.Id, c.Name)).ToList()))
            .ToList()));

    private static async Task<Results<Ok<List<DiscordConnection>>, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> ConnectDiscordChannel(
        Guid boardId, ConnectDiscordRequest request, ClaimsPrincipal principal, IDiscordGateway gateway, PrikBoardDbContext db,
        BoardEvents events, CancellationToken ct)
    {
        // The connection names who made it, so it takes a person.
        if (principal.IsToken()) return TypedResults.Forbid();

        // Only what the bot sees itself: the server and channel names come from Discord, not from the request.
        if (gateway.Servers.FirstOrDefault(s => s.Id == request.GuildId) is not { } server)
        {
            return Problems.Invalid("guildId", "A server PrikBoard's Discord bot is in (listDiscordServers).");
        }
        ChatChannel? channel = null;
        if (request.ChannelId is { } channelId && (channel = server.Channels.FirstOrDefault(c => c.Id == channelId)) is null)
        {
            return Problems.Invalid("channelId", "A channel of that server where the bot may read and post (listDiscordServers).");
        }

        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await MemberEndpoints.LockBoard(db, boardId, ct);

        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Issuer == Agent.DiscordIssuer && a.ClientId == server.Id, ct);
        if (agent is null)
        {
            agent = new Agent { Issuer = Agent.DiscordIssuer, ClientId = server.Id };
            db.Agents.Add(agent);
        }
        agent.DisplayName = AppSignInEndpoints.CleanName(server.Name);
        agent.LastSeenAt = now;

        var guild = await db.ChatWorkspaces.FirstOrDefaultAsync(g => g.Platform == ChatPlatform.Discord && g.WorkspaceId == server.Id, ct);
        if (guild is null)
        {
            guild = new ChatWorkspace { Platform = ChatPlatform.Discord, WorkspaceId = server.Id, AgentId = agent.Id };
            db.ChatWorkspaces.Add(guild);
        }
        guild.ConnectedById = principal.UserId();
        guild.ConnectedAt = now;

        var member = await db.BoardMembers.FirstOrDefaultAsync(m => m.BoardId == boardId && m.PrincipalId == agent.Id, ct);
        if (member is null) db.BoardMembers.Add(new BoardMember { BoardId = boardId, PrincipalId = agent.Id, Role = BoardRole.Viewer });

        var limit = await FindLimitAsync(db, server.Id, boardId, ct);
        if (limit is null)
        {
            limit = new ChatBoardChannels { Platform = ChatPlatform.Discord, WorkspaceId = server.Id, BoardId = boardId };
            db.ChatBoardChannels.Add(limit);
        }
        else if (member is null)
        {
            // Left behind by a removed membership: this connection starts afresh.
            limit.WholeWorkspace = false;
            limit.Channels = [];
        }

        if (channel is null)
        {
            limit.WholeWorkspace = true;
        }
        else if (limit.Channel(channel.Id) is { } known)
        {
            // The same channel again keeps its settings, under its current name.
            limit.Channels = [.. limit.Channels.Select(c => c.Id == channel.Id ? known with { Name = channel.Name } : c)];
        }
        else
        {
            if (limit.Channels.Count >= ChatBoardChannels.MaxChannels)
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Too many channels",
                    detail: $"A board can be connected to at most {ChatBoardChannels.MaxChannels} channels of a server. Connect it to the whole server instead, or remove channels.");
            }
            limit.Channels = [.. limit.Channels, channel];
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        if (member is null)
        {
            await events.AccessChangedAsync(boardId, agent.Id, BoardRole.Viewer);
            await events.BoardsChangedAsync([agent.Id]);
        }
        return TypedResults.Ok(await ConnectionsAsync(db, gateway, boardId, ct));
    }

    private static async Task<Results<Ok<List<DiscordConnection>>, NotFound, ValidationProblem>> UpdateDiscordChannel(
        Guid boardId, string guildId, string channelId, UpdateDiscordChannelRequest request, PrikBoardDbContext db, IDiscordGateway gateway,
        CancellationToken ct)
    {
        if (request.SaveBucketId is { } bucketId && !await db.Buckets.AnyAsync(b => b.Id == bucketId && b.BoardId == boardId, ct))
        {
            return Problems.Invalid("saveBucketId", "A bucket of this board.");
        }
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await MemberEndpoints.LockBoard(db, boardId, ct);
        var limit = await FindLimitAsync(db, guildId, boardId, ct);
        if (limit?.Channel(channelId) is not { } channel) return TypedResults.NotFound();

        var changed = channel with { Updates = DiscordUpdateLists.Flags(request.Updates), SaveBucketId = request.SaveBucketId };
        limit.Channels = [.. limit.Channels.Select(c => c.Id == channelId ? changed : c)];
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return TypedResults.Ok(await ConnectionsAsync(db, gateway, boardId, ct));
    }

    private static async Task<Results<NoContent, NotFound>> RemoveDiscordConnection(
        Guid boardId, string guildId, PrikBoardDbContext db, BoardEvents events, DiscordActivity activity, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await MemberEndpoints.LockBoard(db, boardId, ct);
        var agentId = await DisconnectAsync(db, boardId, guildId, ct);
        if (agentId is null) return TypedResults.NotFound();
        await transaction.CommitAsync(ct);
        await DisconnectedAsync(events, activity, boardId, agentId.Value);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> RemoveDiscordWholeServer(
        Guid boardId, string guildId, PrikBoardDbContext db, BoardEvents events, DiscordActivity activity, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await MemberEndpoints.LockBoard(db, boardId, ct);
        var limit = await FindLimitAsync(db, guildId, boardId, ct);
        if (limit is not { WholeWorkspace: true }) return TypedResults.NotFound();
        return await NarrowAsync(db, events, activity, transaction, limit, l => l.WholeWorkspace = false, ct);
    }

    private static async Task<Results<NoContent, NotFound>> RemoveDiscordChannel(
        Guid boardId, string guildId, string channelId, PrikBoardDbContext db, BoardEvents events, DiscordActivity activity, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await MemberEndpoints.LockBoard(db, boardId, ct);
        var limit = await FindLimitAsync(db, guildId, boardId, ct);
        if (limit?.Channel(channelId) is null) return TypedResults.NotFound();
        return await NarrowAsync(db, events, activity, transaction, limit, l => l.Channels = [.. l.Channels.Where(c => c.Id != channelId)], ct);
    }

    /// <summary>Takes a channel or the whole server away; with nothing left, the server is disconnected from the board.</summary>
    private static async Task<Results<NoContent, NotFound>> NarrowAsync(
        PrikBoardDbContext db, BoardEvents events, DiscordActivity activity, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        ChatBoardChannels limit, Action<ChatBoardChannels> change, CancellationToken ct)
    {
        change(limit);
        if (limit.WholeWorkspace || limit.Channels.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            activity.RefreshBoard(limit.BoardId);
            return TypedResults.NoContent();
        }

        var agentId = await DisconnectAsync(db, limit.BoardId, limit.WorkspaceId, ct);
        await transaction.CommitAsync(ct);
        if (agentId is { } id) await DisconnectedAsync(events, activity, limit.BoardId, id);
        return TypedResults.NoContent();
    }

    private static async Task DisconnectedAsync(BoardEvents events, DiscordActivity activity, Guid boardId, Guid agentId)
    {
        activity.RefreshBoard(boardId);
        await events.AccessChangedAsync(boardId, agentId, null);
        await events.BoardsChangedAsync([agentId]);
    }

    /// <summary>Removes the server's membership and channel limits from the board; its agent id, or null when it wasn't connected.</summary>
    internal static async Task<Guid?> DisconnectAsync(PrikBoardDbContext db, Guid boardId, string guildId, CancellationToken ct)
    {
        var agentId = await LinkPreviews.AgentIdAsync(db, ChatPlatform.Discord, guildId, ct);
        if (agentId is null) return null;
        var removed = await db.BoardMembers.Where(m => m.BoardId == boardId && m.PrincipalId == agentId).ExecuteDeleteAsync(ct);
        var limits = await db.ChatBoardChannels
            .Where(c => c.Platform == ChatPlatform.Discord && c.WorkspaceId == guildId && c.BoardId == boardId).ExecuteDeleteAsync(ct);
        return removed + limits > 0 ? agentId : null;
    }

    /// <summary>Where a Discord server previews a board; null when it doesn't.</summary>
    private static Task<ChatBoardChannels?> FindLimitAsync(PrikBoardDbContext db, string guildId, Guid boardId, CancellationToken ct) =>
        db.ChatBoardChannels.FirstOrDefaultAsync(c => c.Platform == ChatPlatform.Discord && c.WorkspaceId == guildId && c.BoardId == boardId, ct);

    /// <summary>The servers connected to a board, named as the bot sees them now where it can.</summary>
    private static async Task<List<DiscordConnection>> ConnectionsAsync(PrikBoardDbContext db, IDiscordGateway gateway, Guid boardId, CancellationToken ct)
    {
        var rows = await (
                from member in db.BoardMembers
                where member.BoardId == boardId
                join guild in db.ChatWorkspaces on member.PrincipalId equals guild.AgentId
                where guild.Platform == ChatPlatform.Discord
                select new { member.AddedAt, GuildId = guild.WorkspaceId, guild.AgentId, guild.Agent, guild.ConnectedById })
            .AsNoTracking().ToListAsync(ct);
        var guildIds = rows.Select(r => r.GuildId).ToList();
        var limits = await db.ChatBoardChannels.AsNoTracking()
            .Where(c => c.Platform == ChatPlatform.Discord && c.BoardId == boardId && guildIds.Contains(c.WorkspaceId))
            .ToDictionaryAsync(c => c.WorkspaceId, ct);
        var byIds = rows.Where(r => r.ConnectedById != null).Select(r => r.ConnectedById!.Value).Distinct().ToList();
        var names = await db.Principals.AsNoTracking().Where(p => byIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.ShownName, ct);
        var servers = gateway.Servers.ToDictionary(s => s.Id);

        return rows
            .Select(r =>
            {
                servers.TryGetValue(r.GuildId, out var server);
                // A server without its row previews nothing here; show it with no channels.
                limits.TryGetValue(r.GuildId, out var limit);
                var channels = (limit?.Channels ?? []).Select(c => server?.Channels.FirstOrDefault(now => now.Id == c.Id) is { } now ? c with { Name = now.Name } : c);
                return new DiscordConnection(r.GuildId, server?.Name ?? r.Agent!.ShownName, r.AgentId, limit?.WholeWorkspace ?? false,
                    DiscordChannelDetail.From(channels),
                    r.AddedAt,
                    r.ConnectedById is { } by && names.TryGetValue(by, out var name) ? name : null);
            })
            .OrderBy(c => c.GuildName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static async Task<Results<Ok<DiscordAccountStatus>, ForbidHttpResult>> GetDiscordAccount(
        ClaimsPrincipal principal, ChatAccounts accounts, PrikBoardDbContext db, CancellationToken ct)
    {
        if (principal.IsToken()) return TypedResults.Forbid();
        var (kind, linkedAt) = await accounts.StatusAsync(db, ChatPlatform.Discord, principal.UserId(), ct);
        return TypedResults.Ok(new DiscordAccountStatus(kind is not null,
            kind switch { ChatAccountKind.SignIn => DiscordAccountSource.SignIn, ChatAccountKind.Link => DiscordAccountSource.Link, _ => null },
            linkedAt));
    }

    private static Results<Ok<DiscordAccountLinkPreview>, NotFound, ForbidHttpResult> PreviewDiscordAccountLink(
        string code, ClaimsPrincipal principal, ChatAccounts accounts)
    {
        if (principal.IsToken()) return TypedResults.Forbid();
        return accounts.Peek(ChatPlatform.Discord, code) is { } pending
            ? TypedResults.Ok(new DiscordAccountLinkPreview(pending.ChatName, pending.ExpiresAt))
            : TypedResults.NotFound();
    }

    private static async Task<Results<Ok<DiscordAccountStatus>, ValidationProblem, ForbidHttpResult>> LinkDiscordAccount(
        LinkDiscordAccountRequest request, ClaimsPrincipal principal, HttpRequest http, ChatAccounts accounts, PrikBoardDbContext db,
        CancellationToken ct)
    {
        // Like approving an app: a token cannot link accounts, and another site cannot make the person's browser do it.
        if (principal.IsToken() || !SameOrigin.Check(http)) return TypedResults.Forbid();
        if (!await accounts.LinkAsync(db, ChatPlatform.Discord, request.Code, principal.UserId(), ct))
        {
            return Problems.Invalid("code", "Unknown, used or expired code. Use Save to PrikBoard in Discord again for a new one.");
        }
        return TypedResults.Ok(new DiscordAccountStatus(true, DiscordAccountSource.Link, DateTimeOffset.UtcNow));
    }

    private static async Task<Results<NoContent, ForbidHttpResult>> UnlinkDiscordAccount(
        ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        if (principal.IsToken()) return TypedResults.Forbid();
        var userId = principal.UserId();
        await db.ChatAccounts.Where(a => a.Platform == ChatPlatform.Discord && a.UserId == userId).ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }
}
