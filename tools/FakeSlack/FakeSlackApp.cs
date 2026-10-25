using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;

namespace FakeSlack;

/// <summary>
/// A fake Slack for PrikBoard's Slack app: the Web API methods the app calls under <c>/api/</c>, Socket Mode under
/// <c>/ws</c>, a page at <c>/</c> to post messages as people of the workspace, and <c>/_fake/…</c> for tests. It answers
/// the way Slack's documentation says Slack does; it can't tell how real Slack behaves where the docs are silent.
/// </summary>
public static class FakeSlackApp
{
    public const string DefaultUrl = "http://localhost:5090";

    /// <summary>The app, not started yet; <paramref name="configure"/> changes the workspace (tests use their own tokens or channels).</summary>
    public static WebApplication Build(string[] args, Action<FakeSlackOptions>? configure = null)
    {
        var builder = WebApplication.CreateSlimBuilder(args);
        if (string.IsNullOrEmpty(builder.Configuration["urls"]) && string.IsNullOrEmpty(builder.Configuration["ASPNETCORE_URLS"]))
        {
            builder.WebHost.UseUrls(DefaultUrl);
        }
        var options = new FakeSlackOptions();
        builder.Configuration.GetSection("FakeSlack").Bind(options);
        configure?.Invoke(options);
        builder.Services.AddSingleton(new FakeWorkspace(options));

        var app = builder.Build();
        app.UseWebSockets();

        app.MapPost("/api/{method}", WebApiAsync);
        app.Map("/ws", SocketAsync);

        var fake = app.MapGroup("/_fake");
        fake.MapGet("/state", (FakeWorkspace w) => Results.Json(new
        {
            team = new { id = w.Options.TeamId, name = w.Options.TeamName },
            botToken = w.Options.BotToken,
            botUserId = w.Options.BotUserId,
            appToken = w.Options.AppToken,
            unfurlDomains = w.Options.UnfurlDomains,
            channels = w.Options.Channels,
            users = w.Options.Users,
            connections = w.Connections,
        }));
        fake.MapGet("/messages", (FakeWorkspace w, string? channel) => Results.Json(w.Messages
            .Where(m => channel is null || m.Channel == channel)
            .Select(m => new { m.Channel, m.Ts, m.User, m.Text, m.Links, m.Unfurls, m.Attachments })));
        fake.MapDelete("/messages", (FakeWorkspace w, string channel, string ts) => w.Delete(channel, ts) ? Results.NoContent() : Results.NotFound());
        fake.MapPost("/messages", async (FakeWorkspace w, PostRequest request) =>
        {
            if (w.Options.Channels.All(c => c.Id != request.Channel)) return Results.BadRequest(new { error = "channel_not_found" });
            var message = await w.PostAsync(request.Channel, request.User ?? w.Options.Users[0].Id, request.Text);
            return Results.Json(new { message.Channel, message.Ts, message.Links });
        });
        fake.MapPost("/composer", async (FakeWorkspace w, PostRequest request) =>
            Results.Json(new { links = await w.ComposeAsync(request.User ?? w.Options.Users[0].Id, request.Text) }));
        fake.MapGet("/calls", (FakeWorkspace w) => Results.Json(w.Calls));
        fake.MapPost("/commands", async (FakeWorkspace w, PostRequest request) =>
            await w.CommandAsync(request.Channel, request.User ?? w.Options.Users[0].Id, request.Text)
                ? Results.Accepted()
                : Results.Conflict(new { error = "PrikBoard isn't connected" }));
        fake.MapGet("/responses", (FakeWorkspace w, string? channel) =>
            Results.Json(w.Responses.Where(r => channel is null || r.Channel == channel)));
        // A command's response_url: Slack takes the answer without a token, since the URL is the key.
        fake.MapPost("/responses/{id}", async (FakeWorkspace w, string id, HttpRequest http) =>
            w.Respond(id, await JsonNode.ParseAsync(http.Body)) ? Results.Ok() : Results.NotFound());
        fake.MapPost("/disconnect", async (FakeWorkspace w) => Results.Json(new { disconnected = await w.DisconnectAllAsync() }));

        app.MapGet("/", () => Results.Content(Page.Html, "text/html; charset=utf-8"));
        return app;
    }

    /// <summary>A message to post: the channel, who posts it (the first user when left out) and its text.</summary>
    public sealed record PostRequest(string Channel, string? User, string Text);

    /// <summary>
    /// One Web API call. Like Slack: a bearer token, form fields, and 200 with <c>"ok": false</c> and an error code when
    /// refused. Bot methods take the bot token; <c>apps.connections.open</c> takes the app-level token.
    /// </summary>
    private static async Task<IResult> WebApiAsync(string method, HttpRequest http, FakeWorkspace w)
    {
        var o = w.Options;
        var token = http.Headers.Authorization.ToString() is var auth && auth.StartsWith("Bearer ", StringComparison.Ordinal) ? auth[7..] : null;
        var form = http.HasFormContentType ? await http.ReadFormAsync() : null;
        var fields = form?.ToDictionary(f => f.Key, f => f.Value.ToString()) ?? [];

        IResult Answer(object body, string? error = null)
        {
            w.Record(new FakeCall(method, DateTimeOffset.UtcNow, fields, error));
            return Results.Json(body);
        }
        IResult Fail(string error) => Answer(new { ok = false, error }, error);

        var appMethod = method == "apps.connections.open";
        if (token is null) return Fail("not_authed");
        if (token != o.BotToken && token != o.AppToken) return Fail("invalid_auth");
        if (appMethod != (token == o.AppToken)) return Fail("not_allowed_token_type");

        switch (method)
        {
            case "auth.test":
                return Answer(new
                {
                    ok = true, url = $"https://{o.TeamName.Replace(' ', '-').ToLowerInvariant()}.slack.com/", team = o.TeamName, user = o.BotName,
                    team_id = o.TeamId, user_id = o.BotUserId, bot_id = "B0FAKEBOT1", is_enterprise_install = false,
                });
            case "apps.connections.open":
                var socket = $"{(http.IsHttps ? "wss" : "ws")}://{http.Host}/ws?ticket={w.NewTicket()}";
                return Answer(new { ok = true, url = socket });
            case "team.info":
                return Answer(new { ok = true, team = new { id = o.TeamId, name = o.TeamName } });
            case "conversations.list":
                return Answer(new { ok = true, channels = o.Channels.Select(Channel), response_metadata = new { next_cursor = "" } });
            case "conversations.info":
                return o.Channels.FirstOrDefault(c => c.Id == fields.GetValueOrDefault("channel")) is { } channel
                    ? Answer(new { ok = true, channel = Channel(channel) })
                    : Fail("channel_not_found");
            case "users.info":
                return o.Users.FirstOrDefault(u => u.Id == fields.GetValueOrDefault("user")) is { } user
                    ? Answer(new { ok = true, user = new { id = user.Id, name = user.Name, is_admin = user.IsAdmin, is_owner = user.IsAdmin, is_bot = false } })
                    : Fail("user_not_found");
            case "chat.unfurl":
                if (JsonNode.Parse(fields.GetValueOrDefault("unfurls") ?? "{}") is not JsonObject unfurls) return Fail("invalid_unfurls_format");
                return w.Unfurl(fields.GetValueOrDefault("channel") ?? "", fields.GetValueOrDefault("ts") ?? "", unfurls) is { } error
                    ? Fail(error)
                    : Answer(new { ok = true });
            case "chat.postMessage":
                if (o.Channels.FirstOrDefault(c => c.Id == fields.GetValueOrDefault("channel")) is not { } target) return Fail("channel_not_found");
                if (!target.IsMember) return Fail("not_in_channel");
                var posted = w.PostAsBot(target.Id, fields.GetValueOrDefault("text") ?? "",
                    fields.GetValueOrDefault("attachments") is { } attachments ? JsonNode.Parse(attachments) : null);
                return Answer(new { ok = true, channel = target.Id, ts = posted.Ts });
            default:
                return Fail("unknown_method");
        }
    }

    private static object Channel(FakeChannel c) => new
    {
        id = c.Id, name = c.Name, is_channel = true, is_private = false, is_archived = false, is_member = c.IsMember,
        is_ext_shared = c.IsExtShared, is_shared = c.IsExtShared,
    };

    private static async Task<Results<EmptyHttpResult, BadRequest<string>, UnauthorizedHttpResult>> SocketAsync(HttpContext http, FakeWorkspace w)
    {
        if (!http.WebSockets.IsWebSocketRequest) return TypedResults.BadRequest("A WebSocket, from apps.connections.open.");
        if (!w.UseTicket(http.Request.Query["ticket"].ToString())) return TypedResults.Unauthorized();
        using var socket = await http.WebSockets.AcceptWebSocketAsync();
        await w.RunConnectionAsync(socket, $"{http.Request.Scheme}://{http.Request.Host}", http.RequestAborted);
        return TypedResults.Empty;
    }
}
