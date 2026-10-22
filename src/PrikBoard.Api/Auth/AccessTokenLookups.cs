using System.Net.Sockets;
using System.Threading.RateLimiting;

namespace PrikBoard.Api.Auth;

/// <summary>
/// Limits how many unknown personal access tokens (made up, mistyped or revoked) one address may present. Each costs a
/// database lookup, and the checksum doesn't stop anyone from making up well-formed tokens: its algorithm is public. Past
/// the limit, the address gets 429 for every token that isn't cached, until the minute is over; tokens already looked up
/// stay cached and keep working. Addresses are as the API sees them (behind the web container, from X-Forwarded-For); an
/// IPv6 address counts by its /64 network, as one client often has all of one.
/// </summary>
public sealed class AccessTokenLookups(IConfiguration configuration) : IDisposable
{
    public const string Setting = "RateLimits:UnknownAccessTokensPerMinute";

    private readonly PartitionedRateLimiter<string> _unknown = PerAddress(configuration.GetValue(Setting, 30));

    /// <summary>
    /// Whether a token from this caller's address may be looked up; when not, <paramref name="retryAfter"/> says when it
    /// may again.
    /// </summary>
    public bool MayLookUp(HttpContext context, out TimeSpan retryAfter)
    {
        // Asking for no permits only tells whether any are left.
        using var lease = _unknown.AttemptAcquire(AddressOf(context), 0);
        retryAfter = lease.TryGetMetadata(MetadataName.RetryAfter, out var after) ? after : TimeSpan.FromMinutes(1);
        return lease.IsAcquired;
    }

    /// <summary>Counts an unknown token against this caller's address.</summary>
    public void Unknown(HttpContext context) => _unknown.AttemptAcquire(AddressOf(context)).Dispose();

    public void Dispose() => _unknown.Dispose();

    private static PartitionedRateLimiter<string> PerAddress(int limit) => PartitionedRateLimiter.Create<string, string>(address =>
        RateLimitPartition.GetFixedWindowLimiter(address, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = limit,
            Window = TimeSpan.FromMinutes(1),
        }));

    private static string AddressOf(HttpContext context) => context.Connection.RemoteIpAddress switch
    {
        null => "",
        { AddressFamily: AddressFamily.InterNetworkV6, IsIPv4MappedToIPv6: false } v6 => Convert.ToHexString(v6.GetAddressBytes(), 0, 8) + "/64",
        var address => address.MapToIPv4().ToString(),
    };
}
