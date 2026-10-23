using System.Net;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Discord;
using Discord.Net;
using Discord.WebSocket;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Integrations;

/// <summary>Where the Discord bot is.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DiscordBotState>))]
public enum DiscordBotState
{
    /// <summary>No token is set.</summary>
    Off,

    /// <summary>Logging in, or reconnecting after Discord dropped the connection.</summary>
    Connecting,

    Online,

    /// <summary>Discord refused the bot; <see cref="IDiscordGateway.Problem"/> says why. Saving the token again retries.</summary>
    Failed,
}

/// <summary>A server the bot is in, with the channels it can preview links in (it may read and post there).</summary>
public sealed record DiscordServerInfo(string Id, string Name, IReadOnlyList<DiscordChannel> Channels);

/// <summary>The Discord bot's connection, as the endpoints see it; tests replace it with a fake.</summary>
public interface IDiscordGateway
{
    DiscordBotState State { get; }

    /// <summary>Why the bot is <see cref="DiscordBotState.Failed"/>, for admins.</summary>
    string? Problem { get; }

    /// <summary>The bot's application, once it is online.</summary>
    DiscordApplication? Application { get; }

    /// <summary>The servers the bot is in, by name.</summary>
    IReadOnlyList<DiscordServerInfo> Servers { get; }

    /// <summary>
    /// Connects the bot with <paramref name="token"/> (a new token logs out the old one; the same token after a failure
    /// tries again), or logs it out when null. Links starting with <paramref name="publicUrl"/> are previewed.
    /// </summary>
    Task ApplyAsync(string? token, string? publicUrl);

    /// <summary>Makes the bot leave the server; false when it isn't in it.</summary>
    Task<bool> LeaveAsync(string guildId);
}

/// <summary>
/// The Discord bot itself, inside the API (docs/discord.md). It connects out to Discord's gateway, so it needs no inbound
/// port, and answers every message with links to this PrikBoard with one reply holding an embed per link the server may
/// see (<see cref="LinkPreviews"/>). <see cref="DiscordBot"/> gives it its token and public URL, at startup and whenever
/// an admin changes them. One instance only, like the rest of PrikBoard: two would answer every message twice.
/// </summary>
public sealed class DiscordGateway(IServiceScopeFactory scopes, IConfiguration config, ILogger<DiscordGateway> logger)
    : IDiscordGateway, IHostedService
{
    /// <summary>Close codes after which Discord won't take the bot back until something changes.</summary>
    private static readonly Dictionary<int, string> Fatal = new()
    {
        [4004] = "Discord doesn't accept the token any more. Reset it on the Bot tab of your application and save the new one.",
        [4013] = "Discord refused the bot's intents.",
        [4014] = "Turn on Message Content Intent on the Bot tab of your application: without it the bot can't see pasted links.",
    };

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();

    // Per server, not per bot: one busy server can't use up the others' previews.
    private readonly PartitionedRateLimiter<string> _previews = PartitionedRateLimiter.Create<string, string>(guildId =>
        RateLimitPartition.GetFixedWindowLimiter(guildId, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = config.GetValue("RateLimits:LinkPreviewsPerMinute", 60),
            Window = TimeSpan.FromMinutes(1),
        }));

    private DiscordSocketClient? _client;
    private string? _token;
    private Site? _site;
    private volatile DiscordBotState _state;
    private volatile string? _problem;
    private volatile DiscordApplication? _application;

    public DiscordBotState State => _state;
    public string? Problem => _problem;
    public DiscordApplication? Application => _application;

    public IReadOnlyList<DiscordServerInfo> Servers => _client is { } client
        ? client.Guilds.Select(Describe).OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList()
        : [];

    public async Task ApplyAsync(string? token, string? publicUrl)
    {
        await _gate.WaitAsync();
        try
        {
            _site = Uri.TryCreate(publicUrl, UriKind.Absolute, out _) ? new Site(publicUrl!) : null;
            if (token == _token && _state != DiscordBotState.Failed) return;

            await StopClientAsync();
            _token = token;
            _application = null;
            _problem = null;
            if (token is null)
            {
                _state = DiscordBotState.Off;
                return;
            }

            // Message Content is privileged: free in under 100 servers, verification by Discord above that.
            var client = new DiscordSocketClient(new DiscordSocketConfig
            {
                GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
            });
            client.Log += Log;
            client.Ready += () => Background(() => ReadyAsync(client));
            client.Disconnected += error => Disconnected(client, error);
            client.MessageReceived += message => Background(() => HandleMessageAsync(message, _stopping.Token));
            _client = client;
            _state = DiscordBotState.Connecting;
            try
            {
                await client.LoginAsync(TokenType.Bot, token);
                await client.StartAsync();
            }
            catch (Exception error) when (error is HttpException { HttpCode: HttpStatusCode.Unauthorized } or ArgumentException)
            {
                logger.LogWarning(error, "Discord refused the bot's token.");
                await StopClientAsync();
                _state = DiscordBotState.Failed;
                _problem = "Discord doesn't accept the token. Copy it again from the Bot tab of your application (Reset Token).";
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                // Discord is unreachable or having trouble, which must not stop PrikBoard from starting: try again later.
                logger.LogWarning(error, "The Discord bot could not log in; trying again in a minute.");
                await StopClientAsync();
                _state = DiscordBotState.Failed;
                _problem = "Discord could not be reached. The bot tries again every minute.";
                _ = RetryAsync(token);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Logs in again after a minute, unless the token changed or the app stops in the meantime.</summary>
    private async Task RetryAsync(string token)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMinutes(1), _stopping.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (_token == token && _state == DiscordBotState.Failed) await ApplyAsync(token, _site?.Url);
    }

    public async Task<bool> LeaveAsync(string guildId)
    {
        if (!ulong.TryParse(guildId, out var id) || _client?.GetGuild(id) is not { } guild) return false;
        await guild.LeaveAsync();
        return true;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await StopClientAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Logs the current client out; call it holding the gate.</summary>
    private async Task StopClientAsync()
    {
        if (_client is not { } client) return;
        _client = null;
        try
        {
            await client.StopAsync();
            await client.LogoutAsync();
        }
        catch (Exception error)
        {
            logger.LogDebug(error, "Stopping the Discord bot failed.");
        }
        await client.DisposeAsync();
    }

    private async Task ReadyAsync(DiscordSocketClient client)
    {
        if (_client != client) return;
        _state = DiscordBotState.Online;
        var app = await client.GetApplicationInfoAsync();
        _application = new DiscordApplication(app.Id.ToString(), client.CurrentUser.Username, app.IsBotPublic == true);
        logger.LogInformation("The Discord bot {Name} is online in {Servers} server(s).", client.CurrentUser.Username, client.Guilds.Count);
    }

    private Task Disconnected(DiscordSocketClient client, Exception error)
    {
        if (_client != client) return Task.CompletedTask;
        if (error is WebSocketClosedException { CloseCode: var code } && Fatal.TryGetValue(code, out var problem))
        {
            _state = DiscordBotState.Failed;
            _problem = problem;
            logger.LogWarning("Discord closed the bot's connection ({Code}): {Problem}", code, problem);
            // Not from inside the client's own event: stopping waits for its handlers.
            _ = Task.Run(async () =>
            {
                await _gate.WaitAsync();
                try
                {
                    if (_client == client) await StopClientAsync();
                }
                finally
                {
                    _gate.Release();
                }
            });
        }
        else
        {
            _state = DiscordBotState.Connecting;
        }
        return Task.CompletedTask;
    }

    private static DiscordServerInfo Describe(SocketGuild guild)
    {
        var bot = guild.CurrentUser;
        var channels = guild.TextChannels
            // Threads follow their channel, and voice channels' chats are not where links are shared.
            .Where(c => c is not (SocketThreadChannel or SocketVoiceChannel))
            .Where(c => bot?.GetPermissions(c) is { ViewChannel: true, SendMessages: true })
            .OrderBy(c => c.Position)
            .Select(c => new DiscordChannel(c.Id.ToString(), c.Name))
            .ToList();
        return new DiscordServerInfo(guild.Id.ToString(), guild.Name, channels);
    }

    private async Task HandleMessageAsync(SocketMessage message, CancellationToken ct)
    {
        // Never answer bots, this one included.
        if (message is not SocketUserMessage { Author.IsBot: false } user || user.Channel is not SocketGuildChannel channel) return;
        if (_site is not { } site) return;
        var links = site.Links.Find(user.Content);
        if (links.Count == 0) return;

        var guildId = channel.Guild.Id.ToString();
        // A thread previews what its channel does.
        var channelId = (channel is SocketThreadChannel { ParentChannel: { } parent } ? parent.Id : channel.Id).ToString();

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
        List<Embed> embeds = [];
        foreach (var link in links)
        {
            using var lease = _previews.AttemptAcquire(guildId);
            if (!lease.IsAcquired) break;
            if (await LinkPreviews.FindAsync(db, guildId, link, channelId, site.Url, ct) is { } preview)
            {
                embeds.Add(site.Embeds.For(preview, link));
            }
        }
        // Silent about links without a preview: saying "no access" would tell that the board exists.
        if (embeds.Count == 0) return;

        await user.ReplyAsync(embeds: [.. embeds], allowedMentions: AllowedMentions.None);

        // Remove Discord's own "not signed in" preview from the message answered, when the bot may.
        if (channel.Guild.CurrentUser?.GetPermissions(channel).ManageMessages == true)
        {
            await user.ModifyAsync(m => m.Flags = MessageFlags.SuppressEmbeds);
        }
    }

    /// <summary>Gateway handlers must return quickly, so the work runs on its own.</summary>
    private Task Background(Func<Task> work)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await work();
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                logger.LogError(e, "Handling a Discord event failed.");
            }
        });
        return Task.CompletedTask;
    }

    private Task Log(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            _ => LogLevel.Trace,
        };
        logger.Log(level, message.Exception, "{Source}: {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }

    /// <summary>The public URL links are matched against, with what is built from it.</summary>
    private sealed class Site(string url)
    {
        public string Url { get; } = url;
        public LinkFinder Links { get; } = new(url);
        public Embeds Embeds { get; } = new(url);
    }
}
