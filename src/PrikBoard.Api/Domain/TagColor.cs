using System.Text.Json.Serialization;

namespace PrikBoard.Api.Domain;

/// <summary>
/// Colour of a board tag on post-its. Default shows the tag as a shade of the post-it's own fill; the others make it a
/// solid chip in that colour, the same on every post-it (useful for priority or anything that should stand out):
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
