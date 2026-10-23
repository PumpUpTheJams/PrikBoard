using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Integrations;

/// <summary>
/// Who a Discord user is in PrikBoard, for what the bot does on their behalf (Save to PrikBoard). One rule for every
/// action: someone who signs in with a Discord provider is known by that sign-in; anyone else links once, from the bot's
/// "Connect your PrikBoard account" button, which opens <c>#/connect/discord-account</c> with a code. Discord ids are
/// only ever stored as keyed hashes, like sign-in subjects. The codes live in memory (one instance, like the bot).
/// </summary>
public sealed class DiscordAccounts(KeyedHasher hasher, SignInProviders providers, TimeProvider time)
{
    /// <summary>The "provider" a linked Discord user id is hashed under, apart from any sign-in provider's id.</summary>
    public const string HashProvider = "discord-bot";

    /// <summary>How long a link code works.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, PendingLink> _codes = new();

    /// <summary>The PrikBoard user behind a Discord user id, if any.</summary>
    public async Task<Guid?> FindUserAsync(PrikBoardDbContext db, string discordUserId, CancellationToken ct)
    {
        var hash = hasher.Subject(HashProvider, discordUserId);
        if (await db.DiscordAccounts.Where(a => a.DiscordUserHash == hash).Select(a => (Guid?)a.UserId).FirstOrDefaultAsync(ct) is { } linked)
        {
            return linked;
        }
        foreach (var provider in providers.All.Where(p => p.Options.Type == ProviderType.Discord))
        {
            var subject = hasher.Subject(provider.Id, discordUserId);
            if (await db.UserIdentities.Where(i => i.Provider == provider.Id && i.SubjectHash == subject)
                    .Select(i => (Guid?)i.UserId).FirstOrDefaultAsync(ct) is { } signedIn)
            {
                return signedIn;
            }
        }
        return null;
    }

    /// <summary>Whether and how the user is known in Discord.</summary>
    public async Task<(DiscordAccountKind? Kind, DateTimeOffset? LinkedAt)> StatusAsync(PrikBoardDbContext db, Guid userId, CancellationToken ct)
    {
        if (await db.DiscordAccounts.Where(a => a.UserId == userId).Select(a => (DateTimeOffset?)a.LinkedAt).FirstOrDefaultAsync(ct) is { } at)
        {
            return (DiscordAccountKind.Link, at);
        }
        var discord = providers.All.Where(p => p.Options.Type == ProviderType.Discord).Select(p => p.Id).ToList();
        return await db.UserIdentities.AnyAsync(i => i.UserId == userId && discord.Contains(i.Provider), ct)
            ? (DiscordAccountKind.SignIn, null)
            : (null, null);
    }

    /// <summary>A new code for linking this Discord user, for the bot's button.</summary>
    public string StartLink(string discordUserId, string discordName)
    {
        var now = time.GetUtcNow();
        foreach (var (key, old) in _codes) if (old.ExpiresAt <= now) _codes.TryRemove(key, out _);
        var code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
        _codes[code] = new PendingLink(discordUserId, discordName, now + CodeLifetime);
        return code;
    }

    /// <summary>Who a code links, while it works.</summary>
    public PendingLink? Peek(string code) =>
        _codes.TryGetValue(code, out var pending) && pending.ExpiresAt > time.GetUtcNow() ? pending : null;

    /// <summary>Links the code's Discord user to <paramref name="userId"/>, replacing a link either side had; false for an unknown or used code.</summary>
    public async Task<bool> LinkAsync(PrikBoardDbContext db, string code, Guid userId, CancellationToken ct)
    {
        if (!_codes.TryRemove(code, out var pending) || pending.ExpiresAt <= time.GetUtcNow()) return false;
        var hash = hasher.Subject(HashProvider, pending.DiscordUserId);
        await db.DiscordAccounts.Where(a => a.DiscordUserHash == hash || a.UserId == userId).ExecuteDeleteAsync(ct);
        db.DiscordAccounts.Add(new DiscordAccount { DiscordUserHash = hash, UserId = userId });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>A code waiting to be used: the Discord user it links and their name, shown so people can check it is them.</summary>
    public sealed record PendingLink(string DiscordUserId, string DiscordName, DateTimeOffset ExpiresAt);
}

/// <summary>How a user is known in Discord.</summary>
public enum DiscordAccountKind
{
    SignIn,
    Link,
}
