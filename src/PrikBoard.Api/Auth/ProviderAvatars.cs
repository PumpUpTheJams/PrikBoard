using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Auth;

/// <summary>
/// The sign-in provider's picture of a person, which they may take as their avatar. The URL only travels in their own
/// session cookie as a suggestion; taking it downloads a copy, so the URL (which holds the provider's account id) is
/// never stored and other people's browsers never contact the provider.
/// </summary>
public class ProviderAvatars(HttpClient http)
{
    /// <summary>Hosts whose pictures are offered. Only GitHub's: other providers' picture URLs are ignored for now.</summary>
    private static readonly string[] TrustedHosts = ["avatars.githubusercontent.com"];

    /// <summary>Square size asked for, enough for the largest avatar on a high-density screen.</summary>
    private const int Size = 160;

    /// <summary>Whether <paramref name="url"/> is a picture this server will download: https on a trusted host.</summary>
    public static bool IsTrusted(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort
        && TrustedHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);

    /// <summary>Downloads the picture at a trusted <paramref name="url"/>; null when it fails or is not a supported image.</summary>
    public async Task<UserAvatar?> DownloadAsync(Guid userId, string url, CancellationToken ct)
    {
        if (!IsTrusted(url)) return null;
        // GitHub scales its avatars with the s parameter.
        var sized = new UriBuilder(url);
        sized.Query = (sized.Query.TrimStart('?') is { Length: > 0 } query ? query + "&" : "") + $"s={Size}";

        try
        {
            using var response = await http.GetAsync(sized.Uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > UserAvatar.MaxBytes) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > UserAvatar.MaxBytes) return null;
                buffer.Write(chunk, 0, read);
            }

            var data = buffer.ToArray();
            return ImageFormats.Detect(data) is { } info ? new UserAvatar { UserId = userId, ContentType = info.ContentType, Data = data } : null;
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return null; // timed out
        }
    }
}
