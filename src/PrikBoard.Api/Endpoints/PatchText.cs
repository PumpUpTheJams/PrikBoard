namespace PrikBoard.Api.Endpoints;

internal static class PatchText
{
    /// <summary>Null keeps <paramref name="current"/>; an empty or whitespace value clears it.</summary>
    public static string? Apply(string? current, string? incoming) =>
        incoming is null ? current : string.IsNullOrWhiteSpace(incoming) ? null : incoming;
}
