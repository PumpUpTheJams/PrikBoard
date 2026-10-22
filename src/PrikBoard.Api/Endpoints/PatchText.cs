using System.Buffers;
using System.Text;

namespace PrikBoard.Api.Endpoints;

internal static class PatchText
{
    /// <summary>Null keeps <paramref name="current"/>; an empty or whitespace value clears it.</summary>
    public static string? Apply(string? current, string? incoming) =>
        incoming is null ? current : string.IsNullOrWhiteSpace(incoming) ? null : incoming;

    /// <summary>What <see cref="char.IsControl(char)"/> calls control characters: U+0000 to U+001F and U+007F to U+009F.</summary>
    public static readonly string ControlCharacters =
        string.Concat(Enumerable.Range(0, 0x20).Concat(Enumerable.Range(0x7F, 0x21)).Select(c => (char)c));

    private static readonly SearchValues<char> Controls = SearchValues.Create(ControlCharacters);

    /// <summary>A name for one line: without control characters (line breaks, tabs, escape codes), trimmed.</summary>
    public static string OneLine(string text) => Without(text, Controls);

    /// <summary>The text without any of <paramref name="remove"/>, trimmed.</summary>
    public static string Without(string text, SearchValues<char> remove)
    {
        if (!text.AsSpan().ContainsAny(remove)) return text.Trim();
        var kept = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (!remove.Contains(c)) kept.Append(c);
        }
        return kept.ToString().Trim();
    }
}
