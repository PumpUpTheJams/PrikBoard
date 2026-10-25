using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>Exporting a board to a file and importing it as a new board.</summary>
public class BoardTransferApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>
{
    [Fact]
    public async Task An_exported_board_imports_as_a_new_board_with_everything_on_it()
    {
        var owner = await factory.CreateSignedInClientAsync("export-owner@example.com");
        var board = await owner.CreateBoardAsync("Release 2.0");
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { description = "Ship it", rules = "Be brief." })).EnsureSuccessStatusCode();
        var doing = board.Buckets.Single(b => b.Name == "Doing");

        var target = await owner.CreateCardAsync(board.Id, "Target");
        var response = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new
        {
            title = "Write the changelog",
            color = "Mint",
            bucketId = doing.Id,
            tags = new[] { "Docs", "release" },
            components = new object[]
            {
                new { kind = "Text", title = "Why", text = "Everything that changed.", x = 400, y = 600, width = 500, height = 300 },
                new { kind = "Checklist", items = new[] { new { text = "Draft", done = true }, new { text = "Review", done = false } } },
                new
                {
                    kind = "Links",
                    links = new[]
                    {
                        new { title = "Spec", url = "https://example.com/spec" },
                        new { title = "The target", url = target.Path },
                        new { title = "Elsewhere", url = $"/#/boards/{Guid.NewGuid()}/cards/{Guid.NewGuid()}" },
                    },
                },
            },
        });
        response.EnsureSuccessStatusCode();
        var card = (await response.Content.ReadFromJsonAsync<CardDetail>())!;
        (await owner.PostAsJsonAsync($"/api/boards/{board.Id}/tags", new { name = "unused", color = "Teal" })).EnsureSuccessStatusCode();
        var png = TestImages.Png(400, 200);
        var onCanvas = await Upload(owner, board.Id, png, "?fileName=canvas.png&x=600&y=700&width=200&caption=Diagram");
        var attached = await Upload(owner, board.Id, TestImages.Png(100, 100), $"?fileName=attached.png&cardId={card.Id}&x=1000&y=1200");

        var exported = await owner.GetAsync($"/api/boards/{board.Id}/export");
        exported.EnsureSuccessStatusCode();
        Assert.Equal("attachment", exported.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal("Release 2.0.prikboard.json", exported.Content.Headers.ContentDisposition?.FileNameStar);
        var file = await exported.Content.ReadAsStringAsync();
        Assert.Contains("\"format\":\"prikboard-board\"", file);
        Assert.Contains("\"version\":8", file);
        Assert.DoesNotContain("\"color\":\"Blue\"", file);
        Assert.DoesNotContain("export-owner@example.com", file);

        // Someone else imports it: a new board they own, and nothing changes on the original.
        var importer = await factory.CreateSignedInClientAsync("export-importer@example.com");
        var imported = await Import(importer, file);
        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        var copy = (await imported.Content.ReadFromJsonAsync<BoardDetail>())!;
        var me = await importer.MeAsync();

        Assert.NotEqual(board.Id, copy.Id);
        Assert.Equal(("Release 2.0", "Ship it", "Be brief.", BoardRole.Owner), (copy.Name, copy.Description, copy.Rules, copy.YourRole));
        Assert.Equal(board.Buckets.Select(b => (b.Name, b.X, b.Y, b.Width, b.Height)),
            copy.Buckets.Select(b => (b.Name, b.X, b.Y, b.Width, b.Height)));
        Assert.Equal([("docs", TagColor.Default), ("release", TagColor.Default), ("unused", TagColor.Teal)],
            copy.Tags.Select(t => (t.Name, t.Color)).Order());

        var copiedCard = copy.Cards.Single(n => n.Title == "Write the changelog");
        var copiedTarget = copy.Cards.Single(n => n.Title == "Target");
        Assert.Equal(copy.Buckets.Single(b => b.Name == "Doing").Id, copiedCard.BucketId);
        Assert.Equal(CardColor.Mint, copiedCard.Color);
        Assert.Equal(["docs", "release"], copiedCard.Tags);
        Assert.Equal((card.X, card.Y), (copiedCard.X, copiedCard.Y));
        Assert.Equal(card.Components.Select(b => (b.Kind, b.Title, b.Text, b.X, b.Y, b.Width, b.Height)),
            copiedCard.Components.Select(b => (b.Kind, b.Title, b.Text, b.X, b.Y, b.Width, b.Height)));
        Assert.Equal([("Draft", true), ("Review", false)], copiedCard.Components[1].Items!.Select(c => (c.Text, c.Done)));
        Assert.Equal([("Spec", "https://example.com/spec"), ("The target", copiedTarget.Path)],
            copiedCard.Components[2].Links!.Select(l => (l.Title, l.Url)));
        Assert.Equal((me.Id, me.DisplayName), (copiedCard.CreatedById, copiedCard.CreatedBy));
        Assert.Empty(copiedCard.Assignees);

        Assert.Equal(2, copy.Images.Count);
        var copiedCanvas = copy.Images.Single(i => i.CardId is null);
        var copiedAttached = copy.Images.Single(i => i.CardId is not null);
        Assert.Equal(copiedCard.Id, copiedAttached.CardId);
        Assert.Equal(("canvas.png", "Diagram", onCanvas.X, onCanvas.Y, onCanvas.Width, onCanvas.Height),
            (copiedCanvas.FileName, copiedCanvas.Caption, copiedCanvas.X, copiedCanvas.Y, copiedCanvas.Width, copiedCanvas.Height));
        Assert.Equal(png, await (await importer.GetAsync(copiedCanvas.ContentUrl)).Content.ReadAsByteArrayAsync());
        Assert.Equal(("attached.png", attached.Size), (copiedAttached.FileName, copiedAttached.Size));
        Assert.Equal((attached.X, attached.Y, attached.Width), (copiedAttached.X, copiedAttached.Y, copiedAttached.Width));

        var original = (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.Equal(board.Id, original.Id);
        Assert.Equal(2, original.Cards.Count);
        var boards = (await importer.GetFromJsonAsync<List<BoardSummary>>("/api/boards"))!;
        Assert.Equal(copy.Id, Assert.Single(boards).Id);
    }

    [Fact]
    public async Task A_file_from_before_cards_is_refused_with_a_clear_message()
    {
        var client = await factory.CreateSignedInClientAsync("export-version2@example.com");
        // As exportBoard wrote them then: notes with blocks, images by noteId. Versions 1 and 2 go through the same check.
        var file = new
        {
            format = "prikboard-board",
            version = 2,
            exportedAt = DateTimeOffset.UtcNow,
            name = "Old export",
            buckets = Array.Empty<object>(),
            tags = Array.Empty<string>(),
            notes = new[] { new { id = Guid.NewGuid(), title = "Old", color = "Sky", tags = Array.Empty<string>(), x = 40, y = 40, blocks = Array.Empty<object>() } },
            images = Array.Empty<object>(),
        };
        var before = await BoardCount(client);

        var response = await client.PostAsJsonAsync("/api/boards/import", file);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        // The web app shows the first error, so the version comes first.
        var (key, messages) = (await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject().First();
        Assert.Equal("version", key, ignoreCase: true);
        Assert.Equal(BoardExport.TooOldMessage, messages![0]!.GetValue<string>());
        Assert.Equal(before, await BoardCount(client));
    }

    [Fact]
    public async Task Viewers_can_export_but_outsiders_cannot()
    {
        var owner = await factory.CreateSignedInClientAsync("export-share-owner@example.com");
        var viewer = await factory.CreateSignedInClientAsync("export-share-viewer@example.com");
        var outsider = await factory.CreateSignedInClientAsync("export-share-outsider@example.com");
        var board = await owner.CreateBoardAsync("Shared");
        await owner.ShareAsync(board.Id, viewer, BoardRole.Viewer);

        Assert.Equal(HttpStatusCode.OK, (await viewer.GetAsync($"/api/boards/{board.Id}/export")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync($"/api/boards/{board.Id}/export")).StatusCode);
    }

    [Fact]
    public async Task A_token_limited_to_chosen_boards_cannot_import()
    {
        var owner = await factory.CreateSignedInClientAsync("export-limited@example.com");
        var board = await owner.CreateBoardAsync("Limited");
        var file = await (await owner.GetAsync($"/api/boards/{board.Id}/export")).Content.ReadAsStringAsync();
        var (limited, _) = await factory.TokenAsync(owner, "only this board", BoardRole.Editor, [board.Id]);
        var (open, _) = await factory.TokenAsync(owner, "any board");

        Assert.Equal(HttpStatusCode.Forbidden, (await Import(limited, file)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await Import(open, file)).StatusCode);
    }

    [Theory]
    [InlineData("format", "something-else", "format")]
    [InlineData("name", "", "Name")]
    public async Task A_file_that_is_not_a_valid_export_is_refused(string property, object value, string errorKey)
    {
        var client = await factory.CreateSignedInClientAsync("export-invalid@example.com");
        var board = await client.CreateBoardAsync("Valid");
        var file = JsonNode.Parse(await (await client.GetAsync($"/api/boards/{board.Id}/export")).Content.ReadAsStringAsync())!;
        file[property] = JsonSerializer.SerializeToNode(value);
        var before = await BoardCount(client);

        var response = await Import(client, file.ToJsonString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = (await response.Content.ReadFromJsonAsync<JsonObject>())!;
        Assert.Contains(problem["errors"]!.AsObject().Select(e => e.Key), key => key.Equals(errorKey, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(before, await BoardCount(client));
    }

    [Fact]
    public async Task An_export_names_its_format_and_app_version_and_a_version_3_file_still_imports()
    {
        var client = await factory.CreateSignedInClientAsync("export-appversion@example.com");
        var board = await client.CreateBoardAsync("Versions");
        await client.CreateCardAsync(board.Id);
        var file = JsonNode.Parse(await (await client.GetAsync($"/api/boards/{board.Id}/export")).Content.ReadAsStringAsync())!;

        Assert.Equal(BoardExport.CurrentVersion, file["version"]!.GetValue<int>());
        Assert.Equal(8, BoardExport.CurrentVersion);
        Assert.False(string.IsNullOrEmpty(file["appVersion"]!.GetValue<string>()));
        Assert.DoesNotContain("+", file["appVersion"]!.GetValue<string>());

        // As 0.0.27 wrote them: version 3, without appVersion or the buckets' places in the stack, and with bucket colors.
        file["version"] = 3;
        file.AsObject().Remove("appVersion");
        foreach (var bucket in file["buckets"]!.AsArray())
        {
            bucket!.AsObject().Remove("stack");
            bucket["color"] = "Blue";
        }
        Assert.Equal(HttpStatusCode.Created, (await Import(client, file.ToJsonString())).StatusCode);
    }

    [Fact]
    public async Task A_newer_file_version_or_fields_this_server_does_not_know_are_refused_naming_them_and_the_app_version()
    {
        var client = await factory.CreateSignedInClientAsync("export-unknown@example.com");
        var board = await client.CreateBoardAsync("Newer");
        await client.CreateCardAsync(board.Id);
        var file = JsonNode.Parse(await (await client.GetAsync($"/api/boards/{board.Id}/export")).Content.ReadAsStringAsync())!;
        file["appVersion"] = "9.1.0";
        var newer = file.DeepClone();
        newer["version"] = BoardExport.CurrentVersion + 1;
        file["stickers"] = new JsonArray();
        file["cards"]![0]!["priority"] = "high";
        var before = await BoardCount(client);

        var response = await Import(client, file.ToJsonString());
        var newerResponse = await Import(client, newer.ToJsonString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var (key, messages) = (await response.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject().First();
        Assert.Equal("version", key);
        var message = messages![0]!.GetValue<string>();
        Assert.Contains("stickers", message);
        Assert.Contains("cards[0].priority", message);
        Assert.Contains("PrikBoard 9.1.0", message);
        Assert.Equal(before, await BoardCount(client));

        Assert.Equal(HttpStatusCode.BadRequest, newerResponse.StatusCode);
        var (newerKey, newerMessages) = (await newerResponse.Content.ReadFromJsonAsync<JsonObject>())!["errors"]!.AsObject().First();
        Assert.Equal("version", newerKey);
        Assert.Contains("PrikBoard 9.1.0", newerMessages![0]!.GetValue<string>());
    }

    [Fact]
    public async Task An_image_that_is_not_an_image_is_refused_and_nothing_is_created()
    {
        var client = await factory.CreateSignedInClientAsync("export-badimage@example.com");
        var board = await client.CreateBoardAsync("Bad image");
        await Upload(client, board.Id, TestImages.Png(100, 100), "");
        await Upload(client, board.Id, TestImages.Png(200, 100), "");
        var file = JsonNode.Parse(await (await client.GetAsync($"/api/boards/{board.Id}/export")).Content.ReadAsStringAsync())!;
        file["images"]![1]!["data"] = Convert.ToBase64String("<svg onload=alert(1)/>"u8.ToArray());
        var before = await BoardCount(client);
        var files = factory.FilesOnDisk().Length;

        var response = await Import(client, file.ToJsonString());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(before, await BoardCount(client));
        // Not even the good image before it.
        Assert.Equal(files, factory.FilesOnDisk().Length);
    }

    [Fact]
    public async Task An_untitled_card_survives_export_and_import_and_importing_the_same_file_twice_makes_two_boards()
    {
        var client = await factory.CreateSignedInClientAsync("export-untitled@example.com");
        var board = await client.CreateBoardAsync("Untitled");
        await client.CreateCardAsync(board.Id, "");
        var file = await (await client.GetAsync($"/api/boards/{board.Id}/export")).Content.ReadAsStringAsync();

        var response = await Import(client, file);
        var second = (await (await Import(client, file)).Content.ReadFromJsonAsync<BoardDetail>())!;

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var copy = (await response.Content.ReadFromJsonAsync<BoardDetail>())!;
        Assert.Equal("", Assert.Single(copy.Cards).Title);
        Assert.NotEqual(copy.Id, second.Id);
        Assert.NotEqual(copy.Cards.Single().Id, second.Cards.Single().Id);
        Assert.Equal(3, (await client.GetFromJsonAsync<List<BoardSummary>>("/api/boards"))!.Count);
    }

    // Short board ids, board keys and card numbers: the parts of a card's path and readable id (MPCT-415).
    [Theory]
    [InlineData("MCP me some tickets", "MMST")]
    [InlineData("PrikBoard", "PRIK")]
    [InlineData("2026 plans", "PLAN")]
    [InlineData("Café & co", "CC")]
    [InlineData("!!", "PB")]
    public void A_key_comes_from_the_board_name(string name, string key) => Assert.Equal(key, Board.KeyFrom(name));

    [Fact]
    public async Task Boards_get_a_short_id_that_never_changes_and_a_key_owners_can_change()
    {
        var owner = await factory.CreateSignedInClientAsync("ids-owner@example.com");
        var board = await owner.CreateBoardAsync("MCP me some tickets");
        var other = await owner.CreateBoardAsync("MCP me some tickets");
        Assert.Equal("MMST", board.Key);
        Assert.Matches("^[0-9a-hjkmnp-tv-z]{6}$", board.ShortId);
        Assert.NotEqual(board.ShortId, other.ShortId);
        var card = await owner.CreateCardAsync(board.Id);
        Assert.Equal("MMST-1", card.ReadableId);

        var renamed = await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { name = "PrikBoard", key = "mpct" });
        renamed.EnsureSuccessStatusCode();
        var summary = (await renamed.Content.ReadFromJsonAsync<BoardSummary>())!;
        Assert.Equal((board.ShortId, "MPCT", "PrikBoard"), (summary.ShortId, summary.Key, summary.Name));
        var after = (await owner.GetFromJsonAsync<CardDetail>($"/api/cards/{card.Id}"))!;
        Assert.Equal(("MPCT-1", card.Path), (after.ReadableId, after.Path));

        foreach (var key in new[] { "1AB", "A", "TOOLONGAKEY", "MP-CT", "" })
        {
            var response = await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { key });
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, key);
        }
        var editor = await factory.CreateSignedInClientAsync("ids-editor@example.com");
        await owner.ShareAsync(board.Id, editor, BoardRole.Editor);
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.PatchAsJsonAsync($"/api/boards/{board.Id}", new { key = "EDIT" })).StatusCode);
    }

    [Fact]
    public async Task Cards_are_numbered_per_board_and_a_number_is_never_used_twice()
    {
        var owner = await factory.CreateSignedInClientAsync("ids-numbers@example.com");
        var board = await owner.CreateBoardAsync("Numbers");
        var other = await owner.CreateBoardAsync("Other numbers");

        var cards = new List<CardDetail>();
        for (var i = 0; i < 3; i++) cards.Add(await owner.CreateCardAsync(board.Id));
        Assert.Equal([1, 2, 3], cards.Select(n => n.Number));
        Assert.Equal(1, (await owner.CreateCardAsync(other.Id)).Number);

        (await owner.DeleteAsync($"/api/cards/{cards[2].Id}")).EnsureSuccessStatusCode();
        Assert.Equal(4, (await owner.CreateCardAsync(board.Id)).Number);
        var restored = await owner.PostAsJsonAsync($"/api/cards/{cards[2].Id}/restore", new { });
        restored.EnsureSuccessStatusCode();
        Assert.Equal(3, (await restored.Content.ReadFromJsonAsync<CardDetail>())!.Number);

        var together = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => owner.CreateCardAsync(board.Id)));
        Assert.Equal(Enumerable.Range(5, 10), together.Select(n => n.Number).Order());
    }

    [Fact]
    public async Task A_card_is_found_by_the_short_id_and_number_in_its_path()
    {
        var owner = await factory.CreateSignedInClientAsync("ids-lookup@example.com");
        var board = await owner.CreateBoardAsync("Lookup");
        var first = await owner.CreateCardAsync(board.Id, "First");
        var second = await owner.CreateCardAsync(board.Id, "Second");

        var found = (await owner.GetFromJsonAsync<CardDetail>($"/api/boards/{board.ShortId}/cards/2"))!;
        Assert.Equal((second.Id, $"/#/boards/{board.ShortId}/cards/2"), (found.Id, found.Path));

        var stranger = await factory.CreateSignedInClientAsync("ids-stranger@example.com");
        (await owner.DeleteAsync($"/api/cards/{first.Id}")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/boards/{board.ShortId}/cards/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/boards/{board.ShortId}/cards/9")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync("/api/boards/zzzzzz/cards/1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync($"/api/boards/{board.ShortId}/cards/2")).StatusCode);
    }

    [Fact]
    public async Task Links_in_the_older_form_to_cards_on_the_board_are_stored_as_paths()
    {
        var owner = await factory.CreateSignedInClientAsync("ids-links@example.com");
        var board = await owner.CreateBoardAsync("Links");
        var elsewhere = await owner.CreateBoardAsync("Elsewhere");
        var target = await owner.CreateCardAsync(board.Id, "Target");
        var away = await owner.CreateCardAsync(elsewhere.Id, "Away");
        var old = $"/#/boards/{board.Id}/cards/{target.Id}";
        var oldAway = $"/#/boards/{elsewhere.Id}/cards/{away.Id}";

        var response = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new
        {
            title = "Linking",
            components = new[] { new { kind = "Links", links = new[] { new { title = "Target", url = old }, new { title = "Away", url = oldAway } } } },
        });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var linking = (await response.Content.ReadFromJsonAsync<CardDetail>())!;
        // A link to another board stays as it was, so it reveals nothing new about that board (such as its short id).
        Assert.Equal([target.Path, oldAway], linking.Components.Single().Links!.Select(l => l.Url));

        var added = await owner.PostAsJsonAsync($"/api/cards/{linking.Id}/components", new { kind = "Links", links = new[] { new { title = "Again", url = old } } });
        Assert.Equal(target.Path, (await added.Content.ReadFromJsonAsync<CardComponentDetail>())!.Links!.Single().Url);
        var component = linking.Components.Single();
        var updated = await owner.PatchAsJsonAsync($"/api/cards/{linking.Id}/components/{component.Id}", new { links = new[] { new { title = "Target", url = $"  {old}  " } } });
        Assert.Equal(target.Path, (await updated.Content.ReadFromJsonAsync<CardComponentDetail>())!.Links!.Single().Url);
    }

    [Fact]
    public async Task An_imported_board_keeps_the_key_and_the_card_numbers()
    {
        var owner = await factory.CreateSignedInClientAsync("ids-export@example.com");
        var board = await owner.CreateBoardAsync("Exported");
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { key = "EXP" })).EnsureSuccessStatusCode();
        var first = await owner.CreateCardAsync(board.Id, "First");
        var gone = await owner.CreateCardAsync(board.Id, "Gone");
        (await owner.DeleteAsync($"/api/cards/{gone.Id}")).EnsureSuccessStatusCode();
        var linking = await owner.PostAsJsonAsync($"/api/boards/{board.Id}/cards", new
        {
            title = "Third",
            components = new[] { new { kind = "Links", links = new[] { new { title = "First", url = first.Path } } } },
        });
        linking.EnsureSuccessStatusCode();

        var file = await owner.GetStringAsync($"/api/boards/{board.Id}/export");
        var imported = await owner.PostAsync("/api/boards/import", new StringContent(file, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Created, imported.StatusCode);
        var copy = (await imported.Content.ReadFromJsonAsync<BoardDetail>())!;

        Assert.Equal("EXP", copy.Key);
        Assert.NotEqual(board.ShortId, copy.ShortId);
        Assert.Equal([("First", 1), ("Third", 3)], copy.Cards.Select(n => (n.Title, n.Number)).Order());
        var copiedFirst = copy.Cards.Single(n => n.Title == "First");
        Assert.Equal($"/#/boards/{copy.ShortId}/cards/1", copiedFirst.Path);
        Assert.Equal(copiedFirst.Path, copy.Cards.Single(n => n.Title == "Third").Components.Single().Links!.Single().Url);
        Assert.Equal(4, (await owner.CreateCardAsync(copy.Id)).Number);
    }

    private static async Task<int> BoardCount(HttpClient client) =>
        (await client.GetFromJsonAsync<List<BoardSummary>>("/api/boards"))!.Count;

    private static Task<HttpResponseMessage> Import(HttpClient client, string file) =>
        client.PostAsync("/api/boards/import", new StringContent(file, System.Text.Encoding.UTF8, "application/json"));

    private static async Task<ImageDetail> Upload(HttpClient client, Guid boardId, byte[] bytes, string query)
    {
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new("image/png");
        var response = await client.PostAsync($"/api/boards/{boardId}/images{query}", content);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<ImageDetail>())!;
    }
}
