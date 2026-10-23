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
/// A board's Discord link previews (docs/discord.md), set by its owners under Integrations: they pick a server the
/// instance's bot is in (<see cref="IDiscordGateway"/>) and a channel of it, or the whole server. The server becomes a
/// Viewer agent on the board, and <see cref="DiscordBoardChannels"/> narrows where the bot may preview its links. An
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
            .WithDescription("Lists the Discord servers where PrikBoard's bot previews this board's links, with the channels it may do so in.");

        board.MapGet("/servers", ListDiscordServers)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("listDiscordServers")
            .WithSummary("List the Discord servers a board can be connected to")
            .WithDescription("The servers this PrikBoard's Discord bot is in, each with the text channels where it may read and post, " +
                             "and whether the bot is online. State Off means no admin has set up a bot yet.");

        board.MapPost("/", ConnectDiscordChannel)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("connectDiscordChannel")
            .WithSummary("Preview a board's links in a Discord channel")
            .WithDescription("Lets the bot preview this board's links in a channel of a server it is in (adding to the channels it " +
                             "has), or in every channel of the server when channelId is left out. The server becomes a Viewer on the " +
                             "board: anyone in those channels can see the titles and descriptions of the cards linked there. Returns the " +
                             "board's Discord connections. Browser sessions only. 409 when the board already has the maximum number of " +
                             "channels (50) in that server.")
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

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

    private static async Task<Ok<List<DiscordConnection>>> ListDiscordConnections(
        Guid boardId, PrikBoardDbContext db, IDiscordGateway gateway, CancellationToken ct) =>
        TypedResults.Ok(await ConnectionsAsync(db, gateway, boardId, ct));

    private static Ok<DiscordServers> ListDiscordServers(Guid boardId, IDiscordGateway gateway) =>
        TypedResults.Ok(new DiscordServers(gateway.State, gateway.Servers
            .Select(s => new DiscordServerChoice(s.Id, s.Name, DiscordChannelDetail.From(s.Channels)!))
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
        DiscordChannel? channel = null;
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

        var guild = await db.DiscordGuilds.FirstOrDefaultAsync(g => g.GuildId == server.Id, ct);
        if (guild is null)
        {
            guild = new DiscordGuild { GuildId = server.Id, AgentId = agent.Id };
            db.DiscordGuilds.Add(guild);
        }
        guild.ConnectedById = principal.UserId();
        guild.ConnectedAt = now;

        var member = await db.BoardMembers.FirstOrDefaultAsync(m => m.BoardId == boardId && m.PrincipalId == agent.Id, ct);
        if (member is null) db.BoardMembers.Add(new BoardMember { BoardId = boardId, PrincipalId = agent.Id, Role = BoardRole.Viewer });

        List<DiscordChannel>? picked = channel is null ? null : [channel];
        var limit = await db.DiscordBoardChannels.FirstOrDefaultAsync(c => c.GuildId == server.Id && c.BoardId == boardId, ct);
        if (limit is null)
        {
            db.DiscordBoardChannels.Add(new DiscordBoardChannels { GuildId = server.Id, BoardId = boardId, Channels = picked });
        }
        else if (member is null || channel is null || limit.Channels is null)
        {
            // Left behind by a removed membership, so this connection starts afresh; widened to the whole server; or
            // narrowed from the whole server to one channel.
            limit.Channels = picked;
        }
        else
        {
            if (limit.Channels.Count >= DiscordBoardChannels.MaxChannels && limit.Channels.All(c => c.Id != channel.Id))
            {
                return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Too many channels",
                    detail: $"A board can be limited to at most {DiscordBoardChannels.MaxChannels} channels of a server. Connect it to the whole server instead, or remove channels.");
            }
            // Another channel, or the same one under a new name.
            limit.Channels = [.. limit.Channels.Where(c => c.Id != channel.Id), channel];
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

    /// <summary>The servers connected to a board, named as the bot sees them now where it can.</summary>
    private static async Task<List<DiscordConnection>> ConnectionsAsync(PrikBoardDbContext db, IDiscordGateway gateway, Guid boardId, CancellationToken ct)
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
        var servers = gateway.Servers.ToDictionary(s => s.Id);

        return rows
            .Select(r =>
            {
                servers.TryGetValue(r.GuildId, out var server);
                // A server without its row previews nothing here; show it as limited to no channels.
                var channels = limits.TryGetValue(r.GuildId, out var limit) ? limit.Channels?.Select(c =>
                    server?.Channels.FirstOrDefault(now => now.Id == c.Id) ?? c) : [];
                return new DiscordConnection(r.GuildId, server?.Name ?? r.Agent!.ShownName, r.AgentId,
                    DiscordChannelDetail.From(channels),
                    r.AddedAt,
                    r.ConnectedById is { } by && names.TryGetValue(by, out var name) ? name : null);
            })
            .OrderBy(c => c.GuildName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
