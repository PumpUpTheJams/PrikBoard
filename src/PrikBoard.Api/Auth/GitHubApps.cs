using System.Net.Http.Headers;
using System.Text.Json;

namespace PrikBoard.Api.Auth;

/// <summary>
/// The last step of creating a GitHub App from a manifest (the setup's one-click GitHub sign-in): GitHub hands back a
/// code, valid for an hour, that this trades for the new app's client id and secret. The call needs no credentials.
/// </summary>
public class GitHubApps(HttpClient http)
{
    /// <summary>The new app's client credentials and page; null when GitHub refused the code or could not be reached.</summary>
    public async Task<Conversion?> ConvertAsync(string code, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"https://api.github.com/app-manifests/{Uri.EscapeDataString(code)}/conversions");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        try
        {
            using var response = await http.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode) return null;
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var app = json.RootElement;
            // The app's private key (pem) and webhook secret come along too; signing people in needs neither, so they are dropped.
            return new Conversion(app.GetProperty("client_id").GetString()!, app.GetProperty("client_secret").GetString()!,
                app.TryGetProperty("html_url", out var url) ? url.GetString() : null);
        }
        catch (Exception error) when (error is HttpRequestException or JsonException or KeyNotFoundException or TaskCanceledException)
        {
            return null;
        }
    }

    public sealed record Conversion(string ClientId, string ClientSecret, string? HtmlUrl);
}
