using PrikBoard.Api.Auth;

namespace PrikBoard.Api.Domain;

/// <summary>
/// A sign-in provider added in the setup or on the admin page, next to those in <c>Auth:Providers</c> (which win on the
/// same id). <see cref="SignInProviders"/> turns these rows into authentication schemes while the app runs.
/// </summary>
public class SignInProvider
{
    /// <summary>The id is in the login and callback URLs and in every sign-in's hash, so it never changes.</summary>
    public const int IdMaxLength = 50;

    public const int DisplayNameMaxLength = 50;

    /// <summary>The scheme id: lowercase letters, digits and dashes.</summary>
    public required string Id { get; set; }

    public ProviderType Type { get; set; }
    public string? DisplayName { get; set; }

    /// <summary>The OpenID Connect issuer URL; null for GitHub.</summary>
    public string? Authority { get; set; }

    public required string ClientId { get; set; }

    /// <summary>The client secret, encrypted with Data Protection (<see cref="SignInProviders.SecretPurpose"/>).</summary>
    public string? ProtectedClientSecret { get; set; }

    /// <summary>Scopes requested on top of the defaults.</summary>
    public List<string> Scopes { get; set; } = [];

    /// <summary>See <see cref="ProviderOptions.EmailsAreVerified"/>.</summary>
    public bool EmailsAreVerified { get; set; }

    /// <summary>Offered first on the sign-in page; at most one row has it.</summary>
    public bool IsDefault { get; set; }

    /// <summary>The settings page of the GitHub App the setup created for this provider; null when it was entered by hand.</summary>
    public string? GitHubAppUrl { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
