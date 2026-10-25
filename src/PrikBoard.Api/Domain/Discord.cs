namespace PrikBoard.Api.Domain;

/// <summary>
/// Discord's ids (snowflakes: 64-bit numbers written in decimal). A Discord server connected to PrikBoard is a
/// <see cref="ChatWorkspace"/> with platform <see cref="ChatPlatform.Discord"/>; see docs/discord.md.
/// </summary>
public static class DiscordIds
{
    /// <summary>The longest Discord id.</summary>
    public const int MaxLength = 20;
}

/// <summary>
/// The instance's Discord bot, set up by an admin in Admin → Instance (docs/discord.md). One row at most;
/// <c>Discord:BotToken</c> and <c>Discord:PublicUrl</c> in configuration win over it. <see cref="Integrations.DiscordBot"/>
/// reads it at startup and after every change, and connects the bot while the app runs.
/// </summary>
public class DiscordBotSettings
{
    /// <summary>The only row's id.</summary>
    public const int SingleId = 1;

    public const int PublicUrlMaxLength = 500;

    public int Id { get; set; } = SingleId;

    /// <summary>The bot's token, encrypted with Data Protection (<see cref="Integrations.DiscordBot.TokenPurpose"/>).</summary>
    public string? ProtectedBotToken { get; set; }

    /// <summary>The Discord application the token belongs to, as Discord said when it was saved; for the invite link.</summary>
    public string? ApplicationId { get; set; }

    /// <summary>The bot's name in Discord when the token was saved.</summary>
    public string? BotName { get; set; }

    /// <summary>The PrikBoard address people paste links from, e.g. https://board.example.com.</summary>
    public string? PublicUrl { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The admin who changed it last; null once they no longer exist.</summary>
    public Guid? UpdatedById { get; set; }
}
