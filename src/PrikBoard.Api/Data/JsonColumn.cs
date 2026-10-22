using System.Text.Json;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace PrikBoard.Api.Data;

/// <summary>
/// Values stored as one jsonb column. Nothing queries inside these columns, so a plain converter is enough; each column
/// brings its own comparer, fitted to how its value changes.
/// </summary>
public static class JsonColumn
{
    public static ValueConverter<T, string> Converter<T>(JsonSerializerOptions options) where T : class, new() =>
        new(v => JsonSerializer.Serialize(v, options), v => JsonSerializer.Deserialize<T>(v, options) ?? new T());
}
