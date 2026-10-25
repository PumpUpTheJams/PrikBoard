using System.Buffers;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace PrikBoard.Api.Domain;

/// <summary>A project board: a freeform canvas holding buckets, cards, images and drawings.</summary>
public partial class Board
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    /// <summary>
    /// The board in web addresses (/#/boards/{ShortId}/cards/{number}): random, unique and never changed, so links keep
    /// working however the board is renamed. See <see cref="NewShortId"/>.
    /// </summary>
    public string ShortId { get; set; } = NewShortId();
    public required string Name { get; set; }
    /// <summary>
    /// Shown before card numbers, e.g. "MPCT" in MPCT-415. Owners may change it at any time: it is not unique and not part of
    /// any address. New boards take it from their name (<see cref="KeyFrom"/>).
    /// </summary>
    public required string Key { get; set; }
    /// <summary>The number the next card on the board gets. Numbers are never reused, not even those of deleted cards.</summary>
    public int NextCardNumber { get; set; } = 1;
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Markdown written by the owners: what the board is about and how agents should handle its cards.</summary>
    public string? Rules { get; set; }
    /// <summary>When <see cref="Rules"/> last changed, or null if they were never set.</summary>
    public DateTimeOffset? RulesUpdatedAt { get; set; }
    /// <summary>
    /// Anyone with the link can view the board without being a member, signed in or not (a visitor). Tokens still need
    /// membership. Only owners change it.
    /// </summary>
    public bool IsPublic { get; set; }
    /// <summary>Whether visitors of a public board see who is on it (names and pictures), or only anonymous circles and a count.</summary>
    public bool PublicShowsPeople { get; set; }
    /// <summary>
    /// Whether members on the board see each other's pointers live (as thumbtacks in the web app). Visitors of a public board
    /// never do. Only owners change it.
    /// </summary>
    public bool ShowCursors { get; set; } = true;

    public List<Bucket> Buckets { get; set; } = [];
    public List<Card> Cards { get; set; } = [];
    /// <summary>Images on the canvas and images attached to cards.</summary>
    public List<BoardImage> Images { get; set; } = [];
    /// <summary>Freehand drawings on the canvas and on cards.</summary>
    public List<BoardDrawing> Drawings { get; set; } = [];
    /// <summary>The tags cards on this board can have.</summary>
    public List<BoardTag> Tags { get; set; } = [];
    /// <summary>Who may see and change the board; there is always at least one owner.</summary>
    public List<BoardMember> Members { get; set; } = [];

    /// <summary>The longest agent rules a board can have, in characters.</summary>
    public const int RulesMaxLength = 20000;

    public static readonly string[] DefaultBuckets = ["To do", "Doing", "Done"];

    /// <summary>The characters of a short id: digits and lower-case letters without i, l, o and u (Crockford's base32).</summary>
    public const string ShortIdAlphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    public const int ShortIdLength = 6;

    /// <summary>A board key: a letter, then letters and digits, 2 to 10 in all, upper case.</summary>
    public const string KeyPattern = "^[A-Z][A-Z0-9]{1,9}$";
    public const int KeyMaxLength = 10;

    /// <summary>A random short id. About a billion of them, so a clash is rare; the unique index catches one anyway.</summary>
    public static string NewShortId() =>
        string.Create(ShortIdLength, 0, (chars, _) =>
        {
            for (var i = 0; i < chars.Length; i++) chars[i] = ShortIdAlphabet[RandomNumberGenerator.GetInt32(ShortIdAlphabet.Length)];
        });

    public static bool IsShortId(string value) => value.Length == ShortIdLength && !value.AsSpan().ContainsAnyExcept(ShortIdCharacters);

    private static readonly SearchValues<char> ShortIdCharacters = SearchValues.Create(ShortIdAlphabet);

    /// <summary>
    /// A key from a board name: the initials of its words (letters and digits only) or, when that gives fewer than two,
    /// the start of the name. At most four characters, never starting with a digit; "PB" when nothing usable is left.
    /// "MCP me some tickets" becomes MMST, "PrikBoard" PRIK.
    /// </summary>
    public static string KeyFrom(string name)
    {
        var words = NonKeyCharacters().Split(name.ToUpperInvariant()).Where(w => w.Length > 0).ToList();
        var initials = string.Concat(words.Select(w => w[0])).TrimStart(Digits);
        var key = initials.Length >= 2 ? initials : string.Concat(words).TrimStart(Digits);
        key = key[..Math.Min(4, key.Length)];
        return key.Length >= 2 ? key : "PB";
    }

    /// <summary>A key as typed, trimmed and upper-cased; check it with <see cref="IsKey"/>.</summary>
    public static string NormalizeKey(string key) => key.Trim().ToUpperInvariant();

    /// <summary>Whether a (normalized) key matches <see cref="KeyPattern"/>.</summary>
    public static bool IsKey(string key) => KeyRegex().IsMatch(key);

    private static readonly char[] Digits = [.. "0123456789"];

    [GeneratedRegex("[^A-Z0-9]+")]
    private static partial Regex NonKeyCharacters();

    [GeneratedRegex(KeyPattern)]
    private static partial Regex KeyRegex();
}
