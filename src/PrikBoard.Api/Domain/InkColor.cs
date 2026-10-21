using System.Text.Json.Serialization;

namespace PrikBoard.Api.Domain;

/// <summary>
/// Color of a drawing's line. Two rows of matching pairs: light inks (Chalk, Butter, Mint, Sky, Coral, Lilac) show on the
/// dark board and inside an expanded card; dark inks (Ink, Ochre, Pine, Ocean, Brick, Violet) show on a card's light
/// fill. Serialized as a string; clients map the names to their own hex values.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<InkColor>))]
public enum InkColor
{
    Chalk,
    Butter,
    Mint,
    Sky,
    Coral,
    Lilac,
    Ink,
    Ochre,
    Pine,
    Ocean,
    Brick,
    Violet,
}
