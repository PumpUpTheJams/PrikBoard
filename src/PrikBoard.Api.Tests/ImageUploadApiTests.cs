using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

public class ImageUploadApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>
{
    [Fact]
    public async Task An_upload_url_adds_one_image_where_it_was_asked_for_without_authentication()
    {
        var owner = await factory.CreateSignedInClientAsync("upload-url@example.com");
        var board = await owner.CreateBoardAsync("Upload URLs");
        var note = await owner.CreateNoteAsync(board.Id, "Ingest errors");
        var png = TestImages.Png(800, 600);

        var url = await CreateAsync(owner, board.Id, new { noteId = note.Id, caption = "Last night", fileName = "errors.png", width = 200 });
        Assert.StartsWith($"{owner.BaseAddress!.GetLeftPart(UriPartial.Authority)}/api/image-uploads/{ImageUpload.Prefix}", url.UploadUrl);
        Assert.Equal($"curl -sS -T FILE \"{url.UploadUrl}\"", url.Command);
        Assert.InRange(url.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(14), DateTimeOffset.UtcNow.AddMinutes(16));

        // No cookie, no token and no content type, as curl -T sends it.
        var anonymous = factory.CreateAnonymousClient();
        var response = await anonymous.PutAsync(url.UploadUrl, new ByteArrayContent(png));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var image = (await response.Content.ReadFromJsonAsync<ImageDetail>())!;
        Assert.Equal((board.Id, note.Id, "errors.png", "Last night", 200), (image.BoardId, image.NoteId, image.FileName, image.Caption, image.Width));
        var me = await owner.MeAsync();
        Assert.Equal((me.DisplayName, me.Id), (image.AddedBy, image.AddedById));
        Assert.Equal(png, await owner.GetByteArrayAsync(image.ContentUrl));

        // One image per URL.
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PutAsync(url.UploadUrl, new ByteArrayContent(png))).StatusCode);
        var saved = (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.Equal(image.Id, Assert.Single(saved.Images).Id);
    }

    [Fact]
    public async Task A_failed_upload_leaves_the_url_usable()
    {
        var owner = await factory.CreateSignedInClientAsync("upload-retry@example.com");
        var board = await owner.CreateBoardAsync("Upload retry");
        var url = await CreateAsync(owner, board.Id, new { x = 1000, y = 1010 });
        var anonymous = factory.CreateAnonymousClient();

        var svg = await anonymous.PutAsync(url.UploadUrl, new ByteArrayContent("<svg/>"u8.ToArray()));
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, svg.StatusCode);

        var gif = await anonymous.PutAsync(url.UploadUrl, Content(TestImages.Gif(40, 30), "image/gif"));
        Assert.Equal(HttpStatusCode.Created, gif.StatusCode);
        var image = (await gif.Content.ReadFromJsonAsync<ImageDetail>())!;
        Assert.Equal(("image/gif", 1000, 1020), (image.ContentType, image.X, image.Y));
    }

    [Fact]
    public async Task Unknown_and_expired_urls_answer_not_found()
    {
        var owner = await factory.CreateSignedInClientAsync("upload-expired@example.com");
        var board = await owner.CreateBoardAsync("Upload expiry");
        var url = await CreateAsync(owner, board.Id, new { });
        var anonymous = factory.CreateAnonymousClient();
        var png = new ByteArrayContent(TestImages.Png(40, 30));

        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PutAsync($"/api/image-uploads/{ImageUpload.Prefix}nope", png)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PutAsync("/api/image-uploads/pb_pat_nope", png)).StatusCode);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();
            await db.ImageUploads.Where(u => u.BoardId == board.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        }
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PutAsync(url.UploadUrl, png)).StatusCode);
    }

    [Fact]
    public async Task Asking_for_a_url_checks_the_role_and_the_placement()
    {
        var owner = await factory.CreateSignedInClientAsync("upload-checks@example.com");
        var viewer = await factory.CreateSignedInClientAsync("upload-checks-viewer@example.com");
        var board = await owner.CreateBoardAsync("Upload checks");
        var other = await owner.CreateBoardAsync("Upload checks elsewhere");
        var elsewhere = await owner.CreateNoteAsync(other.Id);
        await owner.ShareAsync(board.Id, viewer, BoardRole.Viewer);

        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsJsonAsync($"/api/boards/{board.Id}/image-uploads", new { })).StatusCode);
        await Invalid(new { x = 20 }, "y");
        await Invalid(new { noteId = elsewhere.Id }, "noteId");
        await Invalid(new { width = 20 }, "width");

        async Task Invalid(object request, string field)
        {
            var response = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/image-uploads", request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(field, await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task A_url_stops_working_when_its_creator_loses_edit_access()
    {
        var owner = await factory.CreateSignedInClientAsync("upload-access@example.com");
        var editor = await factory.CreateSignedInClientAsync("upload-access-editor@example.com");
        var board = await owner.CreateBoardAsync("Upload access");
        await owner.ShareAsync(board.Id, editor, BoardRole.Editor);
        var url = await CreateAsync(editor, board.Id, new { });

        var me = await editor.MeAsync();
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}/members/{me.Id}", new { role = BoardRole.Viewer })).EnsureSuccessStatusCode();

        var response = await factory.CreateAnonymousClient().PutAsync(url.UploadUrl, new ByteArrayContent(TestImages.Png(40, 30)));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Empty((await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!.Images);
    }

    [Fact]
    public async Task A_url_from_an_access_token_acts_as_its_agent_and_stops_working_when_the_token_is_revoked()
    {
        var owner = await factory.CreateSignedInClientAsync("upload-token@example.com");
        var board = await owner.CreateBoardAsync("Upload tokens");
        var (agent, token) = await factory.TokenAsync(owner, "Claude Code");
        var anonymous = factory.CreateAnonymousClient();

        var first = await CreateAsync(agent, board.Id, new { });
        var response = await anonymous.PutAsync(first.UploadUrl, new ByteArrayContent(TestImages.Png(40, 30)));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("Claude Code", (await response.Content.ReadFromJsonAsync<ImageDetail>())!.AddedBy);

        var second = await CreateAsync(agent, board.Id, new { });
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/me/tokens/{token.Details.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await anonymous.PutAsync(second.UploadUrl, new ByteArrayContent(TestImages.Png(40, 30)))).StatusCode);
    }

    [Fact]
    public async Task Unused_urls_are_limited_per_caller()
    {
        var owner = await factory.CreateSignedInClientAsync("upload-limit@example.com");
        var board = await owner.CreateBoardAsync("Upload limit");
        for (var i = 0; i < ImageUpload.MaxPendingPerPrincipal; i++) await CreateAsync(owner, board.Id, new { });

        var response = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/image-uploads", new { });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    private static async Task<ImageUploadUrl> CreateAsync(HttpClient client, Guid boardId, object request)
    {
        var response = await client.PostAsJsonAsync($"/api/boards/{boardId}/image-uploads", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ImageUploadUrl>())!;
    }

    private static ByteArrayContent Content(byte[] data, string contentType)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return content;
    }
}
