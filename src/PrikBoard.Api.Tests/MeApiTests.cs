using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using PrikBoard.Api.Auth;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Data;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

public class MeApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>
{
    [Fact]
    public async Task People_choose_their_display_name_and_the_suggestion_goes_away()
    {
        var client = await factory.CreateSignedInClientAsync("grace@example.com");
        Assert.Equal("grace", (await client.MeAsync()).SuggestedDisplayName);

        var named = await Patch(client, new { displayName = "  Grace H.\u0007 " });
        Assert.Equal(("Grace H.", "Grace H.", null), (named.DisplayName, named.ChosenDisplayName, named.SuggestedDisplayName));
        // The cookie was re-issued without the provider's name.
        Assert.Null((await client.MeAsync()).SuggestedDisplayName);

        var cleared = await Patch(client, new { displayName = "" });
        Assert.Null(cleared.ChosenDisplayName);
        Assert.StartsWith("User ", cleared.DisplayName);
        Assert.Null(cleared.SuggestedDisplayName);

        var tooLong = await client.PatchAsJsonAsync("/api/me", new { displayName = new string('x', 101) });
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
    }

    [Fact]
    public async Task A_GitHub_sign_in_shows_a_copy_of_the_picture_to_others_until_the_person_opts_out()
    {
        const string email = "picture@example.com";
        var client = await factory.CreateSignedInClientAsync(email, avatar: PrikBoardApiFactory.GitHubAvatar);
        var me = await client.MeAsync();
        Assert.StartsWith($"/api/avatars/{me.Id}?v=", me.AvatarUrl);
        Assert.Equal(PrikBoardApiFactory.GitHubAvatar, me.SuggestedAvatarUrl);

        // Another person sees the copy on a shared board; the GitHub URL is never handed out.
        var other = await factory.CreateSignedInClientAsync("picture-viewer@example.com");
        var board = await client.CreateBoardAsync();
        await client.ShareAsync(board.Id, other, BoardRole.Viewer);
        var seen = await other.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}");
        Assert.Equal(me.AvatarUrl, seen!.People.Single(p => p.PrincipalId == me.Id).AvatarUrl);

        var picture = await other.GetAsync(me.AvatarUrl);
        Assert.Equal("image/png", picture.Content.Headers.ContentType?.MediaType);
        Assert.Equal(PrikBoardApiFactory.TestAvatar, await picture.Content.ReadAsByteArrayAsync());

        // Signing in again with the same picture keeps the version, so caches stay valid.
        var again = await factory.CreateSignedInClientAsync(email, avatar: PrikBoardApiFactory.GitHubAvatar);
        Assert.Equal(me.AvatarUrl, (await again.MeAsync()).AvatarUrl);

        // Removing it opts out: later sign-ins leave the initials alone.
        var removed = await (await client.DeleteAsync("/api/me/avatar")).Content.ReadFromJsonAsync<Caller>();
        Assert.Equal((null, true), (removed!.AvatarUrl, removed.AvatarOptedOut));
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(me.AvatarUrl)).StatusCode);
        var afterOptOut = await factory.CreateSignedInClientAsync(email, avatar: PrikBoardApiFactory.GitHubAvatar);
        Assert.Null((await afterOptOut.MeAsync()).AvatarUrl);

        // Taking it back opts in again.
        var taken = await (await afterOptOut.PutAsync("/api/me/avatar", null)).Content.ReadFromJsonAsync<Caller>();
        Assert.False(taken!.AvatarOptedOut);
        Assert.StartsWith($"/api/avatars/{me.Id}?v=", taken.AvatarUrl);
    }

    [Fact]
    public async Task A_changed_GitHub_picture_becomes_a_new_file_and_the_sweep_deletes_replaced_and_removed_ones()
    {
        const string email = "changing-picture@example.com";
        var client = await factory.CreateSignedInClientAsync(email, avatar: PrikBoardApiFactory.GitHubAvatar);
        var before = await client.MeAsync();
        var first = (await factory.AvatarPathAsync(before.Id))!;
        var download = await client.GetAsync(before.AvatarUrl);
        using var revalidate = new HttpRequestMessage(HttpMethod.Get, before.AvatarUrl) { Headers = { IfNoneMatch = { download.Headers.ETag! } } };
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(revalidate)).StatusCode);

        var changed = await factory.CreateSignedInClientAsync(email, avatar: PrikBoardApiFactory.OtherGitHubAvatar);
        var after = await changed.MeAsync();
        Assert.NotEqual(before.AvatarUrl, after.AvatarUrl);
        Assert.Equal(PrikBoardApiFactory.OtherAvatar, await changed.GetByteArrayAsync(after.AvatarUrl));
        var second = (await factory.AvatarPathAsync(before.Id))!;

        await factory.SweepFilesAsync();
        Assert.False(File.Exists(first));
        Assert.True(File.Exists(second));

        Assert.Equal(HttpStatusCode.OK, (await changed.DeleteAsync("/api/me/avatar")).StatusCode);
        await factory.SweepFilesAsync();
        Assert.False(File.Exists(second));
    }

    [Fact]
    public async Task Pictures_from_other_hosts_are_ignored_and_a_failed_download_does_not_stop_the_sign_in()
    {
        var client = await factory.CreateSignedInClientAsync("no-picture@example.com", avatar: "https://tracker.example/me.png");
        var me = await client.MeAsync();
        Assert.Equal((null, null), (me.AvatarUrl, me.SuggestedAvatarUrl));
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsync("/api/me/avatar", null)).StatusCode);

        // A trusted host that has no such picture: the sign-in works, without an avatar, and taking it fails.
        var missing = await factory.CreateSignedInClientAsync("gone-picture@example.com",
            avatar: "https://avatars.githubusercontent.com/missing");
        Assert.Null((await missing.MeAsync()).AvatarUrl);
        Assert.Equal(HttpStatusCode.BadGateway, (await missing.PutAsync("/api/me/avatar", null)).StatusCode);
    }

    [Fact]
    public async Task A_linked_sign_in_leads_to_the_same_user_and_can_be_unlinked_but_not_the_last_one()
    {
        var client = await factory.CreateSignedInClientAsync("linker@example.com", subject: "linker-test");
        var me = await client.MeAsync();

        Assert.Equal("Linked", await PrikBoardApiFactory.TestLinkAsync(client, "other", "linker-other"));
        Assert.Equal("AlreadyLinked", await PrikBoardApiFactory.TestLinkAsync(client, "other", "linker-other"));

        var viaOther = await factory.CreateSignedInClientAsync("linker@example.com", subject: "linker-other", provider: "other");
        Assert.Equal(me.Id, (await viaOther.MeAsync()).Id);

        var identities = (await client.MeAsync()).Identities;
        Assert.Equal(["test", "other"], identities.Select(i => i.Provider));

        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync($"/api/me/identities/{identities[1].Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await client.DeleteAsync($"/api/me/identities/{identities[0].Id}")).StatusCode);

        // The unlinked identity no longer leads here; signing in with it starts a new user.
        var again = await factory.CreateSignedInClientAsync("linker@example.com", subject: "linker-other", provider: "other");
        Assert.NotEqual(me.Id, (await again.MeAsync()).Id);
    }

    [Fact]
    public async Task An_identity_of_another_user_cannot_be_linked()
    {
        await factory.CreateSignedInClientAsync("owner-of@example.com", subject: "taken", provider: "other");
        var thief = await factory.CreateSignedInClientAsync("thief@example.com");

        Assert.Equal("InUseByAnotherUser", await PrikBoardApiFactory.TestLinkAsync(thief, "other", "taken"));
        Assert.Single((await thief.MeAsync()).Identities);
    }

    [Fact]
    public async Task No_personal_data_is_stored()
    {
        var client = await factory.CreateSignedInClientAsync("private.person@example.com", subject: "private-subject");
        var me = await client.MeAsync();

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PrikBoardDbContext>();

        var principalColumns = await Columns(db, "Principals");
        Assert.DoesNotContain(principalColumns, c => c is "Name" or "Email" or "Subject" or "Provider");
        Assert.Equal(["EmailHash", "Id", "LinkedAt", "Provider", "SubjectHash", "UserId"], (await Columns(db, "UserIdentities")).Order());

        var identity = await db.UserIdentities.SingleAsync(i => i.UserId == me.Id);
        var hasher = new KeyedHasher(PrikBoardApiFactory.HashKey);
        Assert.Equal(hasher.Subject("test", "private-subject"), identity.SubjectHash);
        Assert.Equal(hasher.Email("Private.Person@Example.com"), identity.EmailHash);
        Assert.Null((await db.Principals.SingleAsync(p => p.Id == me.Id)).DisplayName);

        // Nothing anywhere in the database holds the email or the provider's subject.
        foreach (var (table, column) in await TextColumns(db))
        {
            // Table and column names can't be parameters; these come from the database's own catalog, not from input.
#pragma warning disable EF1002
            var hits = await db.Database.SqlQueryRaw<int>(
                $"""SELECT count(*)::int AS "Value" FROM "{table}" WHERE "{column}"::text ILIKE '%private%'""").SingleAsync();
#pragma warning restore EF1002
            Assert.True(hits == 0, $"{table}.{column} contains personal data");
        }
    }

    private static async Task<Caller> Patch(HttpClient client, object body)
    {
        var response = await client.PatchAsJsonAsync("/api/me", body);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Caller>())!;
    }

    private static Task<List<string>> Columns(PrikBoardDbContext db, string table) =>
        db.Database.SqlQuery<string>(
            $"""SELECT column_name AS "Value" FROM information_schema.columns WHERE table_name = {table}""").ToListAsync();

    private static async Task<List<(string Table, string Column)>> TextColumns(PrikBoardDbContext db)
    {
        var rows = await db.Database.SqlQueryRaw<string>("""
            SELECT table_name || '.' || column_name AS "Value" FROM information_schema.columns
            WHERE table_schema = 'public' AND data_type IN ('text', 'character varying', 'jsonb')
            """).ToListAsync();
        return rows.Select(r => r.Split('.')).Select(p => (p[0], p[1])).ToList();
    }
}
