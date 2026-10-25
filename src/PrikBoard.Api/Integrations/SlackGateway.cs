using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
}

/// <summary>
/// The Slack app itself, inside the API (docs/slack.md). It keeps a Socket Mode connection, a WebSocket it opens out to
/// Slack, so PrikBoard needs no public address for Slack to call; Slack sends events over it and the app acks each one.
/// For now it answers <c>link_shared</c>: the links on the public address in a posted message get previews
/// (<see cref="ChatPreviews"/>, drawn by <see cref="SlackBlocks"/>) where the workspace may see them, through
/// <c>chat.unfurl</c>. <see cref="SlackApp"/> gives it its tokens and public URL. One instance only, like the Discord bot.
/// </summary>
public sealed class SlackGateway(IServiceProvider services, IConfiguration config, ILogger<SlackGateway> logger)
    : ISlackGateway, IHostedService
{
    /// <summary>How long the channel list and a channel's Slack Connect check are kept.</summary>
    private static readonly TimeSpan ChannelsFor = TimeSpan.FromMinutes(1);

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);

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
            if (type == "events_api" && envelope.TryGetProperty("payload", out var payload))
            {
                var copy = payload.Clone();
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await HandleEventAsync(copy, botToken, ct);
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
        if (channelId is null or "COMPOSER" || ts is null || source == "composer") return;
        var urls = e.GetProperty("links").EnumerateArray().Select(l => l.GetProperty("url").GetString()).OfType<string>().ToList();
        if (urls.Count == 0 || await ChannelAsync(botToken, channelId, ct) is not { IsExtShared: false }) return;

        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
        var shown = await ChatPreviews.ForSourcesAsync(db, ChatPlatform.Slack, teamId, channelId, urls, site.Url, ct, allow: () =>
        {
            using var lease = _previews.AttemptAcquire(teamId);
            return lease.IsAcquired;
        });
        if (shown.Count == 0) return;

        try
        {
            await Api.UnfurlAsync(botToken, channelId, ts, shown.ToDictionary(p => p.Source, p => (object)site.Blocks.For(p.Preview, p.Source)), ct);
        }
        catch (SlackApiException error)
        {
            logger.LogInformation("Slack refused an unfurl in {Channel}: {Error}", channelId, error.Error);
        }
    }

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
