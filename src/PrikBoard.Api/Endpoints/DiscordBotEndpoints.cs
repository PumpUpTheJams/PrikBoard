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
/// The instance's Discord bot, set up in Admin → Instance (docs/discord.md): its token and the address links are
/// matched against, which take effect at once (<see cref="DiscordBot.LoadAsync"/>), and the servers it is in. Admins
/// only, sessions only, so no MCP tools, like the other admin endpoints.
/// </summary>
public static class DiscordBotEndpoints
{
    public static RouteGroupBuilder MapDiscordBotEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin/discord").WithTags("Admin").NoBoardScope();

        admin.MapGet("/", GetDiscordBot)
            .RequireAdmin()
            .WithName("getDiscordBot")
            .WithSummary("Get the Discord bot")
            .WithDescription("Admins only. Whether the instance's Discord bot is online, its invite link, the address pasted links " +
                             "are matched against, and the servers it is in with the boards previewed there. Never the token.");

        admin.MapPut("/", SetDiscordBot)
            .RequireAdmin()
            .WithName("setDiscordBot")
            .WithSummary("Set up the Discord bot")
            .WithDescription("Admins only. Saves the bot's token (checked with Discord first: it must be valid and have the Message " +
                             "Content intent; stored encrypted) and the public address, and (re)connects the bot at once. The public " +
                             "address defaults to the one this request came to. Values in the server's configuration " +
                             "(Discord:BotToken, Discord:PublicUrl) win and can't be changed here (400).")
            .ProducesValidationProblem();

        admin.MapDelete("/", DeleteDiscordBot)
            .RequireAdmin()
            .WithName("deleteDiscordBot")
            .WithSummary("Remove the Discord bot")
            .WithDescription("Admins only. Forgets the saved token and address and logs the bot out. Boards stay connected to their " +
                             "servers, so saving a token of the same bot later brings their previews back.");

        admin.MapDelete("/servers/{guildId}", LeaveDiscordServer)
            .RequireAdmin()
            .WithName("leaveDiscordServer")
            .WithSummary("Make the Discord bot leave a server")
            .WithDescription("Admins only. The bot leaves the server and every board connected to it is disconnected. Previews " +
                             "already posted stay in Discord.");

        return api;
    }

    private static async Task<Ok<DiscordBotDetail>> GetDiscordBot(DiscordBot bot, IDiscordGateway gateway, PrikBoardDbContext db, CancellationToken ct) =>
        TypedResults.Ok(await DetailAsync(bot, gateway, db, ct));

    private static async Task<Results<Ok<DiscordBotDetail>, ValidationProblem>> SetDiscordBot(
        SetDiscordBotRequest request, ClaimsPrincipal principal, HttpRequest http, DiscordBot bot, DiscordApplications applications,
        IDiscordGateway gateway, PrikBoardDbContext db, CancellationToken ct)
    {
        var row = await db.DiscordBotSettings.FirstOrDefaultAsync(ct);
        var token = request.BotToken?.Trim();
        if (token is not null && bot.TokenIsConfigured)
        {
            return Problems.Invalid("botToken", $"This server's configuration sets the token ({DiscordBot.TokenSetting}); change it there.");
        }
        if (string.IsNullOrEmpty(token) && row?.ProtectedBotToken is null && !bot.TokenIsConfigured)
        {
            return Problems.Invalid("botToken", "Paste the token from the Bot tab of your Discord application.");
        }

        string? publicUrl = null;
        if (request.PublicUrl is { } url)
        {
            if (bot.PublicUrlIsConfigured)
            {
                return Problems.Invalid("publicUrl", $"This server's configuration sets the address ({DiscordBot.PublicUrlSetting}); change it there.");
            }
            if (PublicUrl(url) is not { } valid)
            {
                return Problems.Invalid("publicUrl", "The address people open PrikBoard at, such as https://board.example.com: http(s), without ? or #.");
            }
            publicUrl = valid;
        }

        if (row is null)
        {
            row = new DiscordBotSettings();
            db.DiscordBotSettings.Add(row);
        }
        if (!string.IsNullOrEmpty(token))
        {
            var (application, problem) = await applications.CheckAsync(token, ct);
            if (application is null) return Problems.Invalid("botToken", problem!);
            row.ProtectedBotToken = bot.ProtectToken(token);
            row.ApplicationId = application.Id;
            row.BotName = AppSignInEndpoints.CleanName(application.BotName);
        }
        // The admin is looking at PrikBoard, so the address they use is the one people paste.
        row.PublicUrl = publicUrl ?? row.PublicUrl ?? PublicUrl($"{http.Scheme}://{http.Host}{http.PathBase}");
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedById = principal.UserId();
        await db.SaveChangesAsync(ct);

        await bot.LoadAsync(db, ct);
        return TypedResults.Ok(await DetailAsync(bot, gateway, db, ct));
    }

    private static async Task<NoContent> DeleteDiscordBot(DiscordBot bot, PrikBoardDbContext db, CancellationToken ct)
    {
        await db.DiscordBotSettings.ExecuteDeleteAsync(ct);
        await bot.LoadAsync(db, ct);
        return TypedResults.NoContent();
    }

    private static async Task<Results<NoContent, NotFound>> LeaveDiscordServer(
        string guildId, IDiscordGateway gateway, PrikBoardDbContext db, BoardEvents events, CancellationToken ct)
    {
        var left = await gateway.LeaveAsync(guildId);
        var agentId = await db.DiscordGuilds.Where(g => g.GuildId == guildId).Select(g => (Guid?)g.AgentId).FirstOrDefaultAsync(ct);
        if (agentId is null) return left ? TypedResults.NoContent() : TypedResults.NotFound();

        // The server's agent goes, and with it its memberships, its DiscordGuild row and its channel limits.
        var boards = await db.BoardMembers.Where(m => m.PrincipalId == agentId).Select(m => m.BoardId).ToListAsync(ct);
        await db.Agents.Where(a => a.Id == agentId).ExecuteDeleteAsync(ct);
        foreach (var boardId in boards)
        {
            await events.AccessChangedAsync(boardId, agentId.Value, null);
            await events.ChangedAsync(boardId, Guid.Empty);
        }
        return TypedResults.NoContent();
    }

    private static async Task<DiscordBotDetail> DetailAsync(DiscordBot bot, IDiscordGateway gateway, PrikBoardDbContext db, CancellationToken ct)
    {
        var settings = bot.Current;
        var servers = gateway.Servers;
        var ids = servers.Select(s => s.Id).ToList();
        var boards = await (
                from guild in db.DiscordGuilds
                where ids.Contains(guild.GuildId)
                join member in db.BoardMembers on guild.AgentId equals member.PrincipalId
                select new { guild.GuildId, member.Board!.Id, member.Board.ShortId, member.Board.Name })
            .AsNoTracking().ToListAsync(ct);
        var limits = await db.DiscordBoardChannels.AsNoTracking().Where(c => ids.Contains(c.GuildId))
            .ToDictionaryAsync(c => (c.GuildId, c.BoardId), ct);

        var source = bot.TokenIsConfigured ? DiscordBotSource.Configuration : settings.HasStoredToken ? DiscordBotSource.Setup : (DiscordBotSource?)null;
        return new DiscordBotDetail(
            gateway.State,
            gateway.Problem,
            source,
            gateway.Application?.BotName ?? settings.BotName,
            gateway.Application?.Id ?? settings.ApplicationId,
            settings.HasToken ? bot.InviteUrl : null,
            gateway.Application?.IsPublic,
            settings.PublicUrl,
            bot.PublicUrlIsConfigured,
            settings.UpdatedAt,
            servers.Select(s => new DiscordBotServer(s.Id, s.Name, s.Channels.Count, boards
                    .Where(b => b.GuildId == s.Id)
                    .OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(b => new DiscordBoardConnection(b.Id, b.ShortId, b.Name,
                        limits.TryGetValue((s.Id, b.Id), out var limit) ? DiscordChannelDetail.From(limit.Channels) : []))
                    .ToList()))
                .ToList());
    }

    /// <summary>A public address as stored: absolute http(s), no user, query or fragment, no trailing slash; null otherwise.</summary>
    internal static string? PublicUrl(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? uri.GetLeftPart(UriPartial.Path).TrimEnd('/')
            : null;
}
