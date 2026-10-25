using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace PrikBoard.Api.Integrations;

/// <summary>A workspace and bot as a bot token's <c>auth.test</c> describes them.</summary>
/// <param name="TeamId">The workspace's id (T…).</param>
/// <param name="TeamName">Its name.</param>
/// <param name="BotUserId">The bot's user id, so the app can ignore its own messages.</param>
/// <param name="BotName">The bot's name.</param>
public sealed record SlackIdentity(string TeamId, string TeamName, string BotUserId, string BotName);

/// <summary>A Slack channel as <c>conversations.list</c> and <c>conversations.info</c> describe it.</summary>
/// <param name="Id">Its id (C… or G…).</param>
/// <param name="Name">Its name, without the #.</param>
/// <param name="IsMember">Whether the bot is in it, which it must be to post there.</param>
/// <param name="IsExtShared">Whether it is shared with other organizations (Slack Connect), whose people would see previews too.</param>
public sealed record SlackChannelInfo(string Id, string Name, bool IsMember, bool IsExtShared);

/// <summary>What a Slack Web API call failed with: Slack's own error code, or what went wrong on the way.</summary>
public sealed class SlackApiException(string error) : Exception($"Slack answered {error}.")
{
    public string Error { get; } = error;

    /// <summary>Whether the token itself was refused, so trying again won't help until it changes.</summary>
    public bool IsAuth => Error is "invalid_auth" or "not_authed" or "account_inactive" or "token_revoked" or "token_expired"
        or "not_allowed_token_type" or "missing_scope";
}

/// <summary>
/// The few Slack Web API methods PrikBoard's Slack app uses, without an SDK. Every call is a form POST with the token as
/// bearer, and Slack answers 200 with <c>"ok": false</c> and an error code when it refuses, which is thrown as a
/// <see cref="SlackApiException"/>. The base address is <c>Slack:ApiUrl</c> (https://slack.com/api/ unless the fake Slack
/// in <c>tools/FakeSlack</c> stands in for it).
/// </summary>
public class SlackApi(HttpClient http)
{
    public const string ApiUrlSetting = "Slack:ApiUrl";
    public const string DefaultApiUrl = "https://slack.com/api/";

    /// <summary>Who a bot token belongs to; thrown for a user or app token, which <c>auth.test</c> accepts too.</summary>
    public async Task<SlackIdentity> AuthTestAsync(string botToken, CancellationToken ct)
    {
        using var json = await CallAsync("auth.test", botToken, [], ct);
        var body = json.RootElement;
        if (!body.TryGetProperty("bot_id", out _)) throw new SlackApiException("not_a_bot_token");
        return new SlackIdentity(
            body.GetProperty("team_id").GetString()!,
            body.GetProperty("team").GetString()!,
            body.GetProperty("user_id").GetString()!,
            body.GetProperty("user").GetString()!);
    }

    /// <summary>A Socket Mode address to connect to with the app-level token; each works once.</summary>
    public async Task<Uri> OpenConnectionAsync(string appToken, CancellationToken ct)
    {
        using var json = await CallAsync("apps.connections.open", appToken, [], ct);
        return new Uri(json.RootElement.GetProperty("url").GetString()!);
    }

    /// <summary>The workspace's channels that aren't archived, public and private (private ones only where the bot was invited).</summary>
    public async Task<List<SlackChannelInfo>> ChannelsAsync(string botToken, CancellationToken ct)
    {
        var channels = new List<SlackChannelInfo>();
        string? cursor = null;
        do
        {
            List<KeyValuePair<string, string>> form =
            [
                new("types", "public_channel,private_channel"),
                new("exclude_archived", "true"),
                new("limit", "200"),
            ];
            if (cursor is not null) form.Add(new("cursor", cursor));
            using var json = await CallAsync("conversations.list", botToken, form, ct);
            channels.AddRange(json.RootElement.GetProperty("channels").EnumerateArray().Select(Channel));
            cursor = json.RootElement.TryGetProperty("response_metadata", out var meta) && meta.TryGetProperty("next_cursor", out var next)
                ? next.GetString()
                : null;
        }
        while (!string.IsNullOrEmpty(cursor));
        return channels;
    }

    /// <summary>One channel; null when the bot can't see it.</summary>
    public async Task<SlackChannelInfo?> ChannelAsync(string botToken, string channelId, CancellationToken ct)
    {
        try
        {
            using var json = await CallAsync("conversations.info", botToken, [new("channel", channelId)], ct);
            return Channel(json.RootElement.GetProperty("channel"));
        }
        catch (SlackApiException error) when (error.Error == "channel_not_found")
        {
            return null;
        }
    }

    /// <summary>Attaches previews to a message's links (also again, to bring them up to date): <paramref name="unfurls"/> maps each URL, as Slack sent it, to its attachment.</summary>
    public async Task UnfurlAsync(string botToken, string channelId, string messageTs, IReadOnlyDictionary<string, object> unfurls, CancellationToken ct)
    {
        using var _ = await CallAsync("chat.unfurl", botToken,
            [new("channel", channelId), new("ts", messageTs), new("unfurls", JsonSerializer.Serialize(unfurls, JsonSerializerOptions.Web))], ct);
    }

    /// <summary>Posts a message as the bot: <paramref name="text"/> is what notifications show, the attachment what the channel does.</summary>
    public async Task PostMessageAsync(string botToken, string channelId, string text, object attachment, CancellationToken ct)
    {
        using var _ = await CallAsync("chat.postMessage", botToken,
            [new("channel", channelId), new("text", text), new("unfurl_links", "false"), new("unfurl_media", "false"),
             new("attachments", JsonSerializer.Serialize(new[] { attachment }, JsonSerializerOptions.Web))], ct);
    }

    /// <summary>Opens a modal for the person who used a shortcut; <paramref name="triggerId"/> works once, for 3 seconds.</summary>
    public async Task OpenViewAsync(string botToken, string triggerId, object view, CancellationToken ct)
    {
        using var _ = await CallAsync("views.open", botToken,
            [new("trigger_id", triggerId), new("view", JsonSerializer.Serialize(view, JsonSerializerOptions.Web))], ct);
    }

    /// <summary>Reacts to a message as the bot; a reaction that is there already counts as done.</summary>
    public async Task AddReactionAsync(string botToken, string channelId, string messageTs, string emoji, CancellationToken ct)
    {
        try
        {
            using var _ = await CallAsync("reactions.add", botToken, [new("channel", channelId), new("timestamp", messageTs), new("name", emoji)], ct);
        }
        catch (SlackApiException error) when (error.Error == "already_reacted")
        {
            // Saved before.
        }
    }

    /// <summary>The link that opens a message in Slack.</summary>
    public async Task<string> PermalinkAsync(string botToken, string channelId, string messageTs, CancellationToken ct)
    {
        using var json = await CallAsync("chat.getPermalink", botToken, [new("channel", channelId), new("message_ts", messageTs)], ct);
        return json.RootElement.GetProperty("permalink").GetString()!;
    }

    /// <summary>A person's name as Slack shows it (display name, else real name, else user name); null for one the bot can't see.</summary>
    public async Task<string?> UserNameAsync(string botToken, string userId, CancellationToken ct)
    {
        try
        {
            using var json = await CallAsync("users.info", botToken, [new("user", userId)], ct);
            var user = json.RootElement.GetProperty("user");
            string? Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.GetString() is { Length: > 0 } s ? s : null;
            return (user.TryGetProperty("profile", out var profile) ? Text(profile, "display_name") ?? Text(profile, "real_name") : null)
                   ?? Text(user, "real_name") ?? Text(user, "name");
        }
        catch (SlackApiException error) when (error.Error is "user_not_found" or "user_not_visible")
        {
            return null;
        }
    }

    /// <summary>
    /// Answers a slash command through the <c>response_url</c> Slack sent with it, which needs no token: it only works for
    /// that command, for half an hour. Only Slack's own address is used (or the configured API's host, for the fake Slack),
    /// so an envelope can't make PrikBoard post elsewhere. False when it is refused.
    /// </summary>
    public async Task<bool> RespondAsync(string responseUrl, object message, CancellationToken ct)
    {
        if (!IsResponseUrl(responseUrl)) return false;
        try
        {
            using var response = await http.PostAsync(responseUrl,
                new StringContent(JsonSerializer.Serialize(message, JsonSerializerOptions.Web), System.Text.Encoding.UTF8, "application/json"), ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return false;
        }
    }

    private bool IsResponseUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && ((uri.Scheme == Uri.UriSchemeHttps && uri.Host == "hooks.slack.com")
            || (http.BaseAddress is { } api && uri.Scheme == api.Scheme && string.Equals(uri.Authority, api.Authority, StringComparison.OrdinalIgnoreCase)));

    private static SlackChannelInfo Channel(JsonElement c) => new(
        c.GetProperty("id").GetString()!,
        c.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
        c.TryGetProperty("is_member", out var member) && member.ValueKind == JsonValueKind.True,
        c.TryGetProperty("is_ext_shared", out var shared) && shared.ValueKind == JsonValueKind.True);

    /// <summary>Calls a method; waits out one rate limit of up to half a minute (<c>Retry-After</c>) before giving up.</summary>
    private async Task<JsonDocument> CallAsync(string method, string token, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        var fields = form.ToList();
        for (var attempt = 0; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, method) { Content = new FormUrlEncodedContent(fields) };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage response;
            try
            {
                response = await http.SendAsync(request, ct);
            }
            catch (Exception error) when (error is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                throw new SlackApiException("unreachable");
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(1);
                    if (attempt > 0 || wait > TimeSpan.FromSeconds(30)) throw new SlackApiException("ratelimited");
                    await Task.Delay(wait, ct);
                    continue;
                }
                if (!response.IsSuccessStatusCode) throw new SlackApiException($"http_{(int)response.StatusCode}");

                JsonDocument json;
                try
                {
                    json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
                }
                catch (JsonException)
                {
                    throw new SlackApiException("invalid_response");
                }
                if (json.RootElement.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.True) return json;
                var error = json.RootElement.TryGetProperty("error", out var code) ? code.GetString() ?? "unknown_error" : "unknown_error";
                json.Dispose();
                throw new SlackApiException(error);
            }
        }
    }
}
