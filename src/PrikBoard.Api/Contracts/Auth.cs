using System.Text.Json.Serialization;

namespace PrikBoard.Api.Contracts;

/// <summary>A sign-in provider a person can pick in the browser.</summary>
/// <param name="Id">Provider id, used in the login URL /api/auth/login/{id}.</param>
/// <param name="DisplayName">Label for the sign-in button, for example "Google".</param>
public record AuthProvider(string Id, string DisplayName);

/// <summary>How a request authenticated.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<CallerKind>))]
public enum CallerKind
{
    /// <summary>A person signed in through the browser, with a session cookie.</summary>
    Session,

    /// <summary>A Bearer access token from a trusted issuer, for example a machine using Client Credentials.</summary>
    Token,
}

/// <summary>Who the request is authenticated as.</summary>
/// <param name="Kind">Session for a signed-in person, Token for a Bearer access token.</param>
/// <param name="Id">For a session the PrikBoard user id; for a token its subject (sub) claim.</param>
/// <param name="Name">Display name; for a machine token the client id.</param>
/// <param name="Email">Verified email of a signed-in person, when the provider shared one.</param>
/// <param name="Provider">Id of the sign-in provider or token issuer, for example "google" or "auth0".</param>
/// <param name="Scopes">What the caller may do: boards:read and/or boards:write. Sessions have both.</param>
public record Caller(CallerKind Kind, string Id, string Name, string? Email, string Provider, IReadOnlyList<string> Scopes);
