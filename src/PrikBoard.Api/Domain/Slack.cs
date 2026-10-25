namespace PrikBoard.Api.Domain;

/// <summary>
/// The instance's Slack app, set up by an admin in Admin → Instance (docs/slack.md). One row at most;
/// <c>Slack:BotToken</c>, <c>Slack:AppToken</c> and <c>Slack:PublicUrl</c> in configuration win over it.
/// <see cref="Integrations.SlackApp"/> reads it at startup and after every change, and connects the app while PrikBoard
/// runs. A connected Slack workspace is a <see cref="ChatWorkspace"/> with platform <see cref="ChatPlatform.Slack"/>.
/// </summary>
public class SlackAppSettings
{
    /// <summary>The only row's id.</summary>
    public const int SingleId = 1;

    public const int PublicUrlMaxLength = 500;

    public int Id { get; set; } = SingleId;

    /// <summary>The bot token (<c>xoxb-…</c>), encrypted with Data Protection (<see cref="Integrations.SlackApp.TokenPurpose"/>).</summary>
    public string? ProtectedBotToken { get; set; }

    /// <summary>The app-level token for Socket Mode (<c>xapp-…</c>), encrypted the same way.</summary>
    public string? ProtectedAppToken { get; set; }

    /// <summary>The workspace the bot token belongs to, as Slack said when it was saved.</summary>
    public string? TeamId { get; set; }

    /// <summary>The workspace's name then.</summary>
    public string? TeamName { get; set; }

    /// <summary>The bot's name in Slack then.</summary>
    public string? BotName { get; set; }

    /// <summary>The PrikBoard address people paste links from, e.g. https://board.example.com; its host is the app's unfurl domain.</summary>
    public string? PublicUrl { get; set; }

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The admin who changed it last; null once they no longer exist.</summary>
    public Guid? UpdatedById { get; set; }
}
