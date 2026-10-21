using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PrikBoard.Discord;

// Mirrors of the API's contracts (src/PrikBoard.Api/Contracts/Discord.cs), only what the bot reads.

public sealed record DiscordChannelDetail(string Id, string Name);

public sealed record DiscordBoardConnection(Guid BoardId, string ShortId, string BoardName, List<DiscordChannelDetail>? Channels);

public sealed record DiscordConnected(string Token, DateTimeOffset ExpiresAt, List<DiscordBoardConnection> Boards);

public sealed record DiscordStatus(string GuildId, string GuildName, DateTimeOffset TokenExpiresAt, List<DiscordBoardConnection> Boards);

public sealed record LinkPreview(
    string Kind,
    string BoardName,
    string? ReadableId,
    string? Title,
    string? Description,
    string? Color,
    string? Path,
    string? Role,
    string? InvitedBy,
    DateTimeOffset? ExpiresAt);

/// <summary>The API turned the server's token away: revoked, expired, or the server was never connected.</summary>
public sealed class TokenRejectedException() : Exception("PrikBoard rejected the server's token.");

/// <summary>The API refused a request with a message worth showing to the person in Discord.</summary>
public sealed class PrikBoardException(string message) : Exception(message);

/// <summary>
/// The PrikBoard REST API, as the bot uses it: only the Discord endpoints, each call with the token of the server it is
/// for. The bot has no credential of its own.
/// </summary>
public sealed class PrikBoardApi(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>exchangeDiscordConnect: trades the code pasted in /prikboard finish for the server's token.</summary>
    public async Task<DiscordConnected> ExchangeAsync(
        string code, string codeVerifier, string guildId, string guildName, string channelId, string channelName, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("/api/discord/connect/token",
            new { code, codeVerifier, guildId, guildName, channelId, channelName }, Json, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) throw new PrikBoardException("Too many tries. Wait a minute and try again.");
        if (!response.IsSuccessStatusCode) throw new PrikBoardException(await ProblemAsync(response, ct));
        return (await response.Content.ReadFromJsonAsync<DiscordConnected>(Json, ct))!;
    }

    /// <summary>getDiscordStatus.</summary>
    public async Task<DiscordStatus> StatusAsync(string token, CancellationToken ct)
    {
        using var response = await http.SendAsync(Request(HttpMethod.Get, "/api/discord/status", token), ct);
        Check(response);
        return (await response.Content.ReadFromJsonAsync<DiscordStatus>(Json, ct))!;
    }

    /// <summary>getLinkPreview: what the link shows, or null when the server may not preview it (or it is no PrikBoard link).</summary>
    public async Task<LinkPreview?> PreviewAsync(string token, string url, string channelId, CancellationToken ct)
    {
        var request = Request(HttpMethod.Post, "/api/link-previews", token);
        request.Content = JsonContent.Create(new { url, channelId }, options: Json);
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.TooManyRequests) return null;
        Check(response);
        return await response.Content.ReadFromJsonAsync<LinkPreview>(Json, ct);
    }

    /// <summary>revokeDiscordToken: the token stops working at once.</summary>
    public async Task RevokeAsync(string token, CancellationToken ct)
    {
        using var response = await http.SendAsync(Request(HttpMethod.Delete, "/api/discord/token", token), ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized) return;
        Check(response);
    }

    private static HttpRequestMessage Request(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private static void Check(HttpResponseMessage response)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) throw new TokenRejectedException();
        response.EnsureSuccessStatusCode();
    }

    /// <summary>The first message of a problem+json answer (a field's error, or the detail).</summary>
    private static async Task<string> ProblemAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var root = problem.RootElement;
            if (root.TryGetProperty("errors", out var errors))
            {
                foreach (var field in errors.EnumerateObject())
                {
                    foreach (var message in field.Value.EnumerateArray()) return message.GetString() ?? "";
                }
            }
            if (root.TryGetProperty("detail", out var detail) && detail.GetString() is { Length: > 0 } text) return text;
            if (root.TryGetProperty("title", out var title) && title.GetString() is { Length: > 0 } heading) return heading;
        }
        catch (JsonException)
        {
            // Not problem+json; fall through.
        }
        return $"PrikBoard answered {(int)response.StatusCode}.";
    }
}
