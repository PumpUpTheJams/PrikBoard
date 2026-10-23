using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Integrations;

/// <summary>
/// The Discord bot's settings: <c>Discord:BotToken</c> and <c>Discord:PublicUrl</c> from configuration (fixed, and they
/// win), else what an admin saved (<see cref="DiscordBotSettings"/>). Loaded at startup and after every change, like
/// <see cref="Auth.SignInProviders"/>, and handed to the <see cref="IDiscordGateway"/>, so a new token needs no restart.
/// </summary>
public sealed class DiscordBot(IConfiguration config, IDataProtectionProvider protection, IDiscordGateway gateway, ILogger<DiscordBot> logger)
{
    /// <summary>The Data Protection purpose of the stored token.</summary>
    public const string TokenPurpose = "PrikBoard.DiscordBot";

    public const string TokenSetting = "Discord:BotToken";
    public const string PublicUrlSetting = "Discord:PublicUrl";

    /// <summary>The bot's permissions in the invite link: View Channels, Send Messages, Manage Messages (optional: it hides Discord's own preview), Embed Links and Read Message History.</summary>
    public const long Permissions = 93184;

    private readonly IDataProtector _tokens = protection.CreateProtector(TokenPurpose);

    /// <summary>The settings in use.</summary>
    public Settings Current { get; private set; } = new(null, null, null, null, false, false);

    /// <summary>Whether the token comes from configuration, so the admin page can't change it.</summary>
    public bool TokenIsConfigured => !string.IsNullOrWhiteSpace(config[TokenSetting]);

    /// <summary>Whether the public URL comes from configuration.</summary>
    public bool PublicUrlIsConfigured => !string.IsNullOrWhiteSpace(config[PublicUrlSetting]);

    public string ProtectToken(string token) => _tokens.Protect(token);

    /// <summary>Reads the settings and brings the bot in line; at startup and after every change.</summary>
    public async Task LoadAsync(PrikBoardDbContext db, CancellationToken ct)
    {
        var row = await db.DiscordBotSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        string? token = null;
        if (TokenIsConfigured)
        {
            token = config[TokenSetting]!.Trim();
        }
        else if (row?.ProtectedBotToken is { } protectedToken)
        {
            try
            {
                token = _tokens.Unprotect(protectedToken);
            }
            catch (CryptographicException error)
            {
                // The Data Protection keys were lost or replaced; an admin can save the token again.
                logger.LogError(error, "The Discord bot's token can't be decrypted; the bot is off");
            }
        }

        var publicUrl = PublicUrlIsConfigured ? config[PublicUrlSetting]!.Trim() : row?.PublicUrl;
        Current = new Settings(publicUrl, row?.ApplicationId, row?.BotName, row?.UpdatedAt, token is not null, row?.ProtectedBotToken is not null);
        await gateway.ApplyAsync(token, publicUrl);
    }

    /// <summary>The link that adds the bot to a server, once its application is known.</summary>
    public string? InviteUrl => (gateway.Application?.Id ?? Current.ApplicationId) is { } id
        ? $"https://discord.com/oauth2/authorize?client_id={id}&scope=bot&permissions={Permissions}"
        : null;

    /// <summary>What the bot runs with; never the token itself.</summary>
    /// <param name="PublicUrl">The address links are matched against.</param>
    /// <param name="ApplicationId">The stored token's application, as Discord said when it was saved.</param>
    /// <param name="BotName">The stored token's bot name then.</param>
    /// <param name="UpdatedAt">When an admin last saved the settings.</param>
    /// <param name="HasToken">Whether the bot has a token to run with.</param>
    /// <param name="HasStoredToken">Whether an admin saved one (configuration may still win).</param>
    public sealed record Settings(string? PublicUrl, string? ApplicationId, string? BotName, DateTimeOffset? UpdatedAt, bool HasToken, bool HasStoredToken);
}
