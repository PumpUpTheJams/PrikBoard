using System.Text.Json.Serialization;

namespace PrikBoard.Api.Domain;

/// <summary>
/// Color of a board tag on cards. Default shows the tag as a shade of the card's own fill; the others make it a
/// solid chip in that color, the same on every card (useful for priority or anything that should stand out):
/// Brick (red), Rust (orange), Ochre (dark yellow), Moss (olive), Pine (green), Teal, Ocean (blue), Indigo, Violet,
/// Berry (pink) and Ink (near black). Serialized as a string.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<TagColor>))]
public enum TagColor
{
    Default,
    Brick,
    Rust,
    Ochre,
    Moss,
    Pine,
    Teal,
    Ocean,
    Indigo,
    Violet,
    Berry,
    Ink,
}
