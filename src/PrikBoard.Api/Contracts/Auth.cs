using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace PrikBoard.Api.Contracts;

/// <summary>A sign-in provider a person can pick in the browser.</summary>
/// <param name="Id">Provider id, used in the login URL /api/auth/login/{id}.</param>
/// <param name="DisplayName">Label for the sign-in button, for example "Google".</param>
public record AuthProvider(string Id, string DisplayName);

/// <summary>The running server build.</summary>
/// <param name="Version">Version of the server that answered, for example "1.2.0+abc1234".</param>
public record ServerVersion(string Version);

/// <summary>How a request authenticated.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CallerKind>))]
public enum CallerKind
{
    /// <summary>A person signed in through the browser, with a session cookie.</summary>
    Session,

    /// <summary>A Bearer access token from a trusted issuer, for example a machine using Client Credentials.</summary>
    Token,
}

/// <summary>Whether a principal is a person or a machine client.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<PrincipalKind>))]
public enum PrincipalKind
{
    User,
    Agent,
}

/// <summary>Who the request is authenticated as. No email or provider name is stored or returned.</summary>
/// <param name="Kind">Session for a signed-in person, Token for a Bearer access token.</param>
/// <param name="PrincipalKind">User (a person) or Agent (a machine client; every token acts as one).</param>
/// <param name="Id">The principal id: what board members and invites refer to. Agents tell a board owner this, or their issuer and client id, to be added.</param>
/// <param name="DisplayName">The name others see: the chosen display name, or a fallback such as "User 1a2b" or "Agent my-client".</param>
/// <param name="ChosenDisplayName">The display name the principal set, or null when they have not picked one.</param>
/// <param name="SuggestedDisplayName">Sessions only: the sign-in provider's name for the person, offered while no name is chosen. Never stored.</param>
/// <param name="Provider">Session: the sign-in provider used. Token: the token issuer id.</param>
/// <param name="ClientId">Tokens only: the client id the agent is known by.</param>
/// <param name="Identities">Sessions only: the sign-ins linked to this user.</param>
/// <param name="Scopes">What the caller may do: boards:read and/or boards:write. Sessions have both.</param>
/// <param name="IsAdmin">Sessions only: whether this person is an admin of this PrikBoard (see the admin endpoints).</param>
public record Caller(
    CallerKind Kind,
    PrincipalKind PrincipalKind,
    Guid Id,
    string DisplayName,
    string? ChosenDisplayName,
    string? SuggestedDisplayName,
    string? Provider,
    string? ClientId,
    IReadOnlyList<LinkedIdentity> Identities,
    IReadOnlyList<string> Scopes,
    bool IsAdmin);

/// <summary>A sign-in linked to a user.</summary>
/// <param name="Id">Identity id, used to unlink it.</param>
/// <param name="Provider">Sign-in provider id, for example "github".</param>
/// <param name="ProviderName">The provider's label, for example "GitHub".</param>
/// <param name="LinkedAt">When it was linked (UTC).</param>
public record LinkedIdentity(Guid Id, string Provider, string ProviderName, DateTimeOffset LinkedAt);

/// <summary>Changes the caller's own profile.</summary>
/// <param name="DisplayName">The name others see, at most 100 characters; an empty string clears it.</param>
public record UpdateMeRequest([property: StringLength(100)] string? DisplayName = null);

/// <summary>A trusted token issuer, needed to add an agent to a board.</summary>
/// <param name="Id">Issuer id as configured, for example "auth0".</param>
public record TokenIssuer(string Id);
