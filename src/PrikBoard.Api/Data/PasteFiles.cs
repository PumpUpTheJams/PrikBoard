using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Data;

/// <summary>
/// Image files uploaded for a paste (uploadPasteImage) ahead of the paste itself, so a big clip doesn't travel as one
/// request. Each is written to its board's folder at once but gets no row until pasteItems inserts one together with its
/// image, as <see cref="FileStore"/> wants. The token naming one is protected by Data Protection and says whose it is and
/// until when, so it needs no table: a file whose paste never comes is a file without a row, which the sweep deletes.
/// </summary>
public static class PasteFiles
{
    private const string Purpose = "PrikBoard.PasteFile";

    /// <summary>How long an uploaded file waits for its paste.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(1);

    /// <summary>A file on disk, in <paramref name="BoardId"/>'s folder, waiting for a paste by <paramref name="PrincipalId"/>.</summary>
    public sealed record Pending(
        Guid FileId, Guid BoardId, Guid PrincipalId, string ContentType, long Size, byte[] Sha256, int PixelWidth, int PixelHeight);

    /// <summary>The token for <paramref name="file"/>, good until <paramref name="expiresAt"/>.</summary>
    public static string Protect(IDataProtectionProvider protection, Pending file, DateTimeOffset expiresAt) =>
        Protector(protection).Protect(JsonSerializer.Serialize(file), expiresAt);

    /// <summary>The file a token names, or null when it is not one of ours or has expired.</summary>
    public static Pending? Unprotect(IDataProtectionProvider protection, string token)
    {
        try
        {
            return JsonSerializer.Deserialize<Pending>(Protector(protection).Unprotect(token));
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    /// <summary>The row for <paramref name="file"/>, named <paramref name="fileName"/>; inserted together with its image.</summary>
    public static StoredFile Row(Pending file, string fileName) => new()
    {
        Id = file.FileId,
        BoardId = file.BoardId,
        FileName = fileName,
        ContentType = file.ContentType,
        Size = file.Size,
        Sha256 = file.Sha256,
    };

    private static ITimeLimitedDataProtector Protector(IDataProtectionProvider protection) =>
        protection.CreateProtector(Purpose).ToTimeLimitedDataProtector();
}
