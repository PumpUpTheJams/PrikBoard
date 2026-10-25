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
/// A board's Slack connections (docs/slack.md), set by its owners under Integrations next to Discord: they pick the
/// workspace the instance's Slack app is in (<see cref="ISlackGateway"/>), and channels of it and/or the whole
/// workspace. The workspace becomes a Viewer agent on the board (<see cref="ChatConnections"/>). Also the link between a
/// person's Slack and PrikBoard accounts, for Save to PrikBoard (<see cref="ChatAccounts"/>). An admin sets the app up
/// (<see cref="SlackAppEndpoints"/>).
/// </summary>
public static class SlackEndpoints
{
    public static RouteGroupBuilder MapSlackEndpoints(this RouteGroupBuilder api)
    {
        var board = api.MapGroup("/boards/{boardId:guid}/slack").WithTags("Slack");

        board.MapGet("/", ListSlackConnections)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("listSlackConnections")
            .WithSummary("List the Slack workspaces connected to a board")
            .WithDescription("Lists the Slack workspaces where PrikBoard's Slack app previews this board's links: the whole workspace " +
                             "and/or channels.");

        board.MapGet("/workspaces", ListSlackWorkspaces)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("listSlackWorkspaces")
            .WithSummary("List the Slack workspaces a board can be connected to")
            .WithDescription("The workspace this PrikBoard's Slack app is installed in, with the channels the app was added to (not " +
                             "those shared with other organizations), and whether the app is online. State Off means no admin has set " +
                             "up the app yet.");

        board.MapPost("/", ConnectSlackChannel)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("connectSlackChannel")
            .WithSummary("Connect a board to a Slack channel")
            .WithDescription("Connects the board to a channel of the app's workspace, or to every channel of it when channelId is left " +
                             "out; channels and the whole workspace can be combined. Its links unfurl there, and anyone in those channels " +
                             "can see the titles, descriptions, tags and people of the cards shown. The workspace becomes a Viewer on " +
                             "the board. Returns the board's Slack connections. Browser sessions only. 409 when the board already has the " +
                             "maximum number of channels (50) in that workspace.")
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status409Conflict);

        board.MapPut("/{teamId}/channels/{channelId}", UpdateSlackChannel)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("updateSlackChannel")
            .WithSummary("Set what a Slack channel does for a board")
            .WithDescription("Sets the board activity the Slack app posts in the channel (new cards, moves between buckets, " +
                             "assignments, agents at work; none by default) and the bucket Save to PrikBoard there puts cards in. " +
                             "Returns the board's Slack connections.")
            .ProducesValidationProblem();

        board.MapDelete("/{teamId}", RemoveSlackConnection)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("removeSlackConnection")
            .WithSummary("Disconnect a Slack workspace from a board")
            .WithDescription("Removes the workspace from the board's members, so its links no longer unfurl there. Previews posted " +
                             "in the last week say the board is no longer available.");

        board.MapDelete("/{teamId}/whole-workspace", RemoveSlackWholeWorkspace)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("removeSlackWholeWorkspace")
            .WithSummary("Stop previewing a board in every channel of a Slack workspace")
            .WithDescription("Keeps the board in the channels picked for it; without any, the workspace is disconnected from the board. " +
                             "404 when the board isn't connected to the whole workspace.");

        board.MapDelete("/{teamId}/channels/{channelId}", RemoveSlackChannel)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("removeSlackChannel")
            .WithSummary("Disconnect a board from a Slack channel")
            .WithDescription("Removes one channel. Removing the last one disconnects the workspace from the board, unless the board is " +
                             "connected to the whole workspace. 404 when the channel is not in the list.");

        var me = api.MapGroup("/me/slack-account").WithTags("Slack").NoBoardScope();

        me.MapGet("/", GetSlackAccount)
            .WithName("getSlackAccount")
            .WithSummary("Whether the Slack app knows who you are")
            .WithDescription("Whether your Slack account is linked to you, for Save to PrikBoard: a link you made from the app. " +
                             "Sessions only.")
            .Produces(StatusCodes.Status403Forbidden);

        me.MapGet("/links/{code}", PreviewSlackAccountLink)
            .WithName("previewSlackAccountLink")
            .WithSummary("Show whose Slack account a link code is for")
            .WithDescription("The Slack name behind a code from the app's \"Connect your PrikBoard account\" link, so you can check " +
                             "it is you before linking. 404 for an unknown, used or expired code. Sessions only.")
            .Produces(StatusCodes.Status403Forbidden);

        me.MapPost("/", LinkSlackAccount)
            .WithName("linkSlackAccount")
            .WithSummary("Link your Slack account")
            .WithDescription("Links the Slack account behind a code from the app to you, replacing an earlier link. What that Slack " +
                             "account then does through the app (Save to PrikBoard) is done as you, so only link your own. Each code " +
                             "works once, for 10 minutes. Browser sessions from PrikBoard's own pages only.")
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status403Forbidden);

        me.MapDelete("/", UnlinkSlackAccount)
            .WithName("unlinkSlackAccount")
            .WithSummary("Unlink your Slack account")
            .WithDescription("Removes the link you made from the app. Sessions only.")
            .Produces(StatusCodes.Status403Forbidden);

        return api;
    }

    private static async Task<Results<Ok<SlackAccountStatus>, ForbidHttpResult>> GetSlackAccount(
        ClaimsPrincipal principal, ChatAccounts accounts, PrikBoardDbContext db, CancellationToken ct)
    {
        if (principal.IsToken()) return TypedResults.Forbid();
        var (kind, linkedAt) = await accounts.StatusAsync(db, ChatPlatform.Slack, principal.UserId(), ct);
        return TypedResults.Ok(new SlackAccountStatus(kind is not null, linkedAt));
    }

    private static Results<Ok<SlackAccountLinkPreview>, NotFound, ForbidHttpResult> PreviewSlackAccountLink(
        string code, ClaimsPrincipal principal, ChatAccounts accounts)
    {
        if (principal.IsToken()) return TypedResults.Forbid();
        return accounts.Peek(ChatPlatform.Slack, code) is { } pending
            ? TypedResults.Ok(new SlackAccountLinkPreview(pending.ChatName, pending.ExpiresAt))
            : TypedResults.NotFound();
    }

    private static async Task<Results<Ok<SlackAccountStatus>, ValidationProblem, ForbidHttpResult>> LinkSlackAccount(
        LinkSlackAccountRequest request, ClaimsPrincipal principal, HttpRequest http, ChatAccounts accounts, PrikBoardDbContext db,
        CancellationToken ct)
    {
        // Like approving an app: a token cannot link accounts, and another site cannot make the person's browser do it.
        if (principal.IsToken() || !SameOrigin.Check(http)) return TypedResults.Forbid();
        if (!await accounts.LinkAsync(db, ChatPlatform.Slack, request.Code, principal.UserId(), ct))
        {
            return Problems.Invalid("code", "Unknown, used or expired code. Use Save to PrikBoard in Slack again for a new one.");
        }
        return TypedResults.Ok(new SlackAccountStatus(true, DateTimeOffset.UtcNow));
    }

    private static async Task<Results<NoContent, ForbidHttpResult>> UnlinkSlackAccount(
        ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        if (principal.IsToken()) return TypedResults.Forbid();
        var userId = principal.UserId();
        await db.ChatAccounts.Where(a => a.Platform == ChatPlatform.Slack && a.UserId == userId).ExecuteDeleteAsync(ct);
        return TypedResults.NoContent();
    }

    private static async Task<Ok<List<SlackConnection>>> ListSlackConnections(
        Guid boardId, PrikBoardDbContext db, ISlackGateway gateway, CancellationToken ct) =>
        TypedResults.Ok(await ConnectionsAsync(db, gateway, boardId, ct));

    private static async Task<Ok<SlackWorkspaces>> ListSlackWorkspaces(Guid boardId, ISlackGateway gateway, CancellationToken ct) =>
        TypedResults.Ok(new SlackWorkspaces(gateway.State, (await gateway.WorkspacesAsync(ct))
            .Select(w => new SlackWorkspaceChoice(w.Id, w.Name, w.Channels.Select(c => new SlackChannelChoice(c.Id, c.Name)).ToList()))
            .ToList()));

    private static async Task<Results<Ok<List<SlackConnection>>, ForbidHttpResult, ValidationProblem, ProblemHttpResult>> ConnectSlackChannel(
        Guid boardId, ConnectSlackRequest request, ClaimsPrincipal principal, ISlackGateway gateway, PrikBoardDbContext db,
        BoardEvents events, CancellationToken ct)
    {
        // The connection names who made it, so it takes a person.
        if (principal.IsToken()) return TypedResults.Forbid();

        // Only what the app sees itself: the workspace and channel names come from Slack, not from the request.
        if ((await gateway.WorkspacesAsync(ct)).FirstOrDefault(w => w.Id == request.TeamId) is not { } workspace)
        {
            return Problems.Invalid("teamId", "The workspace PrikBoard's Slack app is in (listSlackWorkspaces).");
        }
        ChatChannel? channel = null;
        if (request.ChannelId is { } channelId && (channel = workspace.Channels.FirstOrDefault(c => c.Id == channelId)) is null)
        {
            return Problems.Invalid("channelId", "A channel of that workspace the app was added to (listSlackWorkspaces).");
        }

        if (!await ChatConnections.ConnectAsync(db, events, ChatPlatform.Slack, boardId, workspace.Id, workspace.Name, channel, principal.UserId(), ct))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Too many channels",
                detail: $"A board can be connected to at most {ChatBoardChannels.MaxChannels} channels of a workspace. Connect it to the whole workspace instead, or remove channels.");
        }
        return TypedResults.Ok(await ConnectionsAsync(db, gateway, boardId, ct));
    }

    private static async Task<Results<Ok<List<SlackConnection>>, NotFound, ValidationProblem>> UpdateSlackChannel(
        Guid boardId, string teamId, string channelId, UpdateSlackChannelRequest request, PrikBoardDbContext db, ISlackGateway gateway,
        CancellationToken ct)
    {
        if (request.SaveBucketId is { } bucketId && !await db.Buckets.AnyAsync(b => b.Id == bucketId && b.BoardId == boardId, ct))
        {
            return Problems.Invalid("saveBucketId", "A bucket of this board.");
        }
        var found = await ChatConnections.UpdateChannelAsync(db, ChatPlatform.Slack, boardId, teamId, channelId,
            channel => channel with { Updates = ChatUpdateLists.Flags(request.Updates), SaveBucketId = request.SaveBucketId }, ct);
        return found ? TypedResults.Ok(await ConnectionsAsync(db, gateway, boardId, ct)) : TypedResults.NotFound();
    }

    private static Task<Results<NoContent, NotFound>> RemoveSlackConnection(
        Guid boardId, string teamId, PrikBoardDbContext db, BoardEvents events, ChatActivity activity, CancellationToken ct) =>
        ChatConnections.RemoveAsync(db, events, ChatPlatform.Slack, boardId, teamId, activity.RefreshBoard, ct);

    private static Task<Results<NoContent, NotFound>> RemoveSlackWholeWorkspace(
        Guid boardId, string teamId, PrikBoardDbContext db, BoardEvents events, ChatActivity activity, CancellationToken ct) =>
        ChatConnections.RemoveWholeWorkspaceAsync(db, events, ChatPlatform.Slack, boardId, teamId, activity.RefreshBoard, ct);

    private static Task<Results<NoContent, NotFound>> RemoveSlackChannel(
        Guid boardId, string teamId, string channelId, PrikBoardDbContext db, BoardEvents events, ChatActivity activity, CancellationToken ct) =>
        ChatConnections.RemoveChannelAsync(db, events, ChatPlatform.Slack, boardId, teamId, channelId, activity.RefreshBoard, ct);

    /// <summary>The workspaces connected to a board, named as the app sees them now where it can.</summary>
    private static async Task<List<SlackConnection>> ConnectionsAsync(PrikBoardDbContext db, ISlackGateway gateway, Guid boardId, CancellationToken ct)
    {
        var workspaces = (await gateway.WorkspacesAsync(ct)).ToDictionary(w => w.Id);
        return (await ChatConnections.RowsAsync(db, ChatPlatform.Slack, boardId, ct))
            .Select(r =>
            {
                workspaces.TryGetValue(r.WorkspaceId, out var workspace);
                var channels = (r.Limit?.Channels ?? []).Select(c => workspace?.Channels.FirstOrDefault(now => now.Id == c.Id) is { } now ? c with { Name = now.Name } : c);
                return new SlackConnection(r.WorkspaceId, workspace?.Name ?? r.Name, r.AgentId, r.Limit?.WholeWorkspace ?? false,
                    SlackChannelDetail.From(channels), r.AddedAt, r.ConnectedBy);
            })
            .OrderBy(c => c.TeamName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
