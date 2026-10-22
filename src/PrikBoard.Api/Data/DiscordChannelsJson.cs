using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Data;

/// <summary>
/// Stores the channels a Discord server may preview a board in as a jsonb array, <c>[{"id":"123…","name":"general"}]</c>.
/// A null list (the whole server) is a SQL null.
/// </summary>
public static class DiscordChannelsJson
{
    public static readonly ValueConverter<List<DiscordChannel>?, string?> Converter = new(
        v => v == null ? null : JsonSerializer.Serialize(v, JsonSerializerOptions.Web),
        v => v == null ? null : JsonSerializer.Deserialize<List<DiscordChannel>>(v, JsonSerializerOptions.Web));

    /// <summary>Compares the channels (records, so by value) and snapshots a copy, so changes made in place are saved too.</summary>
    public static readonly ValueComparer<List<DiscordChannel>?> Comparer = new(
        (a, b) => a == null ? b == null : b != null && a.SequenceEqual(b),
        v => v == null ? 0 : v.Count,
        v => v == null ? null : v.ToList());
}
