using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Integrations;

/// <summary>
/// The Slack app's settings: <c>Slack:BotToken</c>, <c>Slack:AppToken</c> and <c>Slack:PublicUrl</c> from configuration
/// (fixed, and they win), else what an admin saved (<see cref="SlackAppSettings"/>). Loaded at startup and after every
/// change, like <see cref="DiscordBot"/>, and handed to the <see cref="ISlackGateway"/>, so new tokens need no restart.
/// </summary>
public sealed class SlackApp(IConfiguration config, IDataProtectionProvider protection, ISlackGateway gateway, ILogger<SlackApp> logger)
{
    /// <summary>The Data Protection purpose of the stored tokens.</summary>
    public const string TokenPurpose = "PrikBoard.SlackApp";

    public const string BotTokenSetting = "Slack:BotToken";
    public const string AppTokenSetting = "Slack:AppToken";
    public const string PublicUrlSetting = "Slack:PublicUrl";

    /// <summary>
    /// The bot scopes the app asks for: unfurling links (<c>links:read</c>, <c>links:write</c>), listing channels for board
    /// owners and checking for Slack Connect (<c>channels:read</c>, <c>groups:read</c>), the workspace's name, posting a
    /// board's updates (<c>chat:write</c>) and <c>/prikboard</c> (<c>commands</c>). An app created before the last two
    /// were added needs them added and reinstalling.
    /// </summary>
    public static readonly string[] Scopes = ["links:read", "links:write", "channels:read", "groups:read", "team:read", "chat:write", "commands"];

    /// <summary>The slash command the app answers.</summary>
    public const string Command = "/prikboard";

    private readonly IDataProtector _tokens = protection.CreateProtector(TokenPurpose);

    /// <summary>The settings in use.</summary>
    public Settings Current { get; private set; } = new(null, null, null, null, null, false, false);

    /// <summary>Whether the tokens come from configuration, so the admin page can't change them.</summary>
    public bool TokensAreConfigured => !string.IsNullOrWhiteSpace(config[BotTokenSetting]) || !string.IsNullOrWhiteSpace(config[AppTokenSetting]);

    /// <summary>Whether the public URL comes from configuration.</summary>
    public bool PublicUrlIsConfigured => !string.IsNullOrWhiteSpace(config[PublicUrlSetting]);

    public string ProtectToken(string token) => _tokens.Protect(token);

    /// <summary>Reads the settings and brings the app in line; at startup and after every change.</summary>
    public async Task LoadAsync(PrikBoardDbContext db, CancellationToken ct)
    {
        var row = await db.SlackAppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == SlackAppSettings.SingleId, ct);
        string? botToken = null, appToken = null;
        if (TokensAreConfigured)
        {
            botToken = config[BotTokenSetting]?.Trim();
            appToken = config[AppTokenSetting]?.Trim();
        }
        else if (row is { ProtectedBotToken: { } bot, ProtectedAppToken: { } app })
        {
            try
            {
                (botToken, appToken) = (_tokens.Unprotect(bot), _tokens.Unprotect(app));
            }
            catch (CryptographicException error)
            {
                // The Data Protection keys were lost or replaced; an admin can save the tokens again.
                logger.LogError(error, "The Slack app's tokens can't be decrypted; the app is off");
            }
        }
        if (string.IsNullOrEmpty(botToken) || string.IsNullOrEmpty(appToken)) (botToken, appToken) = (null, null);

        var publicUrl = PublicUrlIsConfigured ? config[PublicUrlSetting]!.Trim() : row?.PublicUrl;
        Current = new Settings(publicUrl, row?.TeamId, row?.TeamName, row?.BotName, row?.UpdatedAt, botToken is not null, row?.ProtectedBotToken is not null);
        await gateway.ApplyAsync(botToken, appToken, publicUrl);
    }

    /// <summary>
    /// The manifest to create the Slack app from: Socket Mode, the <c>link_shared</c> event, <see cref="Command"/>, <see cref="Scopes"/>, and the
    /// public address's host as its unfurl domain (Slack only sends links on those domains).
    /// </summary>
    public static string Manifest(string? publicUrl)
    {
        var host = Uri.TryCreate(publicUrl, UriKind.Absolute, out var uri) ? uri.Host : "board.example.com";
        var manifest = new Dictionary<string, object>
        {
            ["display_information"] = new Dictionary<string, object>
            {
                ["name"] = "PrikBoard",
                ["description"] = "Previews of PrikBoard cards and boards",
                ["background_color"] = "#14161a",
            },
            ["features"] = new Dictionary<string, object>
            {
                ["bot_user"] = new Dictionary<string, object> { ["display_name"] = "PrikBoard", ["always_online"] = true },
                ["unfurl_domains"] = new[] { host },
                ["slash_commands"] = new[]
                {
                    new Dictionary<string, object>
                    {
                        ["command"] = Command,
                        ["description"] = "What is on the PrikBoard board connected to this channel, only for you",
                        ["usage_hint"] = "board",
                        ["should_escape"] = false,
                    },
                },
            },
            ["oauth_config"] = new Dictionary<string, object>
            {
                ["scopes"] = new Dictionary<string, object> { ["bot"] = Scopes },
            },
            ["settings"] = new Dictionary<string, object>
            {
                ["event_subscriptions"] = new Dictionary<string, object> { ["bot_events"] = new[] { "link_shared" } },
                ["org_deploy_enabled"] = false,
                ["socket_mode_enabled"] = true,
                ["token_rotation_enabled"] = false,
            },
        };
        return JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>The page that creates a Slack app with <see cref="Manifest"/> filled in.</summary>
    public static string CreateAppUrl(string? publicUrl) =>
        "https://api.slack.com/apps?new_app=1&manifest_json=" + Uri.EscapeDataString(Manifest(publicUrl));

    /// <summary>What the app runs with; never the tokens themselves.</summary>
    /// <param name="PublicUrl">The address links are matched against.</param>
    /// <param name="TeamId">The saved bot token's workspace, as Slack said when it was saved.</param>
    /// <param name="TeamName">Its name then.</param>
    /// <param name="BotName">The bot's name then.</param>
    /// <param name="UpdatedAt">When an admin last saved the settings.</param>
    /// <param name="HasTokens">Whether the app has both tokens to run with.</param>
    /// <param name="HasStoredTokens">Whether an admin saved them (configuration may still win).</param>
    public sealed record Settings(
        string? PublicUrl, string? TeamId, string? TeamName, string? BotName, DateTimeOffset? UpdatedAt, bool HasTokens, bool HasStoredTokens);
}
