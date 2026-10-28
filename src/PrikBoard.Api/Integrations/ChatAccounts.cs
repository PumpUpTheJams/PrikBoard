using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Integrations;

/// <summary>
/// Who a chat user is in PrikBoard, for what a bot does on their behalf (Save as card). One rule for every action:
/// someone who signs in with the platform's sign-in provider (Discord, or Sign in with Slack) is known by that sign-in;
/// anyone else links once, from the bot's "Connect your PrikBoard account", which opens <c>#/connect/discord-account</c>
/// or <c>#/connect/slack-account</c> with a code. Chat user ids
/// are only ever stored as keyed hashes, like sign-in subjects. The codes live in memory (one instance, like the bots).
/// </summary>
public sealed class ChatAccounts(KeyedHasher hasher, SignInProviders providers, TimeProvider time)
{
    /// <summary>How long a link code works.</summary>
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, PendingLink> _codes = new();

    /// <summary>The "provider" a linked chat user id is hashed under, apart from any sign-in provider's id.</summary>
    public static string HashProvider(ChatPlatform platform) => platform switch
    {
        ChatPlatform.Discord => "discord-bot",
        ChatPlatform.Slack => "slack-bot",
        _ => throw new ArgumentOutOfRangeException(nameof(platform)),
    };

    /// <summary>The PrikBoard user behind a chat user id, if any.</summary>
    public async Task<Guid?> FindUserAsync(PrikBoardDbContext db, ChatPlatform platform, string chatUserId, CancellationToken ct)
    {
        var hash = hasher.Subject(HashProvider(platform), chatUserId);
        if (await db.ChatAccounts.Where(a => a.Platform == platform && a.UserHash == hash).Select(a => (Guid?)a.UserId).FirstOrDefaultAsync(ct) is { } linked)
        {
            return linked;
        }
        foreach (var provider in SignInsOf(platform))
        {
            var subject = hasher.Subject(provider, chatUserId);
            if (await db.UserIdentities.Where(i => i.Provider == provider && i.SubjectHash == subject)
                    .Select(i => (Guid?)i.UserId).FirstOrDefaultAsync(ct) is { } signedIn)
            {
                return signedIn;
            }
        }
        return null;
    }

    /// <summary>Whether and how the user is known on the platform.</summary>
    public async Task<(ChatAccountKind? Kind, DateTimeOffset? LinkedAt)> StatusAsync(
        PrikBoardDbContext db, ChatPlatform platform, Guid userId, CancellationToken ct)
    {
        if (await db.ChatAccounts.Where(a => a.Platform == platform && a.UserId == userId).Select(a => (DateTimeOffset?)a.LinkedAt)
                .FirstOrDefaultAsync(ct) is { } at)
        {
            return (ChatAccountKind.Link, at);
        }
        var signIns = SignInsOf(platform);
        return await db.UserIdentities.AnyAsync(i => i.UserId == userId && signIns.Contains(i.Provider), ct)
            ? (ChatAccountKind.SignIn, null)
            : (null, null);
    }

    /// <summary>
    /// A new code for linking this chat user, for the bot's button. <paramref name="workspaceName"/> is the Slack workspace
    /// or Discord server it was asked from, shown with the name since display names can be anyone's.
    /// </summary>
    public string StartLink(ChatPlatform platform, string chatUserId, string chatName, string workspaceName)
    {
        var now = time.GetUtcNow();
        foreach (var (key, old) in _codes) if (old.ExpiresAt <= now) _codes.TryRemove(key, out _);
        var code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
        _codes[code] = new PendingLink(platform, chatUserId, chatName, workspaceName, now + CodeLifetime);
        return code;
    }

    /// <summary>Who a code for this platform links, while it works.</summary>
    public PendingLink? Peek(ChatPlatform platform, string code) =>
        _codes.TryGetValue(code, out var pending) && pending.Platform == platform && pending.ExpiresAt > time.GetUtcNow() ? pending : null;

    /// <summary>
    /// Links the code's chat user to <paramref name="userId"/>, replacing a link either side had on that platform; false for
    /// an unknown or used code, or one for another platform.
    /// </summary>
    public async Task<bool> LinkAsync(PrikBoardDbContext db, ChatPlatform platform, string code, Guid userId, CancellationToken ct)
    {
        if (Peek(platform, code) is null || !_codes.TryRemove(code, out var pending)) return false;
        var hash = hasher.Subject(HashProvider(platform), pending.ChatUserId);
        await db.ChatAccounts.Where(a => a.Platform == platform && (a.UserHash == hash || a.UserId == userId)).ExecuteDeleteAsync(ct);
        db.ChatAccounts.Add(new ChatAccount { Platform = platform, UserHash = hash, UserId = userId });
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// The sign-in providers whose subjects are the platform's user ids: Discord's are its user ids, Slack's are
    /// <c>{team}:{user}</c> (<see cref="AuthSetup.SlackSubject"/>), the same key the Slack app uses.
    /// </summary>
    private List<string> SignInsOf(ChatPlatform platform)
    {
        var type = platform == ChatPlatform.Slack ? ProviderType.Slack : ProviderType.Discord;
        return providers.All.Where(p => p.Options.Type == type).Select(p => p.Id).ToList();
    }

    /// <summary>
    /// A code waiting to be used: the chat user it links, their name and the workspace or server it came from, shown so
    /// people can check it is them.
    /// </summary>
    public sealed record PendingLink(ChatPlatform Platform, string ChatUserId, string ChatName, string WorkspaceName, DateTimeOffset ExpiresAt);
}

/// <summary>How a user is known on a chat platform.</summary>
public enum ChatAccountKind
{
    SignIn,
    Link,
}
