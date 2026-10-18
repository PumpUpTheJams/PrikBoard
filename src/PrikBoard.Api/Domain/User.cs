namespace PrikBoard.Api.Domain;

/// <summary>A person who signed in through an external identity provider.</summary>
/// <remarks>
/// A user is identified by the provider and that provider's stable subject id, never by email:
/// the same email at two providers is two users, so one provider cannot take over another's account.
/// </remarks>
public class User
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>The configured provider name, for example "google" or "github".</summary>
    public required string Provider { get; set; }

    /// <summary>The provider's stable id for this person (the OIDC "sub" claim).</summary>
    public required string Subject { get; set; }

    public required string Name { get; set; }
    public string? Email { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastLoginAt { get; set; } = DateTimeOffset.UtcNow;
}
