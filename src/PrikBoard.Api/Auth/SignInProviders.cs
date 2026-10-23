using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Auth;

/// <summary>
/// The sign-in providers: those in <c>Auth:Providers</c> (fixed, and they win on the same id) and those added in the setup
/// or on the admin page (<see cref="SignInProvider"/> rows). Each is an authentication scheme named after its id, which
/// this class adds and removes while the app runs; <see cref="SchemeOptions"/> gives the handlers their settings. Holds
/// one instance's view, so PrikBoard runs as one instance (like <see cref="Live.LiveViewers"/>).
/// </summary>
public sealed class SignInProviders(
    IOptions<AuthOptions> options,
    IAuthenticationSchemeProvider schemes,
    IOptionsMonitorCache<OpenIdConnectOptions> openIdConnectOptions,
    IOptionsMonitorCache<OAuthOptions> oauthOptions,
    IDataProtectionProvider protection,
    ILogger<SignInProviders> logger)
{
    /// <summary>The Data Protection purpose of stored client secrets.</summary>
    public const string SecretPurpose = "PrikBoard.SignInProviders";

    private readonly IDataProtector _secrets = protection.CreateProtector(SecretPurpose);
    private readonly Lock _lock = new();
    private IReadOnlyList<Provider> _all = [];

    /// <summary>Every provider that works, the default first.</summary>
    public IReadOnlyList<Provider> All => _all;

    /// <summary>The provider offered first, or null when there is none.</summary>
    public Provider? Default => _all.Count > 0 ? _all[0] : null;

    public Provider? Find(string id) => _all.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Whether <paramref name="id"/> belongs to a provider in <c>Auth:Providers</c>, filled in or not.</summary>
    public bool IsConfigured(string id) => options.Value.Providers.ContainsKey(id);

    public string ProtectSecret(string secret) => _secrets.Protect(secret);

    /// <summary>Reads the stored providers and brings the schemes in line; at startup and after every change.</summary>
    public async Task LoadAsync(PrikBoardDbContext db, CancellationToken ct)
    {
        var rows = await db.SignInProviders.AsNoTracking().OrderBy(p => p.CreatedAt).ToListAsync(ct);
        lock (_lock)
        {
            var next = Merge(rows);
            var before = _all.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var gone in before.Keys.Where(id => next.All(p => !string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))))
            {
                Forget(gone);
            }
            foreach (var provider in next)
            {
                if (before.TryGetValue(provider.Id, out var old) && old.Fingerprint == provider.Fingerprint) continue;
                Forget(provider.Id);
                schemes.TryAddScheme(new AuthenticationScheme(provider.Id, provider.Name, provider.Options.Type switch
                {
                    ProviderType.GitHub => typeof(OAuthHandler<OAuthOptions>),
                    _ => typeof(OpenIdConnectHandler),
                }));
            }
            _all = next;
        }
    }

    /// <summary>Drops a scheme and its cached options, so the next request builds them from the new settings.</summary>
    private void Forget(string id)
    {
        schemes.RemoveScheme(id);
        openIdConnectOptions.TryRemove(id);
        oauthOptions.TryRemove(id);
    }

    private List<Provider> Merge(List<SignInProvider> rows)
    {
        var config = options.Value;
        var configured = config.Providers.Select(p =>
        {
            if (!AuthSetup.IsValidId(p.Key))
            {
                throw new InvalidOperationException($"Auth provider id '{p.Key}' must use lowercase letters, digits and dashes.");
            }
            if (p.Value.Type == ProviderType.OpenIdConnect && string.IsNullOrWhiteSpace(p.Value.Authority))
            {
                throw new InvalidOperationException($"Auth provider '{p.Key}' needs an Authority (the OpenID Connect issuer URL).");
            }
            return new Provider(p.Key, p.Value, null);
        }).ToList();

        var stored = new List<Provider>();
        foreach (var row in rows)
        {
            if (config.Providers.ContainsKey(row.Id))
            {
                logger.LogWarning("Sign-in provider {Id} is in Auth:Providers too; the stored one is not used", row.Id);
                continue;
            }
            string? secret = null;
            try
            {
                secret = row.ProtectedClientSecret is { } protectedSecret ? _secrets.Unprotect(protectedSecret) : null;
            }
            catch (CryptographicException error)
            {
                // The Data Protection keys were lost or replaced. Recovery mode (Setup:Recovery) lets an admin enter it again.
                logger.LogError(error, "The client secret of sign-in provider {Id} can't be decrypted; the provider is off", row.Id);
                continue;
            }
            stored.Add(new Provider(row.Id, new ProviderOptions
            {
                Type = row.Type,
                DisplayName = row.DisplayName,
                Authority = row.Authority,
                ClientId = row.ClientId,
                ClientSecret = secret,
                Scopes = [.. row.Scopes],
                EmailsAreVerified = row.EmailsAreVerified,
            }, row));
        }

        // Auth:DefaultProvider names one of the configured providers; else a stored one may be the default.
        var all = configured.Concat(stored).ToList();
        var first = configured.FirstOrDefault(p => string.Equals(p.Id, config.DefaultProvider, StringComparison.OrdinalIgnoreCase))
                    ?? stored.FirstOrDefault(p => p.Row!.IsDefault)
                    ?? all.FirstOrDefault();
        return first is null ? all : [first, .. all.Where(p => p != first)];
    }

    /// <summary>A working sign-in provider. <see cref="Row"/> is null for one from <c>Auth:Providers</c>.</summary>
    public sealed record Provider(string Id, ProviderOptions Options, SignInProvider? Row)
    {
        /// <summary>The button label.</summary>
        public string Name => Options.DisplayName ?? Id;

        /// <summary>Changes when any setting the scheme uses changes.</summary>
        internal string Fingerprint { get; } = string.Join('\n', Options.Type, Options.DisplayName, Options.Authority,
            Options.ClientId, Options.ClientSecret, string.Join(' ', Options.Scopes));
    }

    /// <summary>
    /// The settings of the provider schemes' handlers, looked up by scheme name each time options are built (after
    /// <see cref="Forget"/>, or the first time).
    /// </summary>
    public sealed class SchemeOptions(SignInProviders providers)
        : IConfigureNamedOptions<OpenIdConnectOptions>, IConfigureNamedOptions<OAuthOptions>
    {
        public void Configure(string? name, OpenIdConnectOptions options)
        {
            if (name is not null && providers.Find(name) is { Options.Type: ProviderType.OpenIdConnect } provider)
            {
                AuthSetup.ConfigureOpenIdConnect(options, provider.Id, provider.Options);
            }
        }

        public void Configure(string? name, OAuthOptions options)
        {
            if (name is not null && providers.Find(name) is { Options.Type: ProviderType.GitHub } provider)
            {
                AuthSetup.ConfigureGitHub(options, provider.Id, provider.Options);
            }
        }

        public void Configure(OpenIdConnectOptions options) => Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
        public void Configure(OAuthOptions options) => Configure(Microsoft.Extensions.Options.Options.DefaultName, options);
    }
}
