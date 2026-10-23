using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PrikBoard.Api.Integrations;

/// <summary>
/// Asks Discord which application a bot token belongs to, before an admin's token is saved, so a wrong token or a
/// missing Message Content intent shows on the admin page rather than in the log.
/// </summary>
public class DiscordApplications(HttpClient http)
{
    /// <summary>Discord's application flags for the Message Content intent: on (verified apps), or on below 100 servers.</summary>
    private const long MessageContentFlags = (1 << 18) | (1 << 19);

    /// <summary>The token's application, or why it can't be used.</summary>
    public async Task<(DiscordApplication? Application, string? Problem)> CheckAsync(string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://discord.com/api/v10/applications/@me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", token);
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                return (null, "Discord doesn't accept this token. Copy it again from the Bot tab of your application (Reset Token).");
            }
            if (!response.IsSuccessStatusCode) return (null, $"Discord answered {(int)response.StatusCode}; try again in a minute.");

            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var app = json.RootElement;
            var flags = app.TryGetProperty("flags", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt64() : 0;
            if ((flags & MessageContentFlags) == 0)
            {
                return (null, "Turn on Message Content Intent on the Bot tab of your application: without it the bot can't see pasted links.");
            }
            var botName = app.TryGetProperty("bot", out var bot) && bot.TryGetProperty("username", out var name) ? name.GetString() : null;
            return (new DiscordApplication(app.GetProperty("id").GetString()!, botName ?? app.GetProperty("name").GetString()!,
                app.TryGetProperty("bot_public", out var isPublic) && isPublic.ValueKind == JsonValueKind.True), null);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or KeyNotFoundException or InvalidOperationException or TaskCanceledException)
        {
            return (null, "Discord could not be reached; try again in a minute.");
        }
    }
}

/// <summary>A Discord application with a bot.</summary>
/// <param name="Id">The application's id, which is also its bot's user id.</param>
/// <param name="BotName">The bot's name.</param>
/// <param name="IsPublic">Whether anyone may add the bot to their server ("Public Bot" on the Bot tab).</param>
public sealed record DiscordApplication(string Id, string BotName, bool IsPublic);
