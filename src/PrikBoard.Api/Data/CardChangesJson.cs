using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Data;

/// <summary>Stores a <see cref="CardChange"/>'s entries as one jsonb array with camelCase names, like <see cref="CardComponentsJson"/>.</summary>
public static class CardChangesJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Beats the enum's own converter, so a kind this version doesn't know reads as Other instead of failing the row.
        Converters = { new KindConverter() },
    };

    private sealed class KindConverter : JsonConverter<CardChangeKind>
    {
        public override CardChangeKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String && Enum.TryParse<CardChangeKind>(reader.GetString(), out var kind) && Enum.IsDefined(kind)
                ? kind
                : CardChangeKind.Other;

        public override void Write(Utf8JsonWriter writer, CardChangeKind value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }

    public static readonly ValueConverter<List<CardChangeEntry>, string> Converter = JsonColumn.Converter<List<CardChangeEntry>>(Options);

    /// <summary>Compares entry by entry and snapshots the list only: entries are replaced, never changed in place (see <see cref="CardHistory"/>).</summary>
    public static readonly ValueComparer<List<CardChangeEntry>> Comparer = new(
        (a, b) => Same(a!, b!),
        v => v.Count,
        v => v.ToList());

    private static bool Same(List<CardChangeEntry> a, List<CardChangeEntry> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            var (x, y) = (a[i], b[i]);
            if (x.Kind != y.Kind || x.Before != y.Before || x.After != y.After || x.Ref != y.Ref
                || !Same(x.Added, y.Added) || !Same(x.Removed, y.Removed))
            {
                return false;
            }
        }
        return true;
    }

    private static bool Same(List<string>? a, List<string>? b) => a is null ? b is null : b is not null && a.SequenceEqual(b);
}
