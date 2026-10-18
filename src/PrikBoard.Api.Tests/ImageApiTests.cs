using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

public class ImageApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>, IAsyncLifetime
{
    private HttpClient _client = null!;

    public async Task InitializeAsync() => _client = await factory.CreateSignedInClientAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task An_uploaded_image_lands_right_of_the_board_at_a_sensible_width_and_can_be_downloaded()
    {
        var board = await CreateBoard("Images");
        var png = TestImages.Png(800, 600);

        var image = await Upload(board.Id, png, "image/png", "?fileName=ingest-errors.png");

        Assert.Equal(("ingest-errors.png", "image/png", png.Length), (image.FileName, image.ContentType, image.Size));
        Assert.Equal((800, 600), (image.PixelWidth, image.PixelHeight));
        // Default buckets end at 840; images go 40 px to the right, at most 360 px wide, keeping the aspect ratio.
        Assert.Equal((880, 40, 360, 270), (image.X, image.Y, image.Width, image.Height));
        Assert.Null(image.NoteId);
        var me = await _client.MeAsync();
        Assert.Equal((me.DisplayName, me.Id), (image.AddedBy, image.AddedById));

        var response = await _client.GetAsync(image.ContentUrl);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal(png, await response.Content.ReadAsByteArrayAsync());

        var snapshot = await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        Assert.Equal(image.Id, Assert.Single(snapshot!.Images).Id);
    }

    [Fact]
    public async Task Position_and_width_snap_to_the_grid_and_the_height_follows_the_aspect_ratio()
    {
        var board = await CreateBoard("Placement");

        var gif = await Upload(board.Id, TestImages.Gif(400, 300), "image/gif", "?x=131&y=289&width=249&caption=throttle%20repro");
        var small = await Upload(board.Id, TestImages.Png(64, 32), "image/png");

        Assert.Equal(("image/gif", 140, 280, 240, 180), (gif.ContentType, gif.X, gif.Y, gif.Width, gif.Height));
        Assert.Equal("throttle repro", gif.Caption);
        Assert.Equal("image.png", small.FileName);
        Assert.Equal((80, 40), (small.Width, small.Height)); // small images are raised to the minimum width
    }

    [Fact]
    public async Task Images_can_be_moved_resized_recaptioned_and_deleted()
    {
        var board = await CreateBoard("Edits");
        var image = await Upload(board.Id, TestImages.Png(1000, 500), "image/png", "?caption=before");

        var moved = await _client.PatchAsJsonAsync($"/api/images/{image.Id}", new { x = 1201, y = 99, width = 611, caption = "" });
        moved.EnsureSuccessStatusCode();
        var updated = (await moved.Content.ReadFromJsonAsync<ImageDetail>())!;
        var tooWide = await _client.PatchAsJsonAsync($"/api/images/{image.Id}", new { width = 5000 });

        Assert.Equal((1200, 100, 620, 310), (updated.X, updated.Y, updated.Width, updated.Height));
        Assert.Null(updated.Caption);
        Assert.Equal(HttpStatusCode.BadRequest, tooWide.StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/images/{image.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync(image.ContentUrl)).StatusCode);
    }

    [Fact]
    public async Task Images_attached_to_a_note_go_when_the_note_goes()
    {
        var board = await CreateBoard("Attached");
        var noteResponse = await _client.PostAsJsonAsync($"/api/boards/{board.Id}/notes", new { title = "With a screenshot" });
        var note = (await noteResponse.Content.ReadFromJsonAsync<NoteDetail>())!;

        var image = await Upload(board.Id, TestImages.Png(300, 200), "image/png", $"?noteId={note.Id}");
        Assert.Equal(note.Id, image.NoteId);

        (await _client.DeleteAsync($"/api/notes/{note.Id}")).EnsureSuccessStatusCode();
        var snapshot = await _client.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        Assert.Empty(snapshot!.Images);
    }

    [Fact]
    public async Task Uploads_that_are_not_supported_images_are_rejected()
    {
        var board = await CreateBoard("Rejects");
        var other = await CreateBoard("Other");
        var otherNote = await (await _client.PostAsJsonAsync($"/api/boards/{other.Id}/notes", new { title = "Elsewhere" }))
            .Content.ReadFromJsonAsync<NoteDetail>();
        var url = $"/api/boards/{board.Id}/images";

        var formPost = await Post(url, TestImages.Png(10, 10), "text/plain");
        var svg = await Post(url, Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"), "image/svg+xml");
        var garbage = await Post(url, Encoding.UTF8.GetBytes("definitely not an image"), "image/png");
        var tooLarge = await Post(url, [.. TestImages.Png(10, 10), .. new byte[ImageFormats.MaxBytes]], "image/png");
        var foreignNote = await Post($"{url}?noteId={otherNote!.Id}", TestImages.Png(10, 10), "image/png");
        var onlyX = await Post($"{url}?x=40", TestImages.Png(10, 10), "image/png");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, formPost.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, svg.StatusCode);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, garbage.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, foreignNote.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, onlyX.StatusCode);
    }

    [Fact]
    public async Task Images_need_a_signed_in_caller()
    {
        var board = await CreateBoard("Private");
        var image = await Upload(board.Id, TestImages.Png(10, 10), "image/png");
        var anonymous = factory.CreateAnonymousClient();

        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(image.ContentUrl)).StatusCode);
        var upload = new ByteArrayContent(TestImages.Png(10, 10)) { Headers = { ContentType = new MediaTypeHeaderValue("image/png") } };
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync($"/api/boards/{board.Id}/images", upload)).StatusCode);
    }

    [Theory]
    [InlineData("png", "image/png", 640, 480)]
    [InlineData("gif", "image/gif", 320, 200)]
    [InlineData("jpeg", "image/jpeg", 1024, 768)]
    [InlineData("webp-lossy", "image/webp", 500, 250)]
    [InlineData("webp-lossless", "image/webp", 1000, 3000)]
    [InlineData("webp-extended", "image/webp", 4000, 2000)]
    public void Formats_and_pixel_sizes_are_read_from_the_file_header(string kind, string contentType, int width, int height)
    {
        var bytes = kind switch
        {
            "png" => TestImages.Png(width, height),
            "gif" => TestImages.Gif(width, height),
            "jpeg" => TestImages.Jpeg(width, height),
            "webp-lossy" => TestImages.WebPLossy(width, height),
            "webp-lossless" => TestImages.WebPLossless(width, height),
            _ => TestImages.WebPExtended(width, height),
        };

        Assert.Equal(new ImageFormats.Info(contentType, width, height), ImageFormats.Detect(bytes));
    }

    private async Task<BoardDetail> CreateBoard(string name)
    {
        var response = await _client.PostAsJsonAsync("/api/boards", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BoardDetail>())!;
    }

    private Task<HttpResponseMessage> Post(string url, byte[] data, string contentType) =>
        _client.PostAsync(url, new ByteArrayContent(data) { Headers = { ContentType = new MediaTypeHeaderValue(contentType) } });

    private async Task<ImageDetail> Upload(Guid boardId, byte[] data, string contentType, string query = "")
    {
        var response = await Post($"/api/boards/{boardId}/images{query}", data, contentType);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ImageDetail>())!;
    }
}

/// <summary>Minimal file headers: enough for the API, which reads formats and sizes without decoding images.</summary>
internal static class TestImages
{
    public static byte[] Png(int width, int height)
    {
        var data = new byte[33];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, (byte)'I', (byte)'H', (byte)'D', (byte)'R' }.CopyTo(data, 0);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(20), (uint)height);
        data[24] = 8; // bit depth
        data[25] = 6; // RGBA
        return data;
    }

    public static byte[] Gif(int width, int height)
    {
        var data = new byte[13];
        "GIF89a"u8.CopyTo(data);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(6), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(8), (ushort)height);
        return data;
    }

    public static byte[] Jpeg(int width, int height)
    {
        // SOI, an APP0 segment to skip, then a baseline SOF0 with the size.
        byte[] data = [0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0x4A, 0x46, 0xFF, 0xC0, 0, 11, 8, 0, 0, 0, 0, 3, 1, 0x22, 0];
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(13), (ushort)height);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(15), (ushort)width);
        return data;
    }

    public static byte[] WebPLossy(int width, int height)
    {
        var data = WebP("VP8 ");
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(26), (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(28), (ushort)height);
        return data;
    }

    public static byte[] WebPLossless(int width, int height)
    {
        var data = WebP("VP8L");
        var bits = (uint)(width - 1) | ((uint)(height - 1) << 14);
        data[20] = 0x2F; // signature
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(21), bits);
        return data;
    }

    public static byte[] WebPExtended(int width, int height)
    {
        var data = WebP("VP8X");
        WriteUInt24(data, 24, width - 1);
        WriteUInt24(data, 27, height - 1);
        return data;
    }

    private static byte[] WebP(string chunk)
    {
        var data = new byte[32];
        "RIFF"u8.CopyTo(data);
        "WEBP"u8.CopyTo(data.AsSpan(8));
        Encoding.ASCII.GetBytes(chunk).CopyTo(data, 12);
        return data;
    }

    private static void WriteUInt24(byte[] data, int offset, int value)
    {
        data[offset] = (byte)value;
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)(value >> 16);
    }
}
