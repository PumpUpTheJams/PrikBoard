using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;
using PrikBoard.Api.Live;

namespace PrikBoard.Api.Endpoints;

/// <summary>
/// What connecting a board to a chat workspace means, for every chat app (<see cref="DiscordEndpoints"/>,
/// <see cref="SlackEndpoints"/>): the workspace becomes a Viewer agent on the board, and <see cref="ChatBoardChannels"/>
/// narrows where its links preview. Each change locks the board row, like membership changes. The endpoints add what
/// differs: which workspaces and channels their bot sees, and their own contracts. <c>refresh</c> tells the platform's
/// activity that a board's posted previews need another look.
/// </summary>
internal static class ChatConnections
{
    /// <summary>A workspace connected to a board, as the owners' list shows it.</summary>
    /// <param name="WorkspaceId">The platform's id for the workspace.</param>
    /// <param name="AgentId">Its member on the board.</param>
    /// <param name="Name">Its name when it was last connected.</param>
    /// <param name="Limit">Where it previews the board; null when its row was lost, so nowhere.</param>
    /// <param name="AddedAt">When it joined the board.</param>
    /// <param name="ConnectedBy">Who made its latest connection, if they still exist.</param>
    internal sealed record Row(string WorkspaceId, Guid AgentId, string Name, ChatBoardChannels? Limit, DateTimeOffset AddedAt, string? ConnectedBy);

    /// <summary>
    /// Connects the board to a channel of the workspace, or to all of it when <paramref name="channel"/> is null. The
    /// workspace's agent is made (or renamed) and joins the board as a Viewer. False when the board already has
    /// <see cref="ChatBoardChannels.MaxChannels"/> channels there; nothing is changed then.
    /// </summary>
    internal static async Task<bool> ConnectAsync(
        PrikBoardDbContext db, BoardEvents events, ChatPlatform platform, Guid boardId, string workspaceId, string workspaceName,
        ChatChannel? channel, Guid userId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await MemberEndpoints.LockBoard(db, boardId, ct);

        var issuer = ChatPlatforms.Issuer(platform);
        var agent = await db.Agents.FirstOrDefaultAsync(a => a.Issuer == issuer && a.ClientId == workspaceId, ct);
        if (agent is null)
        {
            agent = new Agent { Issuer = issuer, ClientId = workspaceId };
            db.Agents.Add(agent);
        }
        agent.DisplayName = AppSignInEndpoints.CleanName(workspaceName);
        agent.LastSeenAt = now;

        var workspace = await db.ChatWorkspaces.FirstOrDefaultAsync(w => w.Platform == platform && w.WorkspaceId == workspaceId, ct);
        if (workspace is null)
        {
            workspace = new ChatWorkspace { Platform = platform, WorkspaceId = workspaceId, AgentId = agent.Id };
            db.ChatWorkspaces.Add(workspace);
        }
        workspace.ConnectedById = userId;
        workspace.ConnectedAt = now;

        var member = await db.BoardMembers.FirstOrDefaultAsync(m => m.BoardId == boardId && m.PrincipalId == agent.Id, ct);
        if (member is null) db.BoardMembers.Add(new BoardMember { BoardId = boardId, PrincipalId = agent.Id, Role = BoardRole.Viewer });

        var limit = await FindLimitAsync(db, platform, workspaceId, boardId, ct);
        if (limit is null)
        {
            limit = new ChatBoardChannels { Platform = platform, WorkspaceId = workspaceId, BoardId = boardId };
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
            if (limit.Channels.Count >= ChatBoardChannels.MaxChannels) return false;
            limit.Channels = [.. limit.Channels, channel];
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        if (member is null)
        {
            await events.AccessChangedAsync(boardId, agent.Id, BoardRole.Viewer);
            await events.BoardsChangedAsync([agent.Id]);
        }
        return true;
    }

    /// <summary>Disconnects the workspace from the board altogether.</summary>
    internal static async Task<Results<NoContent, NotFound>> RemoveAsync(
        PrikBoardDbContext db, BoardEvents events, ChatPlatform platform, Guid boardId, string workspaceId, Action<Guid> refresh, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await MemberEndpoints.LockBoard(db, boardId, ct);
        var agentId = await DisconnectAsync(db, platform, boardId, workspaceId, ct);
        if (agentId is null) return TypedResults.NotFound();
        await transaction.CommitAsync(ct);
        await DisconnectedAsync(events, refresh, boardId, agentId.Value);
        return TypedResults.NoContent();
    }

    /// <summary>Stops previewing the board in every channel; 404 when it wasn't.</summary>
    internal static async Task<Results<NoContent, NotFound>> RemoveWholeWorkspaceAsync(
        PrikBoardDbContext db, BoardEvents events, ChatPlatform platform, Guid boardId, string workspaceId, Action<Guid> refresh, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await MemberEndpoints.LockBoard(db, boardId, ct);
        var limit = await FindLimitAsync(db, platform, workspaceId, boardId, ct);
        if (limit is not { WholeWorkspace: true }) return TypedResults.NotFound();
        return await NarrowAsync(db, events, refresh, transaction, limit, l => l.WholeWorkspace = false, ct);
    }

    /// <summary>Takes one channel away; 404 when it isn't picked for the board.</summary>
    internal static async Task<Results<NoContent, NotFound>> RemoveChannelAsync(
        PrikBoardDbContext db, BoardEvents events, ChatPlatform platform, Guid boardId, string workspaceId, string channelId, Action<Guid> refresh,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await MemberEndpoints.LockBoard(db, boardId, ct);
        var limit = await FindLimitAsync(db, platform, workspaceId, boardId, ct);
        if (limit?.Channel(channelId) is null) return TypedResults.NotFound();
        return await NarrowAsync(db, events, refresh, transaction, limit, l => l.Channels = [.. l.Channels.Where(c => c.Id != channelId)], ct);
    }

    /// <summary>Takes a channel or the whole workspace away; with nothing left, the workspace is disconnected from the board.</summary>
    private static async Task<Results<NoContent, NotFound>> NarrowAsync(
        PrikBoardDbContext db, BoardEvents events, Action<Guid> refresh, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        ChatBoardChannels limit, Action<ChatBoardChannels> change, CancellationToken ct)
    {
        change(limit);
        if (limit.WholeWorkspace || limit.Channels.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            refresh(limit.BoardId);
            return TypedResults.NoContent();
        }

        var agentId = await DisconnectAsync(db, limit.Platform, limit.BoardId, limit.WorkspaceId, ct);
        await transaction.CommitAsync(ct);
        if (agentId is { } id) await DisconnectedAsync(events, refresh, limit.BoardId, id);
        return TypedResults.NoContent();
    }

    private static async Task DisconnectedAsync(BoardEvents events, Action<Guid> refresh, Guid boardId, Guid agentId)
    {
        refresh(boardId);
        await events.AccessChangedAsync(boardId, agentId, null);
        await events.BoardsChangedAsync([agentId]);
    }

    /// <summary>Removes the workspace's membership and channel limits from the board; its agent id, or null when it wasn't connected.</summary>
    internal static async Task<Guid?> DisconnectAsync(PrikBoardDbContext db, ChatPlatform platform, Guid boardId, string workspaceId, CancellationToken ct)
    {
        var agentId = await Integrations.LinkPreviews.AgentIdAsync(db, platform, workspaceId, ct);
        if (agentId is null) return null;
        var removed = await db.BoardMembers.Where(m => m.BoardId == boardId && m.PrincipalId == agentId).ExecuteDeleteAsync(ct);
        var limits = await db.ChatBoardChannels
            .Where(c => c.Platform == platform && c.WorkspaceId == workspaceId && c.BoardId == boardId).ExecuteDeleteAsync(ct);
        return removed + limits > 0 ? agentId : null;
    }

    /// <summary>
    /// Forgets a workspace everywhere: its agent goes, and with it its memberships, its <see cref="ChatWorkspace"/> row
    /// and its channel limits. The boards it was on; empty when PrikBoard didn't know it.
    /// </summary>
    internal static async Task<List<Guid>> ForgetWorkspaceAsync(
        PrikBoardDbContext db, BoardEvents events, ChatPlatform platform, string workspaceId, Action<Guid> refresh, CancellationToken ct)
    {
        var agentId = await Integrations.LinkPreviews.AgentIdAsync(db, platform, workspaceId, ct);
        if (agentId is null) return [];
        var boards = await db.BoardMembers.Where(m => m.PrincipalId == agentId).Select(m => m.BoardId).ToListAsync(ct);
        await db.Agents.Where(a => a.Id == agentId).ExecuteDeleteAsync(ct);
        foreach (var boardId in boards)
        {
            refresh(boardId);
            await events.AccessChangedAsync(boardId, agentId.Value, null);
            await events.ChangedAsync(boardId, Guid.Empty);
        }
        return boards;
    }

    /// <summary>Where a workspace previews a board; null when it doesn't.</summary>
    internal static Task<ChatBoardChannels?> FindLimitAsync(PrikBoardDbContext db, ChatPlatform platform, string workspaceId, Guid boardId, CancellationToken ct) =>
        db.ChatBoardChannels.FirstOrDefaultAsync(c => c.Platform == platform && c.WorkspaceId == workspaceId && c.BoardId == boardId, ct);

    /// <summary>The platform's workspaces connected to a board, with where each previews it.</summary>
    internal static async Task<List<Row>> RowsAsync(PrikBoardDbContext db, ChatPlatform platform, Guid boardId, CancellationToken ct)
    {
        var rows = await (
                from member in db.BoardMembers
                where member.BoardId == boardId
                join workspace in db.ChatWorkspaces on member.PrincipalId equals workspace.AgentId
                where workspace.Platform == platform
                select new { member.AddedAt, workspace.WorkspaceId, workspace.AgentId, workspace.Agent, workspace.ConnectedById })
            .AsNoTracking().ToListAsync(ct);
        var ids = rows.Select(r => r.WorkspaceId).ToList();
        var limits = await db.ChatBoardChannels.AsNoTracking()
            .Where(c => c.Platform == platform && c.BoardId == boardId && ids.Contains(c.WorkspaceId))
            .ToDictionaryAsync(c => c.WorkspaceId, ct);
        var byIds = rows.Where(r => r.ConnectedById != null).Select(r => r.ConnectedById!.Value).Distinct().ToList();
        var names = await db.Principals.AsNoTracking().Where(p => byIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.ShownName, ct);
        return rows
            .Select(r => new Row(r.WorkspaceId, r.AgentId, r.Agent!.ShownName, limits.GetValueOrDefault(r.WorkspaceId), r.AddedAt,
                r.ConnectedById is { } by && names.TryGetValue(by, out var name) ? name : null))
            .ToList();
    }

    /// <summary>The boards a platform's workspaces preview, for the admin page: per workspace id, each board with where it previews.</summary>
    internal static async Task<ILookup<string, (Guid Id, string ShortId, string Name, ChatBoardChannels? Limit)>> BoardsByWorkspaceAsync(
        PrikBoardDbContext db, ChatPlatform platform, IReadOnlyCollection<string> workspaceIds, CancellationToken ct)
    {
        var boards = await (
                from workspace in db.ChatWorkspaces
                where workspace.Platform == platform && workspaceIds.Contains(workspace.WorkspaceId)
                join member in db.BoardMembers on workspace.AgentId equals member.PrincipalId
                select new { workspace.WorkspaceId, member.Board!.Id, member.Board.ShortId, member.Board.Name })
            .AsNoTracking().ToListAsync(ct);
        var limits = await db.ChatBoardChannels.AsNoTracking().Where(c => c.Platform == platform && workspaceIds.Contains(c.WorkspaceId))
            .ToDictionaryAsync(c => (c.WorkspaceId, c.BoardId), ct);
        return boards
            .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
            .ToLookup(b => b.WorkspaceId, b => (b.Id, b.ShortId, b.Name, limits.GetValueOrDefault((b.WorkspaceId, b.Id))));
    }
}
