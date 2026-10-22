using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Data;

/// <summary>
/// Stores a drawing's strokes as one jsonb array with camelCase names, each with its points as <c>[x, y, pressure]</c>
/// triples, e.g. <c>[{"strokeWidth":4,"points":[[0,0,0.5],[12.5,3,0.62]]}]</c>. Nothing queries inside the column, so a
/// plain converter is enough. The migration DrawingStrokes wrote the same shape from the single stroke drawings had before.
/// </summary>
public static class DrawingStrokesJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static string Write(List<DrawingStroke> strokes) => JsonSerializer.Serialize(strokes, Options);

    private static List<DrawingStroke> Read(string json) => JsonSerializer.Deserialize<List<DrawingStroke>>(json, Options) ?? [];

    public static readonly ValueConverter<List<DrawingStroke>, string> Converter = new(v => Write(v), v => Read(v));

    /// <summary>Compares by content and snapshots a deep copy, so changes made in place are saved too.</summary>
    public static readonly ValueComparer<List<DrawingStroke>> Comparer = new(
        (a, b) => Write(a!) == Write(b!),
        v => Write(v).GetHashCode(),
        v => Read(Write(v)));
}
