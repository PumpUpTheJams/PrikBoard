using System.Text.Json.Serialization;

namespace PrikBoard.Api.Domain;

/// <summary>Post-it colour. Serialized as a string so API clients can use readable names.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NoteColor>))]
public enum NoteColor
{
    Yellow,
    Pink,
    Blue,
    Green,
    Orange,
    Purple,
}
