using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Endpoints;

internal static class StoredFileResults
{
    /// <summary>
    /// Answers with the file, shown inline but saved under its own name. A file's content never changes for its id and
    /// access is checked on every request, so browsers may cache it privately for good. The id is the ETag, so
    /// revalidations get a 304, and ranges are served. Small files come from memory, larger ones straight from disk.
    /// </summary>
    public static async Task<Results<FileContentHttpResult, PhysicalFileHttpResult, NotFound>> ServeAsync(
        StoredFile file, FileStore files, HttpResponse response, CancellationToken ct)
    {
        var data = await files.ReadSmallAsync(file, ct);
        if (data is null && !files.Exists(file)) return TypedResults.NotFound();

        var disposition = new ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(file.FileName);
        response.Headers[HeaderNames.ContentDisposition] = disposition.ToString();
        response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        response.Headers[HeaderNames.CacheControl] = "private, max-age=31536000, immutable";

        var entityTag = new EntityTagHeaderValue($"\"{file.Id:N}\"");
        return data is not null
            ? TypedResults.File(data, file.ContentType, lastModified: file.CreatedAt, entityTag: entityTag, enableRangeProcessing: true)
            : TypedResults.PhysicalFile(files.PathOf(file), file.ContentType, lastModified: file.CreatedAt, entityTag: entityTag,
                enableRangeProcessing: true);
    }
}
