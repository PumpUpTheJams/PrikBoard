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
            if (x.Kind != y.Kind || x.Level != y.Level || x.Done != y.Done || !SameRuns(x.Runs, y.Runs)) return false;
            if (x.Cells is null != y.Cells is null) return false;
            if (x.Cells is null) continue;
            if (x.Cells.Count != y.Cells!.Count) return false;
            for (var c = 0; c < x.Cells.Count; c++)
            {
                if (!SameRuns(x.Cells[c], y.Cells[c])) return false;
            }
        }
        return true;
    }

    private static bool SameRuns(List<LabelRun> a, List<LabelRun> b)
    {
        if (a.Count != b.Count) return false;
        for (var r = 0; r < a.Count; r++)
        {
            if (a[r] != b[r]) return false;
        }
        return true;
    }
}
