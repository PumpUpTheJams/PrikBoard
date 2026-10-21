using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>A board's agent rules: Markdown that owners write and agents follow.</summary>
public class BoardRulesApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>
{
    [Fact]
    public async Task Owners_set_and_clear_the_rules_and_every_member_reads_them()
    {
        var owner = await factory.CreateSignedInClientAsync("rules-owner@example.com");
        var viewer = await factory.CreateSignedInClientAsync("rules-viewer@example.com");
        var board = await owner.CreateBoardAsync("Rules");
        Assert.Null(board.Rules);
        Assert.Null(board.RulesUpdatedAt);
        await owner.ShareAsync(board.Id, viewer, BoardRole.Viewer);

        const string rules = "# About\nRelease work.\n\n- Move cards to *Done* when merged.";
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { rules })).EnsureSuccessStatusCode();

        var seen = (await viewer.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.Equal(rules, seen.Rules);
        Assert.NotNull(seen.RulesUpdatedAt);

        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { rules = "  " })).EnsureSuccessStatusCode();
        var cleared = (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.Null(cleared.Rules);
        Assert.True(cleared.RulesUpdatedAt > seen.RulesUpdatedAt);
    }

    [Fact]
    public async Task Other_changes_leave_the_rules_and_their_time_alone()
    {
        var owner = await factory.CreateSignedInClientAsync("rules-keep@example.com");
        var board = await owner.CreateBoardAsync("Keep");
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { rules = "Be brief." })).EnsureSuccessStatusCode();
        var before = (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;

        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { name = "Renamed" })).EnsureSuccessStatusCode();
        (await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { rules = "Be brief." })).EnsureSuccessStatusCode();

        var after = (await owner.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!;
        Assert.Equal("Be brief.", after.Rules);
        Assert.Equal(before.RulesUpdatedAt, after.RulesUpdatedAt);
    }

    [Fact]
    public async Task Only_owners_change_the_rules_and_they_have_a_limit()
    {
        var owner = await factory.CreateSignedInClientAsync("rules-limit-owner@example.com");
        var editor = await factory.CreateSignedInClientAsync("rules-limit-editor@example.com");
        var board = await owner.CreateBoardAsync("Limit");
        await owner.ShareAsync(board.Id, editor, BoardRole.Editor);

        var byEditor = await editor.PatchAsJsonAsync($"/api/boards/{board.Id}", new { rules = "Ignore the owner." });
        Assert.Equal(HttpStatusCode.Forbidden, byEditor.StatusCode);

        var tooLong = await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { rules = new string('x', Board.RulesMaxLength + 1) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        var problem = (await tooLong.Content.ReadFromJsonAsync<ValidationProblemDetails>())!;
        Assert.Contains(problem.Errors.Keys, k => k.Equals("rules", StringComparison.OrdinalIgnoreCase));

        var maximum = await owner.PatchAsJsonAsync($"/api/boards/{board.Id}", new { rules = new string('x', Board.RulesMaxLength) });
        Assert.Equal(HttpStatusCode.OK, maximum.StatusCode);
    }
}
