using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Data;

/// <summary>
/// Stores a card's components as one jsonb array with camelCase names, e.g.
/// <c>[{"id":…,"kind":"Checklist","x":20,"y":20,"width":340,"height":260,"title":null,"text":null,"items":[{"text":…,"done":false}],"links":[]}]</c>.
/// </summary>
public static class CardComponentsJson
{
    public static readonly ValueConverter<List<CardComponent>, string> Converter = JsonColumn.Converter<List<CardComponent>>(JsonSerializerOptions.Web);

    /// <summary>
    /// Compares by content and snapshots a deep copy, so changes made in place are saved too. Both go through the JSON
    /// (as UTF-8, the cheapest form), so a new field on a component needs no code here.
    /// </summary>
    public static readonly ValueComparer<List<CardComponent>> Comparer = new(
        (a, b) => Same(a!, b!),
        v => v.Count,
        v => JsonSerializer.Deserialize<List<CardComponent>>(Utf8(v), JsonSerializerOptions.Web)!);

    private static bool Same(List<CardComponent> a, List<CardComponent> b) =>
        a.Count == b.Count && Utf8(a).AsSpan().SequenceEqual(Utf8(b));

    private static byte[] Utf8(List<CardComponent> components) => JsonSerializer.SerializeToUtf8Bytes(components, JsonSerializerOptions.Web);
}
