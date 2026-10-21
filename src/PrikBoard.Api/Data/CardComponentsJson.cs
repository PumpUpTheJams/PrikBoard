using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Data;

/// <summary>
/// Stores a card's components as one jsonb array with camelCase names, e.g.
/// <c>[{"id":…,"kind":"Checklist","x":20,"y":20,"width":340,"height":260,"title":null,"text":null,"items":[{"text":…,"done":false}],"links":[]}]</c>.
/// Nothing queries inside the column, so a plain converter is enough. The migration NoteBlocks wrote the same shape (as Blocks on Notes, renamed by CardsAndComponents).
/// </summary>
public static class CardComponentsJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static string Write(List<CardComponent> components) => JsonSerializer.Serialize(components, Options);

    private static List<CardComponent> Read(string json) => JsonSerializer.Deserialize<List<CardComponent>>(json, Options) ?? [];

    public static readonly ValueConverter<List<CardComponent>, string> Converter = new(v => Write(v), v => Read(v));

    /// <summary>Compares by content and snapshots a deep copy, so changes made in place are saved too.</summary>
    public static readonly ValueComparer<List<CardComponent>> Comparer = new(
        (a, b) => Write(a!) == Write(b!),
        v => Write(v).GetHashCode(),
        v => Read(Write(v)));
}
