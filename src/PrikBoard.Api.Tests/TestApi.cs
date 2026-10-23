using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>Small HTTP helpers shared by the tests: boards, cards, members, display names and tokens.</summary>
internal static class TestApi
{
    public static async Task<Caller> MeAsync(this HttpClient client) => (await client.GetFromJsonAsync<Caller>("/api/me"))!;

    public static async Task<BoardDetail> CreateBoardAsync(this HttpClient client, string name = "Shared")
    {
        var response = await client.PostAsJsonAsync("/api/boards", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<BoardDetail>())!;
    }

    /// <summary>The owner adds the member (by their principal id) to the board with a role.</summary>
    public static async Task ShareAsync(this HttpClient owner, Guid boardId, HttpClient member, BoardRole role)
    {
        var me = await member.MeAsync();
        var response = await owner.PostAsJsonAsync($"/api/boards/{boardId}/members", new { principalId = me.Id, role });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>Sets the caller's display name.</summary>
    public static async Task RenameAsync(this HttpClient client, string displayName) =>
        (await client.PatchAsJsonAsync("/api/me", new { displayName })).EnsureSuccessStatusCode();

    /// <summary>A personal access token of <paramref name="owner"/>, and a client that uses it.</summary>
    public static async Task<(HttpClient Client, CreatedAccessToken Token)> TokenAsync(
        this PrikBoardApiFactory factory, HttpClient owner, string name, BoardRole access = BoardRole.Editor, Guid[]? boardIds = null)
    {
        var response = await owner.PostAsJsonAsync("/api/me/tokens", new { name, access, boardIds });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var token = (await response.Content.ReadFromJsonAsync<CreatedAccessToken>())!;
        var client = factory.CreateAnonymousClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return (client, token);
    }

    public static async Task<CardDetail> CreateCardAsync(this HttpClient client, Guid boardId, string title = "A card")
    {
        var response = await client.PostAsJsonAsync($"/api/boards/{boardId}/cards", new { title });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<CardDetail>())!;
    }
}
