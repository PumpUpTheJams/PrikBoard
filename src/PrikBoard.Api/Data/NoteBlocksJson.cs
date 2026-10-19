using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Data;

/// <summary>
/// Stores a note's blocks as one jsonb array with camelCase names, e.g.
/// <c>[{"id":…,"kind":"Checklist","x":20,"y":20,"width":340,"height":260,"title":null,"text":null,"items":[{"text":…,"done":false}],"links":[]}]</c>.
/// Nothing queries inside the column, so a plain converter is enough. The migration NoteBlocks writes the same shape.
/// </summary>
public static class NoteBlocksJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static string Write(List<NoteBlock> blocks) => JsonSerializer.Serialize(blocks, Options);

    private static List<NoteBlock> Read(string json) => JsonSerializer.Deserialize<List<NoteBlock>>(json, Options) ?? [];

    public static readonly ValueConverter<List<NoteBlock>, string> Converter = new(v => Write(v), v => Read(v));

    /// <summary>Compares by content and snapshots a deep copy, so changes made in place are saved too.</summary>
    public static readonly ValueComparer<List<NoteBlock>> Comparer = new(
        (a, b) => Write(a!) == Write(b!),
        v => Write(v).GetHashCode(),
        v => Read(Write(v)));
}
