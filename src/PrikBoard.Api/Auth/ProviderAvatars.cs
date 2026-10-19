using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Auth;

/// <summary>
/// The sign-in provider's picture of a person, which becomes their avatar unless they opted out. A copy is downloaded, so
/// the URL (which holds the provider's account id) is never stored and other people's browsers never contact the
/// provider. The URL also travels in the person's own session cookie, so they can take the picture again later.
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

    /// <summary>
    /// Makes the picture at <paramref name="url"/> the user's avatar (saving the context), or keeps the current one when
    /// it is the same. False when it cannot be downloaded; then nothing changes.
    /// </summary>
    public async Task<bool> TakeAsync(PrikBoardDbContext db, User user, string url, CancellationToken ct)
    {
        var avatar = await DownloadAsync(user.Id, url, ct);
        if (avatar is null) return false;

        var current = await db.UserAvatars.FirstOrDefaultAsync(a => a.UserId == user.Id, ct);
        if (current is null || !current.Data.AsSpan().SequenceEqual(avatar.Data))
        {
            if (current is null)
            {
                db.UserAvatars.Add(avatar);
            }
            else
            {
                current.ContentType = avatar.ContentType;
                current.Data = avatar.Data;
            }
            // A new version, so browsers fetch it instead of their cached copy.
            user.AvatarUpdatedAt = DateTimeOffset.UtcNow;
        }
        user.AvatarOptedOut = false;
        await db.SaveChangesAsync(ct);
        return true;
    }

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
