using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Data;

namespace PrikBoard.Api.Auth;

/// <summary>
/// The key that opens the setup of a new PrikBoard: printed in the log on a start without users (or with
/// <c>Setup:Recovery</c>), never stored. Whoever enters it gets a setup cookie (<see cref="Scheme"/>, an hour) that lets
/// them manage the sign-in providers and run the checks, and lets the first sign-in in that browser become the admin.
/// So the first admin is whoever can read the server's log, not whoever signs in first.
/// </summary>
public sealed class SetupKey(IConfiguration configuration)
{
    public const string Scheme = "Setup";
    public const string RecoveryKey = "Setup:Recovery";

    /// <summary>Names the key a setup cookie was issued for, so a cookie from an earlier key stops working.</summary>
    private const string KeyIdClaim = "prikboard_setup_key";

    private const int Length = 20;

    /// <summary>Crockford's base32: no I, L, O or U, so the key reads and types without mix-ups.</summary>
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    private readonly Lock _lock = new();
    private byte[]? _hash;
    private string? _id;

    /// <summary>Started with <c>Setup:Recovery</c>: the key works while users exist, for providers and checks only.</summary>
    public bool Recovery { get; } = configuration.GetValue<bool>(RecoveryKey);

    /// <summary>Whether a key is waiting to be entered.</summary>
    public bool IsOpen
    {
        get { lock (_lock) return _hash is not null; }
    }

    /// <summary>
    /// At startup: opens a key when there are no users yet or in recovery mode, and logs it. The key is new on every start.
    /// </summary>
    public async Task OpenIfNeededAsync(PrikBoardDbContext db, ILogger logger, CancellationToken ct)
    {
        if (await db.Users.AnyAsync(ct))
        {
            if (!Recovery) return;
            logger.LogWarning(
                "Recovery mode ({Setting}): open PrikBoard in the browser and enter the setup key {Key} to change the sign-in " +
                "providers. Remove the setting and restart when you are done", RecoveryKey, Open());
            return;
        }
        logger.LogWarning("PrikBoard isn't set up yet. Open it in the browser and enter the setup key {Key}", Open());
    }

    /// <summary>Makes a new key, replacing any earlier one, and returns it for the log (also used by tests).</summary>
    public string Open()
    {
        var key = new string(RandomNumberGenerator.GetItems<char>(Alphabet, Length));
        lock (_lock)
        {
            _hash = Hash(key);
            _id = Guid.NewGuid().ToString("N");
        }
        return string.Join('-', key.Chunk(5).Select(part => new string(part)));
    }

    /// <summary>Ends the setup once the first admin signed in; recovery mode stays open until the restart.</summary>
    public void CloseUnlessRecovery()
    {
        if (Recovery) return;
        lock (_lock)
        {
            _hash = null;
            _id = null;
        }
    }

    /// <summary>The setup cookie's principal when <paramref name="input"/> is the open key; null otherwise.</summary>
    public ClaimsPrincipal? Unlock(string input)
    {
        lock (_lock)
        {
            if (_hash is null || !CryptographicOperations.FixedTimeEquals(_hash, Hash(Normalize(input)))) return null;
            return new ClaimsPrincipal(new ClaimsIdentity([new Claim(KeyIdClaim, _id!)], Scheme));
        }
    }

    /// <summary>Whether the request carries a setup cookie for the key that is open now.</summary>
    public async Task<bool> GrantedAsync(HttpContext http)
    {
        string? id;
        lock (_lock) id = _id;
        if (id is null) return false;
        var result = await http.AuthenticateAsync(Scheme);
        return result.Succeeded && result.Principal.FindFirstValue(KeyIdClaim) == id;
    }

    /// <summary>Upper case, without dashes and spaces, and the letters Crockford reads as digits.</summary>
    private static string Normalize(string input)
    {
        var builder = new StringBuilder(input.Length);
        foreach (var c in input.ToUpperInvariant())
        {
            if (c is '-' or ' ') continue;
            builder.Append(c switch { 'O' => '0', 'I' or 'L' => '1', _ => c });
        }
        return builder.ToString();
    }

    private static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));
}
