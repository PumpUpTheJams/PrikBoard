using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Data;

/// <summary>
/// Stores a drawing's strokes as one jsonb array with camelCase names, each with its points as <c>[x, y, pressure]</c>
/// triples, e.g. <c>[{"strokeWidth":4,"points":[[0,0,0.5],[12.5,3,0.62]]}]</c>.
/// </summary>
public static class DrawingStrokesJson
{
    public static readonly ValueConverter<List<DrawingStroke>, string> Converter = JsonColumn.Converter<List<DrawingStroke>>(JsonSerializerOptions.Web);

    /// <summary>
    /// Compares point by point and snapshots the list only: strokes and their points are replaced, never changed in place
    /// (see <see cref="BoardDrawing.SetStrokes"/>). A drawing can hold 50,000 points, too many to copy on every load.
    /// </summary>
    public static readonly ValueComparer<List<DrawingStroke>> Comparer = new(
        (a, b) => Same(a!, b!),
        v => v.Count,
        v => v.ToList());

    private static bool Same(List<DrawingStroke> a, List<DrawingStroke> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            var (x, y) = (a[i], b[i]);
            if (ReferenceEquals(x, y)) continue;
            if (x.StrokeWidth != y.StrokeWidth || x.Points.Length != y.Points.Length) return false;
            for (var p = 0; p < x.Points.Length; p++)
            {
                if (!x.Points[p].AsSpan().SequenceEqual(y.Points[p])) return false;
            }
        }
        return true;
    }
}
