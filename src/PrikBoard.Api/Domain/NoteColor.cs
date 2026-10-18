using System.Text.Json.Serialization;

namespace PrikBoard.Api.Domain;

/// <summary>
/// Post-it fill: Butter (yellow), Mint (green), Sky (blue), Coral (salmon red) or Lilac (purple).
/// Teams map fills to kinds of work. Serialized as a string so API clients can use readable names.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<NoteColor>))]
public enum NoteColor
{
    Butter,
    Mint,
    Sky,
    Coral,
    Lilac,
}
