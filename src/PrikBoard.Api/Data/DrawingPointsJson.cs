using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PrikBoard.Api.Data;

/// <summary>
/// Stores a drawing's points as one jsonb array of <c>[x, y, pressure]</c> triples, e.g. <c>[[0,0,0.5],[12.5,3,0.62]]</c>.
/// Nothing queries inside the column and the points never change after the drawing is made, so a plain converter is enough.
/// </summary>
public static class DrawingPointsJson
{
    private static string Write(double[][] points) => JsonSerializer.Serialize(points);

    private static double[][] Read(string json) => JsonSerializer.Deserialize<double[][]>(json) ?? [];

    public static readonly ValueConverter<double[][], string> Converter = new(v => Write(v), v => Read(v));

    public static readonly ValueComparer<double[][]> Comparer = new(
        (a, b) => Write(a!) == Write(b!),
        v => Write(v).GetHashCode(),
        v => Read(Write(v)));
}
