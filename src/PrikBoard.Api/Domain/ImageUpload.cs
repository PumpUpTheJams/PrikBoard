namespace PrikBoard.Api.Domain;

/// <summary>
/// A one-time upload URL for an image (createImageUpload, MCP begin_image_upload): an agent sends the file there with a
/// plain PUT (curl -T), so the bytes never pass through its conversation as base64. The URL's token is the credential;
/// only its hash is stored. It holds where the image goes and who asked, whose access is checked again on upload. Spent on
/// the first upload attempt; expired ones are deleted by <see cref="Data.TombstonePurger"/>.
/// </summary>
public class ImageUpload
{
    /// <summary>How long the URL can be used.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    /// <summary>URLs waiting to be used per caller, so a token in a loop cannot pile them up.</summary>
    public const int MaxPendingPerPrincipal = 20;

    /// <summary>Starts every upload token, so it is recognisable in logs and to secret scanners.</summary>
    public const string Prefix = "pb_upl_";

    public Guid Id { get; set; } = Guid.CreateVersion7();

    public Guid BoardId { get; set; }

    /// <summary>The note to attach the image to, or null for the board.</summary>
    public Guid? NoteId { get; set; }

    public int? X { get; set; }
    public int? Y { get; set; }
    public int? Width { get; set; }
    public string? Caption { get; set; }
    public string? FileName { get; set; }

    /// <summary>Who asked for the URL: the image is added by them.</summary>
    public Guid PrincipalId { get; set; }

    /// <summary>Whose board membership counts (the owner of a personal access token, or <see cref="PrincipalId"/>).</summary>
    public Guid MemberId { get; set; }

    /// <summary>The highest role the caller had, as capped by their token.</summary>
    public BoardRole Cap { get; set; }

    /// <summary>The personal access token that asked, which must still be valid on upload; null for other callers.</summary>
    public Guid? AccessTokenId { get; set; }

    /// <summary>SHA-256 of the token (256 random bits).</summary>
    public required byte[] TokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset ExpiresAt { get; set; }
}
