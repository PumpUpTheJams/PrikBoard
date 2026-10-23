using System.Security.Cryptography;

namespace PrikBoard.Api.Domain;

/// <summary>
/// A file kept on disk by <see cref="Data.FileStore"/>, such as an image or an avatar. The row holds what the API needs
/// to serve it; the file itself is named after <see cref="Id"/>, never after <see cref="FileName"/>, in the folder of
/// its <see cref="BoardId"/>. A file's content never changes: new content is a new file. A row is only ever inserted
/// together with the row that refers to it
/// (<see cref="BoardImage.FileId"/>, <see cref="User.AvatarFileId"/>), and the sweep deletes rows nothing refers to.
/// </summary>
public class StoredFile
{
    public const int MaxFileNameLength = 255;
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>
    /// The board the file belongs to, which is its folder on disk; null for files of no board (avatars). Never changes, as
    /// images never move between boards. Not a foreign key: the sweep deletes the file once its board is gone.
    /// </summary>
    public Guid? BoardId { get; set; }
    /// <summary>The name to download it as, e.g. "ingest-errors.png".</summary>
    public required string FileName { get; set; }
    /// <summary>Detected from the file itself, see <see cref="ImageFormats"/>.</summary>
    public required string ContentType { get; set; }
    public long Size { get; set; }
    public required byte[] Sha256 { get; set; }
    /// <summary>When the content was added; served as Last-Modified.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>The record for <paramref name="data"/>; the file still has to be written (<see cref="Data.FileStore.WriteAsync"/>).</summary>
    public static StoredFile For(Guid? boardId, string fileName, string contentType, ReadOnlySpan<byte> data, DateTimeOffset? createdAt = null) => new()
    {
        BoardId = boardId,
        FileName = fileName,
        ContentType = contentType,
        Size = data.Length,
        Sha256 = SHA256.HashData(data),
        CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
    };
}
