using System.Globalization;

namespace PrikBoard.Api.Domain;

/// <summary>
/// Links on cards: web addresses, or another card on PrikBoard by its path in the web app. A card's path has no host,
/// so the link keeps working wherever PrikBoard runs. Paths name the board by its short id and the card by its number
/// (/#/boards/{shortId}/cards/{number}); the older form with both ids (/#/boards/{boardId}/cards/{cardId}) stays valid.
/// </summary>
public static class CardLinks
{
    private const string BoardsPrefix = "/#/boards/";
    private const string CardsSegment = "/cards/";

    /// <summary>Where the web app shows the card: its board, with the card opened up.</summary>
    public static string CardPath(string boardShortId, int number) => $"{BoardsPrefix}{boardShortId}{CardsSegment}{number}";

    /// <summary>The older form of a card's path, by ids. Exports still refer to the cards in the file this way.</summary>
    public static string CardPath(Guid boardId, Guid cardId) => $"{BoardsPrefix}{boardId}{CardsSegment}{cardId}";

    /// <summary>How people and agents refer to a card: the board's key and the card's number, e.g. "MPCT-415".</summary>
    public static string ReadableId(string boardKey, int number) => $"{boardKey}-{number}";

    public static bool IsValid(string url) => IsWebUrl(url) || TryParse(url, out _);

    private static bool IsWebUrl(string url) =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    /// <summary>Whether the link is another card's path (in either form), and which card that is.</summary>
    public static bool TryParse(string url, out LinkedCard card)
    {
        card = default;
        url = url.Trim();
        if (!url.StartsWith(BoardsPrefix, StringComparison.Ordinal)) return false;
        var rest = url[BoardsPrefix.Length..];
        var split = rest.IndexOf(CardsSegment, StringComparison.Ordinal);
        if (split <= 0) return false;
        var (board, target) = (rest[..split], rest[(split + CardsSegment.Length)..]);
        if (Board.IsShortId(board) && TryParseNumber(target, out var number))
        {
            card = new LinkedCard(board, number, Guid.Empty);
            return true;
        }
        if (Guid.TryParseExact(board, "D", out _) && Guid.TryParseExact(target, "D", out var cardId))
        {
            card = new LinkedCard(null, 0, cardId);
            return true;
        }
        return false;
    }

    /// <summary>A card number as written in a path or readable id: digits without a leading zero.</summary>
    public static bool TryParseNumber(string value, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number > 0
        && number.ToString(CultureInfo.InvariantCulture) == value;
}

/// <summary>The card a link points at: by its board's short id and its number, or (the older form) by its id.</summary>
public readonly record struct LinkedCard(string? BoardShortId, int Number, Guid CardId)
{
    public bool ByNumber => BoardShortId is not null;
}

/// <summary>What a card's readable id and path take from its board.</summary>
public sealed record BoardRef(string ShortId, string Key)
{
    public static BoardRef Of(Board board) => new(board.ShortId, board.Key);
}
