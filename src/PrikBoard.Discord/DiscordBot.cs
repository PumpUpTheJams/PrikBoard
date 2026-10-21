using System.Text;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Options;

namespace PrikBoard.Discord;

public sealed class BotOptions
{
    /// <summary>The bot's token from the Discord developer portal. Belongs to the bot only; the API never sees it.</summary>
    public string BotToken { get; set; } = "";
}

public sealed class PrikBoardOptions
{
    /// <summary>Where the bot reaches the API, e.g. http://api:8080 in compose.</summary>
    public string ApiUrl { get; set; } = "";

    /// <summary>The PrikBoard address people paste links from (the API's Discord:PublicUrl).</summary>
    public string PublicUrl { get; set; } = "";
}

/// <summary>
/// The PrikBoard Discord bot (docs/discord.md). It connects out to Discord's gateway, so it needs no inbound port, and
/// reaches PrikBoard over HTTP only, with each server's own token. /prikboard connect, finish, status and disconnect
/// manage the connection; every message with PrikBoard links gets one reply with an embed per link the server may see.
/// </summary>
public sealed class DiscordBot(
    IOptions<BotOptions> bot, IOptions<PrikBoardOptions> prikboard, PrikBoardApi api, GuildTokens tokens, PendingConnects pending,
    TimeProvider time, ILogger<DiscordBot> logger) : BackgroundService
{
    /// <summary>/prikboard status warns this long before the token ends; connecting again renews it.</summary>
    private static readonly TimeSpan RenewWarning = TimeSpan.FromDays(14);

    private readonly LinkFinder _links = new(prikboard.Value.PublicUrl);
    private readonly Embeds _embeds = new(prikboard.Value.PublicUrl);
    private readonly string _site = prikboard.Value.PublicUrl.TrimEnd('/');

    private DiscordSocketClient? _client;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Message Content is privileged: free in under 100 servers, verification by Discord above that.
        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages | GatewayIntents.MessageContent,
        });
        _client.Log += Log;
        _client.Ready += RegisterCommandsAsync;
        _client.SlashCommandExecuted += command => Background(() => HandleCommandAsync(command, stoppingToken));
        _client.MessageReceived += message => Background(() => HandleMessageAsync(message, stoppingToken));

        await _client.LoginAsync(TokenType.Bot, bot.Value.BotToken);
        await _client.StartAsync();
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
        await _client.StopAsync();
        await _client.LogoutAsync();
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
            catch (Exception e)
            {
                logger.LogError(e, "Handling a Discord event failed.");
            }
        });
        return Task.CompletedTask;
    }

    private async Task RegisterCommandsAsync()
    {
        var command = new SlashCommandBuilder()
            .WithName("prikboard")
            .WithDescription("Preview PrikBoard links in this server")
            .WithContextTypes(InteractionContextType.Guild)
            // Shown to admins by default; the handler checks Manage Server itself too.
            .WithDefaultMemberPermissions(GuildPermission.ManageGuild)
            .AddOption(new SlashCommandOptionBuilder().WithName("connect").WithType(ApplicationCommandOptionType.SubCommand)
                .WithDescription("Connect PrikBoard boards to this channel or server"))
            .AddOption(new SlashCommandOptionBuilder().WithName("finish").WithType(ApplicationCommandOptionType.SubCommand)
                .WithDescription("Finish connecting with the code PrikBoard showed")
                .AddOption("code", ApplicationCommandOptionType.String, "The code from PrikBoard's connect page", isRequired: true))
            .AddOption(new SlashCommandOptionBuilder().WithName("status").WithType(ApplicationCommandOptionType.SubCommand)
                .WithDescription("Which boards this server previews"))
            .AddOption(new SlashCommandOptionBuilder().WithName("disconnect").WithType(ApplicationCommandOptionType.SubCommand)
                .WithDescription("Stop previewing PrikBoard links in this server"))
            .Build();
        await _client!.BulkOverwriteGlobalApplicationCommandsAsync([command]);
    }

    private async Task HandleCommandAsync(SocketSlashCommand command, CancellationToken ct)
    {
        if (command.CommandName != "prikboard") return;
        if (command.GuildId is not { } guildId || command.User is not SocketGuildUser user)
        {
            await command.RespondAsync("Use this in a server.", ephemeral: true);
            return;
        }
        // Discord puts the member's permissions in the interaction, so this is what Discord vouches for.
        if (!user.GuildPermissions.ManageGuild)
        {
            await command.RespondAsync("Only people with the Manage Server permission can do this.", ephemeral: true);
            return;
        }

        var sub = command.Data.Options.First();
        switch (sub.Name)
        {
            case "connect":
                await ConnectAsync(command, guildId);
                break;
            case "finish":
                await command.DeferAsync(ephemeral: true);
                await command.FollowupAsync(await FinishAsync(command, guildId, (string)sub.Options.First().Value, ct), ephemeral: true);
                break;
            case "status":
                await command.DeferAsync(ephemeral: true);
                await command.FollowupAsync(await StatusAsync(guildId, ct), ephemeral: true);
                break;
            case "disconnect":
                await command.DeferAsync(ephemeral: true);
                await command.FollowupAsync(await DisconnectAsync(guildId, ct), ephemeral: true);
                break;
        }
    }

    private async Task ConnectAsync(SocketSlashCommand command, ulong guildId)
    {
        var channelId = command.ChannelId ?? 0;
        var channelName = (_client!.GetChannel(channelId) as IGuildChannel)?.Name ?? channelId.ToString();
        // No API call yet: the board owner approves on PrikBoard, and the code comes back through /prikboard finish.
        var challenge = pending.Start(guildId, channelId, channelName);
        var link = $"{_site}/#/connect/discord?code_challenge={challenge}";
        await command.RespondAsync(
            $"1. A PrikBoard board owner opens {link}, signs in and picks the boards to preview here, in #{channelName} only or in the whole server.\n" +
            "2. Run `/prikboard finish` here, in this server, with the code PrikBoard shows.\n" +
            $"The link works for {PendingConnects.Lifetime.TotalMinutes:0} minutes. Only send it to someone you trust with the boards' titles and descriptions.",
            ephemeral: true);
    }

    private async Task<string> FinishAsync(SocketSlashCommand command, ulong guildId, string code, CancellationToken ct)
    {
        if (pending.Take(guildId) is not { } started)
        {
            return "Run `/prikboard connect` in this server first; it works for 10 minutes. A code started in another server doesn't work here.";
        }

        var guildName = _client!.GetGuild(guildId)?.Name ?? guildId.ToString();
        try
        {
            var connected = await api.ExchangeAsync(code.Trim(), started.Verifier, guildId.ToString(), guildName,
                started.ChannelId.ToString(), started.ChannelName, ct);
            tokens.Set(guildId, connected.Token);
            logger.LogInformation("Connected server {Guild} to {Boards} board(s).", guildId, connected.Boards.Count);
            return $"Connected **{Escape(guildName)}** to PrikBoard.\n{Describe(connected.Boards)}";
        }
        catch (PrikBoardException e)
        {
            return $"Not connected: {e.Message}";
        }
    }

    private async Task<string> StatusAsync(ulong guildId, CancellationToken ct)
    {
        if (tokens.Get(guildId) is not { } token) return "This server isn't connected to PrikBoard. Run `/prikboard connect` to connect it.";
        try
        {
            var status = await api.StatusAsync(token, ct);
            var text = new StringBuilder(Describe(status.Boards));
            var expires = status.TokenExpiresAt.ToUnixTimeSeconds();
            text.Append($"\nThe connection expires <t:{expires}:D>.");
            if (status.TokenExpiresAt - time.GetUtcNow() < RenewWarning)
            {
                text.Append(" Run `/prikboard connect` again soon to renew it.");
            }
            return text.ToString();
        }
        catch (TokenRejectedException)
        {
            tokens.Remove(guildId);
            return "PrikBoard no longer accepts this server's connection (it was revoked or expired). Run `/prikboard connect` to connect again.";
        }
    }

    private async Task<string> DisconnectAsync(ulong guildId, CancellationToken ct)
    {
        if (tokens.Get(guildId) is not { } token) return "This server isn't connected to PrikBoard.";
        await api.RevokeAsync(token, ct);
        tokens.Remove(guildId);
        return "Disconnected. The bot no longer previews PrikBoard links here. Previews already posted stay; " +
               "board owners can also remove the server from their boards' members.";
    }

    private static string Describe(List<DiscordBoardConnection> boards)
    {
        if (boards.Count == 0) return "It previews no boards: owners removed it from them. Run `/prikboard connect` to add boards.";
        var text = new StringBuilder("It previews links to:");
        foreach (var board in boards)
        {
            var where = board.Channels is null
                ? "every channel"
                : board.Channels.Count == 0 ? "no channel" : string.Join(", ", board.Channels.Select(c => $"<#{c.Id}>"));
            text.Append($"\n• **{Escape(board.BoardName)}** in {where}");
        }
        return text.ToString();
    }

    private async Task HandleMessageAsync(SocketMessage message, CancellationToken ct)
    {
        // Never answer bots, ourselves included.
        if (message is not SocketUserMessage { Author.IsBot: false } user || user.Channel is not SocketGuildChannel channel) return;
        var links = _links.Find(user.Content);
        if (links.Count == 0) return;
        // A server without a token gets no previews, and no API call.
        if (tokens.Get(channel.Guild.Id) is not { } token) return;

        LinkPreview?[] previews;
        try
        {
            previews = await Task.WhenAll(links.Select(link => api.PreviewAsync(token, link, channel.Id.ToString(), ct)));
        }
        catch (TokenRejectedException)
        {
            logger.LogWarning("PrikBoard rejected the token of server {Guild}; forgetting it.", channel.Guild.Id);
            tokens.Remove(channel.Guild.Id);
            return;
        }

        // Silent about links without a preview: saying "no access" would tell that the board exists.
        var embeds = links.Zip(previews).Where(p => p.Second is not null).Select(p => _embeds.For(p.Second!, p.First)).ToArray();
        if (embeds.Length == 0) return;

        await user.ReplyAsync(embeds: embeds, allowedMentions: AllowedMentions.None);

        // Remove Discord's own "not signed in" preview from the message we answered, when we may.
        if (channel.Guild.CurrentUser.GetPermissions(channel).ManageMessages)
        {
            await user.ModifyAsync(m => m.Flags = MessageFlags.SuppressEmbeds);
        }
    }

    private static string Escape(string text) => Format.Sanitize(text);

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
}
