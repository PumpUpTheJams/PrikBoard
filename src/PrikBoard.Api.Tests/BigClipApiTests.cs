using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>
/// Copying and pasting a whole board's worth: clips without their images' data (copyItems' imageData), images uploaded
/// ahead of the paste (uploadPasteImage), checkPaste, and who the copies' creator is.
/// </summary>
public class BigClipApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>
{
    [Fact]
    public async Task A_clip_without_image_data_pastes_with_the_images_uploaded_first()
    {
        var owner = await factory.CreateSignedInClientAsync("big-clip@example.com");
        var from = await owner.CreateBoardAsync("Big source");
        var to = await owner.CreateBoardAsync("Big target");
        var card = await owner.CreateCardAsync(from.Id, "With a picture");
        var onCard = TestImages.Png(300, 100);
        var loose = TestImages.Png(120, 240);
        var attached = await UploadAsync(owner, from.Id, onCard, $"?fileName=on-card.png&cardId={card.Id}&x=40&y=400&width=200");
        var board = await UploadAsync(owner, from.Id, loose, "?fileName=loose.png&x=2000&y=2000&width=120");

        var copied = await owner.PostAsJsonAsync($"/api/boards/{from.Id}/copy",
            new { cardIds = new[] { card.Id }, imageIds = new[] { board.Id }, imageData = false });
        Assert.Equal(HttpStatusCode.OK, copied.StatusCode);
        var clip = (await copied.Content.ReadFromJsonAsync<JsonObject>())!;
        var images = clip["images"]!.AsArray();
        Assert.All(images, i => Assert.Equal("", (string?)i!["data"]));
        Assert.Equal([attached.Height, board.Height], images.Select(i => (int)i!["height"]!));

        // Checked before anything is uploaded; without data or uploads the paste itself is refused.
        var check = await owner.PostAsJsonAsync($"/api/boards/{to.Id}/paste/check", new { clip, x = 0, y = 0 });
        Assert.Equal(HttpStatusCode.NoContent, check.StatusCode);
        var missing = await owner.PostAsJsonAsync($"/api/boards/{to.Id}/paste", new { clip, x = 0, y = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.Contains("clip.images[0].data", await missing.Content.ReadAsStringAsync());
        Assert.Empty((await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{to.Id}"))!.Cards);

        // The web app fetches each image and uploads it to the board it pastes on.
        var tokens = new List<string>();
        foreach (var image in images)
        {
            var bytes = await owner.GetByteArrayAsync($"/api/images/{(string?)image!["id"]}/content");
            tokens.Add((await UploadForPasteAsync(owner, to.Id, bytes)).Token);
        }
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync($"/api/boards/{to.Id}/paste/check", new { clip, x = 0, y = 0, imageFiles = tokens })).StatusCode);

        var response = await owner.PostAsJsonAsync($"/api/boards/{to.Id}/paste", new { clip, x = 0, y = 0, imageFiles = tokens });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var pasted = (await response.Content.ReadFromJsonAsync<PastedItems>())!;
        var copy = Assert.Single(pasted.Cards);
        var pastedOnCard = pasted.Images.Single(i => i.CardId == copy.Id);
        var pastedLoose = pasted.Images.Single(i => i.CardId is null);
        Assert.Equal(("on-card.png", 200, attached.Height), (pastedOnCard.FileName, pastedOnCard.Width, pastedOnCard.Height));
        Assert.Equal(("loose.png", 120), (pastedLoose.FileName, pastedLoose.Width));
        Assert.Equal(onCard, await owner.GetByteArrayAsync($"/api/images/{pastedOnCard.Id}/content"));
        Assert.Equal(loose, await owner.GetByteArrayAsync($"/api/images/{pastedLoose.Id}/content"));
        Assert.True(File.Exists((await factory.ImageFileAsync(pastedLoose.Id)).Path));

        // An upload goes with one paste only.
        var again = await owner.PostAsJsonAsync($"/api/boards/{to.Id}/paste", new { clip, x = 0, y = 0, imageFiles = tokens });
        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Contains("imageFiles", await again.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_upload_works_for_its_own_person_and_board_only()
    {
        var owner = await factory.CreateSignedInClientAsync("upload-owner@example.com");
        var other = await factory.CreateSignedInClientAsync("upload-other@example.com");
        var from = await owner.CreateBoardAsync("Upload source");
        var to = await owner.CreateBoardAsync("Upload target");
        var elsewhere = await owner.CreateBoardAsync("Upload elsewhere");
        await owner.ShareAsync(to.Id, other, BoardRole.Editor);
        var image = await UploadAsync(owner, from.Id, TestImages.Png(50, 50), "?x=0&y=0");
        var clip = await CopyWithoutDataAsync(owner, from.Id, image.Id);
        var png = TestImages.Png(50, 50);

        async Task<HttpResponseMessage> PasteWith(HttpClient client, string token) =>
            await client.PostAsJsonAsync($"/api/boards/{to.Id}/paste", new { clip, x = 0, y = 0, imageFiles = new[] { token } });

        var forAnotherBoard = await UploadForPasteAsync(owner, elsewhere.Id, png);
        var byOwner = await UploadForPasteAsync(owner, to.Id, png);
        foreach (var (client, token) in new[] { (owner, forAnotherBoard.Token), (other, byOwner.Token), (owner, "not-a-token") })
        {
            var refused = await PasteWith(client, token);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("imageFiles[0]", await refused.Content.ReadAsStringAsync());
        }
        Assert.Equal(HttpStatusCode.OK, (await PasteWith(owner, byOwner.Token)).StatusCode);

        // Not an image, or from someone who can only view.
        var text = new StringContent("hello");
        text.Headers.ContentType = new("text/plain");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await owner.PostAsync($"/api/boards/{to.Id}/paste-images", text)).StatusCode);
        var notAnImage = new ByteArrayContent("hello"u8.ToArray());
        notAnImage.Headers.ContentType = new("image/png");
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await owner.PostAsync($"/api/boards/{to.Id}/paste-images", notAnImage)).StatusCode);
        var viewer = await factory.CreateSignedInClientAsync("upload-viewer@example.com");
        await owner.ShareAsync(to.Id, viewer, BoardRole.Viewer);
        var content = new ByteArrayContent(png);
        content.Headers.ContentType = new("image/png");
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.PostAsync($"/api/boards/{to.Id}/paste-images", content)).StatusCode);
    }

    [Fact]
    public async Task Uploads_no_paste_used_are_swept()
    {
        var owner = await factory.CreateSignedInClientAsync("upload-sweep@example.com");
        var board = await owner.CreateBoardAsync("Upload sweep");
        await UploadForPasteAsync(owner, board.Id, TestImages.Png(70, 70));
        var pasting = Path.Combine(factory.FilesPath, "pasting");
        Assert.NotEmpty(Directory.GetFiles(pasting));

        await factory.SweepFilesAsync(DateTimeOffset.UtcNow.AddMinutes(30));
        Assert.NotEmpty(Directory.GetFiles(pasting));
        await factory.SweepFilesAsync(DateTimeOffset.UtcNow.AddHours(3));
        Assert.Empty(Directory.GetFiles(pasting));
    }

    [Fact]
    public async Task Copies_keep_their_creator_on_the_board_they_came_from_only()
    {
        var author = await factory.CreateSignedInClientAsync("clip-author@example.com");
        var copier = await factory.CreateSignedInClientAsync("clip-copier@example.com");
        var board = await author.CreateBoardAsync("Creators");
        var own = await copier.CreateBoardAsync("Copier's own");
        await author.ShareAsync(board.Id, copier, BoardRole.Editor);
        var card = await author.CreateCardAsync(board.Id, "Written by the author");
        var authorId = (await author.MeAsync()).Id;
        var copierId = (await copier.MeAsync()).Id;

        var clip = await CopyAsync(copier, board.Id, card.Id);
        Assert.Equal(authorId, Guid.Parse((string)clip["cards"]![0]!["createdById"]!));
        var here = await PasteAsync(copier, board.Id, new { clip, x = 1000, y = 1000 });
        Assert.Equal(authorId, Assert.Single(here.Cards).CreatedById);
        var there = await PasteAsync(copier, own.Id, new { clip, x = 1000, y = 1000 });
        Assert.Equal(copierId, Assert.Single(there.Cards).CreatedById);
    }

    [Fact]
    public async Task A_check_adds_nothing_and_refuses_what_a_paste_would()
    {
        var owner = await factory.CreateSignedInClientAsync("clip-check@example.com");
        var from = await owner.CreateBoardAsync("Check source");
        var to = await owner.CreateBoardAsync("Check target");
        var card = await owner.CreateCardAsync(from.Id, "Checked");
        var clip = await CopyAsync(owner, from.Id, card.Id);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync($"/api/boards/{to.Id}/paste/check", new { clip, x = 0, y = 0 })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync($"/api/boards/{to.Id}/paste/check", new { clip })).StatusCode);
        var after = (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{to.Id}"))!;
        Assert.Empty(after.Cards);
        Assert.Equal(3, after.Buckets.Count);
        // The number it would have taken is still free.
        Assert.Equal(1, Assert.Single((await PasteAsync(owner, to.Id, new { clip, x = 0, y = 0 })).Cards).Number);

        var cut = (JsonObject)clip.DeepClone();
        cut["cut"] = true;
        var sameBoard = await owner.PostAsJsonAsync($"/api/boards/{from.Id}/paste/check", new { clip = cut, x = 0, y = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, sameBoard.StatusCode);
        Assert.Contains("clip.cut", await sameBoard.Content.ReadAsStringAsync());
        var newer = (JsonObject)clip.DeepClone();
        newer["version"] = BoardClip.CurrentVersion + 1;
        var refused = await owner.PostAsJsonAsync($"/api/boards/{to.Id}/paste/check", new { clip = newer, x = 0, y = 0 });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("clip.version", await refused.Content.ReadAsStringAsync());

        // The cut itself, checked on another board, leaves the original where it is.
        Assert.Equal(HttpStatusCode.NoContent, (await owner.PostAsJsonAsync($"/api/boards/{to.Id}/paste/check", new { clip = cut, x = 0, y = 0 })).StatusCode);
        Assert.Single((await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{from.Id}"))!.Cards);
    }

    [Fact]
    public async Task A_whole_board_of_cards_pastes_in_one_go()
    {
        var owner = await factory.CreateSignedInClientAsync("clip-whole@example.com");
        var from = await owner.CreateBoardAsync("Whole source");
        var to = await owner.CreateBoardAsync("Whole target");
        var cards = Enumerable.Range(0, BoardClip.MaxCards).Select(i => new JsonObject
        {
            ["id"] = Guid.NewGuid(),
            ["number"] = i + 1,
            ["title"] = $"Card {i + 1}",
            ["color"] = "Butter",
            ["tags"] = new JsonArray("bulk"),
            ["x"] = i % 50 * 220,
            ["y"] = i / 50 * 140,
            ["components"] = new JsonArray(new JsonObject { ["kind"] = "Text", ["text"] = $"Note {i + 1}" }),
        });
        var clip = new JsonObject
        {
            ["format"] = "prikboard-clip",
            ["version"] = BoardClip.CurrentVersion,
            ["copiedAt"] = DateTimeOffset.UtcNow,
            ["source"] = new JsonObject { ["url"] = "http://localhost", ["boardShortId"] = from.ShortId, ["boardKey"] = from.Key },
            ["cards"] = new JsonArray([.. cards]),
            ["images"] = new JsonArray(),
            ["drawings"] = new JsonArray(),
        };

        var pasted = await PasteAsync(owner, to.Id, new { clip, x = 0, y = 4000 });
        Assert.Equal(BoardClip.MaxCards, pasted.Cards.Count);
        Assert.Equal(Enumerable.Range(1, BoardClip.MaxCards), pasted.Cards.Select(n => n.Number).Order());
        Assert.Equal(BoardClip.MaxCards, (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{to.Id}"))!.Cards.Count);
    }

    private static async Task<JsonObject> CopyAsync(HttpClient client, Guid boardId, params Guid[] cardIds)
    {
        var response = await client.PostAsJsonAsync($"/api/boards/{boardId}/copy", new { cardIds });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private static async Task<JsonObject> CopyWithoutDataAsync(HttpClient client, Guid boardId, Guid imageId)
    {
        var response = await client.PostAsJsonAsync($"/api/boards/{boardId}/copy", new { imageIds = new[] { imageId }, imageData = false });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonObject>())!;
    }

    private static async Task<PastedItems> PasteAsync(HttpClient client, Guid boardId, object request)
    {
        var response = await client.PostAsJsonAsync($"/api/boards/{boardId}/paste", request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PastedItems>())!;
    }

    private static async Task<PasteImageFile> UploadForPasteAsync(HttpClient client, Guid boardId, byte[] bytes)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("image/png");
        var response = await client.PostAsync($"/api/boards/{boardId}/paste-images", content);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PasteImageFile>())!;
    }

    private static async Task<ImageDetail> UploadAsync(HttpClient client, Guid boardId, byte[] bytes, string query)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("image/png");
        var response = await client.PostAsync($"/api/boards/{boardId}/images{query}", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ImageDetail>())!;
    }
}
