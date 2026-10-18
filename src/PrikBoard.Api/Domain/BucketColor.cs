using System.Text.Json.Serialization;

namespace PrikBoard.Api.Domain;

/// <summary>Bucket outline colour. Serialized as a string so API clients can use readable names.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<BucketColor>))]
public enum BucketColor
{
    Blue,
    Amber,
    Violet,
    Teal,
    Rose,
}
