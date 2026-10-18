using System.Net;
using System.Net.Http.Json;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>Small HTTP helpers shared by the tests that involve several people.</summary>
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

    public static async Task<NoteDetail> CreateNoteAsync(this HttpClient client, Guid boardId, string title = "A note")
    {
        var response = await client.PostAsJsonAsync($"/api/boards/{boardId}/notes", new { title });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<NoteDetail>())!;
    }
}
