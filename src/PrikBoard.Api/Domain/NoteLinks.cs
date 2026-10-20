using System.Globalization;

namespace PrikBoard.Api.Domain;

/// <summary>
/// Links on notes: web addresses, or another note on PrikBoard by its path in the web app. A note's path has no host,
/// so the link keeps working wherever PrikBoard runs. Paths name the board by its short id and the note by its number
/// (/#/boards/{shortId}/notes/{number}); the older form with both ids (/#/boards/{boardId}/notes/{noteId}) stays valid.
/// </summary>
public static class NoteLinks
{
    private const string BoardsPrefix = "/#/boards/";
    private const string NotesSegment = "/notes/";

    /// <summary>Where the web app shows the note: its board, with the note opened up.</summary>
    public static string NotePath(string boardShortId, int number) => $"{BoardsPrefix}{boardShortId}{NotesSegment}{number}";

    /// <summary>The older form of a note's path, by ids. Exports still refer to the notes in the file this way.</summary>
    public static string NotePath(Guid boardId, Guid noteId) => $"{BoardsPrefix}{boardId}{NotesSegment}{noteId}";

    /// <summary>How people and agents refer to a note: the board's key and the note's number, e.g. "MPCT-415".</summary>
    public static string ReadableId(string boardKey, int number) => $"{boardKey}-{number}";

    public static bool IsValid(string url) => IsWebUrl(url) || TryParse(url, out _);

    private static bool IsWebUrl(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Whether the link is another note's path (in either form), and which note that is.</summary>
    public static bool TryParse(string url, out LinkedNote note)
    {
        note = default;
        url = url.Trim();
        if (!url.StartsWith(BoardsPrefix, StringComparison.Ordinal)) return false;
        var rest = url[BoardsPrefix.Length..];
        var split = rest.IndexOf(NotesSegment, StringComparison.Ordinal);
        if (split <= 0) return false;
        var (board, target) = (rest[..split], rest[(split + NotesSegment.Length)..]);
        if (Board.IsShortId(board) && TryParseNumber(target, out var number))
        {
            note = new LinkedNote(board, number, Guid.Empty);
            return true;
        }
        if (Guid.TryParseExact(board, "D", out _) && Guid.TryParseExact(target, "D", out var noteId))
        {
            note = new LinkedNote(null, 0, noteId);
            return true;
        }
        return false;
    }

    /// <summary>A note number as written in a path or readable id: digits without a leading zero.</summary>
    public static bool TryParseNumber(string value, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0
        && number.ToString(CultureInfo.InvariantCulture) == value;
}

/// <summary>The note a link points at: by its board's short id and its number, or (the older form) by its id.</summary>
public readonly record struct LinkedNote(string? BoardShortId, int Number, Guid NoteId)
{
    public bool ByNumber => BoardShortId is not null;
}

/// <summary>What a note's readable id and path take from its board.</summary>
public sealed record BoardRef(string ShortId, string Key)
{
    public static BoardRef Of(Board board) => new(board.ShortId, board.Key);
}
