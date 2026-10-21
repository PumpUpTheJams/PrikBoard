using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Data;

/// <summary>
/// Stores the channels a Discord server may preview a board in as a jsonb array, <c>[{"id":"123…","name":"general"}]</c>.
/// A null list (the whole server) is a SQL null. Nothing queries inside the column.
/// </summary>
public static class DiscordChannelsJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static string Write(List<DiscordChannel> channels) => JsonSerializer.Serialize(channels, Options);

    private static List<DiscordChannel> Read(string json) => JsonSerializer.Deserialize<List<DiscordChannel>>(json, Options) ?? [];

    public static readonly ValueConverter<List<DiscordChannel>?, string?> Converter = new(
        v => v == null ? null : Write(v),
        v => v == null ? null : Read(v));

    /// <summary>Compares by content and snapshots a copy, so changes made in place are saved too.</summary>
    public static readonly ValueComparer<List<DiscordChannel>?> Comparer = new(
        (a, b) => (a == null ? null : Write(a)) == (b == null ? null : Write(b)),
        v => v == null ? 0 : Write(v).GetHashCode(),
        v => v == null ? null : v.ToList());
}
