using System.Text.Json.Serialization;

namespace PrikBoard.Api.Auth;

/// <summary>The <c>Auth</c> configuration section: sign-in providers and accepted access tokens.</summary>
public class AuthOptions
{
    public const string Section = "Auth";

    /// <summary>
    /// Sign-in providers keyed by id (lowercase letters, digits and dashes). The id appears in the login and callback URLs.
    /// A provider without a ClientId is skipped, so templates can stay in appsettings.json until they are filled in.
    /// </summary>
    public Dictionary<string, ProviderOptions> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The provider offered first, as the main sign-in button.</summary>
    public string? DefaultProvider { get; set; }

    /// <summary>Access tokens that machines (Client Credentials) and other OAuth clients send as Bearer tokens.</summary>
    public TokenOptions Tokens { get; set; } = new();
}

[JsonConverter(typeof(JsonStringEnumConverter<ProviderType>))]
public enum ProviderType
{
    /// <summary>Any OpenID Connect provider: Google, Microsoft, Auth0, Okta, Keycloak and so on.</summary>
    OpenIdConnect,

    /// <summary>GitHub, which offers OAuth 2.0 but not OpenID Connect for signing users in.</summary>
    GitHub,
}

public class ProviderOptions
{
    public ProviderType Type { get; set; } = ProviderType.OpenIdConnect;

    /// <summary>Button label in the UI; defaults to the provider id.</summary>
    public string? DisplayName { get; set; }

    /// <summary>OpenID Connect issuer URL, for example https://accounts.google.com. Not used for GitHub.</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }

    /// <summary>Scopes requested on top of the defaults (openid, profile and email for OpenID Connect).</summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>
    /// Treat this provider's email claim as verified even without email_verified (for example an Entra tenant you
    /// control). Only verified emails make people findable through exact-email search. GitHub emails are always verified.
    /// </summary>
    public bool EmailsAreVerified { get; set; }
}

/// <summary>
/// Which external identity providers' access tokens (JWTs) the API accepts, typically from the Client Credentials grant
/// for machine-to-machine clients. PrikBoard's own personal access tokens are separate (<see cref="AccessTokenHandler"/>).
/// </summary>
public class TokenOptions
{
    /// <summary>The audience (aud) tokens must be issued for, unless an issuer overrides it.</summary>
    public string Audience { get; set; } = "api://prikboard";

    /// <summary>Trusted token issuers keyed by id. An issuer without an Authority is skipped.</summary>
    public Dictionary<string, TokenIssuerOptions> Issuers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class TokenIssuerOptions
{
    /// <summary>The issuer's base URL; its OpenID Connect discovery document supplies the signing keys.</summary>
    public string? Authority { get; set; }

    /// <summary>The exact iss value in its tokens, when it differs from <see cref="Authority"/>.</summary>
    public string? Issuer { get; set; }

    /// <summary>Overrides <see cref="TokenOptions.Audience"/> for this issuer.</summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Client ids (azp, client_id or appid) of interactive apps, such as the VS Code extension, whose tokens act for the
    /// person who signed in: the token's <c>sub</c> resolves to the same <c>User</c> as that person's browser sign-in.
    /// Every other client of this issuer, for example one using Client Credentials, acts as an agent.
    /// </summary>
    public string[] UserClients { get; set; } = [];

    /// <summary>
    /// The sign-in provider id that <c>sub</c> belongs to for <see cref="UserClients"/> tokens; defaults to this issuer's id.
    /// Must match the id of the sign-in provider of the same identity provider, or the person becomes a second user.
    /// </summary>
    public string? UserProvider { get; set; }
}
