using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Net.Http.Headers;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Endpoints;

public static class ImageEndpoints
{
    public static RouteGroupBuilder MapImageEndpoints(this RouteGroupBuilder api)
    {
        var images = api.MapGroup("/").WithTags("Images");

        images.MapPost("/boards/{boardId:guid}/images", UploadImage)
            .RequireBoardRole(BoardRole.Editor)
            .WithName("uploadImage")
            .WithSummary("Add an image or GIF to a board")
            .WithDescription("Send the file itself as the request body with its content type (image/png, image/jpeg, image/gif or " +
                             "image/webp; at most 10 MB), for example curl --data-binary @shot.png -H \"Content-Type: image/png\". " +
                             "Query parameters: fileName, noteId (attach to a note instead of the board), x and y (top-left on the " +
                             "board, or on the note's canvas with noteId; default: a free spot), width (display width; default: the image's own width, " +
                             "at most 360) and caption. The format and pixel size are read from the file; SVG is not accepted.")
            .Accepts<Stream>(ImageFormats.ContentTypes[0], ImageFormats.ContentTypes[1..])
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status415UnsupportedMediaType);

        images.MapGet("/images/{imageId:guid}/content", GetImageContent)
            .RequireBoardRole(BoardRole.Viewer, BoardScope.Image)
            .WithName("getImageContent")
            .WithSummary("Download an image")
            .WithDescription("Returns the image file with its content type. An image's content never changes, so it can be cached.");

        images.MapPatch("/images/{imageId:guid}", UpdateImage)
            .RequireBoardRole(BoardRole.Editor, BoardScope.Image)
            .WithName("updateImage")
            .WithSummary("Move, resize or re-caption an image")
            .WithDescription("Changes any of position, display width (the height follows the aspect ratio) and caption, " +
                             "attaches the image to a note (noteId), or puts an attached image back on the board (detach). " +
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
                             "409 when it is not deleted, or when it was attached to a note that is deleted (restore the note instead).")
            .ProducesProblem(StatusCodes.Status409Conflict);

        return api;
    }

    private static async Task<Results<Created<ImageDetail>, NotFound, ValidationProblem, ProblemHttpResult>> UploadImage(
        Guid boardId, Stream body, HttpRequest request, ClaimsPrincipal principal, PrikBoardDbContext db, CancellationToken ct,
        string? fileName = null, Guid? noteId = null, int? x = null, int? y = null, int? width = null, string? caption = null)
    {
        if (!await db.Boards.AnyAsync(b => b.Id == boardId, ct)) return TypedResults.NotFound();

        // Only image types (or octet-stream): form encodings would let another site post here with someone's session cookie.
        var declared = request.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
        if (declared is null || !(declared.StartsWith("image/") || declared == "application/octet-stream"))
        {
            return Unsupported("Send the image file itself as the request body, with Content-Type image/png, image/jpeg, image/gif or image/webp.");
        }

        if ((Invalid(x is null != y is null, x is null ? "x" : "y", "Give x and y together.")
            ?? Invalid(x is < Layout.MinCoordinate or > Layout.MaxCoordinate || y is < Layout.MinCoordinate or > Layout.MaxCoordinate, "x", $"Coordinates run from {Layout.MinCoordinate} to {Layout.MaxCoordinate}.")
            ?? Invalid(width is < Layout.MinImageWidth or > Layout.MaxImageWidth, "width", $"Width runs from {Layout.MinImageWidth} to {Layout.MaxImageWidth}.")
            ?? Invalid(caption?.Length > 500, "caption", "A caption is at most 500 characters.")) is { } invalid)
        {
            return invalid;
        }
        var note = noteId is null ? null : await db.Notes.FirstOrDefaultAsync(n => n.Id == noteId && n.BoardId == boardId, ct);
        if (noteId is not null && note is null)
        {
            return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["noteId"] = ["The note does not exist on this board."] });
        }

        var data = await ReadUpTo(body, ImageFormats.MaxBytes, ct);
        if (data is null)
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status413PayloadTooLarge, title: "Image too large",
                detail: $"Images can be at most {ImageFormats.MaxBytes / 1024 / 1024} MB.");
        }
        if (ImageFormats.Detect(data) is not { } info)
        {
            return Unsupported("The file is not a PNG, JPEG, GIF or WebP image (or it is larger than 20000 pixels on a side).");
        }

        var image = new BoardImage
        {
            BoardId = boardId,
            NoteId = noteId,
            FileName = CleanFileName(fileName, info.ContentType),
            ContentType = info.ContentType,
            Size = data.Length,
            PixelWidth = info.Width,
            PixelHeight = info.Height,
            Caption = PatchText.Apply(null, caption),
            AddedBy = await db.Principals.FindAsync([principal.PrincipalId()], ct),
            Content = new ImageContent { Data = data },
        };
        image.Resize(width ?? Math.Min(info.Width, Layout.DefaultImageWidth));
        await PlaceAsync(db, image, note, x, y, ct);

        db.Images.Add(image);
        await db.SaveChangesAsync(ct);
        return TypedResults.Created($"/api/images/{image.Id}", ImageDetail.From(image));
    }

    private static async Task<Results<FileContentHttpResult, NotFound>> GetImageContent(
        Guid imageId, HttpResponse response, PrikBoardDbContext db, CancellationToken ct)
    {
        var image = await db.Images.AsNoTracking()
            .Where(i => i.Id == imageId)
            .Select(i => new { i.ContentType, i.FileName, i.Content!.Data })
            .FirstOrDefaultAsync(ct);
        if (image is null) return TypedResults.NotFound();

        response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        response.Headers[HeaderNames.ContentDisposition] = new ContentDispositionHeaderValue("inline") { FileNameStar = image.FileName }.ToString();
        // Content never changes for an id, and every request is authenticated, so private caching is safe.
        response.Headers[HeaderNames.CacheControl] = "private, max-age=31536000, immutable";
        return TypedResults.File(image.Data, image.ContentType, entityTag: new EntityTagHeaderValue($"\"{imageId:N}\""));
    }

    private static async Task<Results<Ok<ImageDetail>, NotFound, ValidationProblem>> UpdateImage(
        Guid imageId, UpdateImageRequest request, PrikBoardDbContext db, CancellationToken ct)
    {
        var image = await db.Images.Include(i => i.AddedBy).FirstOrDefaultAsync(i => i.Id == imageId, ct);
        if (image is null) return TypedResults.NotFound();

        var detach = request.Detach == true;
        var moving = (request.NoteId is { } to && to != image.NoteId) || (detach && image.NoteId is not null);
        if ((Invalid(request.NoteId is not null && detach, "detach", "Give either a noteId or detach, not both.")
            ?? Invalid(moving && request.X is null != request.Y is null, request.X is null ? "x" : "y", "Give x and y together.")) is { } invalid)
        {
            return invalid;
        }
        if (request.Width is { } width) image.Resize(width);
        if (moving)
        {
            var note = detach ? null : await db.Notes.FirstOrDefaultAsync(n => n.Id == request.NoteId && n.BoardId == image.BoardId, ct);
            if (!detach && note is null)
            {
                return TypedResults.ValidationProblem(new Dictionary<string, string[]> { ["noteId"] = ["The note does not exist on this board."] });
            }
            image.NoteId = note?.Id;
            await PlaceAsync(db, image, note, request.X, request.Y, ct);
        }
        else if (image.NoteId is null)
        {
            (image.X, image.Y) = (Layout.Snap(request.X ?? image.X), Layout.Snap(request.Y ?? image.Y));
        }
        else
        {
            (image.X, image.Y) = Layout.OnNoteCanvas(request.X ?? image.X, request.Y ?? image.Y, image.Width, image.Height);
        }
        image.Caption = PatchText.Apply(image.Caption, request.Caption);
        image.UpdatedAt = DateTimeOffset.UtcNow;

        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ImageDetail.From(image));
    }

    /// <summary>
    /// Puts <paramref name="image"/> at X and Y (snapped) or in a free spot: on <paramref name="note"/>'s canvas, or on the
    /// board when it is null.
    /// </summary>
    private static async Task PlaceAsync(PrikBoardDbContext db, BoardImage image, Note? note, int? x, int? y, CancellationToken ct)
    {
        if (note is not null)
        {
            var attached = await db.Images.Where(i => i.NoteId == note.Id && i.Id != image.Id).ToListAsync(ct);
            (image.X, image.Y) = x is { } noteX && y is { } noteY
                ? Layout.OnNoteCanvas(noteX, noteY, image.Width, image.Height)
                : Layout.NextNoteSpot(Layout.TakenOnNote(note, attached), image.Width, image.Height);
            return;
        }
        (image.X, image.Y) = x is { } boardX && y is { } boardY
            ? (Layout.Snap(boardX), Layout.Snap(boardY))
            : Layout.NextImageSpot(
                await db.Buckets.Where(b => b.BoardId == image.BoardId).ToListAsync(ct),
                await db.Notes.Where(n => n.BoardId == image.BoardId).ToListAsync(ct),
                await db.Images.Where(i => i.BoardId == image.BoardId && i.Id != image.Id).ToListAsync(ct));
    }

    private static async Task<Results<NoContent, NotFound>> DeleteImage(Guid imageId, PrikBoardDbContext db, CancellationToken ct)
    {
        var deleted = await db.Images.Where(i => i.Id == imageId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.DeletedAt, DateTimeOffset.UtcNow), ct);
        return deleted == 0 ? TypedResults.NotFound() : TypedResults.NoContent();
    }

    internal static async Task<Results<Ok<ImageDetail>, NotFound, ProblemHttpResult>> RestoreImage(
        Guid imageId, PrikBoardDbContext db, CancellationToken ct)
    {
        var image = await db.Images.IgnoreQueryFilters([PrikBoardDbContext.SoftDelete])
            .Include(i => i.AddedBy)
            .FirstOrDefaultAsync(i => i.Id == imageId, ct);
        if (image is null) return TypedResults.NotFound();
        if (image.DeletedAt is null) return TagEndpoints.NotDeleted("image");
        if (image.NoteId is { } noteId && !await db.Notes.AnyAsync(n => n.Id == noteId, ct))
        {
            return TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: "Note deleted",
                detail: "The image is attached to a deleted note. Restore the note (restoreNote) to bring back its images.");
        }

        image.DeletedAt = null;
        await db.SaveChangesAsync(ct);
        return TypedResults.Ok(ImageDetail.From(image));
    }

    /// <summary>The whole body, or null when it is longer than <paramref name="limit"/> bytes.</summary>
    private static async Task<byte[]?> ReadUpTo(Stream body, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > limit) return null;
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    /// <summary>Just the file name part, without control characters; a generic name when none is usable.</summary>
    internal static string CleanFileName(string? fileName, string contentType)
    {
        var name = new string(Path.GetFileName(fileName ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return name.Length > 0 ? Truncate(name, 255) : $"image.{contentType["image/".Length..].Replace("jpeg", "jpg")}";
    }

    private static string Truncate(string value, int length) => value.Length <= length ? value : value[..length];

    private static ValidationProblem? Invalid(bool condition, string field, string message) =>
        condition ? TypedResults.ValidationProblem(new Dictionary<string, string[]> { [field] = [message] }) : null;

    private static ProblemHttpResult Unsupported(string detail) =>
        TypedResults.Problem(statusCode: StatusCodes.Status415UnsupportedMediaType, title: "Unsupported image", detail: detail);
}
