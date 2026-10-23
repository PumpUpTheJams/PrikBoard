using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Data;

/// <summary>
/// Stores the channels a board is connected to in a Discord server as a jsonb array,
/// <c>[{"id":"123…","name":"general","updates":3,"saveBucketId":null}]</c>. Older rows have only id and name.
/// </summary>
public static class DiscordChannelsJson
{
    public static readonly ValueConverter<List<DiscordChannel>, string> Converter = new(
        v => JsonSerializer.Serialize(v, JsonSerializerOptions.Web),
        v => JsonSerializer.Deserialize<List<DiscordChannel>>(v, JsonSerializerOptions.Web) ?? new List<DiscordChannel>());

    /// <summary>Compares the channels (records, so by value) and snapshots a copy, so changes made in place are saved too.</summary>
    public static readonly ValueComparer<List<DiscordChannel>> Comparer = new(
        (a, b) => a!.SequenceEqual(b!),
        v => v.Count,
        v => v.ToList());
}
