using System.Text.Json.Serialization;

namespace PrikBoard.Api.Domain;

/// <summary>
/// The side a bucket grows towards when the cards put into it don't fit. Left and Up move its left or top edge out;
/// what is already in it stays where it is. Serialized as a string.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<GrowSide>))]
public enum GrowSide
{
    Down,
    Right,
    Left,
    Up,
}
