using System.Text.Json.Serialization;

namespace PrikBoard.Api.Domain;

/// <summary>
/// A personal access token: a person lets an agent (Claude Code, a script) use the API and MCP on their behalf. An
/// <see cref="AccessTokenKind.Agent"/> token acts as its own <see cref="Agent"/> (so changes show as that agent), with
/// the owner's access to boards capped at <see cref="MaxRole"/> and optionally limited to <see cref="BoardIds"/>. An
/// <see cref="AccessTokenKind.App"/> token signs one of PrikBoard's own apps (the VS Code extension) in as the owner.
/// Only a hash of the token is stored.
/// </summary>
public class AccessToken
{
    public const int DefaultDays = 90;
    public const int MaxDays = 365;

    /// <summary>Per person, so a leaked session cannot mint tokens without bound.</summary>
    public const int MaxPerOwner = 50;

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public AccessTokenKind Kind { get; set; } = AccessTokenKind.Agent;

    public Guid OwnerId { get; set; }
    public User? Owner { get; set; }

    /// <summary>
    /// The agent an <see cref="AccessTokenKind.Agent"/> token acts as (its display name is the token's name). Null for
    /// app tokens.
    /// </summary>
    public Guid? AgentId { get; set; }
    public Agent? Agent { get; set; }

    /// <summary>The name of an <see cref="AccessTokenKind.App"/> token, for example "VS Code on laptop"; agent tokens use their agent's name.</summary>
    public string? Name { get; set; }

    /// <summary>SHA-256 of the token (256 random bits, so no key or slow hash is needed).</summary>
    public required byte[] TokenHash { get; set; }

    /// <summary>The last characters of the token, to tell tokens apart in a list.</summary>
    public required string Hint { get; set; }

    /// <summary>Viewer (read-only, scope boards:read) or Editor (boards:read and boards:write). Never Owner. App tokens ignore it.</summary>
    public BoardRole MaxRole { get; set; }

    /// <summary>The boards the token may use; null for all of the owner's boards.</summary>
    public List<Guid>? BoardIds { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>Updated at most about once a minute.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }
}

/// <summary>Who a personal access token acts as.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<AccessTokenKind>))]
public enum AccessTokenKind
{
    /// <summary>Its own agent, with the owner's board access capped and limited.</summary>
    Agent = 1,

    /// <summary>The owner themselves, from a PrikBoard app they signed in (the VS Code extension).</summary>
    App = 2,

    // 3 was Discord, for the Discord bot when it ran on its own; the DiscordBotInApi migration deleted those tokens.
}
