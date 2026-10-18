using System.Buffers.Binary;

namespace PrikBoard.Api.Domain;

/// <summary>
/// Recognises the supported image formats from their first bytes and reads the pixel size from the header, without
/// decoding the image. The declared content type of an upload is never trusted. SVG is deliberately not supported,
/// because it can carry script.
/// </summary>
public static class ImageFormats
{
    public const int MaxBytes = 10 * 1024 * 1024;
    public const int MaxPixels = 20_000;

    public static readonly string[] ContentTypes = ["image/png", "image/jpeg", "image/gif", "image/webp"];

    public record Info(string ContentType, int Width, int Height);

    /// <summary>The format and pixel size, or null when the bytes are not a supported, well-formed image.</summary>
    public static Info? Detect(ReadOnlySpan<byte> data)
    {
        var info = Png(data) ?? Gif(data) ?? Jpeg(data) ?? WebP(data);
        return info is { Width: > 0 and <= MaxPixels, Height: > 0 and <= MaxPixels } ? info : null;
    }

    private static Info? Png(ReadOnlySpan<byte> d) =>
        d.Length >= 24 && d[..8].SequenceEqual((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])
                       && d.Slice(12, 4).SequenceEqual("IHDR"u8)
            ? new Info("image/png", (int)BinaryPrimitives.ReadUInt32BigEndian(d[16..]), (int)BinaryPrimitives.ReadUInt32BigEndian(d[20..]))
            : null;

    private static Info? Gif(ReadOnlySpan<byte> d) =>
        d.Length >= 10 && (d[..6].SequenceEqual("GIF87a"u8) || d[..6].SequenceEqual("GIF89a"u8))
            ? new Info("image/gif", BinaryPrimitives.ReadUInt16LittleEndian(d[6..]), BinaryPrimitives.ReadUInt16LittleEndian(d[8..]))
            : null;

    private static Info? Jpeg(ReadOnlySpan<byte> d)
    {
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return null;
        var i = 2;
        while (i + 4 <= d.Length)
        {
            if (d[i] != 0xFF) return null;
            var marker = d[i + 1];
            if (marker == 0xFF) { i++; continue; }                     // fill byte
            if (marker is 0x01 or >= 0xD0 and <= 0xD9) { i += 2; continue; } // markers without a length
            var length = BinaryPrimitives.ReadUInt16BigEndian(d[(i + 2)..]);
            // Start-of-frame markers carry the size; C4 (DHT), C8 (JPG) and CC (DAC) share the range but don't.
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                return i + 9 <= d.Length
                    ? new Info("image/jpeg", BinaryPrimitives.ReadUInt16BigEndian(d[(i + 7)..]), BinaryPrimitives.ReadUInt16BigEndian(d[(i + 5)..]))
                    : null;
            }
            i += 2 + length;
        }
        return null;
    }

    private static Info? WebP(ReadOnlySpan<byte> d)
    {
        if (d.Length < 30 || !d[..4].SequenceEqual("RIFF"u8) || !d.Slice(8, 4).SequenceEqual("WEBP"u8)) return null;
        var chunk = d.Slice(12, 4);
        if (chunk.SequenceEqual("VP8 "u8))
        {
            return new Info("image/webp", BinaryPrimitives.ReadUInt16LittleEndian(d[26..]) & 0x3FFF,
                BinaryPrimitives.ReadUInt16LittleEndian(d[28..]) & 0x3FFF);
        }
        if (chunk.SequenceEqual("VP8L"u8))
        {
            int b1 = d[22], b2 = d[23], b3 = d[24];
            return new Info("image/webp", 1 + (((b1 & 0x3F) << 8) | d[21]), 1 + (((b3 & 0x0F) << 10) | (b2 << 2) | ((b1 & 0xC0) >> 6)));
        }
        if (chunk.SequenceEqual("VP8X"u8))
        {
            return new Info("image/webp", 1 + (d[24] | (d[25] << 8) | (d[26] << 16)), 1 + (d[27] | (d[28] << 8) | (d[29] << 16)));
        }
        return null;
    }
}
