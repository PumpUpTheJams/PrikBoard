using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
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
/// workspace. The workspace becomes a Viewer agent on the board (<see cref="ChatConnections"/>). An admin sets the app up
/// (<see cref="SlackAppEndpoints"/>).
/// </summary>
public static class SlackEndpoints
{
    /// <summary>Slack previews don't follow their cards yet (docs/slack.md, phase 3), so there is nothing to refresh.</summary>
    private static readonly Action<Guid> NoRefresh = _ => { };

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

        board.MapDelete("/{teamId}", RemoveSlackConnection)
            .RequireBoardRole(BoardRole.Owner)
            .WithName("removeSlackConnection")
            .WithSummary("Disconnect a Slack workspace from a board")
            .WithDescription("Removes the workspace from the board's members, so its links no longer unfurl there. Previews already " +
                             "posted stay in Slack.");

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

        return api;
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

    private static Task<Results<NoContent, NotFound>> RemoveSlackConnection(
        Guid boardId, string teamId, PrikBoardDbContext db, BoardEvents events, CancellationToken ct) =>
        ChatConnections.RemoveAsync(db, events, ChatPlatform.Slack, boardId, teamId, NoRefresh, ct);

    private static Task<Results<NoContent, NotFound>> RemoveSlackWholeWorkspace(
        Guid boardId, string teamId, PrikBoardDbContext db, BoardEvents events, CancellationToken ct) =>
        ChatConnections.RemoveWholeWorkspaceAsync(db, events, ChatPlatform.Slack, boardId, teamId, NoRefresh, ct);

    private static Task<Results<NoContent, NotFound>> RemoveSlackChannel(
        Guid boardId, string teamId, string channelId, PrikBoardDbContext db, BoardEvents events, CancellationToken ct) =>
        ChatConnections.RemoveChannelAsync(db, events, ChatPlatform.Slack, boardId, teamId, channelId, NoRefresh, ct);

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
