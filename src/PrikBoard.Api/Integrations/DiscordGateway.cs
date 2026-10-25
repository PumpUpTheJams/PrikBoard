using System.Collections.Concurrent;
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
public sealed record DiscordServerInfo(string Id, string Name, IReadOnlyList<ChatChannel> Channels);

/// <summary>The Discord bot's connection, as the rest of PrikBoard sees it; tests replace it with a fake.</summary>
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

    /// <summary>Posts embeds in a channel; the message's id, or null when the bot can't post there.</summary>
    Task<string?> PostAsync(string channelId, IReadOnlyList<Embed> embeds);

    /// <summary>Replaces the embeds of one of the bot's messages; false when the message is gone.</summary>
    Task<bool> EditAsync(string channelId, string messageId, IReadOnlyList<Embed> embeds);
}

/// <summary>
/// The Discord bot itself, inside the API (docs/discord.md). It connects out to Discord's gateway, so it needs no inbound
/// port. It answers messages with previews of links and readable ids (<see cref="ChatPreviews"/>), offers Save to
/// PrikBoard on messages (<see cref="ChatSaves"/>) and /prikboard board, and posts and edits what
/// <see cref="ChatActivity"/> passes on. <see cref="DiscordBot"/> gives it its token and public URL, at startup and
/// whenever an admin changes them. One instance only, like the rest of PrikBoard: two would answer every message twice.
/// </summary>
public sealed class DiscordGateway(IServiceScopeFactory scopes, IConfiguration config, ILogger<DiscordGateway> logger)
    : IDiscordGateway, IHostedService
{
    /// <summary>The message command that saves a message as a card.</summary>
    public const string SaveCommand = "Save as card";

    /// <summary>Close codes after which Discord won't take the bot back until something changes.</summary>
    private static readonly Dictionary<int, string> Fatal = new()
    {
        [4004] = "Discord doesn't accept the token any more. Reset it on the Bot tab of your application and save the new one.",
        [4013] = "Discord refused the bot's intents.",
        [4014] = "Turn on Message Content Intent on the Bot tab of your application: without it the bot can't see pasted links.",
    };

    /// <summary>How long Save as card's choices wait for the Save button.</summary>
    private static readonly TimeSpan DraftLifetime = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stopping = new();
    private readonly ConcurrentDictionary<string, SaveDraft> _drafts = new();

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
            client.MessageCommandExecuted += command => Background(() => SaveCommandAsync(command, _stopping.Token));
            client.SlashCommandExecuted += command => Background(() => SlashCommandAsync(command, _stopping.Token));
            client.SelectMenuExecuted += component => Background(() => DraftChoiceAsync(component));
            client.ButtonExecuted += component => Background(() => DraftButtonAsync(component, _stopping.Token));
            client.ModalSubmitted += modal => Background(() => TitleSubmittedAsync(modal));
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

    public async Task<string?> PostAsync(string channelId, IReadOnlyList<Embed> embeds)
    {
        if (!ulong.TryParse(channelId, out var id) || _client?.GetChannel(id) is not IMessageChannel channel) return null;
        try
        {
            var message = await channel.SendMessageAsync(embeds: [.. embeds], allowedMentions: AllowedMentions.None);
            return message.Id.ToString();
        }
        catch (HttpException error)
        {
            logger.LogInformation(error, "The Discord bot could not post in channel {Channel}.", channelId);
            return null;
        }
    }

    public async Task<bool> EditAsync(string channelId, string messageId, IReadOnlyList<Embed> embeds)
    {
        if (!ulong.TryParse(channelId, out var id) || !ulong.TryParse(messageId, out var message)) return false;
        // A channel the bot can't see any more keeps its message, but the bot can't follow it either.
        if (_client?.GetChannel(id) is not ITextChannel channel) return _client is null;
        try
        {
            await channel.ModifyMessageAsync(message, m => m.Embeds = embeds.ToArray());
            return true;
        }
        catch (HttpException error) when (error.HttpCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden)
        {
            return false;
        }
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
        await client.BulkOverwriteGlobalApplicationCommandsAsync(
        [
            new MessageCommandBuilder().WithName(SaveCommand).WithContextTypes(InteractionContextType.Guild).Build(),
            new SlashCommandBuilder()
                .WithName("prikboard")
                .WithDescription("PrikBoard in this channel")
                .WithContextTypes(InteractionContextType.Guild)
                .AddOption(new SlashCommandOptionBuilder().WithName("board").WithType(ApplicationCommandOptionType.SubCommand)
                    .WithDescription("What is on the board connected to this channel, only for you"))
                .Build(),
        ]);
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
            .Select(c => new ChatChannel(c.Id.ToString(), c.Name))
            .ToList();
        return new DiscordServerInfo(guild.Id.ToString(), guild.Name, channels);
    }

    /// <summary>A thread counts as its channel.</summary>
    private static string ChannelOf(IChannel channel) =>
        (channel is SocketThreadChannel { ParentChannel: { } parent } ? parent.Id : channel.Id).ToString();

    private async Task HandleMessageAsync(SocketMessage message, CancellationToken ct)
    {
        // Never answer bots, this one included.
        if (message is not SocketUserMessage { Author.IsBot: false } user || user.Channel is not SocketGuildChannel channel) return;
        if (_site is not { } site) return;
        if (LinkPreviews.Sources(user.Content, site.Links).Count == 0) return;

        var guildId = channel.Guild.Id.ToString();
        var channelId = ChannelOf(channel);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
        var shown = await ChatPreviews.ForMessageAsync(db, ChatPlatform.Discord, guildId, channelId, user.Content, site.Links, site.Url, ct,
            allow: () =>
            {
                using var lease = _previews.AttemptAcquire(guildId);
                return lease.IsAcquired;
            });
        // Silent about sources without a preview: saying "no access" would tell that the board exists.
        if (shown.Count == 0) return;

        var reply = await user.ReplyAsync(embeds: shown.Select(s => site.Embeds.For(s.Preview, s.Source)).ToArray(), allowedMentions: AllowedMentions.None);
        await ChatPreviews.RememberAsync(db, ChatPlatform.Discord, reply.Id.ToString(), guildId, channelId, shown, ct);

        // Remove Discord's own "not signed in" preview from the message answered, when the bot may.
        if (channel.Guild.CurrentUser?.GetPermissions(channel).ManageMessages == true && user.Embeds.Count > 0)
        {
            await user.ModifyAsync(m => m.Flags = MessageFlags.SuppressEmbeds);
        }
    }

    private static string NameOf(IUser user) =>
        (user as IGuildUser)?.DisplayName ?? user.GlobalName ?? user.Username;

    /// <summary>Save as card on a message: who you are, then where it goes (<see cref="SaveDraft"/>).</summary>
    private async Task SaveCommandAsync(SocketMessageCommand command, CancellationToken ct)
    {
        if (command.CommandName != SaveCommand) return;
        if (_site is not { } site || command.GuildId is not { } guild || command.Data.Message.Channel is not IGuildChannel channel)
        {
            await command.RespondAsync($"{SaveCommand} works in servers only.", ephemeral: true);
            return;
        }
        await command.DeferAsync(ephemeral: true);

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
        var accounts = scope.ServiceProvider.GetRequiredService<ChatAccounts>();
        if (await accounts.FindUserAsync(db, ChatPlatform.Discord, command.User.Id.ToString(), ct) is not { } userId)
        {
            var code = accounts.StartLink(ChatPlatform.Discord, command.User.Id.ToString(), NameOf(command.User));
            await command.FollowupAsync(
                $"PrikBoard doesn't know you yet. Connect your PrikBoard account once, then use {SaveCommand} again.",
                components: new ComponentBuilder()
                    .WithButton("Connect your PrikBoard account", style: ButtonStyle.Link, url: $"{site.Url}/#/connect/discord-account?code={code}")
                    .Build(),
                ephemeral: true);
            return;
        }

        var channelId = ChannelOf(channel);
        var targets = await ChatSaves.TargetsAsync(db, userId, ChatPlatform.Discord, guild.ToString(), channelId, ct);
        if (targets.Count == 0)
        {
            await command.FollowupAsync(
                "There is no board here you can add cards to. A board owner connects boards to this server under Integrations in PrikBoard.",
                ephemeral: true);
            return;
        }

        var message = command.Data.Message;
        var saved = new SavedMessage(message.CleanContent, NameOf(message.Author), channel.Name, message.GetJumpUrl(), message.Timestamp,
            message.Attachments.Select(a => new SavedAttachment(a.Url, a.Filename, a.ContentType, a.Size)).ToList());
        var token = Guid.NewGuid().ToString("N");
        foreach (var (key, old) in _drafts) if (old.ExpiresAt <= DateTimeOffset.UtcNow) _drafts.TryRemove(key, out _);
        var draft = new SaveDraft(command.User.Id, userId, saved, message, targets, DateTimeOffset.UtcNow + DraftLifetime)
        {
            BoardId = targets[0].BoardId,
            BucketId = targets[0].DefaultBucketId,
            Title = ChatSaves.TitleFor(saved),
        };
        _drafts[token] = draft;
        await command.FollowupAsync(draft.Text(), components: draft.Components(token), ephemeral: true);
    }

    /// <summary>The board or bucket picked in a draft.</summary>
    private async Task DraftChoiceAsync(SocketMessageComponent component)
    {
        if (DraftOf(component.Data.CustomId) is not var (token, kind, draft) || draft is null) return;
        var value = component.Data.Values.FirstOrDefault();
        if (kind == "board" && Guid.TryParse(value, out var boardId) && draft.Targets.FirstOrDefault(t => t.BoardId == boardId) is { } target)
        {
            draft.BoardId = target.BoardId;
            draft.BucketId = target.DefaultBucketId;
        }
        else if (kind == "bucket" && Guid.TryParse(value, out var bucketId))
        {
            draft.BucketId = bucketId;
        }
        await component.UpdateAsync(m =>
        {
            m.Content = draft.Text();
            m.Components = draft.Components(token);
        });
    }

    private async Task DraftButtonAsync(SocketMessageComponent component, CancellationToken ct)
    {
        if (DraftOf(component.Data.CustomId) is not var (token, kind, draft)) return;
        if (draft is null)
        {
            await component.UpdateAsync(m =>
            {
                m.Content = $"This took too long; use {SaveCommand} again.";
                m.Components = new ComponentBuilder().Build();
            });
            return;
        }
        switch (kind)
        {
            case "title":
                await component.RespondWithModalAsync(new ModalBuilder()
                    .WithTitle("Card title")
                    .WithCustomId($"pb-save:title:{token}")
                    .AddTextInput("Title", "title", TextInputStyle.Short, value: draft.Title, maxLength: 200, required: true)
                    .Build());
                break;
            case "cancel":
                _drafts.TryRemove(token, out _);
                await component.UpdateAsync(m =>
                {
                    m.Content = "Not saved.";
                    m.Components = new ComponentBuilder().Build();
                });
                break;
            case "save":
                _drafts.TryRemove(token, out _);
                await component.DeferAsync(ephemeral: true);
                await SaveDraftAsync(component, draft, ct);
                break;
        }
    }

    private async Task TitleSubmittedAsync(SocketModal modal)
    {
        if (DraftOf(modal.Data.CustomId) is not var (token, _, draft) || draft is null) return;
        if (modal.Data.Components.FirstOrDefault(c => c.CustomId == "title")?.Value is { } title && title.Trim().Length > 0)
        {
            draft.Title = title.Trim();
        }
        await modal.UpdateAsync(m =>
        {
            m.Content = draft.Text();
            m.Components = draft.Components(token);
        });
    }

    private async Task SaveDraftAsync(SocketMessageComponent component, SaveDraft draft, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
        var saves = scope.ServiceProvider.GetRequiredService<ChatSaves>();
        var (card, problem) = await saves.SaveAsync(db, draft.UserId, draft.BoardId, draft.BucketId, draft.Title, draft.Message, ct);
        if (card is null || _site is not { } site)
        {
            await component.ModifyOriginalResponseAsync(m =>
            {
                m.Content = $"Not saved: {problem}";
                m.Components = new ComponentBuilder().Build();
            });
            return;
        }

        var board = draft.Targets.First(t => t.BoardId == draft.BoardId);
        var preview = await LinkPreviews.CardAsync(db, (await db.Cards.FindAsync([card.Id], ct))!, (await db.Boards.FindAsync([board.BoardId], ct))!, ct);
        await component.ModifyOriginalResponseAsync(m =>
        {
            m.Content = $"Saved as **{card.ReadableId}** on {Format.Sanitize(board.Name)}.";
            m.Embeds = new[] { site.Embeds.For(preview, card.Path) };
            m.Components = new ComponentBuilder().WithButton("Open in PrikBoard", style: ButtonStyle.Link, url: site.Url + card.Path).Build();
        });
        // A pin on the message tells the channel it is on a board now.
        try
        {
            await draft.Source.AddReactionAsync(new Emoji("📌"));
        }
        catch (HttpException)
        {
            // Without Add Reactions the save still stands.
        }
    }

    /// <summary>/prikboard board: the board connected to this channel, only for whoever asked.</summary>
    private async Task SlashCommandAsync(SocketSlashCommand command, CancellationToken ct)
    {
        if (command.CommandName != "prikboard") return;
        if (_site is not { } site || command.Channel is not IGuildChannel channel || command.GuildId is not { } guild)
        {
            await command.RespondAsync("Use this in a server channel.", ephemeral: true);
            return;
        }
        await command.DeferAsync(ephemeral: true);
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
        var boards = await ChatPreviews.BoardsInChannelAsync(db, ChatPlatform.Discord, guild.ToString(), ChannelOf(channel), ct);
        if (boards.Count == 0)
        {
            await command.FollowupAsync("No PrikBoard board is connected to this channel. A board owner connects one under Integrations.", ephemeral: true);
            return;
        }
        var snapshot = await ChatPreviews.SnapshotAsync(db, boards[0], DateTimeOffset.UtcNow, ct);
        var others = boards.Count > 1 ? $"Also here: {string.Join(", ", boards.Skip(1).Select(b => Format.Sanitize(b.Name)))}" : null;
        await command.FollowupAsync(others, embed: site.Embeds.Snapshot(snapshot), ephemeral: true);
    }

    /// <summary>The draft a component's custom id ("pb-save:{kind}:{token}") names; its draft is null once gone.</summary>
    private (string Token, string Kind, SaveDraft? Draft)? DraftOf(string customId)
    {
        var parts = customId.Split(':');
        if (parts is not ["pb-save", var kind, var token]) return null;
        return (token, kind, _drafts.TryGetValue(token, out var draft) && draft.ExpiresAt > DateTimeOffset.UtcNow ? draft : null);
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
        public string Url { get; } = url.TrimEnd('/');
        public LinkFinder Links { get; } = new(url);
        public Embeds Embeds { get; } = new(url);
    }

    /// <summary>A message being saved: who saves it, what it is, and the board, bucket and title picked so far.</summary>
    private sealed class SaveDraft(ulong discordUserId, Guid userId, SavedMessage message, IMessage source, List<SaveTarget> targets, DateTimeOffset expiresAt)
    {
        public ulong DiscordUserId { get; } = discordUserId;
        public Guid UserId { get; } = userId;
        public SavedMessage Message { get; } = message;
        public IMessage Source { get; } = source;
        public List<SaveTarget> Targets { get; } = targets;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public Guid BoardId { get; set; }
        public Guid? BucketId { get; set; }
        public required string Title { get; set; }

        public string Text() => $"Save **{Format.Sanitize(Title)}** to PrikBoard:";

        public MessageComponent Components(string token)
        {
            var target = Targets.First(t => t.BoardId == BoardId);
            var components = new ComponentBuilder();
            if (Targets.Count > 1)
            {
                components.WithSelectMenu(new SelectMenuBuilder().WithCustomId($"pb-save:board:{token}").WithPlaceholder("Board")
                    .WithOptions(Targets.Take(25).Select(t => new SelectMenuOptionBuilder(Cut(t.Name), t.BoardId.ToString(), isDefault: t.BoardId == BoardId)).ToList()));
            }
            if (target.Buckets.Count > 0)
            {
                components.WithSelectMenu(new SelectMenuBuilder().WithCustomId($"pb-save:bucket:{token}").WithPlaceholder("Bucket")
                    .WithOptions(target.Buckets.Take(25).Select(b => new SelectMenuOptionBuilder(Cut(b.Name), b.Id.ToString(), isDefault: b.Id == BucketId)).ToList()));
            }
            return components
                .WithButton("Save", $"pb-save:save:{token}", ButtonStyle.Success)
                .WithButton("Edit title", $"pb-save:title:{token}", ButtonStyle.Secondary)
                .WithButton("Cancel", $"pb-save:cancel:{token}", ButtonStyle.Secondary)
                .Build();
        }

        private static string Cut(string text) => text.Length <= 100 ? text : text[..99] + "…";
    }
}
