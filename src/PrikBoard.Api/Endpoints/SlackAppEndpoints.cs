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
/// The instance's Slack app, set up in Admin → Instance next to the Discord bot (docs/slack.md): its two tokens and the
/// address links are matched against, which take effect at once (<see cref="SlackApp.LoadAsync"/>), the manifest to
/// create the app from, and the workspace it is in. Admins only, sessions only, so no MCP tools.
/// </summary>
public static class SlackAppEndpoints
{
    public static RouteGroupBuilder MapSlackAppEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin/slack").WithTags("Admin").NoBoardScope();

        admin.MapGet("/", GetSlackApp)
            .RequireAdmin()
            .WithName("getSlackApp")
            .WithSummary("Get the Slack app")
            .WithDescription("Admins only. Whether the instance's Slack app is online, the manifest to create it from, the address " +
                             "pasted links are matched against, and the workspace it is in with the boards previewed there. Never the tokens.");

        admin.MapPut("/", SetSlackApp)
            .RequireAdmin()
            .WithName("setSlackApp")
            .WithSummary("Set up the Slack app")
            .WithDescription("Admins only. Saves the app's bot token (xoxb-…) and app-level token (xapp-…, for Socket Mode), each " +
                             "checked with Slack first and stored encrypted, and the public address, and (re)connects the app at once. " +
                             "The public address defaults to the one this request came to. Values in the server's configuration " +
                             "(Slack:BotToken, Slack:AppToken, Slack:PublicUrl) win and can't be changed here (400).")
            .ProducesValidationProblem();

        admin.MapDelete("/", DeleteSlackApp)
            .RequireAdmin()
            .WithName("deleteSlackApp")
            .WithSummary("Remove the Slack app")
            .WithDescription("Admins only. Forgets the saved tokens and address and disconnects the app. Boards stay connected to " +
                             "their workspace, so saving the same app's tokens later brings their previews back.");

        admin.MapDelete("/workspaces/{teamId}", ForgetSlackWorkspace)
            .RequireAdmin()
            .WithName("forgetSlackWorkspace")
            .WithSummary("Disconnect every board from a Slack workspace")
            .WithDescription("Admins only. Every board connected to the workspace is disconnected. The app stays installed in Slack; " +
                             "uninstall it there. Previews already posted stay in Slack.");

        return api;
    }

    private static async Task<Ok<SlackAppDetail>> GetSlackApp(SlackApp app, ISlackGateway gateway, PrikBoardDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await DetailAsync(app, gateway, db, ct));

    private static async Task<Results<Ok<SlackAppDetail>, ValidationProblem>> SetSlackApp(
        SetSlackAppRequest request, ClaimsPrincipal principal, HttpRequest http, SlackApp app, SlackApi slack, ISlackGateway gateway,
        PrikBoardDbContext db, CancellationToken ct)
    {
        var row = await db.SlackAppSettings.FirstOrDefaultAsync(s => s.Id == SlackAppSettings.SingleId, ct);
        var botToken = request.BotToken?.Trim();
        var appToken = request.AppToken?.Trim();
        if ((botToken is not null || appToken is not null) && app.TokensAreConfigured)
        {
            return Problems.Invalid(botToken is not null ? "botToken" : "appToken",
                $"This server's configuration sets the tokens ({SlackApp.BotTokenSetting}, {SlackApp.AppTokenSetting}); change them there.");
        }
        if (!app.TokensAreConfigured)
        {
            if (string.IsNullOrEmpty(botToken) && row?.ProtectedBotToken is null)
            {
                return Problems.Invalid("botToken", "Paste the Bot User OAuth Token (xoxb-…) from your Slack app's OAuth & Permissions page.");
            }
            if (string.IsNullOrEmpty(appToken) && row?.ProtectedAppToken is null)
            {
                return Problems.Invalid("appToken", "Paste an app-level token (xapp-…) with connections:write, from Basic Information → App-Level Tokens.");
            }
        }

        string? publicUrl = null;
        if (request.PublicUrl is { } url)
        {
            if (app.PublicUrlIsConfigured)
            {
                return Problems.Invalid("publicUrl", $"This server's configuration sets the address ({SlackApp.PublicUrlSetting}); change it there.");
            }
            if (DiscordBotEndpoints.PublicUrl(url) is not { } valid)
            {
                return Problems.Invalid("publicUrl", "The address people open PrikBoard at, such as https://board.example.com: http(s), without ? or #.");
            }
            publicUrl = valid;
        }

        SlackIdentity? identity = null;
        if (!string.IsNullOrEmpty(botToken))
        {
            if (!botToken.StartsWith("xoxb-", StringComparison.Ordinal))
            {
                return Problems.Invalid("botToken", "That isn't a bot token: copy the Bot User OAuth Token, which starts with xoxb-.");
            }
            try
            {
                identity = await slack.AuthTestAsync(botToken, ct);
            }
            catch (SlackApiException error)
            {
                return Problems.Invalid("botToken", error.IsAuth || error.Error == "not_a_bot_token"
                    ? $"Slack doesn't accept this bot token ({error.Error}). Copy it again from OAuth & Permissions, after installing the app."
                    : $"Slack could not check the token ({error.Error}); try again in a minute.");
            }
        }
        if (!string.IsNullOrEmpty(appToken))
        {
            if (!appToken.StartsWith("xapp-", StringComparison.Ordinal))
            {
                return Problems.Invalid("appToken", "That isn't an app-level token: those start with xapp-.");
            }
            try
            {
                // Opening a connection is the only check Slack offers for these; the address is simply not used.
                await slack.OpenConnectionAsync(appToken, ct);
            }
            catch (SlackApiException error)
            {
                return Problems.Invalid("appToken", error.IsAuth
                    ? $"Slack doesn't accept this app-level token ({error.Error}). It needs connections:write, and Socket Mode must be on."
                    : $"Slack could not check the token ({error.Error}); try again in a minute.");
            }
        }

        if (row is null)
        {
            row = new SlackAppSettings();
            db.SlackAppSettings.Add(row);
        }
        if (identity is not null)
        {
            row.ProtectedBotToken = app.ProtectToken(botToken!);
            row.TeamId = identity.TeamId;
            row.TeamName = AppSignInEndpoints.CleanName(identity.TeamName);
            row.BotName = AppSignInEndpoints.CleanName(identity.BotName);
        }
        if (!string.IsNullOrEmpty(appToken)) row.ProtectedAppToken = app.ProtectToken(appToken);
        // The admin is looking at PrikBoard, so the address they use is the one people paste.
        row.PublicUrl = publicUrl ?? row.PublicUrl ?? DiscordBotEndpoints.PublicUrl($"{http.Scheme}://{http.Host}{http.PathBase}");
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedById = principal.UserId();
        await db.SaveChangesAsync(ct);

        await app.LoadAsync(db, ct);
        return TypedResults.Ok(await DetailAsync(app, gateway, db, ct));
    }

    private static async Task<NoContent> DeleteSlackApp(SlackApp app, PrikBoardDbContext db, CancellationToken ct)
    {
        await db.SlackAppSettings.ExecuteDeleteAsync(ct);
        await app.LoadAsync(db, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> ForgetSlackWorkspace(
        string teamId, PrikBoardDbContext db, BoardEvents events, CancellationToken ct)
    {
        if (await LinkPreviews.AgentIdAsync(db, ChatPlatform.Slack, teamId, ct) is null) return TypedResults.NotFound();
        await ChatConnections.ForgetWorkspaceAsync(db, events, ChatPlatform.Slack, teamId, _ => { }, ct);
        return TypedResults.NoContent();
    }

    private static async Task<SlackAppDetail> DetailAsync(SlackApp app, ISlackGateway gateway, PrikBoardDbContext db, CancellationToken ct)
    {
        var settings = app.Current;
        var workspaces = await gateway.WorkspacesAsync(ct);
        var boards = await ChatConnections.BoardsByWorkspaceAsync(db, ChatPlatform.Slack, workspaces.Select(w => w.Id).ToList(), ct);
        var source = app.TokensAreConfigured ? SlackAppSource.Configuration : settings.HasStoredTokens ? SlackAppSource.Setup : (SlackAppSource?)null;
        return new SlackAppDetail(
            gateway.State,
            gateway.Problem,
            source,
            gateway.Identity?.TeamId ?? settings.TeamId,
            gateway.Identity?.TeamName ?? settings.TeamName,
            gateway.Identity?.BotName ?? settings.BotName,
            settings.PublicUrl,
            app.PublicUrlIsConfigured,
            settings.UpdatedAt,
            SlackApp.Manifest(settings.PublicUrl),
            SlackApp.CreateAppUrl(settings.PublicUrl),
            workspaces.Select(w => new SlackAppWorkspace(w.Id, w.Name, w.Channels.Count, boards[w.Id]
                    .Select(b => new SlackBoardConnection(b.Id, b.ShortId, b.Name, b.Limit?.WholeWorkspace ?? false,
                        SlackChannelDetail.From(b.Limit?.Channels ?? [])))
                    .ToList()))
                .ToList());
    }
}
