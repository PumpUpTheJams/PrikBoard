using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>Whether this PrikBoard still needs setting up, for the web app's first page.</summary>
/// <param name="Needed">No one has an account yet: the setup must make the first admin.</param>
/// <param name="Recovery">The server runs in recovery mode (Setup:Recovery): the setup key works for the sign-in providers.</param>
/// <param name="Unlocked">This browser entered the current setup key (unlockSetup).</param>
public sealed record SetupStatus(bool Needed, bool Recovery, bool Unlocked);

/// <summary>The setup key from the server's log.</summary>
/// <param name="Key">The key as printed, with or without dashes, in any case.</param>
public sealed record UnlockSetupRequest([property: Required, StringLength(40, MinimumLength = 1)] string Key);

/// <summary>Where a sign-in provider's settings come from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ProviderSource>))]
public enum ProviderSource
{
    /// <summary>Auth:Providers in the server's configuration: read-only here.</summary>
    Configuration,

    /// <summary>Added in the setup or on the admin page.</summary>
    Setup,
}

/// <summary>A sign-in provider as the setup shows it. The client secret is never returned.</summary>
/// <param name="Id">The provider id, in the login URL and the callback URL (/api/auth/callback/{id}). It never changes.</param>
/// <param name="Type">OpenIdConnect (Google, Microsoft, Auth0, Keycloak and others) or GitHub.</param>
/// <param name="DisplayName">The sign-in button's label.</param>
/// <param name="Authority">The OpenID Connect issuer URL; null for GitHub.</param>
/// <param name="ClientId">The client id from the provider's app registration.</param>
/// <param name="HasSecret">Whether a client secret is stored.</param>
/// <param name="Scopes">Scopes asked for on top of the defaults.</param>
/// <param name="EmailsAreVerified">Treat the provider's emails as verified even without email_verified.</param>
/// <param name="IsDefault">Offered first on the sign-in page.</param>
/// <param name="Source">Configuration providers can't be changed here.</param>
/// <param name="GitHubAppUrl">The GitHub App the setup created for it; null otherwise.</param>
/// <param name="SignIns">How many people's sign-ins use this provider; deleting it locks out those without another one.</param>
public sealed record SignInProviderDetail(
    string Id, ProviderType Type, string DisplayName, string? Authority, string ClientId, bool HasSecret, List<string> Scopes,
    bool EmailsAreVerified, bool IsDefault, ProviderSource Source, string? GitHubAppUrl, int SignIns);

/// <summary>A new sign-in provider.</summary>
/// <param name="Id">Lowercase letters, digits and dashes, for example "google". It is part of the callback URL and can't change later.</param>
/// <param name="Type">OpenIdConnect or GitHub.</param>
/// <param name="DisplayName">The sign-in button's label, for example "Google"; defaults to the id.</param>
/// <param name="Authority">OpenID Connect only: the issuer URL, such as https://accounts.google.com (https, or http on localhost).</param>
/// <param name="ClientId">From the provider's app registration.</param>
/// <param name="ClientSecret">From the provider's app registration; GitHub needs one. Stored encrypted and never shown again.</param>
/// <param name="Scopes">Extra scopes; usually none.</param>
/// <param name="EmailsAreVerified">Treat emails as verified even without email_verified (for example a work tenant you control).</param>
/// <param name="IsDefault">Offer it first on the sign-in page.</param>
public sealed record CreateSignInProviderRequest(
    [property: Required, StringLength(SignInProvider.IdMaxLength, MinimumLength = 1)] string Id,
    [property: Required] ProviderType Type,
    [property: StringLength(SignInProvider.DisplayNameMaxLength)] string? DisplayName,
    [property: StringLength(500)] string? Authority,
    [property: Required, StringLength(500, MinimumLength = 1)] string ClientId,
    [property: StringLength(1000)] string? ClientSecret,
    List<string>? Scopes = null,
    bool EmailsAreVerified = false,
    bool IsDefault = false);

/// <summary>Changes a sign-in provider added in the setup. Fields left out (null) stay as they are.</summary>
/// <param name="DisplayName">The sign-in button's label; empty goes back to the id.</param>
/// <param name="Authority">OpenID Connect only: the issuer URL.</param>
/// <param name="ClientId">From the provider's app registration.</param>
/// <param name="ClientSecret">A new client secret; empty removes it.</param>
/// <param name="Scopes">Replaces the extra scopes.</param>
/// <param name="EmailsAreVerified">Treat emails as verified even without email_verified.</param>
/// <param name="IsDefault">Offer it first on the sign-in page (true), or no longer (false).</param>
public sealed record UpdateSignInProviderRequest(
    [property: StringLength(SignInProvider.DisplayNameMaxLength)] string? DisplayName = null,
    [property: StringLength(500)] string? Authority = null,
    [property: StringLength(500, MinimumLength = 1)] string? ClientId = null,
    [property: StringLength(1000)] string? ClientSecret = null,
    List<string>? Scopes = null,
    bool? EmailsAreVerified = null,
    bool? IsDefault = null);

/// <summary>An OpenID Connect issuer URL to try before saving a provider.</summary>
/// <param name="Authority">For example https://accounts.google.com or https://login.microsoftonline.com/common/v2.0.</param>
public sealed record CheckOpenIdAuthorityRequest([property: Required, StringLength(500, MinimumLength = 1)] string Authority);

/// <summary>The issuer's discovery document answered.</summary>
/// <param name="Issuer">The issuer it names.</param>
public sealed record OpenIdAuthority(string Issuer);

/// <summary>Starts creating a GitHub App for signing in with GitHub, so nothing needs copying by hand.</summary>
/// <param name="Id">The new provider's id, usually "github".</param>
/// <param name="Name">The app's name on GitHub, which must be unique on all of GitHub (at most 34 characters).</param>
/// <param name="Organization">The GitHub organization that should own the app; omit for your own account.</param>
/// <param name="ReturnUrl">The PrikBoard page to come back to, such as /#/setup.</param>
public sealed record BeginGitHubAppRequest(
    [property: Required, StringLength(SignInProvider.IdMaxLength, MinimumLength = 1)] string Id,
    [property: Required, StringLength(34, MinimumLength = 1)] string Name,
    [property: StringLength(39)] string? Organization = null,
    [property: StringLength(200)] string? ReturnUrl = null);

/// <summary>
/// What the browser posts to GitHub: a form to <see cref="PostUrl"/> with one field, "manifest", holding
/// <see cref="Manifest"/>. GitHub then comes back to PrikBoard, which saves the provider and returns to the page.
/// </summary>
/// <param name="PostUrl">GitHub's page for new apps, with the state that brings the answer back here.</param>
/// <param name="Manifest">The app's settings as JSON: its name, homepage, callback URL and the one permission it needs (read email addresses).</param>
public sealed record GitHubAppManifest(string PostUrl, string Manifest);

/// <summary>How one system check came out.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CheckStatus>))]
public enum CheckStatus
{
    Ok,
    Warning,
    Failed,
}

/// <summary>One thing the setup checks about how this PrikBoard runs.</summary>
/// <param name="Id">database, migrations, storage, keys, hashKey, https, providers or version.</param>
/// <param name="Title">What was checked.</param>
/// <param name="Status">Ok, Warning or Failed.</param>
/// <param name="Message">What was found, such as a version or a path.</param>
public sealed record SystemCheck(string Id, string Title, CheckStatus Status, string Message);
