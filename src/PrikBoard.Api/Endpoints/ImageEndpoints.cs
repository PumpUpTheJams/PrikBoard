using System.Buffers;
using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;
using PrikBoard.Api.Live;

namespace PrikBoard.Api.Endpoints;

public static class ImageEndpoints
{
    public static RouteGroupBuilder MapImageEndpoints(this RouteGroupBuilder api)
    {
        var images = api.MapGroup("/").WithTags("Images");

        images.MapPost("/boards/{boardId:guid}/images", UploadImage)
            .RequireBoardRole(BoardRole.Editor)
            .WithName("uploadImage")
            .WithSummary("Upload an image")
            .WithDescription("Send the file itself as the request body with its content type (image/png, image/jpeg, image/gif or " +
                             "image/webp; at most 10 MB, 413 otherwise), for example curl --data-binary @shot.png -H \"Content-Type: image/png\". " +
                             "Query parameters: fileName, cardId (attach to a card instead of the board), x and y (top-left on the " +
                             "board, or on the card's canvas with cardId; default: a free spot), width (display width; default: the image's own width, " +
                             "at most 360) and caption. The format and pixel size are read from the file; other formats, SVG included, get a 415.")
            .Accepts<Stream>(ImageFormats.ContentTypes[0], ImageFormats.ContentTypes[1..])
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        images.MapPost("/boards/{boardId:guid}/image-uploads", CreateImageUpload)
            .RequireBoardRole(BoardRole.Editor)
            .Quiet()
            .WithName("createImageUpload")
            .WithSummary("Get a one-time image upload URL")
            .WithDescription("For agents that can run commands: instead of sending the file through this API (or base64 through MCP), " +
                             "get a URL and send the file there with a plain PUT, e.g. curl -T shot.png \"<uploadUrl>\". The URL needs no " +
                             "authentication, adds one image where this request says (a card, x and y, width, caption, file name) as you, " +
                             $"and expires after {ImageUpload.Lifetime.TotalMinutes:0} minutes. Your access is checked again on upload. " +
                             $"At most {ImageUpload.MaxPendingPerPrincipal} unused URLs per caller (409 beyond that).")
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict);

        images.MapPut("/image-uploads/{token}", CompleteImageUpload)
            .AllowAnonymous()
            .NoBoardScope()
            .WithName("completeImageUpload")
            .WithSummary("Upload an image to a one-time URL")
            .WithDescription("The uploadUrl that createImageUpload returns. Send the file itself as the body (PNG, JPEG, GIF or WebP, at most 10 MB; " +
                             "the format is read from the file, so the content type does not matter). No authentication: the URL is the key. " +
                             "Answers the new image (201); 404 when the URL is unknown, used or expired, or its creator lost access. " +
                             "After a 400, 413 or 415 the URL can be used again.")
            .Accepts<Stream>(ImageFormats.ContentTypes[0], [.. ImageFormats.ContentTypes[1..], "application/octet-stream"])
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        images.MapGet("/images/{imageId:guid}/content", GetImageContent)
            .RequireBoardRole(BoardRole.Viewer, BoardScope.Image)
            .AllowVisitors()
            .WithName("getImageContent")
            .WithSummary("Download an image")
            .WithDescription("Returns the image file with its content type, inline but with its own name for saving. An image's content " +
                             "never changes, so it can be cached for good; revalidating with its ETag answers 304, and byte ranges are served.");

        images.MapPatch("/images/{imageId:guid}", UpdateImage)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Image)
            .WithName("updateImage")
            .WithSummary("Move, resize or re-caption an image")
            .WithDescription("Changes any of position, display width (the height follows the aspect ratio) and caption, " +
                             "attaches the image to a card (cardId), or puts an attached image back on the board (detach). " +
                             "Omitted fields stay unchanged.")
            .ProducesValidationProblem();

        images.MapDelete("/images/{imageId:guid}", DeleteImage)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Image)
            .WithName("deleteImage")
            .WithSummary("Delete an image")
            .WithDescription($"Deletes the image. restoreImage brings it back within {Tombstones.RetentionText}.");

        images.MapPost("/images/{imageId:guid}/restore", RestoreImage)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Image)
            .WithName("restoreImage")
            .WithSummary("Restore a deleted image")
            .WithDescription($"Brings back an image deleted in the last {Tombstones.RetentionText}, with the same id, place and file. " +
                             "409 when it is not deleted, or when it was attached to a card that is deleted (restore the card instead).")
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    private static async Task<Results<Created<ImageDetail>, NotFound, ValidationProblem, ProblemHttpResult>> UploadImage(
        Guid boardId, Stream body, HttpRequest request, ClaimsPrincipal principal, PrikBoardDbContext db, FileStore files, CancellationToken ct,
        string? fileName = null, Guid? cardId = null, int? x = null, int? y = null, int? width = null, string? caption = null)
    {
        // Only image types (or octet-stream): form encodings would let another site post here with someone's session cookie.
        var declared = request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
        if (declared is null || !(declared.StartsWith("image/") || declared == "application/octet-stream"))
        {
            if (!await db.Boards.AnyAsync(b => b.Id == boardId, ct)) return TypedResults.NotFound();
            return Unsupported("Send the image file itself as the request body, with Content-Type image/png, image/jpeg, image/gif or image/webp.");
        }
        return await AddImage(boardId, body, principal.PrincipalId(), db, files, ct, fileName, cardId, x, y, width, caption);
    }

    /// <summary>
    /// uploadImage after its content type check: reads the file from <paramref name="body"/>, detects its format and places it.
    /// The add_image MCP tool calls this with the decoded file, and completeImageUpload with the upload URL's placement.
    /// </summary>
    internal static async Task<Results<Created<ImageDetail>, NotFound, ValidationProblem, ProblemHttpResult>> AddImage(
        Guid boardId, Stream body, Guid addedById, PrikBoardDbContext db, FileStore files, CancellationToken ct,
        string? fileName = null, Guid? cardId = null, int? x = null, int? y = null, int? width = null, string? caption = null)
    {
        if (!await db.Boards.AnyAsync(b => b.Id == boardId, ct)) return TypedResults.NotFound();

        if (InvalidPlacement(x, y, width, caption) is { } invalid) return invalid;
        var card = cardId is null ? null : await db.Cards.AsNoTracking().FirstOrDefaultAsync(n => n.Id == cardId && n.BoardId == boardId, ct);
        if (cardId is not null && card is null) return Problems.CardNotOnBoard();

        var data = await ReadUpTo(body, ImageFormats.MaxBytes, ct);
        if (data is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Image too large", detail: ImageFormats.TooLarge);
        }
        if (!TryFromBytes(boardId, data, fileName, caption, width, out var image, out var problem)) return Unsupported(problem);

        image.CardId = cardId;
        image.AddedBy = await db.Principals.FindAsync([addedById], ct);
        await PlaceAsync(db, image, card, x, y, ct);
        Layout.Place(image, await BucketEndpoints.StackAsync(db, boardId, ct));

        // On a card: saved under its lock, so a card deleted while the file came in doesn't get a live image (completeImageUpload
        // already has a transaction, which spends the URL only with a saved image).
        await using var transaction = cardId is null || db.Database.CurrentTransaction is not null ? null : await db.Database.BeginTransactionAsync(ct);
        if (cardId is { } id && !await CardEndpoints.LockLiveCardAsync(db, id, boardId, ct)) return Problems.CardNotOnBoard();
        await files.WriteAsync(image.File!, data, ct);
        db.Images.Add(image);
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return TypedResults.Created($"/api/images/{image.Id}", ImageDetail.From(image));
    }

    /// <summary>
    /// A new image on <paramref name="boardId"/> from a file's bytes (uploads, imports and pastes), at
    /// <paramref name="width"/> (default: its own width, at most <see cref="Layout.DefaultImageWidth"/>), not yet placed,
    /// written or saved. False with the answer in <paramref name="problem"/> when the file is too large or not an image.
    /// </summary>
    internal static bool TryFromBytes(
        Guid boardId, byte[] data, string? fileName, string? caption, int? width,
        [NotNullWhen(true)] out BoardImage? image, [NotNullWhen(false)] out string? problem)
    {
        image = null;
        if (data.Length > ImageFormats.MaxBytes)
        {
            problem = ImageFormats.TooLarge;
            return false;
        }
        if (ImageFormats.Detect(data) is not { } info)
        {
            problem = ImageFormats.NotAnImage;
            return false;
        }
        problem = null;
        image = new BoardImage
        {
            BoardId = boardId,
            PixelWidth = info.Width,
            PixelHeight = info.Height,
            Caption = PatchText.Apply(null, caption),
            File = StoredFile.For(boardId, CleanFileName(fileName, info.ContentType), info.ContentType, data),
        };
        image.Resize(width ?? Math.Min(info.Width, Layout.DefaultImageWidth));
        return true;
    }

    /// <summary>
    /// createImageUpload: checks the placement now (it is checked again on upload) and stores it with who is asking, under
    /// the hash of a new random token that the URL carries. The begin_image_upload MCP tool calls this too.
    /// </summary>
    internal static async Task<Results<Ok<ImageUploadUrl>, ValidationProblem, ProblemHttpResult>> CreateImageUpload(
        Guid boardId, CreateImageUploadRequest request, HttpContext http, PrikBoardDbContext db, CancellationToken ct)
    {
        if (InvalidPlacement(request.X, request.Y, request.Width, request.Caption) is { } invalid) return invalid;
        if (request.CardId is { } cardId && !await db.Cards.AnyAsync(n => n.Id == cardId && n.BoardId == boardId, ct)) return Problems.CardNotOnBoard();

        var principalId = http.User.PrincipalId();
        var now = DateTimeOffset.UtcNow;
        await db.ImageUploads.Where(u => u.PrincipalId == principalId && u.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        if (await db.ImageUploads.CountAsync(u => u.PrincipalId == principalId, ct) >= ImageUpload.MaxPendingPerPrincipal)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Too many upload URLs waiting",
                detail: $"Use the upload URLs you asked for, or wait up to {ImageUpload.Lifetime.TotalMinutes:0} minutes for them to expire.");
        }

        var caller = BoardCaller.Of(http.User);
        var token = ImageUpload.Prefix + Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var upload = new ImageUpload
        {
            BoardId = boardId,
            CardId = request.CardId,
            X = request.X,
            Y = request.Y,
            Width = request.Width,
            Caption = request.Caption,
            FileName = request.FileName,
            PrincipalId = principalId,
            MemberId = caller.MemberId,
            Cap = caller.Cap,
            AccessTokenId = http.User.IsAccessToken() ? http.User.TokenId() : null,
            TokenHash = AccessTokens.Hash(token),
            ExpiresAt = now.Add(ImageUpload.Lifetime),
        };
        db.ImageUploads.Add(upload);
        await db.SaveChangesAsync(ct);

        var url = $"{ClipEndpoints.ServerUrl(http.Request)}/api/image-uploads/{token}";
        return TypedResults.Ok(new ImageUploadUrl(url, upload.ExpiresAt, $"curl -sS -T FILE \"{url}\""));
    }

    private static async Task<Results<Created<ImageDetail>, NotFound, ValidationProblem, ProblemHttpResult>> CompleteImageUpload(
        string token, Stream body, HttpContext http, PrikBoardDbContext db, FileStore files, BoardEvents events, CancellationToken ct)
    {
        if (!token.StartsWith(ImageUpload.Prefix, StringComparison.Ordinal)) return TypedResults.NotFound();
        var hash = AccessTokens.Hash(token);

        // The row lock makes a second upload to the same URL wait, and then find it spent.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var upload = await db.ImageUploads
            .FromSql($"""SELECT * FROM "ImageUploads" WHERE "TokenHash" = {hash} FOR UPDATE""")
            .FirstOrDefaultAsync(ct);
        if (upload is null || upload.ExpiresAt <= DateTimeOffset.UtcNow) return TypedResults.NotFound();
        db.ImageUploads.Remove(upload);
        if (!await StillAllowedAsync(db, upload, ct))
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return TypedResults.NotFound();
        }

        // Saves the image and spends the URL together; a failed upload saves nothing, so the URL can be used again.
        var result = await AddImage(upload.BoardId, body, upload.PrincipalId, db, files, ct,
            upload.FileName, upload.CardId, upload.X, upload.Y, upload.Width, upload.Caption);
        if (result.Result is not Created<ImageDetail>) return result;
        await transaction.CommitAsync(ct);
        await events.ChangedAsync(upload.BoardId, upload.PrincipalId);
        return result;
    }

    /// <summary>
    /// Whoever asked for the upload URL may still add images there: their (or their token owner's) role, capped as it
    /// was, is still Editor or more, and the access token they used is still valid and still reaches the board.
    /// </summary>
    private static async Task<bool> StillAllowedAsync(PrikBoardDbContext db, ImageUpload upload, CancellationToken ct)
    {
        var role = await BoardAccess.RoleOnBoardAsync(db, upload.BoardId, upload.MemberId, ct);
        if (role is not { } member || (member < upload.Cap ? member : upload.Cap) < BoardRole.Editor) return false;
        if (upload.AccessTokenId is not { } tokenId) return true;
        var now = DateTimeOffset.UtcNow;
        return await db.AccessTokens.AnyAsync(
            t => t.Id == tokenId && t.ExpiresAt > now && (t.BoardIds == null || t.BoardIds.Contains(upload.BoardId)), ct);
    }

    private static async Task<Results<FileContentHttpResult, PhysicalFileHttpResult, NotFound>> GetImageContent(
        Guid imageId, HttpResponse response, PrikBoardDbContext db, FileStore files, CancellationToken ct)
    {
        var file = await db.Images.AsNoTracking().Where(i => i.Id == imageId).Select(i => i.File).FirstOrDefaultAsync(ct);
        if (file is null) return TypedResults.NotFound();
        return await StoredFileResults.ServeAsync(file, files, response, ct);
    }

    private static async Task<Results<Ok<ImageDetail>, NotFound, ValidationProblem>> UpdateImage(
        Guid imageId, UpdateImageRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        var image = await db.Images.Include(i => i.AddedBy).Include(i => i.File).FirstOrDefaultAsync(i => i.Id == imageId, ct);
        if (image is null) return TypedResults.NotFound();

        var detach = request.Detach == true;
        var moving = (request.CardId is { } to && to != image.CardId) || (detach && image.CardId is not null);
        if (request.CardId is not null && detach) return Problems.Invalid("detach", "Give either a cardId or detach, not both.");
        if (moving && request.X is null != request.Y is null) return Problems.Invalid(request.X is null ? "x" : "y", "Give x and y together.");
        if (request.Width is { } width) image.Resize(width);
        if (moving)
        {
            var card = detach ? null : await db.Cards.AsNoTracking().FirstOrDefaultAsync(n => n.Id == request.CardId && n.BoardId == image.BoardId, ct);
            if (!detach && card is null)
            {
                return Problems.CardNotOnBoard();
            }
            image.CardId = card?.Id;
            await PlaceAsync(db, image, card, request.X, request.Y, ct);
        }
        else if (image.CardId is null)
        {
            (image.X, image.Y) = (Layout.Snap(request.X ?? image.X), Layout.Snap(request.Y ?? image.Y));
        }
        else
        {
            (image.X, image.Y) = Layout.OnCardCanvas(request.X ?? image.X, request.Y ?? image.Y, image.Width, image.Height);
        }
        // An image that moved (or changed size) joins the bucket it now sits in; only a new caption leaves it where it is.
        if (moving || request.X is not null || request.Y is not null || request.Width is not null)
        {
            Layout.Place(image, await BucketEndpoints.StackAsync(db, image.BoardId, ct));
        }
        image.Caption = PatchText.Apply(image.Caption, request.Caption);
        image.UpdatedAt = DateTimeOffset.UtcNow;

        // Onto a card: saved under its lock, so the card can't be deleted in between and leave this image live.
        var onto = moving ? image.CardId : null;
        await using var transaction = onto is null ? null : await db.Database.BeginTransactionAsync(ct);
        if (onto is { } cardId && !await CardEndpoints.LockLiveCardAsync(db, cardId, image.BoardId, ct)) return Problems.CardNotOnBoard();
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return TypedResults.Ok(ImageDetail.From(image));
    }

    /// <summary>
    /// Puts <paramref name="image"/> at X and Y (snapped) or in a free spot: on <paramref name="card"/>'s canvas, or on the
    /// board when it is null.
    /// </summary>
    private static async Task PlaceAsync(PrikBoardDbContext db, BoardImage image, Card? card, int? x, int? y, CancellationToken ct)
    {
        if (card is not null)
        {
            var attached = await db.Images.AsNoTracking().Where(i => i.CardId == card.Id && i.Id != image.Id).ToListAsync(ct);
            (image.X, image.Y) = x is { } cardX && y is { } cardY
                ? Layout.OnCardCanvas(cardX, cardY, image.Width, image.Height)
                : Layout.NextCardSpot(Layout.TakenOnCard(card, attached), image.Width, image.Height);
            return;
        }
        (image.X, image.Y) = x is { } boardX && y is { } boardY
            ? (Layout.Snap(boardX), Layout.Snap(boardY))
            : Layout.NextImageSpot(
                await db.Buckets.Where(b => b.BoardId == image.BoardId).ToListAsync(ct),
                await db.Cards.AsNoTracking().Where(n => n.BoardId == image.BoardId).ToListAsync(ct),
                await db.Images.AsNoTracking().Where(i => i.BoardId == image.BoardId && i.Id != image.Id).ToListAsync(ct));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteImage(
        Guid imageId, ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct)
    {
        var image = await db.Images.AsNoTracking().Where(i => i.Id == imageId)
            .Select(i => new { i.BoardId, i.CardId, Label = i.Caption ?? i.File!.FileName })
            .FirstOrDefaultAsync(ct);
        var deleted = await db.Images.Where(i => i.Id == imageId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.DeletedAt, DateTimeOffset.UtcNow), ct);
        if (deleted == 0) return TypedResults.NotFound();
        if (image?.CardId is { } cardId)
        {
            CardHistory.Record(db, principal, cardId, image.BoardId, new(CardChangeKind.ImageRemoved, Before: image.Label, Ref: imageId));
            await db.SaveChangesAsync(ct);
        }
        return TypedResults.NoContent();
    }

    internal static async Task<Results<Ok<ImageDetail>, NotFound, ProblemHttpResult>> RestoreImage(
        Guid imageId, PrikBoardDbContext db, CancellationToken ct)
    {
        var image = await db.Images.WithDeleted()
            .Include(i => i.AddedBy)
            .Include(i => i.File)
            .FirstOrDefaultAsync(i => i.Id == imageId, ct);
        if (image is null) return TypedResults.NotFound();
        if (image.DeletedAt is null) return Problems.NotDeleted("image");
        if (image.CardId is { } cardId && !await db.Cards.AnyAsync(n => n.Id == cardId, ct))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Card deleted",
                detail: "The image is attached to a deleted card. Restore the card (restoreCard) to bring back its images.");
        }

        image.DeletedAt = null;
        Layout.Recheck(image, await BucketEndpoints.StackAsync(db, image.BoardId, ct));
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ImageDetail.From(image));
    }

    /// <summary>The whole body, or null when it is longer than <paramref name="limit"/> bytes.</summary>
    private static async Task<byte[]?> ReadUpTo(Stream body, int limit, CancellationToken ct)
    {
        // A stream that knows its length (add_image's decoded file) goes straight into an array of that size.
        if (body.CanSeek)
        {
            var length = body.Length - body.Position;
            if (length > limit) return null;
            var data = new byte[length];
            await body.ReadExactlyAsync(data, ct);
            return data;
        }

        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(FileStore.BufferSize);
        try
        {
            int read;
            while ((read = await body.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > limit) return null;
                buffer.Write(chunk, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
        return buffer.ToArray();
    }

    /// <summary>Just the file name part, without control characters; a generic name when none is usable.</summary>
    internal static string CleanFileName(string? fileName, string contentType)
    {
        var name = PatchText.OneLine(Path.GetFileName(fileName ?? ""));
        return name.Length > 0 ? Truncate(name, StoredFile.MaxFileNameLength) : $"image.{ImageFormats.ExtensionOf(contentType)}";
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    /// <summary>The checks on where an image goes that DataAnnotations can't make (uploadImage takes them as query parameters).</summary>
    private static ValidationProblem? InvalidPlacement(int? x, int? y, int? width, string? caption)
    {
        if (x is null != y is null) return Problems.Invalid(x is null ? "x" : "y", "Give x and y together.");
        if (x is < Layout.MinCoordinate or > Layout.MaxCoordinate || y is < Layout.MinCoordinate or > Layout.MaxCoordinate)
        {
            return Problems.Invalid("x", $"Coordinates run from {Layout.MinCoordinate} to {Layout.MaxCoordinate}.");
        }
        if (width is < Layout.MinImageWidth or > Layout.MaxImageWidth)
        {
            return Problems.Invalid("width", $"Width runs from {Layout.MinImageWidth} to {Layout.MaxImageWidth}.");
        }
        if (caption?.Length > BoardImage.MaxCaptionLength) return Problems.Invalid("caption", $"A caption is at most {BoardImage.MaxCaptionLength} characters.");
        return null;
    }

    private static ProblemHttpResult Unsupported(string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Unsupported image", detail: detail);
}
