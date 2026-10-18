namespace PrikBoard.Api.Auth;

/// <summary>The <c>Auth</c> configuration section: sign-in providers and who may sign in.</summary>
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

    /// <summary>Emails that may sign in. When this and <see cref="AllowedEmailDomains"/> are both empty, anyone may.</summary>
    public string[] AllowedEmails { get; set; } = [];

    /// <summary>Email domains (for example "example.com") whose users may sign in.</summary>
    public string[] AllowedEmailDomains { get; set; } = [];

    /// <summary>Access tokens that machines (Client Credentials) and other OAuth clients send as Bearer tokens.</summary>
    public TokenOptions Tokens { get; set; } = new();

    public bool Allows(string? email)
    {
        if (AllowedEmails.Length == 0 && AllowedEmailDomains.Length == 0) return true;
        if (email is null) return false;

        var domain = email[(email.LastIndexOf('@') + 1)..];
        return AllowedEmails.Contains(email, StringComparer.OrdinalIgnoreCase)
               || AllowedEmailDomains.Contains(domain, StringComparer.OrdinalIgnoreCase);
    }
}

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
}

/// <summary>
/// Which access tokens the API accepts. PrikBoard does not issue tokens: an external identity provider does,
/// typically through the Client Credentials grant for machine-to-machine clients.
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
}
