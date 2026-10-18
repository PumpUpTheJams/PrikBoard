using System.ComponentModel.DataAnnotations;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Contracts;

/// <summary>A pending invite link. The link itself is only shown once, when the invite is created.</summary>
/// <param name="Id">Invite id, used to revoke it.</param>
/// <param name="Role">The role whoever accepts it gets.</param>
/// <param name="CreatedBy">Display name of the owner who created it, if they still exist.</param>
/// <param name="CreatedAt">When it was created (UTC).</param>
/// <param name="ExpiresAt">When it stops working (UTC).</param>
public record BoardInviteDetail(Guid Id, BoardRole Role, string? CreatedBy, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt);

/// <summary>Creates a single-use invite link.</summary>
/// <param name="Role">The role whoever accepts it gets.</param>
/// <param name="ExpiresInDays">Days until it expires, 1 to 30; defaults to 7.</param>
public record CreateInviteRequest(BoardRole Role, [property: Range(1, BoardInvite.MaxDays)] int? ExpiresInDays = null);

/// <summary>A new invite. Share <paramref name="Path"/> (after the site's address); the token is not shown again.</summary>
/// <param name="Id">Invite id, used to revoke it.</param>
/// <param name="Role">The role whoever accepts it gets.</param>
/// <param name="ExpiresAt">When it stops working (UTC).</param>
/// <param name="Token">The secret token; anyone who has it (once) can join.</param>
/// <param name="Path">The link path in the web app, for example "/#/invite/abc…".</param>
public record CreatedInvite(Guid Id, BoardRole Role, DateTimeOffset ExpiresAt, string Token, string Path);

/// <summary>An invite token, sent in the body so it stays out of logs.</summary>
/// <param name="Token">The token from the invite link.</param>
public record InviteTokenRequest([property: Required, StringLength(100)] string Token);

/// <summary>What accepting an invite would do.</summary>
/// <param name="BoardId">The board.</param>
/// <param name="BoardName">Its name.</param>
/// <param name="Role">The role the invite grants.</param>
/// <param name="InvitedBy">Display name of the owner who created it, if they still exist.</param>
/// <param name="ExpiresAt">When it stops working (UTC).</param>
/// <param name="YourRole">Your current role on the board, or null when you are not a member.</param>
public record InvitePreview(Guid BoardId, string BoardName, BoardRole Role, string? InvitedBy, DateTimeOffset ExpiresAt, BoardRole? YourRole);

/// <summary>The result of accepting an invite.</summary>
/// <param name="BoardId">The board you are now a member of.</param>
/// <param name="Role">Your role: the invite's, or the one you already had if it was higher.</param>
public record AcceptedInvite(Guid BoardId, BoardRole Role);
