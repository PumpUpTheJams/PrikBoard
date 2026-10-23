using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Auth;

/// <summary>
/// The sign-in provider's picture of a person (its <c>picture</c> claim: GitHub, Google, Discord, Steam or any OpenID
/// Connect provider), which becomes their avatar unless they opted out. A copy is downloaded, so the URL (which holds the
/// provider's account id) is never stored and other people's browsers never contact the provider. The URL also travels
/// in the person's own session cookie, so they can take the picture again later. The URL comes from a provider, so the
/// download only connects to public addresses (<see cref="PublicOnlyHandler"/>): a picture URL can't reach this
/// server's own network.
/// </summary>
public class ProviderAvatars(HttpClient http, FileStore files)
{
    public const int MaxBytes = 1024 * 1024;

    /// <summary>Picture URLs longer than this are ignored: they travel in the session cookie.</summary>
    public const int MaxUrlLength = 500;

    /// <summary>GitHub's avatar host, which scales its pictures with the s parameter.</summary>
    private const string GitHubHost = "avatars.githubusercontent.com";

    /// <summary>Square size asked GitHub for, enough for the largest avatar on a high-density screen.</summary>
    private const int Size = 160;

    /// <summary>Whether <paramref name="url"/> is a picture this server will try to download: https on the default port, by host name.</summary>
    public static bool IsTrusted([NotNullWhen(true)] string? url) =>
        url is { Length: <= MaxUrlLength } && Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && uri.HostNameType == UriHostNameType.Dns;

    /// <summary>
    /// The download's connection handler: it resolves the host itself and connects only to a public address, never to
    /// loopback, private, link-local or other special ranges, also after a redirect.
    /// </summary>
    public static SocketsHttpHandler PublicOnlyHandler() => new()
    {
        MaxAutomaticRedirections = 3,
        ConnectCallback = async (context, ct) =>
        {
            var host = context.DnsEndPoint.Host;
            var address = (await Dns.GetHostAddressesAsync(host, ct)).FirstOrDefault(IsPublic)
                          ?? throw new HttpRequestException($"{host} has no public address.");
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>Whether <paramref name="address"/> is on the public internet.</summary>
    internal static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] is 0 or 10 or 127 or >= 224
                     || (b[0] == 100 && b[1] is >= 64 and < 128)
                     || (b[0] == 169 && b[1] == 254)
                     || (b[0] == 172 && b[1] is >= 16 and < 32)
                     || (b[0] == 192 && b[1] == 168)
                     || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2)
                     || (b[0] == 198 && b[1] is 18 or 19));
        }
        return address.AddressFamily == AddressFamily.InterNetworkV6
               && !address.Equals(IPAddress.IPv6None) && !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal
               && !address.IsIPv6UniqueLocal && !address.IsIPv6Multicast;
    }

    /// <summary>
    /// Makes the picture at <paramref name="url"/> the user's avatar (saving the context), or keeps the current one when
    /// it is the same. False when it cannot be downloaded; then nothing changes.
    /// </summary>
    public async Task<bool> TakeAsync(PrikBoardDbContext db, User user, string url, CancellationToken ct)
    {
        if (await DownloadAsync(url, ct) is not { } picture) return false;

        var file = StoredFile.For(null, $"avatar.{ImageFormats.ExtensionOf(picture.ContentType)}", picture.ContentType, picture.Data);
        var current = user.AvatarFileId is { } currentId ? await db.StoredFiles.FindAsync([currentId], ct) : null;
        if (current is null || !current.Sha256.AsSpan().SequenceEqual(file.Sha256))
        {
            // The old file goes at the next sweep, once nothing refers to it.
            await files.WriteAsync(file, picture.Data, ct);
            db.StoredFiles.Add(file);
            user.AvatarFile = file;
            // A new version, so browsers fetch it instead of their cached copy.
            user.AvatarUpdatedAt = DateTimeOffset.UtcNow;
        }
        user.AvatarOptedOut = false;
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Downloads the picture at a trusted <paramref name="url"/>; null when it fails or is not a supported image.</summary>
    public async Task<(byte[] Data, string ContentType)?> DownloadAsync(string url, CancellationToken ct)
    {
        if (!IsTrusted(url)) return null;
        var sized = new UriBuilder(url);
        if (sized.Host.Equals(GitHubHost, StringComparison.OrdinalIgnoreCase))
        {
            sized.Query = (sized.Query.TrimStart('?') is { Length: > 0 } query ? query + "&" : "") + $"s={Size}";
        }

        try
        {
            using var response = await http.GetAsync(sized.Uri, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > MaxBytes) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[FileStore.BufferSize];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > MaxBytes) return null;
                buffer.Write(chunk, 0, read);
            }

            var data = buffer.ToArray();
            return ImageFormats.Detect(data) is { } info ? (data, info.ContentType) : null;
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
