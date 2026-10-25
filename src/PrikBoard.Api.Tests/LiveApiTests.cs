using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using PrikBoard.Api.Contracts;
using PrikBoard.Api.Domain;

namespace PrikBoard.Api.Tests;

/// <summary>The live hub at /api/live: board events, presence, drags and cursors, over SignalR long polling against the test server.</summary>
public class LiveApiTests(PrikBoardApiFactory factory) : IClassFixture<PrikBoardApiFactory>, IAsyncDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private readonly List<HubConnection> connections = [];

    [Fact]
    public async Task Members_hear_about_changes_others_save_restores_and_new_rules_included()
    {
        var owner = await SessionAsync("live-owner@example.com");
        var editor = await SessionAsync("live-editor@example.com");
        var board = await owner.Http.CreateBoardAsync();
        await owner.Http.ShareAsync(board.Id, editor.Http, BoardRole.Editor);

        var live = await ConnectAsync(owner);
        var changes = live.Inbox<BoardChange>("BoardChanged");
        var joined = await live.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);
        Assert.Equal(BoardRole.Owner, joined.Role);

        var card = await editor.Http.CreateCardAsync(board.Id);

        var change = await changes.NextAsync();
        Assert.Equal(board.Id, change.BoardId);
        Assert.Equal((await editor.Http.MeAsync()).Id, change.By);

        // A restore finds the board through the deleted card.
        (await editor.Http.DeleteAsync($"/api/cards/{card.Id}")).EnsureSuccessStatusCode();
        await changes.DrainAsync();
        (await editor.Http.PostAsync($"/api/cards/{card.Id}/restore", null)).EnsureSuccessStatusCode();
        Assert.Equal(board.Id, (await changes.NextAsync()).BoardId);

        // New agent rules tell open boards too.
        await changes.DrainAsync();
        (await owner.Http.PatchAsJsonAsync($"/api/boards/{board.Id}", new { rules = "Keep cards short." })).EnsureSuccessStatusCode();
        Assert.Equal(board.Id, (await changes.NextAsync()).BoardId);
    }

    [Fact]
    public async Task Only_the_tab_that_saved_is_not_told_again_and_nobody_silences_someone_else()
    {
        var owner = await SessionAsync("live-tabs@example.com");
        var editor = await SessionAsync("live-silence-editor@example.com");
        var board = await owner.Http.CreateBoardAsync();
        await owner.Http.ShareAsync(board.Id, editor.Http, BoardRole.Editor);
        var tab1 = await ConnectAsync(owner);
        var tab2 = await ConnectAsync(owner);
        var inbox1 = tab1.Inbox<BoardChange>("BoardChanged");
        var inbox2 = tab2.Inbox<BoardChange>("BoardChanged");
        await tab1.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);
        await tab2.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/boards/{board.Id}/cards") { Content = JsonContent.Create(new { title = "From tab 1" }) };
        request.Headers.Add("X-PrikBoard-Live", tab1.Connection.ConnectionId);
        Assert.Equal(HttpStatusCode.Created, (await owner.Http.SendAsync(request)).StatusCode);

        await inbox2.NextAsync();
        Assert.False(await inbox1.AnyAsync());

        // Someone else's connection id does not silence it.
        var sneaky = new HttpRequestMessage(HttpMethod.Post, $"/api/boards/{board.Id}/cards") { Content = JsonContent.Create(new { title = "Sneaky" }) };
        sneaky.Headers.Add("X-PrikBoard-Live", tab1.Connection.ConnectionId);
        await editor.Http.SendAsync(sneaky);

        await inbox1.NextAsync();
    }

    [Fact]
    public async Task Reads_and_lookups_send_nothing()
    {
        var owner = await SessionAsync("live-quiet@example.com");
        var board = await owner.Http.CreateBoardAsync();
        var watcher = await SessionAsync("live-quiet-watcher@example.com");
        await owner.Http.ShareAsync(board.Id, watcher.Http, BoardRole.Viewer);
        var live = await ConnectAsync(watcher);
        var changes = live.Inbox<BoardChange>("BoardChanged");
        await live.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);

        await owner.Http.GetAsync($"/api/boards/{board.Id}");
        await owner.Http.PostAsJsonAsync($"/api/boards/{board.Id}/members/lookup", new { email = "nobody@example.com" });

        Assert.False(await changes.AnyAsync());
    }

    [Fact]
    public async Task Only_members_can_join_a_board()
    {
        var owner = await SessionAsync("live-private@example.com");
        var stranger = await SessionAsync("live-stranger@example.com");
        var board = await owner.Http.CreateBoardAsync();

        var live = await ConnectAsync(stranger);
        Assert.Null(await live.Connection.InvokeAsync<JoinedBoard?>("Join", board.Id));
        Assert.Null(await live.Connection.InvokeAsync<JoinedBoard?>("Join", Guid.NewGuid()));
    }

    [Fact]
    public async Task Viewers_see_who_is_on_the_board()
    {
        var owner = await SessionAsync("live-presence-owner@example.com");
        var viewer = await SessionAsync("live-presence-viewer@example.com");
        var board = await owner.Http.CreateBoardAsync();
        await owner.Http.ShareAsync(board.Id, viewer.Http, BoardRole.Viewer);
        await viewer.Http.PatchAsJsonAsync("/api/me", new { displayName = "Vera" });

        var ownerLive = await ConnectAsync(owner);
        var presence = ownerLive.Inbox<BoardPresence>("Presence");
        await ownerLive.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);

        var viewerLive = await ConnectAsync(viewer);
        var joined = await viewerLive.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);
        Assert.Equal(2, joined.Viewers.Count);

        var update = await presence.NextAsync();
        Assert.Contains(update.Viewers, v => v.DisplayName == "Vera" && v.Kind == PrincipalKind.User);

        // A second tab of the same person is listed once.
        var secondTab = await ConnectAsync(viewer);
        Assert.Equal(2, (await secondTab.Connection.InvokeAsync<JoinedBoard>("Join", board.Id)).Viewers.Count);

        await viewerLive.Connection.StopAsync();
        await secondTab.Connection.StopAsync();
        var left = await presence.NextMatchingAsync(p => p.Viewers.Count == 1);
        Assert.DoesNotContain(left.Viewers, v => v.DisplayName == "Vera");
    }

    [Fact]
    public async Task Editors_drags_reach_the_others_and_viewers_cannot_drag()
    {
        var owner = await SessionAsync("live-drag-owner@example.com");
        var viewer = await SessionAsync("live-drag-viewer@example.com");
        var board = await owner.Http.CreateBoardAsync();
        var card = await owner.Http.CreateCardAsync(board.Id);
        await owner.Http.ShareAsync(board.Id, viewer.Http, BoardRole.Viewer);

        var ownerLive = await ConnectAsync(owner);
        var viewerLive = await ConnectAsync(viewer);
        var ownerDrags = ownerLive.Inbox<RemoteDrag>("Dragging");
        var viewerDrags = viewerLive.Inbox<RemoteDrag>("Dragging");
        var ended = viewerLive.Inbox<Guid>("DragEnded");
        var joined = await ownerLive.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);
        await viewerLive.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);

        await ownerLive.Connection.InvokeAsync("Drag", new DragUpdate(DragKind.Card, card.Id, 42, -7));
        var drag = await viewerDrags.NextAsync();
        Assert.Equal((DragKind.Card, card.Id, 42d, -7d, joined.Key), (drag.Kind, drag.Id, drag.Dx, drag.Dy, drag.Key));

        // A resize names its corner or edge. Waiting out the hub's throttle first keeps it from being dropped.
        await Task.Delay(60);
        var bucketId = board.Buckets[0].Id;
        await ownerLive.Connection.InvokeAsync("Drag", new DragUpdate(DragKind.Resize, bucketId, -20, 40, ResizeHandle.TopLeft));
        var resize = await viewerDrags.NextAsync();
        Assert.Equal((DragKind.Resize, bucketId, ResizeHandle.TopLeft), (resize.Kind, resize.Id, resize.Handle));

        // Drawings move on their own too.
        await Task.Delay(60);
        var drawingId = Guid.NewGuid();
        await ownerLive.Connection.InvokeAsync("Drag", new DragUpdate(DragKind.Drawing, drawingId, 60, 0));
        var drawing = await viewerDrags.NextAsync();
        Assert.Equal((DragKind.Drawing, drawingId, 60d), (drawing.Kind, drawing.Id, drawing.Dx));

        // Several selected items moving together list them all; a group of none is no drag.
        await Task.Delay(60);
        await ownerLive.Connection.InvokeAsync("Drag", new DragUpdate(DragKind.Group, card.Id, 20, 20, Items: [card.Id, bucketId]));
        var group = await viewerDrags.NextAsync();
        Assert.Equal((DragKind.Group, card.Id), (group.Kind, group.Id));
        Assert.Equal([card.Id, bucketId], group.Items);
        var empty = await Assert.ThrowsAsync<HubException>(() =>
            ownerLive.Connection.InvokeAsync("Drag", new DragUpdate(DragKind.Group, card.Id, 20, 20, Items: [])));
        Assert.Contains("Group", empty.Message);

        await ownerLive.Connection.InvokeAsync("DragEnd");
        Assert.Equal(joined.Key, await ended.NextAsync());
        Assert.False(await ownerDrags.AnyAsync());

        var refused = await Assert.ThrowsAsync<HubException>(() =>
            viewerLive.Connection.InvokeAsync("Drag", new DragUpdate(DragKind.Card, card.Id, 1, 1)));
        Assert.Contains("Editor", refused.Message);
    }

    [Fact]
    public async Task Members_see_where_the_others_point()
    {
        var owner = await SessionAsync("live-cursor-owner@example.com");
        var viewer = await SessionAsync("live-cursor-viewer@example.com");
        var board = await owner.Http.CreateBoardAsync();
        Assert.True(board.ShowCursors);
        await owner.Http.ShareAsync(board.Id, viewer.Http, BoardRole.Viewer);

        var ownerLive = await ConnectAsync(owner);
        var viewerLive = await ConnectAsync(viewer);
        var cursors = ownerLive.Inbox<RemoteCursor>("CursorMoved");
        var hidden = ownerLive.Inbox<Guid>("CursorHidden");
        var ownCursors = viewerLive.Inbox<RemoteCursor>("CursorMoved");
        await ownerLive.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);
        var joined = await viewerLive.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);

        // Viewers point too: a cursor changes nothing.
        await viewerLive.Connection.InvokeAsync("MoveCursor", new CursorUpdate(120.5, -40));
        var cursor = await cursors.NextAsync();
        Assert.Equal((joined.Key, (await viewer.Http.MeAsync()).Id, 120.5, -40d), (cursor.Key, cursor.PrincipalId, cursor.X, cursor.Y));
        Assert.False(await ownCursors.AnyAsync());

        var off = await Assert.ThrowsAsync<HubException>(() =>
            viewerLive.Connection.InvokeAsync("MoveCursor", new CursorUpdate(1e9, 0)));
        Assert.Contains("point", off.Message);

        // Someone joining later sees the pointer where it stands, without waiting for it to move.
        var lateLive = await ConnectAsync(owner);
        var lateCursors = lateLive.Inbox<RemoteCursor>("CursorMoved");
        await lateLive.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);
        var late = await lateCursors.NextAsync();
        Assert.Equal((joined.Key, 120.5, -40d), (late.Key, late.X, late.Y));

        await viewerLive.Connection.InvokeAsync("HideCursor");
        Assert.Equal(joined.Key, await hidden.NextAsync());
        await viewerLive.Connection.InvokeAsync("Leave");
        Assert.Equal(joined.Key, await hidden.NextAsync());
    }

    [Fact]
    public async Task Members_see_what_the_others_have_selected()
    {
        var owner = await SessionAsync("live-select-owner@example.com");
        var viewer = await SessionAsync("live-select-viewer@example.com");
        var board = await owner.Http.CreateBoardAsync();
        var card = await owner.Http.CreateCardAsync(board.Id);
        await owner.Http.ShareAsync(board.Id, viewer.Http, BoardRole.Viewer);

        var ownerLive = await ConnectAsync(owner);
        var viewerLive = await ConnectAsync(viewer);
        var selections = ownerLive.Inbox<RemoteSelection>("Selected");
        var ownSelections = viewerLive.Inbox<RemoteSelection>("Selected");
        await ownerLive.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);
        var joined = await viewerLive.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);

        // Viewers select too (selecting changes nothing); the same item twice counts once.
        var bucketId = board.Buckets[0].Id;
        await viewerLive.Connection.InvokeAsync("Select", new SelectionUpdate([card.Id, bucketId, card.Id]));
        var selected = await selections.NextAsync();
        Assert.Equal((joined.Key, (await viewer.Http.MeAsync()).Id), (selected.Key, selected.PrincipalId));
        Assert.Equal([card.Id, bucketId], selected.Items);
        Assert.False(await ownSelections.AnyAsync());

        // Someone joining later sees it at once.
        var lateLive = await ConnectAsync(owner);
        var lateSelections = lateLive.Inbox<RemoteSelection>("Selected");
        await lateLive.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);
        Assert.Equal([card.Id, bucketId], (await lateSelections.NextAsync()).Items);

        await viewerLive.Connection.InvokeAsync("Select", new SelectionUpdate([]));
        Assert.Empty((await selections.NextAsync()).Items);

        // Leaving takes the pins along.
        await viewerLive.Connection.InvokeAsync("Select", new SelectionUpdate([card.Id]));
        await selections.NextAsync();
        await viewerLive.Connection.InvokeAsync("Leave");
        var gone = await selections.NextAsync();
        Assert.Equal((joined.Key, 0), (gone.Key, gone.Items.Count));
    }

    [Fact]
    public async Task Cursors_follow_the_board_setting_on_open_connections()
    {
        var owner = await SessionAsync("live-cursor-setting-owner@example.com");
        var editor = await SessionAsync("live-cursor-setting-editor@example.com");
        var board = await owner.Http.CreateBoardAsync();
        await owner.Http.ShareAsync(board.Id, editor.Http, BoardRole.Editor);

        var ownerLive = await ConnectAsync(owner);
        var editorLive = await ConnectAsync(editor);
        var cursors = ownerLive.Inbox<RemoteCursor>("CursorMoved");
        await ownerLive.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);
        await editorLive.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);

        // Only owners change it.
        Assert.Equal(HttpStatusCode.Forbidden, (await editor.Http.PatchAsJsonAsync($"/api/boards/{board.Id}", new { showCursors = false })).StatusCode);

        var response = await owner.Http.PatchAsJsonAsync($"/api/boards/{board.Id}", new { showCursors = false });
        response.EnsureSuccessStatusCode();
        Assert.False((await response.Content.ReadFromJsonAsync<BoardSummary>())!.ShowCursors);
        var refused = await Assert.ThrowsAsync<HubException>(() =>
            editorLive.Connection.InvokeAsync("MoveCursor", new CursorUpdate(10, 10)));
        Assert.Contains("cursors", refused.Message);
        Assert.False((await owner.Http.GetFromJsonAsync<BoardDetail>($"/api/boards/{board.Id}"))!.ShowCursors);

        // Back on, without joining again.
        (await owner.Http.PatchAsJsonAsync($"/api/boards/{board.Id}", new { showCursors = true })).EnsureSuccessStatusCode();
        await editorLive.Connection.InvokeAsync("MoveCursor", new CursorUpdate(10, 10));
        Assert.Equal(10d, (await cursors.NextAsync()).X);

        (await owner.Http.PatchAsJsonAsync($"/api/boards/{board.Id}", new { showCursors = false })).EnsureSuccessStatusCode();
        await Assert.ThrowsAsync<HubException>(() => editorLive.Connection.InvokeAsync("Select", new SelectionUpdate([board.Buckets[0].Id])));
    }

    [Fact]
    public async Task Visitors_of_a_public_board_never_see_or_send_cursors()
    {
        var owner = await SessionAsync("live-cursor-public-owner@example.com");
        var editor = await SessionAsync("live-cursor-public-editor@example.com");
        var board = await owner.Http.CreateBoardAsync("Live public cursors");
        await owner.Http.ShareAsync(board.Id, editor.Http, BoardRole.Editor);
        (await owner.Http.PatchAsJsonAsync($"/api/boards/{board.Id}", new { isPublic = true, publicShowsPeople = true })).EnsureSuccessStatusCode();

        var ownerLive = await ConnectAsync(owner);
        var editorLive = await ConnectAsync(editor);
        var guest = await ConnectAsync(new Session(factory.CreateAnonymousClient(), ""));
        var memberCursors = editorLive.Inbox<RemoteCursor>("CursorMoved");
        var guestCursors = guest.Inbox<RemoteCursor>("CursorMoved");
        var guestHidden = guest.Inbox<Guid>("CursorHidden");
        await ownerLive.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);
        await editorLive.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);
        Assert.True((await guest.Connection.InvokeAsync<JoinedBoard>("Join", board.Id)).Visiting);

        // The members of a public board still see each other.
        await ownerLive.Connection.InvokeAsync("MoveCursor", new CursorUpdate(5, 5));
        await memberCursors.NextAsync();
        await ownerLive.Connection.InvokeAsync("HideCursor");
        Assert.False(await guestCursors.AnyAsync());
        Assert.False(await guestHidden.AnyAsync());

        var guestSelections = guest.Inbox<RemoteSelection>("Selected");
        await ownerLive.Connection.InvokeAsync("Select", new SelectionUpdate([board.Buckets[0].Id]));
        Assert.False(await guestSelections.AnyAsync());

        await Assert.ThrowsAsync<HubException>(() => guest.Connection.InvokeAsync("MoveCursor", new CursorUpdate(5, 5)));
        await Assert.ThrowsAsync<HubException>(() => guest.Connection.InvokeAsync("Select", new SelectionUpdate([])));
    }

    [Fact]
    public async Task Role_changes_and_removals_apply_to_open_connections()
    {
        var owner = await SessionAsync("live-role-owner@example.com");
        var editor = await SessionAsync("live-role-editor@example.com");
        var board = await owner.Http.CreateBoardAsync();
        await owner.Http.ShareAsync(board.Id, editor.Http, BoardRole.Editor);
        var editorId = (await editor.Http.MeAsync()).Id;

        var live = await ConnectAsync(editor);
        var changes = live.Inbox<BoardChange>("BoardChanged");
        var access = live.Inbox<Guid>("AccessChanged");
        var lists = live.Inbox<object?>("BoardsChanged");
        await live.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);
        await live.Connection.InvokeAsync("Drag", new DragUpdate(DragKind.Card, Guid.NewGuid(), 1, 1));

        await owner.Http.PatchAsJsonAsync($"/api/boards/{board.Id}/members/{editorId}", new { role = BoardRole.Viewer });
        await access.NextAsync();

        await Assert.ThrowsAsync<HubException>(() => live.Connection.InvokeAsync("Drag", new DragUpdate(DragKind.Card, Guid.NewGuid(), 2, 2)));

        // Removed members stop hearing about the board.
        await access.DrainAsync();
        await lists.DrainAsync();
        await owner.Http.DeleteAsync($"/api/boards/{board.Id}/members/{editorId}");
        Assert.Equal(board.Id, await access.NextAsync());
        await lists.NextAsync();
        await changes.DrainAsync();

        await owner.Http.CreateCardAsync(board.Id);
        Assert.False(await changes.AnyAsync());
        Assert.Null(await live.Connection.InvokeAsync<JoinedBoard?>("Join", board.Id));
    }

    [Fact]
    public async Task Renaming_or_deleting_a_board_updates_every_members_list()
    {
        var owner = await SessionAsync("live-lists-owner@example.com");
        var viewer = await SessionAsync("live-lists-viewer@example.com");
        var board = await owner.Http.CreateBoardAsync();
        await owner.Http.ShareAsync(board.Id, viewer.Http, BoardRole.Viewer);

        // Not joined to the board: list events go to the person wherever they are.
        var live = await ConnectAsync(viewer);
        var lists = live.Inbox<object?>("BoardsChanged");

        await owner.Http.PatchAsJsonAsync($"/api/boards/{board.Id}", new { name = "Renamed" });
        await lists.NextAsync();
        await owner.Http.DeleteAsync($"/api/boards/{board.Id}");
        await lists.NextAsync();
    }

    [Fact]
    public async Task Cross_site_pages_cannot_connect_with_the_cookie()
    {
        var person = await SessionAsync("live-csrf@example.com");
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(person, origin: "https://evil.example"));
    }

    [Fact]
    public async Task Anonymous_callers_only_follow_public_boards_and_see_who_is_there_as_the_board_allows()
    {
        var owner = await SessionAsync("live-public-owner@example.com");
        var board = await owner.Http.CreateBoardAsync("Live public");
        var guest = await ConnectAsync(new Session(factory.CreateAnonymousClient(), ""));
        Assert.Null(await guest.Connection.InvokeAsync<JoinedBoard?>("Join", board.Id));

        (await owner.Http.PatchAsJsonAsync($"/api/boards/{board.Id}", new { isPublic = true })).EnsureSuccessStatusCode();
        var member = await ConnectAsync(owner);
        var memberPresence = member.Inbox<BoardPresence>("Presence");
        await member.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);

        // With people hidden, the guest only sees how many are there; the member sees one guest.
        var joined = await guest.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);
        Assert.Equal((true, BoardRole.Viewer, 0, 2), (joined.Visiting, joined.Role, joined.Viewers.Count, joined.Guests));
        var seen = await memberPresence.NextMatchingAsync(p => p.Guests == 1);
        Assert.Single(seen.Viewers);

        // Changes reach the guest, without who made them; drags are for editors.
        var changes = guest.Inbox<BoardChange>("BoardChanged");
        await owner.Http.CreateCardAsync(board.Id);
        var change = await changes.NextAsync();
        Assert.Equal((board.Id, Guid.Empty), (change.BoardId, change.By));
        await Assert.ThrowsAnyAsync<HubException>(() => guest.Connection.InvokeAsync("Drag", new DragUpdate(DragKind.Card, Guid.NewGuid(), 20, 20)));

        // Showing people: the guest is asked to look again and then sees the members by name.
        var access = guest.Inbox<Guid>("AccessChanged");
        (await owner.Http.PatchAsJsonAsync($"/api/boards/{board.Id}", new { publicShowsPeople = true })).EnsureSuccessStatusCode();
        Assert.Equal(board.Id, await access.NextAsync());
        var rejoined = await guest.Connection.InvokeAsync<JoinedBoard>("Join", board.Id);
        Assert.Equal(((await owner.Http.MeAsync()).DisplayName, 1), (Assert.Single(rejoined.Viewers).DisplayName, rejoined.Guests));

        // Members-only again: the guest is told and can't join.
        (await owner.Http.PatchAsJsonAsync($"/api/boards/{board.Id}", new { isPublic = false })).EnsureSuccessStatusCode();
        Assert.Equal(board.Id, await access.NextAsync());
        Assert.Null(await guest.Connection.InvokeAsync<JoinedBoard?>("Join", board.Id));
    }

    [Fact]
    public async Task A_wrong_token_cannot_connect()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => ConnectAsync(new Session(factory.CreateAnonymousClient(), ""), bearer: "pb_pat_not-a-token"));
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var connection in connections) await connection.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    private sealed record Session(HttpClient Http, string Cookie);

    /// <summary>A signed-in HTTP client plus its cookie, which the hub connection sends too.</summary>
    private async Task<Session> SessionAsync(string email)
    {
        var http = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = false });
        var response = await PrikBoardApiFactory.TestSignInAsync(http, email);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var cookie = string.Join("; ", response.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]));
        http.DefaultRequestHeaders.Add("Cookie", cookie);
        return new Session(http, cookie);
    }

    private async Task<Live> ConnectAsync(Session session, string origin = "http://localhost", string? bearer = null)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/api/live"), options =>
            {
                // The test server has no real sockets; long polling goes through its in-memory handler.
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                if (session.Cookie.Length > 0) options.Headers["Cookie"] = session.Cookie;
                if (bearer is not null) options.Headers["Authorization"] = $"Bearer {bearer}";
                options.Headers["Origin"] = origin;
            })
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()))
            .Build();
        connections.Add(connection);
        var live = new Live(connection);
        await connection.StartAsync();
        return live;
    }

    private sealed record Live(HubConnection Connection)
    {
        /// <summary>Collects one kind of server message. Subscribe before the action that triggers it.</summary>
        public Inbox<T> Inbox<T>(string method)
        {
            var channel = Channel.CreateUnbounded<T>();
            if (typeof(T) == typeof(object)) Connection.On(method, () => channel.Writer.TryWrite(default!));
            else Connection.On<T>(method, message => channel.Writer.TryWrite(message));
            return new Inbox<T>(channel.Reader, this);
        }

        /// <summary>
        /// A round trip to the hub. A connection gets its messages in order, so once the answer is back, everything sent to
        /// it before has arrived. The hub has no method to call for nothing, so this calls one it doesn't have.
        /// </summary>
        public async Task SyncAsync() =>
            await Assert.ThrowsAsync<HubException>(() => Connection.InvokeAsync("Sync"));
    }

    private sealed record Inbox<T>(ChannelReader<T> Reader, Live Live)
    {
        public async Task<T> NextAsync()
        {
            using var timeout = new CancellationTokenSource(Wait);
            return await Reader.ReadAsync(timeout.Token);
        }

        public async Task<T> NextMatchingAsync(Func<T, bool> match)
        {
            while (true)
            {
                var message = await NextAsync();
                if (match(message)) return message;
            }
        }

        /// <summary>Whether a message has come for anything done before: the server sends them before it answers.</summary>
        public async Task<bool> AnyAsync()
        {
            await Live.SyncAsync();
            return Reader.TryPeek(out _);
        }

        /// <summary>Lets messages already on their way arrive, then forgets them.</summary>
        public async Task DrainAsync()
        {
            await Live.SyncAsync();
            while (Reader.TryRead(out _)) { }
        }
    }
}
