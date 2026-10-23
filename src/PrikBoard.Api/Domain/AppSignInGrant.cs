namespace PrikBoard.Api.Domain;

/// <summary>
/// A person approved signing an app in (the VS Code extension): the one-time code the app trades, with its PKCE
/// verifier, for an <see cref="AccessTokenKind.App"/> token. Only a hash of the code is stored; it is deleted when used.
/// </summary>
public class AppSignInGrant
{
    /// <summary>How long the app has to trade the code.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(5);

    /// <summary>Codes waiting to be traded per person, so a session cannot pile them up.</summary>
    public const int MaxPendingPerOwner = 5;

    /// <summary>A PKCE S256 code challenge: base64url SHA-256 without padding, also for <see cref="DiscordConnectGrant"/>.</summary>
    public const int CodeChallengeLength = 43;

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid OwnerId { get; set; }
    public User? Owner { get; set; }

    /// <summary>SHA-256 of the code (256 random bits).</summary>
    public required byte[] CodeHash { get; set; }

    /// <summary>The app's PKCE code challenge: base64url SHA-256 of its verifier (S256).</summary>
    public required string CodeChallenge { get; set; }

    /// <summary>The name the token gets, as the app gave it.</summary>
    public required string Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
}
