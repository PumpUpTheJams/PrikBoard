namespace PrikBoard.Api.Domain;

/// <summary>Anything ordered among its siblings by a zero-based position.</summary>
public interface IPositioned
{
    int Position { get; set; }
}

public static class Positioning
{
    /// <summary>
    /// Puts <paramref name="item"/> at <paramref name="position"/> among <paramref name="siblings"/>
    /// (at the end when null; clamped to the valid range) and renumbers everything 0..n-1.
    /// </summary>
    public static void Place<T>(IEnumerable<T> siblings, T item, int? position) where T : class, IPositioned
    {
        var ordered = siblings.Where(s => s != item).OrderBy(s => s.Position).ToList();
        var index = Math.Clamp(position ?? ordered.Count, 0, ordered.Count);
        ordered.Insert(index, item);
        Number(ordered);
    }

    /// <summary>Closes gaps left behind after an item was removed or moved away.</summary>
    public static void Renumber<T>(IEnumerable<T> siblings) where T : IPositioned =>
        Number(siblings.OrderBy(s => s.Position).ToList());

    private static void Number<T>(List<T> ordered) where T : IPositioned
    {
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].Position = i;
        }
    }
}
