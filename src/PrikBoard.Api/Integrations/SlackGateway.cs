using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.RateLimiting;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Integrations;

/// <summary>Where the Slack app is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<SlackAppState>))]
public enum SlackAppState
{
    /// <summary>No tokens are set.</summary>
    Off,

    /// <summary>Opening the Socket Mode connection, or opening a new one after Slack dropped or refreshed it.</summary>
    Connecting,

    Online,

    /// <summary>Slack refused a token; <see cref="ISlackGateway.Problem"/> says why. Saving the tokens again retries.</summary>
    Failed,
}

/// <summary>The Slack workspace the app is installed in, with the channels board owners can pick: those the bot is in, not shared with other organizations.</summary>
public sealed record SlackWorkspaceInfo(string Id, string Name, IReadOnlyList<ChatChannel> Channels);

/// <summary>The Slack app's connection, as the rest of PrikBoard sees it; tests run it against the fake Slack in tools/FakeSlack.</summary>
public interface ISlackGateway
{
    SlackAppState State { get; }

    /// <summary>Why the app is <see cref="SlackAppState.Failed"/>, or why it is still connecting, for admins.</summary>
    string? Problem { get; }

    /// <summary>The workspace and bot behind the bot token, once it is known.</summary>
    SlackIdentity? Identity { get; }

    /// <summary>The workspaces the app is in (one, for an app installed in a single workspace); empty while it is not online.</summary>
    Task<IReadOnlyList<SlackWorkspaceInfo>> WorkspacesAsync(CancellationToken ct);

    /// <summary>
    /// Connects with these tokens (new ones close the old connection; the same ones after a failure try again), or
    /// disconnects when they are null. Links on <paramref name="publicUrl"/> are unfurled.
    /// </summary>
    Task ApplyAsync(string? botToken, string? appToken, string? publicUrl);

    /// <summary>
    /// Unfurls a message's links again, for <see cref="ChatActivity"/> (each URL to its attachment). A message in a channel
    /// that has since been shared with other organizations gets <paramref name="whenShared"/> instead. False when the
    /// message or its channel is gone, so it needn't be followed any longer.
    /// </summary>
    Task<bool> RefreshUnfurlsAsync(
        string channelId, string messageTs, IReadOnlyDictionary<string, object> unfurls, IReadOnlyDictionary<string, object> whenShared, CancellationToken ct);

    /// <summary>Posts a message as the bot; nothing in a channel shared with other organizations, or while the app is offline.</summary>
    Task PostAsync(string channelId, string text, object attachment, CancellationToken ct);
}

/// <summary>
/// The Slack app itself, inside the API (docs/slack.md). It keeps a Socket Mode connection, a WebSocket it opens out to
/// Slack, so PrikBoard needs no public address for Slack to call; Slack sends events over it and the app acks each one.
/// It answers <c>link_shared</c>: the links on the public address in a posted message get previews
/// (<see cref="ChatPreviews"/>, drawn by <see cref="SlackBlocks"/>) where the workspace may see them, through
/// <c>chat.unfurl</c>, and the message is remembered so <see cref="ChatActivity"/> can unfurl it again as its cards change
/// (<see cref="SlackActivityTarget"/>), and post the updates channels follow. <c>/prikboard board</c> shows the board
/// connected to the channel, only to whoever asked. <see cref="SlackApp"/> gives it its tokens and public URL. One
/// instance only, like the Discord bot.
/// </summary>
public sealed class SlackGateway(IServiceProvider services, IConfiguration config, ILogger<SlackGateway> logger)
    : ISlackGateway, IHostedService
{
    /// <summary>How long the channel list and a channel's Slack Connect check are kept.</summary>
    private static readonly TimeSpan ChannelsFor = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

    /// <summary>How long Save to PrikBoard's modal waits to be submitted.</summary>
    private static readonly TimeSpan DraftLifetime = TimeSpan.FromMinutes(15);

    /// <summary>How long the modal waits for the message's names before opening anyway; Slack's trigger works for 3 seconds.</summary>
    private static readonly TimeSpan ReadForTitle = TimeSpan.FromSeconds(1.5);

    private readonly ConcurrentDictionary<string, SaveDraft> _drafts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, (SlackChannelInfo? Channel, DateTimeOffset At)> _channelInfo = new();

    // Per workspace, like the Discord bot's per server.
    private readonly PartitionedRateLimiter<string> _previews = PartitionedRateLimiter.Create<string, string>(teamId =>
        RateLimitPartition.GetFixedWindowLimiter(teamId, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = config.GetValue("RateLimits:LinkPreviewsPerMinute", 60),
            Window = TimeSpan.FromMinutes(1),
        }));

    private CancellationTokenSource? _run;
    private Task _running = Task.CompletedTask;
    private (string Bot, string App)? _tokens;
    private Site? _site;
    private (IReadOnlyList<SlackWorkspaceInfo> Workspaces, DateTimeOffset At)? _workspaces;
    private volatile SlackAppState _state;
    private volatile string? _problem;
    private volatile SlackIdentity? _identity;

    public SlackAppState State => _state;
    public string? Problem => _problem;
    public SlackIdentity? Identity => _identity;

    private SlackApi Api => services.GetRequiredService<SlackApi>();

    public async Task<IReadOnlyList<SlackWorkspaceInfo>> WorkspacesAsync(CancellationToken ct)
    {
        if (_state != SlackAppState.Online || _identity is not { } identity || _tokens is not { } tokens) return [];
        if (_workspaces is { } cached && DateTimeOffset.UtcNow - cached.At < ChannelsFor) return cached.Workspaces;
        try
        {
            var channels = (await Api.ChannelsAsync(tokens.Bot, ct))
                .Where(c => c is { IsMember: true, IsExtShared: false })
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new ChatChannel(c.Id, c.Name))
                .ToList();
            IReadOnlyList<SlackWorkspaceInfo> workspaces = [new SlackWorkspaceInfo(identity.TeamId, identity.TeamName, channels)];
            _workspaces = (workspaces, DateTimeOffset.UtcNow);
            return workspaces;
        }
        catch (SlackApiException error)
        {
            logger.LogWarning("Listing the Slack workspace's channels failed: {Error}", error.Error);
            return _workspaces?.Workspaces ?? [];
        }
    }

    public async Task ApplyAsync(string? botToken, string? appToken, string? publicUrl)
    {
        await _gate.WaitAsync();
        try
        {
            _site = Uri.TryCreate(publicUrl, UriKind.Absolute, out _) ? new Site(publicUrl!) : null;
            (string, string)? tokens = botToken is null || appToken is null ? null : (botToken, appToken);
            if (tokens == _tokens && _state != SlackAppState.Failed) return;

            await StopRunAsync();
            _tokens = tokens;
            _identity = null;
            _workspaces = null;
            _problem = null;
            if (tokens is null)
            {
                _state = SlackAppState.Off;
                return;
            }
            var (bot, app) = tokens.Value;
            _state = SlackAppState.Connecting;
            _run = new CancellationTokenSource();
            var ct = _run.Token;
            _running = Task.Run(() => RunAsync(bot, app, ct), CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopRunAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Closes the current connection; call it holding the gate.</summary>
    private async Task StopRunAsync()
    {
        if (_run is not { } run) return;
        _run = null;
        await run.CancelAsync();
        try
        {
            await _running;
        }
        catch (Exception error) when (error is OperationCanceledException or WebSocketException)
        {
            // Stopping.
        }
        run.Dispose();
    }

    /// <summary>
    /// Learns who the bot token is, then keeps a Socket Mode connection open: a new one whenever Slack closes it or asks
    /// for a refresh, waiting a little longer after each failure. A refused token ends it as <see cref="SlackAppState.Failed"/>.
    /// </summary>
    private async Task RunAsync(string botToken, string appToken, CancellationToken ct)
    {
        var backoff = TimeSpan.FromSeconds(1);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _identity ??= await Api.AuthTestAsync(botToken, ct);
                var url = await Api.OpenConnectionAsync(appToken, ct);
                using var socket = new ClientWebSocket();
                await socket.ConnectAsync(url, ct);
                await ReceiveAsync(socket, botToken, ct, () => backoff = TimeSpan.FromSeconds(1));
            }
            catch (SlackApiException error) when (error.IsAuth)
            {
                _state = SlackAppState.Failed;
                _problem = error.Error switch
                {
                    "not_a_bot_token" => "The bot token isn't a bot token. Copy the Bot User OAuth Token (xoxb-…) from OAuth & Permissions.",
                    "not_allowed_token_type" => "The app-level token isn't one. Create it under Basic Information → App-Level Tokens with connections:write (xapp-…).",
                    _ => $"Slack refused a token ({error.Error}). Copy both tokens again from your Slack app and save them.",
                };
                logger.LogWarning("Slack refused the app's tokens: {Error}", error.Error);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is SlackApiException or WebSocketException or HttpRequestException or JsonException)
            {
                _state = SlackAppState.Connecting;
                _problem = "Slack could not be reached. The app keeps trying.";
                logger.LogInformation(error, "The Slack app's connection failed; trying again in {Seconds} s.", backoff.TotalSeconds);
            }

            if (ct.IsCancellationRequested) return;
            _state = SlackAppState.Connecting;
            try
            {
                await Task.Delay(backoff, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }
    }

    /// <summary>Reads envelopes until the socket closes or Slack asks for a new connection.</summary>
    private async Task ReceiveAsync(ClientWebSocket socket, string botToken, CancellationToken ct, Action connected)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            using var json = JsonDocument.Parse(message.ToArray());
            message.SetLength(0);
            var envelope = json.RootElement;
            var type = envelope.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "hello")
            {
                _state = SlackAppState.Online;
                _problem = null;
                connected();
                logger.LogInformation("The Slack app is online in {Team}.", _identity?.TeamName);
                continue;
            }
            if (type == "disconnect")
            {
                // A refresh (Slack renews connections every few hours) or a warning: open a new connection.
                await CloseQuietlyAsync(socket);
                return;
            }

            // Every envelope must be acknowledged within 3 seconds, so the work runs on its own.
            if (envelope.TryGetProperty("envelope_id", out var id) && id.GetString() is { } envelopeId)
            {
                await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { envelope_id = envelopeId }), WebSocketMessageType.Text, true, ct);
            }
            if (type is "events_api" or "slash_commands" or "interactive" && envelope.TryGetProperty("payload", out var payload))
            {
                var copy = payload.Clone();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await (type switch
                        {
                            "events_api" => HandleEventAsync(copy, botToken, ct),
                            "slash_commands" => SlashCommandAsync(copy, botToken, ct),
                            _ => InteractiveAsync(copy, botToken, ct),
                        });
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    {
                        logger.LogError(error, "Handling a Slack event failed.");
                    }
                }, CancellationToken.None);
            }
        }
    }

    private static async Task CloseQuietlyAsync(ClientWebSocket socket)
    {
        try
        {
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "refresh", CancellationToken.None);
        }
        catch (WebSocketException)
        {
            // Already gone.
        }
    }

    private async Task HandleEventAsync(JsonElement payload, string botToken, CancellationToken ct)
    {
        if (!payload.TryGetProperty("event", out var e) || e.GetProperty("type").GetString() != "link_shared") return;
        var teamId = payload.TryGetProperty("team_id", out var team) ? team.GetString() : _identity?.TeamId;
        await LinkSharedAsync(teamId, e, botToken, ct);
    }

    /// <summary>
    /// Previews the links of a posted message where the workspace may see their boards. Links typed in the message box
    /// (<c>source: composer</c>) are left alone: Slack doesn't say which channel they are for, and previews depend on it.
    /// Nothing for channels shared with other organizations, and nothing said about links without a preview.
    /// </summary>
    private async Task LinkSharedAsync(string? teamId, JsonElement e, string botToken, CancellationToken ct)
    {
        if (_site is not { } site || teamId is null) return;
        var channelId = e.TryGetProperty("channel", out var c) ? c.GetString() : null;
        var ts = e.TryGetProperty("message_ts", out var m) ? m.GetString() : null;
        var source = e.TryGetProperty("source", out var s) ? s.GetString() : null;
        var urls = e.TryGetProperty("links", out var links)
            ? links.EnumerateArray().Select(l => l.TryGetProperty("url", out var url) ? url.GetString() : null).OfType<string>().ToList()
            : [];
        // What Slack sent, so a link that previews nothing can be told from one that never arrived (invite tokens masked).
        logger.LogInformation("Slack link_shared in {Channel} ({Source}): {Links}", channelId, source, string.Join(" ", urls.Select(Masked)));
        if (channelId is null or "COMPOSER" || ts is null || source == "composer") return;
        if (urls.Count == 0) return;
        if (await ChannelAsync(botToken, channelId, ct) is not { IsExtShared: false } channel)
        {
            logger.LogInformation("No unfurl in {Channel}: the app can't see it, or it is shared with another organization.", channelId);
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
        var shown = await ChatPreviews.ForSourcesAsync(db, ChatPlatform.Slack, teamId, channelId, urls, site.Url, ct, allow: () =>
        {
            using var lease = _previews.AttemptAcquire(teamId);
            return lease.IsAcquired;
        });
        if (shown.Count == 0)
        {
            logger.LogInformation("No unfurl in #{Channel}: none of the links preview there (address {Site}).", channel.Name, site.Url);
            return;
        }

        try
        {
            await Api.UnfurlAsync(botToken, channelId, ts, shown.ToDictionary(p => p.Source, p => (object)site.Blocks.For(p.Preview, p.Source)), ct);
        }
        catch (SlackApiException error)
        {
            logger.LogInformation("Slack refused an unfurl in {Channel}: {Error}", channelId, error.Error);
            return;
        }
        // Followed for a week, so the previews keep up with their cards. Slack's message timestamp is its id in the channel.
        await ChatPreviews.RememberAsync(db, ChatPlatform.Slack, ts, teamId, channelId, shown, ct);
    }

    /// <summary>
    /// <c>/prikboard board</c>: the board connected to this channel, only for whoever asked (an ephemeral answer through
    /// the command's <c>response_url</c>). Anything else gets a short help.
    /// </summary>
    private async Task SlashCommandAsync(JsonElement payload, string botToken, CancellationToken ct)
    {
        string? Field(string name) => payload.TryGetProperty(name, out var value) ? value.GetString() : null;
        if (Field("command") != SlackApp.Command || Field("response_url") is not { } responseUrl) return;
        var teamId = Field("team_id") ?? _identity?.TeamId;
        var channelId = Field("channel_id");
        if (_site is not { } site || teamId is null || channelId is null) return;

        object answer;
        if (!string.Equals(Field("text")?.Trim(), "board", StringComparison.OrdinalIgnoreCase))
        {
            answer = Ephemeral("Try `/prikboard board`: what is on the PrikBoard board connected to this channel, only for you.");
        }
        else if (await ChannelAsync(botToken, channelId, ct) is not { IsExtShared: false })
        {
            answer = Ephemeral("PrikBoard doesn't show boards in channels shared with other organizations.");
        }
        else
        {
            await using var scope = services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
            var boards = await ChatPreviews.BoardsInChannelAsync(db, ChatPlatform.Slack, teamId, channelId, ct);
            if (boards.Count == 0)
            {
                answer = Ephemeral("No PrikBoard board is connected to this channel. A board owner connects one under Integrations in the board menu.");
            }
            else
            {
                var snapshot = await ChatPreviews.SnapshotAsync(db, boards[0], DateTimeOffset.UtcNow, ct);
                answer = new { response_type = "ephemeral", text = snapshot.Name, attachments = new[] { site.Blocks.Snapshot(snapshot) } };
            }
        }
        if (!await Api.RespondAsync(responseUrl, answer, ct)) logger.LogInformation("Answering /prikboard in {Channel} failed.", channelId);

        static object Ephemeral(string text) => new { response_type = "ephemeral", text };
    }

    /// <summary>Shortcuts and modals: Save to PrikBoard on a message, and its modal being submitted. Everything else is ignored.</summary>
    private Task InteractiveAsync(JsonElement payload, string botToken, CancellationToken ct) =>
        (Text(payload, "type"), payload) switch
        {
            ("message_action", _) when Text(payload, "callback_id") == SlackBlocks.SaveCallback => SaveShortcutAsync(payload, botToken, ct),
            ("view_submission", _) when payload.TryGetProperty("view", out var view) && Text(view, "callback_id") == SlackBlocks.SaveCallback =>
                SaveSubmittedAsync(view, botToken, ct),
            _ => Task.CompletedTask,
        };

    /// <summary>
    /// Save to PrikBoard on a message: who you are (a link to connect your account if PrikBoard doesn't know you yet),
    /// then a modal to pick the board and bucket and change the title. The modal opens first, since Slack's
    /// <c>trigger_id</c> only works for 3 seconds; reading the message (names, permalink) goes on meanwhile.
    /// </summary>
    private async Task SaveShortcutAsync(JsonElement payload, string botToken, CancellationToken ct)
    {
        var teamId = Text(payload, "team", "id") ?? _identity?.TeamId;
        var channelId = Text(payload, "channel", "id");
        var slackUserId = Text(payload, "user", "id");
        var triggerId = Text(payload, "trigger_id");
        if (_site is not { } site || teamId is null || channelId is null || slackUserId is null || triggerId is null
            || !payload.TryGetProperty("message", out var message)) return;

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<ChatAccounts>();
        // Slack user ids are only unique within their workspace.
        var accountId = $"{teamId}:{slackUserId}";
        if (await accounts.FindUserAsync(db, ChatPlatform.Slack, accountId, ct) is not { } userId)
        {
            var code = accounts.StartLink(ChatPlatform.Slack, accountId, Text(payload, "user", "name") ?? Text(payload, "user", "username") ?? slackUserId);
            await OpenAsync(botToken, triggerId, SlackBlocks.NoticeView(
                $"PrikBoard doesn't know you yet. <{site.Url}/#/connect/slack-account?code={code}|Connect your PrikBoard account> once, " +
                "then use Save to PrikBoard again."), ct);
            return;
        }

        var targets = await ChatSaves.TargetsAsync(db, userId, ChatPlatform.Slack, teamId, channelId, ct);
        if (targets.Count == 0)
        {
            await OpenAsync(botToken, triggerId, SlackBlocks.NoticeView(
                "There is no board here you can add cards to. A board owner connects boards to this workspace under Integrations in PrikBoard."), ct);
            return;
        }

        var raw = Text(message, "text") ?? "";
        var ts = Text(message, "ts") ?? "";
        var channelName = Text(payload, "channel", "name") ?? channelId;
        var token = Guid.NewGuid().ToString("N");
        foreach (var (key, old) in _drafts) if (old.ExpiresAt <= DateTimeOffset.UtcNow) _drafts.TryRemove(key, out _);
        var draft = new SaveDraft(userId, channelId, ts, Text(payload, "response_url"), targets, DateTimeOffset.UtcNow + DraftLifetime,
            ReadMessageAsync(botToken, channelId, channelName, message.Clone(), ct));
        _drafts[token] = draft;

        // The title is the message's first line with names, if reading it takes no longer than the trigger can wait.
        var read = await Task.WhenAny(draft.Message, Task.Delay(ReadForTitle, ct)) == draft.Message && draft.Message.IsCompletedSuccessfully
            ? draft.Message.Result
            : new SavedMessage(SlackText.Plain(raw, _ => null), "someone", channelName, "", DateTimeOffset.UtcNow, [], ChatPlatform.Slack);
        await OpenAsync(botToken, triggerId, SlackBlocks.SaveView(token, targets, ChatSaves.TitleFor(read), read.AuthorName, channelName), ct);
    }

    /// <summary>The message as the card takes it: mentions as names, its author, its link and its files.</summary>
    private async Task<SavedMessage> ReadMessageAsync(string botToken, string channelId, string channelName, JsonElement message, CancellationToken ct)
    {
        var raw = Text(message, "text") ?? "";
        var names = new Dictionary<string, string?>();
        foreach (Match mention in Regex.Matches(raw, "<@([A-Z0-9]+)(?:\\|[^>]*)?>"))
        {
            var id = mention.Groups[1].Value;
            if (!names.ContainsKey(id)) names[id] = await Api.UserNameAsync(botToken, id, ct);
        }
        var ts = Text(message, "ts") ?? "0";
        string jumpUrl;
        try
        {
            jumpUrl = await Api.PermalinkAsync(botToken, channelId, ts, ct);
        }
        catch (SlackApiException)
        {
            jumpUrl = $"https://slack.com/app_redirect?channel={channelId}";
        }
        var sentAt = double.TryParse(ts, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000))
            : DateTimeOffset.UtcNow;
        var attachments = message.TryGetProperty("files", out var fileList) && fileList.ValueKind == JsonValueKind.Array
            ? fileList.EnumerateArray()
                .Where(f => Text(f, "url_private") is not null)
                .Select(f => new SavedAttachment(Text(f, "url_private")!, Text(f, "name") ?? "image",
                    Text(f, "mimetype"), f.TryGetProperty("size", out var size) && size.TryGetInt64(out var bytes) ? bytes : long.MaxValue))
                .ToList()
            : [];
        return new SavedMessage(SlackText.Plain(raw, id => names.GetValueOrDefault(id)), await NameAsync(botToken, message, ct), channelName, jumpUrl,
            sentAt, attachments, ChatPlatform.Slack);
    }

    /// <summary>Who wrote a message: a person's name, or a bot's.</summary>
    private async Task<string> NameAsync(string botToken, JsonElement message, CancellationToken ct) =>
        (Text(message, "user") is { } user ? await Api.UserNameAsync(botToken, user, ct) : null)
        ?? Text(message, "username") ?? "someone";

    /// <summary>The modal was submitted: the card is made as the person, who gets an answer only they see, and the message a 📌.</summary>
    private async Task SaveSubmittedAsync(JsonElement view, string botToken, CancellationToken ct)
    {
        if (_site is not { } site || Text(view, "private_metadata") is not { } token || !_drafts.TryRemove(token, out var draft)) return;
        var values = view.GetProperty("state").GetProperty("values");
        var where = SlackBlocks.ParseWhere(Text(values, "where", "where", "selected_option", "value"));
        var title = Text(values, "title", "title", "value") ?? "";
        if (where is not var (boardId, bucketId) || draft.Targets.FirstOrDefault(t => t.BoardId == boardId) is not { } target
            || (bucketId is { } picked ? target.Buckets.All(b => b.Id != picked) : target.Buckets.Count > 0))
        {
            await AnswerAsync(draft, "Not saved: that board or bucket isn't one you can save to here.", null, ct);
            return;
        }

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
        var saves = scope.ServiceProvider.GetRequiredService<ChatSaves>();
        SavedMessage message;
        try
        {
            message = await draft.Message;
        }
        catch (SlackApiException error)
        {
            await AnswerAsync(draft, $"Not saved: Slack didn't let PrikBoard read the message ({error.Error}). Try again in a minute.", null, ct);
            return;
        }
        var (card, problem) = await saves.SaveAsync(db, draft.UserId, boardId, bucketId, title, message, ct, filesToken: botToken);
        if (card is null)
        {
            await AnswerAsync(draft, $"Not saved: {problem}", null, ct);
            return;
        }

        var preview = await LinkPreviews.CardAsync(db, (await db.Cards.FindAsync([card.Id], ct))!, (await db.Boards.FindAsync([boardId], ct))!, ct);
        await AnswerAsync(draft, $"Saved as {card.ReadableId} on {target.Name}.", site.Blocks.For(preview, site.Url + card.Path), ct);
        // A pin on the message tells the channel it is on a board now.
        try
        {
            await Api.AddReactionAsync(botToken, draft.ChannelId, draft.MessageTs, "pushpin", ct);
        }
        catch (SlackApiException error)
        {
            logger.LogInformation("The Slack app could not pin a saved message in {Channel}: {Error}", draft.ChannelId, error.Error);
        }
    }

    /// <summary>An answer only the person who saved sees, through the shortcut's <c>response_url</c>.</summary>
    private async Task AnswerAsync(SaveDraft draft, string text, object? attachment, CancellationToken ct)
    {
        if (draft.ResponseUrl is not { } url) return;
        object answer = attachment is null
            ? new { response_type = "ephemeral", text = SlackBlocks.Escape(text) }
            : new { response_type = "ephemeral", text = SlackBlocks.Escape(text), attachments = new[] { attachment } };
        if (!await Api.RespondAsync(url, answer, ct)) logger.LogInformation("Answering Save to PrikBoard in {Channel} failed.", draft.ChannelId);
    }

    private async Task OpenAsync(string botToken, string triggerId, object view, CancellationToken ct)
    {
        try
        {
            await Api.OpenViewAsync(botToken, triggerId, view, ct);
        }
        catch (SlackApiException error)
        {
            logger.LogInformation("The Slack app could not open a modal: {Error}", error.Error);
        }
    }

    /// <summary>A string at a path of properties, or null where any is missing.</summary>
    private static string? Text(JsonElement element, params string[] path)
    {
        foreach (var name in path)
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out element)) return null;
        }
        return element.ValueKind == JsonValueKind.String ? element.GetString() : null;
    }

    /// <summary>A message being saved: who saves it and where it is, the boards offered, and the message as it is being read.</summary>
    private sealed record SaveDraft(
        Guid UserId, string ChannelId, string MessageTs, string? ResponseUrl, IReadOnlyList<SaveTarget> Targets, DateTimeOffset ExpiresAt,
        Task<SavedMessage> Message);

    public async Task<bool> RefreshUnfurlsAsync(
        string channelId, string messageTs, IReadOnlyDictionary<string, object> unfurls, IReadOnlyDictionary<string, object> whenShared, CancellationToken ct)
    {
        // Offline: kept, and looked at again with the next change.
        if (_state != SlackAppState.Online || _tokens is not { } tokens) return true;
        try
        {
            var channel = await ChannelAsync(tokens.Bot, channelId, ct);
            if (channel is null) return false;
            await Api.UnfurlAsync(tokens.Bot, channelId, messageTs, channel.IsExtShared ? whenShared : unfurls, ct);
            return true;
        }
        catch (SlackApiException error) when (error.Error is "cannot_find_message" or "message_not_found" or "channel_not_found" or "is_archived")
        {
            return false;
        }
        catch (SlackApiException error)
        {
            logger.LogInformation("Slack refused to unfurl a message again in {Channel}: {Error}", channelId, error.Error);
            return true;
        }
    }

    public async Task PostAsync(string channelId, string text, object attachment, CancellationToken ct)
    {
        if (_state != SlackAppState.Online || _tokens is not { } tokens) return;
        try
        {
            if (await ChannelAsync(tokens.Bot, channelId, ct) is not { IsExtShared: false }) return;
            await Api.PostMessageAsync(tokens.Bot, channelId, text, attachment, ct);
        }
        catch (SlackApiException error)
        {
            logger.LogInformation("The Slack app could not post in {Channel}: {Error}", channelId, error.Error);
        }
    }

    /// <summary>A link for the log: an invite's token is the key to the board, so it is left out.</summary>
    private static string Masked(string url) =>
        url.IndexOf("#/invite/", StringComparison.Ordinal) is var at and >= 0 ? url[..(at + "#/invite/".Length)] + "…" : url;

    /// <summary>A channel as Slack describes it, kept for a minute; null when the bot can't see it.</summary>
    private async Task<SlackChannelInfo?> ChannelAsync(string botToken, string channelId, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (_channelInfo.TryGetValue(channelId, out var known) && now - known.At < ChannelsFor) return known.Channel;
        var channel = await Api.ChannelAsync(botToken, channelId, ct);
        _channelInfo[channelId] = (channel, now);
        return channel;
    }

    private sealed class Site(string url)
    {
        public string Url { get; } = url.TrimEnd('/');
        public SlackBlocks Blocks { get; } = new(url);
    }
}
