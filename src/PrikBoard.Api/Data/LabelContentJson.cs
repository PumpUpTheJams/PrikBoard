using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Data;

/// <summary>
/// Stores a label's content as one jsonb array with camelCase names, e.g.
/// <c>[{"kind":"Bullet","runs":[{"text":"Read the "},{"text":"repo","bold":true,"link":"https://example.com"}]}]</c>.
/// </summary>
public static class LabelContentJson
{
    public static readonly ValueConverter<List<LabelBlock>, string> Converter = JsonColumn.Converter<List<LabelBlock>>(JsonSerializerOptions.Web);

    /// <summary>
    /// Compares run by run and snapshots the list only: blocks and runs are replaced, never changed in place (see
    /// <see cref="BoardLabel.SetContent"/>). This runs for every tracked label on every save, so it allocates nothing.
    /// </summary>
    public static readonly ValueComparer<List<LabelBlock>> Comparer = new(
        (a, b) => Same(a!, b!),
        v => v.Count,
        v => v.ToList());

    private static bool Same(List<LabelBlock> a, List<LabelBlock> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            var (x, y) = (a[i], b[i]);
            if (ReferenceEquals(x, y)) continue;
            if (x.Kind != y.Kind || x.Runs.Count != y.Runs.Count) return false;
            for (var r = 0; r < x.Runs.Count; r++)
            {
                if (x.Runs[r] != y.Runs[r]) return false;
            }
        }
        return true;
    }
}
