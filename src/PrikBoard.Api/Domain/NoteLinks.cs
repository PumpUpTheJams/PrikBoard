namespace PrikBoard.Api.Domain;

/// <summary>
/// Links on notes: web addresses, or another note on PrikBoard by its path in the web app. A note's path has no host,
/// so the link keeps working wherever PrikBoard runs.
/// </summary>
public static class NoteLinks
{
    private const string BoardsPrefix = "/#/boards/";
    private const string NotesSegment = "/notes/";

    /// <summary>Where the web app shows the note: its board, with the note opened up.</summary>
    public static string NotePath(Guid boardId, Guid noteId) => $"{BoardsPrefix}{boardId}{NotesSegment}{noteId}";

    public static bool IsValid(string url) => IsWebUrl(url) || IsNotePath(url.Trim());

    private static bool IsWebUrl(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool IsNotePath(string url)
    {
        if (!url.StartsWith(BoardsPrefix, StringComparison.Ordinal)) return false;
        var rest = url[BoardsPrefix.Length..];
        var split = rest.IndexOf(NotesSegment, StringComparison.Ordinal);
        return split > 0
            && Guid.TryParseExact(rest[..split], "D", out _)
            && Guid.TryParseExact(rest[(split + NotesSegment.Length)..], "D", out _);
    }
}
