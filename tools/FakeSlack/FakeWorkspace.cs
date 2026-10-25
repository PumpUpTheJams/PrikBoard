using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace FakeSlack;

/// <summary>A channel of the fake workspace.</summary>
/// <param name="Id">Its id, C… like Slack's.</param>
/// <param name="Name">Its name, without the #.</param>
/// <param name="IsMember">Whether the app's bot was added to it.</param>
/// <param name="IsExtShared">Whether it is shared with another organization (Slack Connect).</param>
public sealed record FakeChannel(string Id, string Name, bool IsMember = true, bool IsExtShared = false);

/// <summary>A person in the fake workspace.</summary>
public sealed record FakeUser(string Id, string Name, bool IsAdmin = false);

/// <summary>What the fake workspace looks like and which tokens it accepts. The defaults are what docs/slack.md describes.</summary>
public sealed class FakeSlackOptions
{
    public string BotToken { get; set; } = "xoxb-fake-bot-token";
    public string AppToken { get; set; } = "xapp-fake-app-token";
    public string TeamId { get; set; } = "T0FAKE0001";
    public string TeamName { get; set; } = "Night owls";
    public string BotUserId { get; set; } = "U0PRIKBOT1";
    public string BotName { get; set; } = "prikboard";

    /// <summary>The app's unfurl domains: links on these hosts get <c>link_shared</c>, like the manifest's <c>unfurl_domains</c>.</summary>
    public List<string> UnfurlDomains { get; set; } = ["localhost"];

    public List<FakeChannel> Channels { get; set; } =
    [
        new("C0GENERAL1", "general"),
        new("C0DEVELOP1", "dev"),
        new("C0RANDOM01", "random", IsMember: false),
        new("C0PARTNER1", "partners", IsExtShared: true),
    ];

    public List<FakeUser> Users { get; set; } =
    [
        new("U0ADMIN001", "Ada (admin)", IsAdmin: true),
        new("U0JIMMY001", "Jimmy"),
    ];
}

/// <summary>A message in the fake workspace, with the unfurls the app attached to it.</summary>
public sealed class FakeMessage(string channel, string ts, string user, string text)
{
    public string Channel { get; } = channel;
    public string Ts { get; } = ts;
    public string User { get; } = user;
    public string Text { get; } = text;

    /// <summary>The links that got a <c>link_shared</c> event.</summary>
    public List<string> Links { get; } = [];

    /// <summary>What <c>chat.unfurl</c> attached, per URL.</summary>
    public Dictionary<string, JsonNode?> Unfurls { get; } = [];

    /// <summary>A bot message's attachments (<c>chat.postMessage</c>), as the app sent them.</summary>
    public JsonNode? Attachments { get; init; }

    /// <summary>The files posted with it.</summary>
    public List<FakeFile> Files { get; } = [];

    /// <summary>The bot's reactions on it, by emoji name.</summary>
    public List<string> Reactions { get; } = [];
}

/// <summary>An answer to a slash command, which only the person who ran it sees (<c>response_type: ephemeral</c>).</summary>
/// <param name="Channel">Where the command was run.</param>
/// <param name="User">Who ran it, and who sees the answer.</param>
/// <param name="Command">The command as typed.</param>
/// <param name="Body">What the app sent to the command's <c>response_url</c>.</param>
public sealed record FakeResponse(string Channel, string User, string Command, JsonNode? Body);

/// <summary>A Web API call the app made, for tests and the page: the method and its fields, without the token.</summary>
public sealed record FakeCall(string Method, DateTimeOffset At, Dictionary<string, string> Fields, string? Error);

/// <summary>
/// The fake workspace's state: its messages, the calls the app made, and the Socket Mode connections. Everything is in
/// memory and goes when the process stops.
/// </summary>
public sealed partial class FakeWorkspace(FakeSlackOptions options)
{
    private readonly Lock _lock = new();
    private readonly List<FakeMessage> _messages = [];
    private readonly List<FakeCall> _calls = [];
    private readonly ConcurrentDictionary<string, bool> _tickets = new();
    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();
    private readonly ConcurrentDictionary<string, (string Channel, string User, string Text)> _pendingCommands = new();
    private readonly List<FakeResponse> _responses = [];
    private volatile string? _baseUrl;
    private long _clock = DateTimeOffset.UtcNow.ToUnixTimeSeconds() * 1_000_000;

    public FakeSlackOptions Options { get; } = options;

    [GeneratedRegex(@"https?://[^\s<>|]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    public int Connections => _connections.Count;

    public IReadOnlyList<FakeMessage> Messages
    {
        get
        {
            lock (_lock) return [.. _messages];
        }
    }

    public IReadOnlyList<FakeCall> Calls
    {
        get
        {
            lock (_lock) return [.. _calls];
        }
    }

    public void Record(FakeCall call)
    {
        lock (_lock) _calls.Add(call);
    }

    /// <summary>A ticket for one Socket Mode connection, as <c>apps.connections.open</c> hands out.</summary>
    public string NewTicket()
    {
        var ticket = Guid.NewGuid().ToString("N");
        _tickets[ticket] = true;
        return ticket;
    }

    public bool UseTicket(string ticket) => _tickets.TryRemove(ticket, out _);

    /// <summary>A Slack-style message timestamp: seconds and microseconds, unique and increasing.</summary>
    private string NextTs()
    {
        var micros = Interlocked.Increment(ref _clock);
        return $"{micros / 1_000_000}.{micros % 1_000_000:D6}";
    }

    /// <summary>
    /// Posts a message as a person, as Slack would after they press Enter, and sends <c>link_shared</c> for its links on
    /// the unfurl domains.
    /// </summary>
    public async Task<FakeMessage> PostAsync(string channel, string user, string text, IEnumerable<FakeFile>? files = null)
    {
        var message = new FakeMessage(channel, NextTs(), user, text);
        message.Files.AddRange(files ?? []);
        message.Links.AddRange(UnfurlLinks(text));
        lock (_lock) _messages.Add(message);
        if (message.Links.Count > 0)
        {
            await SendEventAsync(LinkShared(channel, message.Ts, user, message.Links, "conversations_history", Guid.NewGuid().ToString("N")));
        }
        return message;
    }

    /// <summary>
    /// What Slack sends while someone types a link in the message box, before sending it: <c>link_shared</c> with
    /// channel COMPOSER and <c>source: composer</c>. The app is meant to leave these alone.
    /// </summary>
    public async Task<int> ComposeAsync(string user, string text)
    {
        var links = UnfurlLinks(text);
        if (links.Count > 0) await SendEventAsync(LinkShared("COMPOSER", NextTs(), user, links, "composer", Guid.NewGuid().ToString("N")));
        return links.Count;
    }

    /// <summary>A message the bot posted (<c>chat.postMessage</c>).</summary>
    public FakeMessage PostAsBot(string channel, string text, JsonNode? attachments)
    {
        var message = new FakeMessage(channel, NextTs(), Options.BotUserId, text) { Attachments = attachments };
        lock (_lock) _messages.Add(message);
        return message;
    }

    /// <summary>Deletes a message, as someone in Slack would; unfurling it again then fails like Slack's.</summary>
    public bool Delete(string channel, string ts)
    {
        lock (_lock) return _messages.RemoveAll(m => m.Channel == channel && m.Ts == ts) > 0;
    }

    /// <summary>The answers to slash commands, in order.</summary>
    public IReadOnlyList<FakeResponse> Responses
    {
        get
        {
            lock (_lock) return [.. _responses];
        }
    }

    /// <summary>
    /// Runs a slash command as a person, as Slack does when they press Enter on <c>/prikboard …</c>: a
    /// <c>slash_commands</c> envelope whose <c>response_url</c> points back here. False without a connection.
    /// </summary>
    public async Task<bool> CommandAsync(string channel, string user, string text)
    {
        var parts = text.Trim().Split(' ', 2);
        var id = Guid.NewGuid().ToString("N");
        _pendingCommands[id] = (channel, user, text.Trim());
        var connection = _connections.Values.FirstOrDefault();
        if (connection is null || _baseUrl is null) return false;
        await connection.SendAsync(new
        {
            envelope_id = Guid.NewGuid().ToString(),
            type = "slash_commands",
            accepts_response_payload = true,
            payload = new
            {
                token = "fake-verification-token",
                team_id = Options.TeamId,
                team_domain = Options.TeamName.Replace(' ', '-').ToLowerInvariant(),
                channel_id = channel,
                channel_name = Options.Channels.FirstOrDefault(c => c.Id == channel)?.Name ?? channel,
                user_id = user,
                user_name = Options.Users.FirstOrDefault(u => u.Id == user)?.Name ?? user,
                command = parts[0],
                text = parts.Length > 1 ? parts[1] : "",
                api_app_id = "A0FAKEAPP1",
                response_url = $"{_baseUrl}/_fake/responses/{id}",
                trigger_id = Guid.NewGuid().ToString("N"),
            },
        });
        return true;
    }

    /// <summary>Takes an answer for a command's <c>response_url</c>; false for one that isn't known.</summary>
    public bool Respond(string id, JsonNode? body)
    {
        if (!_pendingCommands.TryGetValue(id, out var command)) return false;
        lock (_lock) _responses.Add(new FakeResponse(command.Channel, command.User, command.Text, body));
        return true;
    }

    /// <summary>Attaches unfurls to a message, as <c>chat.unfurl</c> does; the error Slack would give, or null.</summary>
    public string? Unfurl(string channel, string ts, JsonObject unfurls)
    {
        lock (_lock)
        {
            if (_messages.FirstOrDefault(m => m.Channel == channel && m.Ts == ts) is not { } message) return "cannot_find_message";
            foreach (var (url, unfurl) in unfurls)
            {
                if (!message.Links.Contains(url)) return "cannot_unfurl_url";
                message.Unfurls[url] = unfurl?.DeepClone();
            }
            return null;
        }
    }

    private List<string> UnfurlLinks(string text) =>
        UrlPattern().Matches(text)
            .Select(m => m.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']'))
            .Where(url => Uri.TryCreate(url, UriKind.Absolute, out var uri)
                          && Options.UnfurlDomains.Any(d => string.Equals(d, uri.Host, StringComparison.OrdinalIgnoreCase)))
            .Distinct()
            .ToList();

    private object LinkShared(string channel, string ts, string user, IEnumerable<string> links, string source, string unfurlId) => new
    {
        type = "link_shared",
        channel,
        is_bot_user_member = Options.Channels.FirstOrDefault(c => c.Id == channel)?.IsMember ?? false,
        user,
        message_ts = ts,
        unfurl_id = unfurlId,
        source,
        links = links.Select(url => new { domain = new Uri(url).Host, url }).ToList(),
        event_ts = ts,
    };

    /// <summary>Sends an Events API envelope to one open connection, like Slack spreads events over an app's connections.</summary>
    public async Task<bool> SendEventAsync(object e)
    {
        var connection = _connections.Values.FirstOrDefault();
        if (connection is null) return false;
        var envelope = new
        {
            envelope_id = Guid.NewGuid().ToString(),
            type = "events_api",
            accepts_response_payload = false,
            retry_attempt = 0,
            retry_reason = "",
            payload = new
            {
                token = "fake-verification-token",
                team_id = Options.TeamId,
                api_app_id = "A0FAKEAPP1",
                @event = e,
                type = "event_callback",
                event_id = "Ev" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant(),
                event_time = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            },
        };
        await connection.SendAsync(envelope);
        return true;
    }

    /// <summary>Asks every connection to reconnect, as Slack does every few hours (<c>refresh_requested</c>).</summary>
    public async Task<int> DisconnectAllAsync()
    {
        var all = _connections.Values.ToList();
        foreach (var connection in all) await connection.SendAsync(new { type = "disconnect", reason = "refresh_requested" });
        return all.Count;
    }

    /// <summary>Runs one Socket Mode connection: hello, then the app's acks until it closes.</summary>
    public async Task RunConnectionAsync(WebSocket socket, string baseUrl, CancellationToken ct)
    {
        // Where the app reaches the fake, for the response_url of commands.
        _baseUrl = baseUrl;
        var id = Guid.NewGuid();
        var connection = new Connection(socket);
        _connections[id] = connection;
        try
        {
            await connection.SendAsync(new
            {
                type = "hello",
                num_connections = _connections.Count,
                debug_info = new { host = "fake-slack", approximate_connection_time = 18060 },
                connection_info = new { app_id = "A0FAKEAPP1" },
            });
            var buffer = new byte[16 * 1024];
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                    break;
                }
                // Acks ({"envelope_id": …}) need no answer; the fake doesn't redeliver.
            }
        }
        catch (Exception error) when (error is WebSocketException or OperationCanceledException)
        {
            // The app went away.
        }
        finally
        {
            _connections.TryRemove(id, out _);
        }
    }

    private sealed class Connection(WebSocket socket)
    {
        private readonly SemaphoreSlim _send = new(1, 1);

        public async Task SendAsync(object message)
        {
            await _send.WaitAsync();
            try
            {
                if (socket.State == WebSocketState.Open)
                {
                    await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text, true, CancellationToken.None);
                }
            }
            catch (WebSocketException)
            {
                // Closed meanwhile.
            }
            finally
            {
                _send.Release();
            }
        }
    }
}
